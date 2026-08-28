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
/// start/get/cancel が返す job snapshot。実行中は result を持たない。
/// </summary>
public sealed record AgentJobSnapshot(
    string JobId,
    string RequestId,
    string Status,
    int PollAfterMs,
    AgentRunResult? Result,
    string? Error);

/// <summary>
/// 1 件の agent job。MCP request の lifetime とは独立した CTS を持つ。
/// </summary>
public sealed class AgentJob
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private string _status = AgentJobStatus.Running;
    private AgentRunResult? _result;
    private string? _error;
    private DateTimeOffset _lastUpdatedAt;

    public AgentJob(string jobId, string requestId, AgentRunRequest request, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(request);
        JobId = jobId;
        RequestId = requestId;
        Request = request;
        CreatedAt = createdAt;
        _lastUpdatedAt = createdAt;
    }

    public string JobId { get; }

    public string RequestId { get; }

    public AgentRunRequest Request { get; }

    public DateTimeOffset CreatedAt { get; }

    public CancellationToken CancellationToken => _cancellation.Token;

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

    public void RequestCancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }

        TryFinish(AgentJobStatus.Cancelled, result: null, error: "cancelled");
    }

    public void Complete(AgentRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        TryFinish(AgentJobStatus.Completed, result, error: null);
    }

    public void Fail(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        TryFinish(AgentJobStatus.Failed, result: null, error);
    }

    public void MarkCancelled()
    {
        TryFinish(AgentJobStatus.Cancelled, result: null, error: "cancelled");
    }

    public AgentJobSnapshot CreateSnapshot(int pollAfterMs)
    {
        lock (_gate)
        {
            return new AgentJobSnapshot(
                JobId,
                RequestId,
                _status,
                pollAfterMs,
                _result,
                _error);
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

    private void TryFinish(string status, AgentRunResult? result, string? error)
    {
        lock (_gate)
        {
            if (IsTerminalStatus(_status))
            {
                return;
            }

            _status = status;
            _result = result;
            _error = error;
            _lastUpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static bool IsTerminalStatus(string status)
    {
        return status is AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled;
    }
}
