using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// agent job の登録・照会・取消。実行中 worker は process 内。request_id / terminal result は disk に残し、
/// Facade 再起動後の二重 submit を fail-closed で防ぐ。
/// </summary>
public sealed class AgentJobService
{
    public const int DefaultPollAfterMs = 2000;
    public const string InterruptedError = "agent job was interrupted because the facade process exited.";

    private readonly AgentFacade _facade;
    private readonly TimeProvider _timeProvider;
    private readonly string _storeDirectory;
    private readonly ConcurrentDictionary<string, AgentJob> _byRequestId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentJob> _byJobId = new(StringComparer.Ordinal);

    public AgentJobService(AgentFacade facade)
        : this(facade, GetDefaultJobStoreDirectory(), TimeProvider.System)
    {
    }

    public AgentJobService(AgentFacade facade, string jobStoreDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobStoreDirectory);
        _facade = facade;
        _storeDirectory = Path.GetFullPath(jobStoreDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_storeDirectory);
    }

    public string StoreDirectory => _storeDirectory;

    public static string GetDefaultJobStoreDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "." + AgentRunLogFactory.DefaultProductDirectoryName,
            "jobs");
    }

    public AgentJobSnapshot Start(string requestId, AgentRunRequest request)
    {
        var key = NormalizeRequestId(requestId);
        AgentFacade.Validate(request);
        var fingerprint = ComputeRequestFingerprint(request);

        if (_byRequestId.TryGetValue(key, out var existing))
        {
            EnsureSameRequest(existing.Request, request);
            return existing.CreateSnapshot(DefaultPollAfterMs);
        }

        var stored = TryReadByRequestId(key);
        if (stored is not null)
        {
            EnsureSameFingerprint(stored, fingerprint);
            var recovered = RecoverStored(stored);
            return ToSnapshot(recovered);
        }

        var jobId = AgentRunLogFactory.CreateRunId(_timeProvider);
        var created = new AgentJob(jobId, key, request, _timeProvider.GetUtcNow());
        if (!_byRequestId.TryAdd(key, created))
        {
            created.Discard();
            var winner = _byRequestId[key];
            EnsureSameRequest(winner.Request, request);
            return winner.CreateSnapshot(DefaultPollAfterMs);
        }

        if (!_byJobId.TryAdd(jobId, created))
        {
            created.Discard();
            _byRequestId.TryRemove(key, out _);
            throw new InvalidOperationException("Failed to register agent job.");
        }

        try
        {
            Persist(ToRecord(created.CreateSnapshot(DefaultPollAfterMs), fingerprint));
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            Unregister(created);
            throw;
        }

        _ = RunWorkerAsync(created, fingerprint);
        return created.CreateSnapshot(DefaultPollAfterMs);
    }

    public AgentJobSnapshot Get(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("job_id is required.");
        }

        var id = jobId.Trim();
        if (_byJobId.TryGetValue(id, out var live))
        {
            return live.CreateSnapshot(DefaultPollAfterMs);
        }

        var stored = TryReadByJobId(id);
        if (stored is null)
        {
            throw new KeyNotFoundException($"Unknown job '{id}'.");
        }

        return ToSnapshot(RecoverStored(stored));
    }

    public AgentJobSnapshot Cancel(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("job_id is required.");
        }

        var id = jobId.Trim();
        if (_byJobId.TryGetValue(id, out var live))
        {
            live.RequestCancel();
            var snapshot = live.CreateSnapshot(DefaultPollAfterMs);
            Persist(ToRecord(snapshot, ComputeRequestFingerprint(live.Request)));
            Evict(live);
            return snapshot;
        }

        var record = TryReadByJobId(id);
        if (record is null)
        {
            throw new KeyNotFoundException($"Unknown job '{id}'.");
        }

        return ToSnapshot(RecoverStored(record));
    }

    private async Task RunWorkerAsync(AgentJob job, string fingerprint)
    {
        try
        {
            var result = await _facade.RunAsync(
                    job.Request,
                    onStdoutLine: null,
                    job.CancellationToken,
                    job.JobId)
                .ConfigureAwait(false);
            job.Complete(result);
        }
        catch (OperationCanceledException ex)
        {
            CliJson.TraceException(ex);
            job.MarkCancelled();
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            job.Fail("agent job failed. See server traces for details.");
        }

        Persist(ToRecord(job.CreateSnapshot(DefaultPollAfterMs), fingerprint));
        Evict(job);
    }

    private void Unregister(AgentJob job)
    {
        _byRequestId.TryRemove(job.RequestId, out _);
        _byJobId.TryRemove(job.JobId, out _);
        job.Discard();
    }

    private void Evict(AgentJob job)
    {
        if (!job.IsTerminal)
        {
            return;
        }

        Unregister(job);
    }

    private AgentJobRecord RecoverStored(AgentJobRecord stored)
    {
        if (stored.Status != AgentJobStatus.Running)
        {
            return stored;
        }

        var failed = stored with
        {
            Status = AgentJobStatus.Failed,
            Error = InterruptedError,
            Result = null,
        };
        Persist(failed);
        return failed;
    }

    private AgentJobRecord? TryReadByRequestId(string requestId)
    {
        return TryReadFile(RequestRecordPath(requestId));
    }

    private AgentJobRecord? TryReadByJobId(string jobId)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_storeDirectory, "req-*.json"))
            {
                var record = TryReadFile(path);
                if (record is not null && string.Equals(record.JobId, jobId, StringComparison.Ordinal))
                {
                    return record;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw;
        }
    }

    private AgentJobRecord? TryReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<AgentJobRecord>(File.ReadAllText(path), AgentJson.Options);
            if (record is null || string.IsNullOrWhiteSpace(record.JobId) || string.IsNullOrWhiteSpace(record.RequestId))
            {
                throw new InvalidOperationException("Job record is invalid.");
            }

            return record;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw;
        }
    }

    private void Persist(AgentJobRecord record)
    {
        try
        {
            Directory.CreateDirectory(_storeDirectory);
            WriteAtomic(RequestRecordPath(record.RequestId), JsonSerializer.Serialize(record, AgentJson.Options));
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw;
        }
    }

    private string RequestRecordPath(string requestId)
    {
        return Path.Combine(_storeDirectory, "req-" + HashText(requestId) + ".json");
    }

    private static void WriteAtomic(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    internal static string NormalizeRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("request_id is required.");
        }

        return requestId.Trim();
    }

    internal static void EnsureSameRequest(AgentRunRequest existing, AgentRunRequest request)
    {
        if (!SameRequest(existing, request))
        {
            throw new ArgumentException(
                "request_id is already bound to a different start_agent request.");
        }
    }

    internal static void EnsureSameFingerprint(AgentJobRecord stored, string fingerprint)
    {
        if (!string.Equals(stored.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "request_id is already bound to a different start_agent request.");
        }
    }

    internal static bool SameRequest(AgentRunRequest left, AgentRunRequest right)
    {
        return string.Equals(ComputeRequestFingerprint(left), ComputeRequestFingerprint(right), StringComparison.Ordinal);
    }

    internal static string ComputeRequestFingerprint(AgentRunRequest request)
    {
        var builder = new StringBuilder();
        builder.Append(request.Agent).Append('\n');
        builder.Append(request.Prompt).Append('\n');
        builder.Append(request.WorkingDirectory).Append('\n');
        builder.Append(request.SessionId ?? string.Empty).Append('\n');
        builder.Append(request.AutoApprove ? "1" : "0").Append('\n');
        if (request.Skills is not null)
        {
            foreach (var skill in request.Skills)
            {
                builder.Append(skill).Append('\n');
            }
        }

        return HashText(builder.ToString());
    }

    private static string HashText(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static AgentJobSnapshot ToSnapshot(AgentJobRecord record)
    {
        return new AgentJobSnapshot(
            record.JobId,
            record.RequestId,
            record.Status,
            record.PollAfterMs,
            record.Result,
            record.Error);
    }

    private static AgentJobRecord ToRecord(AgentJobSnapshot snapshot, string fingerprint)
    {
        return new AgentJobRecord(
            snapshot.JobId,
            snapshot.RequestId,
            snapshot.Status,
            snapshot.PollAfterMs,
            fingerprint,
            snapshot.Result,
            snapshot.Error);
    }
}

internal sealed record AgentJobRecord(
    string JobId,
    string RequestId,
    string Status,
    int PollAfterMs,
    string RequestFingerprint,
    AgentRunResult? Result,
    string? Error);


