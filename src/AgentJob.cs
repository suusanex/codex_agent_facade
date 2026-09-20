/// <summary>
/// job の公開状態。MCP Tasks の working/completed/failed/cancelled に寄せるが、wire は独自 JSON。
/// </summary>
public static class AgentJobStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// start/get/cancel/wait が内部で扱う job snapshot。実行中は result を持たない。
/// MCP 公開 JSON は <see cref="AgentJobPublicSnapshot"/> を使う。
/// </summary>
public sealed record AgentJobSnapshot(
    string JobId,
    string RequestId,
    string Status,
    AgentRunResult? Result,
    string? Error,
    AgentJobFailure? Failure);

/// <summary>
/// MCP 公開用の job snapshot。内部の CLI raw output は含めない。
/// </summary>
public sealed record AgentJobPublicSnapshot(
    string JobId,
    string RequestId,
    string Status,
    AgentRunPublicResult? Result,
    string? Error,
    AgentJobFailure? Failure);

/// <summary>
/// MCP 公開用の terminal result。Driver が抽出した outputText と run log 識別情報だけを返す。
/// </summary>
public sealed record AgentRunPublicResult(
    string Agent,
    string SessionId,
    int ExitCode,
    string OutputText,
    string RunId,
    string EventsLogPath,
    string TextLogPath,
    string OutputKind = "assistant_transcript");

/// <summary>
/// 失敗時に親が復旧判断へ使う、公開可能な最小限の診断情報。
/// </summary>
public sealed record AgentJobFailure(
    string Kind,
    string Summary,
    int? ExitCode = null,
    string? RunId = null,
    string? EventsLogPath = null,
    string? TextLogPath = null);

internal static class AgentJobPublicProjection
{
    public static AgentJobPublicSnapshot From(AgentJobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new AgentJobPublicSnapshot(
            snapshot.JobId,
            snapshot.RequestId,
            snapshot.Status,
            snapshot.Result is null ? null : From(snapshot.Result),
            snapshot.Error,
            snapshot.Failure);
    }

    public static AgentRunPublicResult From(AgentRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new AgentRunPublicResult(
            result.Agent,
            result.SessionId,
            result.ExitCode,
            result.OutputText,
            result.RunId,
            result.EventsLogPath,
            result.TextLogPath,
            result.OutputKind);
    }
}

/// <summary>
/// 1 件の agent job。MCP request の lifetime とは独立した CTS を持つ。
/// </summary>
public sealed class AgentJob
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _status = AgentJobStatus.Running;
    private AgentRunResult? _result;
    private string? _error;
    private AgentJobFailure? _failure;

    public AgentJob(string jobId, string requestId, AgentRunRequest request, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(request);
        JobId = jobId;
        RequestId = requestId;
        Request = request;
        CreatedAt = createdAt;
    }

    public string JobId { get; }

    public string RequestId { get; }

    public AgentRunRequest Request { get; }

    public DateTimeOffset CreatedAt { get; }

    public CancellationToken CancellationToken => _cancellation.Token;

    /// <summary>
    /// terminal 遷移で完了する。waiter の CT とは独立しており、job 自体は cancel しない。
    /// </summary>
    public Task WhenTerminal => _terminal.Task;

    public bool IsTerminal
    {
        get
        {
            lock (_gate)
            {
                return IsTerminalStatus(_status);
            }
        }
    }

    public bool RequestCancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }

        return TryFinish(AgentJobStatus.Cancelled, result: null, error: "cancelled");
    }

    public bool Complete(AgentRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return TryFinish(AgentJobStatus.Completed, result, error: null);
    }

    public bool Fail(string error, AgentJobFailure? failure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return TryFinish(AgentJobStatus.Failed, result: null, error, failure);
    }

    public bool MarkCancelled()
    {
        return TryFinish(AgentJobStatus.Cancelled, result: null, error: "cancelled");
    }

    public AgentJobSnapshot CreateSnapshot()
    {
        lock (_gate)
        {
            return new AgentJobSnapshot(
                JobId,
                RequestId,
                _status,
                _result,
                _error,
                _failure);
        }
    }

    public void Discard()
    {
        try
        {
            _cancellation.Dispose();
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }
    }

    private bool TryFinish(string status, AgentRunResult? result, string? error, AgentJobFailure? failure = null)
    {
        lock (_gate)
        {
            if (IsTerminalStatus(_status))
            {
                return false;
            }

            _status = status;
            _result = result;
            _error = error;
            _failure = failure;
        }

        _terminal.TrySetResult();
        return true;
    }

    private static bool IsTerminalStatus(string status)
    {
        return status is AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled;
    }
}
