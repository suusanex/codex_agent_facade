using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// agent job の登録・照会・取消。実行中 worker は process 内。request_id / terminal result は disk に残し、
/// Facade 再起動後の二重 submit を fail-closed で防ぐ。
/// </summary>
public sealed class AgentJobService
{
    public const int DefaultWaitTimeoutSeconds = 300;
    public const int MinWaitTimeoutSeconds = 1;
    public const int MaxWaitTimeoutSeconds = 86400;
    public const string InterruptedError = "agent job was interrupted because the facade process exited.";

    private readonly AgentFacade _facade;
    private readonly TimeProvider _timeProvider;
    private readonly string _storeDirectory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _terminalLogs = new(StringComparer.Ordinal);
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
        _logger = FacadeLog.CreateLogger(FacadeLogging.LoggerCategory);
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
            EnsureBound(existing, request, fingerprint);
            EnsureJulesMonitor(existing, fingerprint);
            return existing.CreateSnapshot();
        }

        var stored = TryReadByRequestId(key);
        if (stored is not null)
        {
            EnsureSameFingerprint(stored, fingerprint);
            if (CanResumeJules(stored))
            {
                var resumed = AttachJules(stored);
                EnsureJulesMonitor(resumed, fingerprint);
                return resumed.CreateSnapshot();
            }

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
            return winner.CreateSnapshot();
        }

        if (!_byJobId.TryAdd(jobId, created))
        {
            created.Discard();
            _byRequestId.TryRemove(key, out _);
            throw new InvalidOperationException("Failed to register agent job.");
        }

        try
        {
            Persist(ToRecord(created.CreateSnapshot(), fingerprint));
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            Unregister(created);
            throw;
        }

        _ = RunWorkerAsync(created, fingerprint);
        return created.CreateSnapshot();
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
            EnsureJulesMonitor(live, live.BoundFingerprint ?? ComputeRequestFingerprint(live.Request));
            return live.CreateSnapshot();
        }

        var stored = TryReadByJobId(id);
        if (stored is null)
        {
            throw new KeyNotFoundException($"Unknown job '{id}'.");
        }

        if (CanResumeJules(stored))
        {
            var resumed = AttachJules(stored);
            EnsureJulesMonitor(resumed, stored.RequestFingerprint);
            return resumed.CreateSnapshot();
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
            if (IsJulesJob(live))
            {
                throw new InvalidOperationException("Jules cancel is unsupported. The remote session was not changed.");
            }

            var fingerprint = live.BoundFingerprint ?? ComputeRequestFingerprint(live.Request);
            if (live.RequestCancel(next => Persist(ToRecord(next, fingerprint))))
            {
                LogTerminal(live.CreateSnapshot(), AgentJobStatus.Cancelled, exitCode: null, errorType: null);
            }

            var snapshot = live.CreateSnapshot();
            Evict(live);
            return snapshot;
        }

        var record = TryReadByJobId(id);
        if (record is null)
        {
            throw new KeyNotFoundException($"Unknown job '{id}'.");
        }

        if (IsJulesRecord(record))
        {
            throw new InvalidOperationException("Jules cancel is unsupported. The remote session was not changed.");
        }

        return ToSnapshot(RecoverStored(record));
    }

    /// <summary>
    /// 既存 job が terminal になるまで待つ。新しい worker は開始・再実行しない。
    /// timeout では running snapshot を返し、waiter の cancel は job を止めない。
    /// </summary>
    public async Task<AgentJobSnapshot> WaitAsync(
        string jobId,
        int timeoutSeconds = DefaultWaitTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (timeoutSeconds < MinWaitTimeoutSeconds || timeoutSeconds > MaxWaitTimeoutSeconds)
        {
            throw new ArgumentException(
                "timeout_seconds must be between "
                + MinWaitTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " and "
                + MaxWaitTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ".");
        }

        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("job_id is required.");
        }

        var id = jobId.Trim();
        Get(id);
        if (_byJobId.TryGetValue(id, out var live) && !live.IsTerminal)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await live.WhenTerminal.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Get(id);
            }
        }

        return Get(id);
    }

    private async Task RunWorkerAsync(AgentJob job, string fingerprint)
    {
        if (!job.TryBeginMonitor())
        {
            return;
        }

        try
        {
            if (IsJules(job.Request))
            {
                await RunJulesAsync(job, fingerprint).ConfigureAwait(false);
            }
            else
            {
                await RunLocalAsync(job, fingerprint).ConfigureAwait(false);
            }
        }
        finally
        {
            // 保存に失敗して terminal になっていないときも、waiter を running のまま残さない。
            // Jules の監視中断は remote の停止ではないので、running のまま残す。
            if (!job.IsTerminal && !IsJules(job.Request) && job.Jules is null)
            {
                job.Fail("agent job failed. See the server log for details.");
            }

            job.EndMonitorIfStillRunning();
            Evict(job);
        }
    }

    private async Task RunLocalAsync(AgentJob job, string fingerprint)
    {
        try
        {
            var result = await _facade.RunAsync(
                    job.Request,
                    onStdoutLine: null,
                    job.CancellationToken,
                    job.JobId)
                .ConfigureAwait(false);
            if (job.Complete(result, next => Persist(ToRecord(next, fingerprint))))
            {
                LogTerminal(job.CreateSnapshot(), AgentJobStatus.Completed, result.ExitCode, errorType: null);
            }
        }
        catch (OperationCanceledException ex)
        {
            CliJson.TraceException(ex);
            if (job.MarkCancelled(next => Persist(ToRecord(next, fingerprint))))
            {
                LogTerminal(job.CreateSnapshot(), AgentJobStatus.Cancelled, exitCode: null, errorType: ex.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            var failure = CreateFailure(ex);
            if (job.Fail("agent job failed. See the server log for details.", failure, next => Persist(ToRecord(next, fingerprint))))
            {
                LogTerminal(job.CreateSnapshot(), AgentJobStatus.Failed, exitCode: failure.ExitCode, errorType: ex.GetType().Name);
            }
        }
    }

    private async Task RunJulesAsync(AgentJob job, string fingerprint)
    {
        var resume = job.Jules?.SessionId is not null ? job.Jules : null;
        var context = CreateJulesContext(job, fingerprint, resume);
        try
        {
            var outcome = await _facade.RunJulesAsync(job.Request, context, job.CancellationToken).ConfigureAwait(false);
            await ApplyJulesAsync(job, fingerprint, outcome).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            CliJson.TraceException(ex);
            var sessionId = job.Jules?.SessionId;
            var phase = job.Jules?.RemotePhase;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            if (phase is JulesRemotePhase.SubmittingCreate or JulesRemotePhase.SubmissionUnknown)
            {
                var failure = new AgentJobFailure(
                    "submission_unknown",
                    "Jules session create result is unknown. The request was not sent again.");
                if (job.Fail(failure.Summary, failure, next => Persist(ToRecord(next, fingerprint))))
                {
                    LogTerminal(job.CreateSnapshot(), AgentJobStatus.Failed, exitCode: null, errorType: ex.GetType().Name);
                }

                return;
            }

            if (job.MarkCancelled(next => Persist(ToRecord(next, fingerprint))))
            {
                LogTerminal(job.CreateSnapshot(), AgentJobStatus.Cancelled, exitCode: null, errorType: ex.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            var failure = CreateFailure(ex);
            if (job.Fail("agent job failed. See the server log for details.", failure, next => Persist(ToRecord(next, fingerprint))))
            {
                LogTerminal(job.CreateSnapshot(), AgentJobStatus.Failed, exitCode: failure.ExitCode, errorType: ex.GetType().Name);
            }
        }
    }

    private void EnsureJulesMonitor(AgentJob job, string fingerprint)
    {
        if (job.IsTerminal || !IsJulesJob(job))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(job.Jules?.SessionId)
            && job.Jules?.RemotePhase is not JulesRemotePhase.SubmittingCreate and not JulesRemotePhase.SubmissionUnknown)
        {
            return;
        }

        if (!job.TryBeginMonitor())
        {
            return;
        }

        _ = ObserveJulesAsync(job, fingerprint);
    }

    private async Task ObserveJulesAsync(AgentJob job, string fingerprint)
    {
        try
        {
            await RunJulesAsync(job, fingerprint).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            if (!job.IsTerminal)
            {
                var failure = CreateFailure(ex);
                job.Fail("agent job failed. See the server log for details.", failure, next => Persist(ToRecord(next, fingerprint)));
            }
        }
        finally
        {
            job.EndMonitorIfStillRunning();
            Evict(job);
        }
    }

    private async Task ApplyJulesAsync(AgentJob job, string fingerprint, JulesRunOutcome outcome)
    {
        switch (outcome)
        {
            case JulesCompletedOutcome completed:
                await ReleaseJulesLockAsync(job, fingerprint).ConfigureAwait(false);
                if (job.Complete(completed.Result, next => Persist(ToRecord(next, fingerprint))))
                {
                    LogTerminal(job.CreateSnapshot(), AgentJobStatus.Completed, completed.Result.ExitCode, errorType: null);
                }

                break;
            case JulesWaitingOutcome waiting:
                await ReleaseJulesLockAsync(job, fingerprint).ConfigureAwait(false);
                if (job.SettleWaiting(waiting.Result, next => Persist(ToRecord(next, fingerprint))))
                {
                    LogTerminal(job.CreateSnapshot(), AgentJobStatus.WaitingForInput, exitCode: null, errorType: null);
                }

                break;
            case JulesFailedOutcome failed:
                if (failed.ReleaseSessionLock)
                {
                    await ReleaseJulesLockAsync(job, fingerprint).ConfigureAwait(false);
                }

                var failure = CreateFailure(failed.Exception);
                if (job.Fail("agent job failed. See the server log for details.", failure, next => Persist(ToRecord(next, fingerprint))))
                {
                    LogTerminal(job.CreateSnapshot(), AgentJobStatus.Failed, exitCode: failure.ExitCode, errorType: failed.Exception.GetType().Name);
                }

                break;
            case JulesSuspendedOutcome:
                break;
            default:
                throw new InvalidOperationException("Unknown Jules outcome.");
        }
    }

    private JulesRunContext CreateJulesContext(AgentJob job, string fingerprint, JulesJobState? resume)
    {
        return new JulesRunContext
        {
            JobId = job.JobId,
            RequestId = job.RequestId,
            JobStoreDirectory = _storeDirectory,
            Persist = state => job.UpdateJules(state, snapshot => Persist(ToRecord(snapshot, fingerprint))),
            IsSessionLockedByOther = sessionId => IsJulesSessionLockedByOther(sessionId, job.JobId),
            Resume = resume,
            EventsLogPath = resume?.EventsLogPath,
            TextLogPath = resume?.TextLogPath,
        };
    }

    private async Task ReleaseJulesLockAsync(AgentJob job, string fingerprint)
    {
        var state = job.Jules;
        if (state is not { SessionLockHeld: true } || string.IsNullOrWhiteSpace(state.SessionId))
        {
            return;
        }

        var sessionId = state.SessionId;
        // 先に lock file を消す。ここで落ちても record の SessionLockHeld が残り、二重送信は止まる。
        // 逆順だと record だけ unlocked になり、古い owner の lock file が session を永久に塞ぐ。
        await JulesSessionGate.ReleaseAsync(_storeDirectory, sessionId, job.JobId, CancellationToken.None).ConfigureAwait(false);
        job.UpdateJules(state with { SessionLockHeld = false }, snapshot => Persist(ToRecord(snapshot, fingerprint)));
    }

    private bool IsJulesSessionLockedByOther(string sessionId, string jobId)
    {
        foreach (var job in _byJobId.Values)
        {
            if (string.Equals(job.JobId, jobId, StringComparison.Ordinal))
            {
                continue;
            }

            if (BlocksJulesSession(job.Jules, job.IsTerminal, sessionId))
            {
                return true;
            }
        }

        foreach (var path in Directory.EnumerateFiles(_storeDirectory, "req-*.json"))
        {
            var record = TryReadFile(path);
            if (record is null || string.Equals(record.JobId, jobId, StringComparison.Ordinal))
            {
                continue;
            }

            if (BlocksJulesSession(record.Jules, IsSettledStatus(record.Status), sessionId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 同じ session ID の他 job が送信中、または未 settled なら継続を始めない。
    /// create の running job は SessionLockHeld が false のまま session ID を公開する。
    /// </summary>
    private static bool BlocksJulesSession(JulesJobState? state, bool settled, string sessionId)
    {
        if (state is null
            || string.IsNullOrWhiteSpace(state.SessionId)
            || !string.Equals(state.SessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        return state.SessionLockHeld || !settled;
    }

    private static bool IsSettledStatus(string status)
    {
        return status is AgentJobStatus.Completed
            or AgentJobStatus.Failed
            or AgentJobStatus.Cancelled
            or AgentJobStatus.WaitingForInput;
    }

    private AgentJob AttachJules(AgentJobRecord stored)
    {
        if (_byJobId.TryGetValue(stored.JobId, out var live))
        {
            return live;
        }

        var sessionId = stored.Jules?.SessionId ?? stored.SessionId;
        var request = new AgentRunRequest(AgentFacade.JulesAgent, "(recovered)", ".", sessionId, null);
        var created = new AgentJob(
            stored.JobId,
            stored.RequestId,
            request,
            _timeProvider.GetUtcNow(),
            stored.RequestFingerprint,
            stored.Jules);
        if (!_byRequestId.TryAdd(stored.RequestId, created))
        {
            created.Discard();
            return _byRequestId[stored.RequestId];
        }

        if (!_byJobId.TryAdd(stored.JobId, created))
        {
            created.Discard();
            _byRequestId.TryRemove(stored.RequestId, out _);
            throw new InvalidOperationException("Failed to register agent job.");
        }

        return created;
    }

    private static void EnsureBound(AgentJob existing, AgentRunRequest request, string fingerprint)
    {
        if (existing.BoundFingerprint is not null)
        {
            if (!string.Equals(existing.BoundFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new ArgumentException("request_id is already bound to a different start_agent request.");
            }

            return;
        }

        EnsureSameRequest(existing.Request, request);
    }

    private static bool IsJules(AgentRunRequest request)
    {
        return string.Equals(request.Agent.Trim(), AgentFacade.JulesAgent, StringComparison.Ordinal);
    }

    private static bool IsJulesJob(AgentJob job)
    {
        return IsJules(job.Request) || job.Jules is not null;
    }

    private static bool IsJulesRecord(AgentJobRecord record)
    {
        return record.Jules is not null
            || string.Equals(record.Result?.Agent, AgentFacade.JulesAgent, StringComparison.Ordinal);
    }

    private static bool CanResumeJules(AgentJobRecord stored)
    {
        // session ID だけでは baseline と prompt hash が無い。checkpoint ごと残っているものだけ再開する。
        return stored.Status == AgentJobStatus.Running
            && !string.IsNullOrWhiteSpace(stored.Jules?.SessionId);
    }

    private void LogTerminal(AgentJobSnapshot snapshot, string expectedStatus, int? exitCode, string? errorType)
    {
        if (!string.Equals(snapshot.Status, expectedStatus, StringComparison.Ordinal))
        {
            return;
        }

        if (!_terminalLogs.TryAdd(snapshot.JobId, 0))
        {
            return;
        }

        var message = "JOB phase=" + snapshot.Status
            + " jobId=" + snapshot.JobId
            + " status=" + snapshot.Status;
        if (exitCode is not null)
        {
            message += " exitCode=" + exitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(errorType) && snapshot.Status == AgentJobStatus.Failed)
        {
            message += " errorType=" + errorType;
        }

        _logger.LogInformation(message);
    }

    private void Unregister(AgentJob job)
    {
        _byRequestId.TryRemove(job.RequestId, out _);
        _byJobId.TryRemove(job.JobId, out _);
        job.Discard();
    }

    private void Evict(AgentJob job)
    {
        // 保存済みの terminal だけ登録から外す。未保存の失敗を外すと、
        // 次の Get が disk の running を facade_interrupted として上書きする。
        if (!job.IsTerminal || !job.TerminalPersisted)
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

        if (CanResumeJules(stored))
        {
            return stored;
        }

        var phase = stored.Jules?.RemotePhase ?? stored.RemotePhase;
        if (string.IsNullOrWhiteSpace(stored.Jules?.SessionId)
            && phase is JulesRemotePhase.SubmittingCreate or JulesRemotePhase.SubmissionUnknown)
        {
            var failed = stored with
            {
                Status = AgentJobStatus.Failed,
                Error = "agent job failed. See the server log for details.",
                Result = null,
                Failure = new AgentJobFailure(
                    "submission_unknown",
                    "Jules session create result is unknown. The request was not sent again."),
                Jules = stored.Jules is null
                    ? null
                    : stored.Jules with
                    {
                        RemotePhase = JulesRemotePhase.SubmissionUnknown,
                        SessionLockHeld = false,
                    },
                RemotePhase = JulesRemotePhase.SubmissionUnknown,
            };
            LogTerminal(ToSnapshot(failed), AgentJobStatus.Failed, exitCode: null, errorType: "submission_unknown");
            Persist(failed);
            return failed;
        }

        var interrupted = stored with
        {
            Status = AgentJobStatus.Failed,
            Error = InterruptedError,
            Result = null,
            Failure = new AgentJobFailure(
                "facade_interrupted",
                InterruptedError),
        };
        LogTerminal(ToSnapshot(interrupted), AgentJobStatus.Failed, exitCode: null, errorType: "InterruptedError");
        Persist(interrupted);
        return interrupted;
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
        // 完了保存の置換と照会が重なる。共有違反のときだけ短い間隔で読み直す。
        const int attempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var record = JsonSerializer.Deserialize<AgentJobRecord>(reader.ReadToEnd(), AgentJson.Options);
                if (record is null || string.IsNullOrWhiteSpace(record.JobId) || string.IsNullOrWhiteSpace(record.RequestId))
                {
                    throw new InvalidOperationException("Job record is invalid.");
                }

                return record;
            }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                CliJson.TraceException(ex);
                Thread.Sleep(TimeSpan.FromMilliseconds(10 * attempt));
            }
            catch (Exception ex)
            {
                CliJson.TraceException(ex);
                throw;
            }
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
        // Windows では宛先を共有読みしている間、File.Move の上書きが
        // UnauthorizedAccessException になる。照会と完了保存は同時に起きるので、
        // 共有違反だけ短い間隔でやり直す。権限不足のときは回数を使い切って失敗させる。
        const int attempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var moved = false;
            try
            {
                File.WriteAllText(temp, contents);
                File.Move(temp, path, overwrite: true);
                moved = true;
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                CliJson.TraceException(ex);
                Thread.Sleep(TimeSpan.FromMilliseconds(10 * attempt));
            }
            finally
            {
                if (!moved)
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (Exception ex)
                    {
                        CliJson.TraceException(ex);
                    }
                }
            }
        }
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
        if (request.RunMode is not null)
        {
            var planAwareRequest = new RequestFingerprintV3(
                request.Agent,
                request.Prompt,
                request.WorkingDirectory,
                request.SessionId ?? string.Empty,
                request.AutoApprove,
                request.Skills ?? Array.Empty<string>(),
                string.IsNullOrWhiteSpace(request.Model) ? null : request.Model,
                request.ReasoningEffort,
                request.Fast,
                request.RunMode);
            return HashText("request-v3\0" + JsonSerializer.Serialize(planAwareRequest, AgentJson.Options));
        }

        if (request.ReasoningEffort is not null || request.Fast is not null)
        {
            var versionedRequest = new RequestFingerprintV2(
                request.Agent,
                request.Prompt,
                request.WorkingDirectory,
                request.SessionId ?? string.Empty,
                request.AutoApprove,
                request.Skills ?? Array.Empty<string>(),
                string.IsNullOrWhiteSpace(request.Model) ? null : request.Model,
                request.ReasoningEffort,
                request.Fast);
            return HashText("request-v2\0" + JsonSerializer.Serialize(versionedRequest, AgentJson.Options));
        }

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

        // ReasoningEffort/Fast が未指定のときは既存形式の hash を使い、既存requestとの互換性を維持する。
        // 未指定は何も足さない。モデル導入前に保存した request の fingerprint と一致させる。
        // skill は末尾改行付きで連結する。model は改行で終わらない接尾辞にし、
        // skill 名が model=... でも同一 request と誤認しない。model 自体の改行は Validate で拒否する。
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            builder.Append('\u0001').Append("model=").Append(request.Model);
        }

        return HashText(builder.ToString());
    }

    private sealed record RequestFingerprintV2(
        string Agent,
        string Prompt,
        string WorkingDirectory,
        string? SessionId,
        bool AutoApprove,
        IReadOnlyList<string>? Skills,
        string? Model,
        string? ReasoningEffort,
        bool? Fast);

    private sealed record RequestFingerprintV3(
        string Agent,
        string Prompt,
        string WorkingDirectory,
        string? SessionId,
        bool AutoApprove,
        IReadOnlyList<string>? Skills,
        string? Model,
        string? ReasoningEffort,
        bool? Fast,
        string? RunMode);

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
            record.Result,
            record.Error,
            record.Failure,
            record.SessionId ?? record.Jules?.SessionId,
            record.SessionUrl ?? record.Jules?.SessionUrl,
            record.InputPrompt ?? record.Jules?.InputPrompt,
            record.RemotePhase ?? record.Jules?.RemotePhase,
            record.Artifacts ?? record.Jules?.Artifacts,
            record.Jules);
    }

    private static AgentJobRecord ToRecord(AgentJobSnapshot snapshot, string fingerprint)
    {
        return new AgentJobRecord(
            snapshot.JobId,
            snapshot.RequestId,
            snapshot.Status,
            fingerprint,
            snapshot.Result,
            snapshot.Error,
            snapshot.Failure,
            snapshot.Jules,
            snapshot.SessionId,
            snapshot.SessionUrl,
            snapshot.InputPrompt,
            snapshot.RemotePhase,
            snapshot.Artifacts);
    }

    private static AgentJobFailure CreateFailure(Exception exception)
    {
        return CliJson.CreateFailure(exception);
    }
}

internal sealed record AgentJobRecord(
    string JobId,
    string RequestId,
    string Status,
    string RequestFingerprint,
    AgentRunResult? Result,
    string? Error,
    AgentJobFailure? Failure,
    JulesJobState? Jules = null,
    string? SessionId = null,
    string? SessionUrl = null,
    string? InputPrompt = null,
    string? RemotePhase = null,
    IReadOnlyList<AgentJobArtifact>? Artifacts = null);


