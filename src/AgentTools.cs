using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

/// <summary>
/// Codex から見える MCP tools。agent 固有処理は持たない。
/// </summary>
[McpServerToolType]
public sealed class AgentTools
{
    private readonly AgentJobService _jobs;

    public AgentTools(AgentJobService jobs)
    {
        _jobs = jobs;
    }

    [McpServerTool(Name = "start_agent"), Description("Start a coding agent job (github-copilot, grok-build, or devin-cli) and return a jobId immediately. Pass a caller-generated request_id and reuse it if this result is lost. Poll get_agent_job. This facade does not plan or split the task.")]
    public string StartAgent(
        [Description("Caller-generated idempotency key. Reuse the exact same value to recover a lost start_agent result without starting a second agent.")] string request_id,
        [Description("Target agent. github-copilot, grok-build, or devin-cli.")] string agent,
        [Description("User prompt forwarded to the selected agent without reinterpretation.")] string prompt,
        [Description("Working directory or worktree for the agent process.")] string working_directory,
        [Description("Existing external agent session id. Omit to start a new session.")] string? session_id = null,
        [Description("Codex-format skill names. Each driver converts them to that agent's native invocation.")] string[]? skills = null,
        [Description("When true (default), pass the CLI native non-interactive auto-approve flag. Set false to observe question/permission blocking on this same MCP path.")] bool auto_approve = true)
    {
        var invocationId = Guid.NewGuid().ToString("N");
        var startedAt = Stopwatch.GetTimestamp();
        LogStarted("start_agent", invocationId, request_id, null);
        try
        {
            var snapshot = _jobs.Start(
                request_id,
                new AgentRunRequest(
                    Agent: agent,
                    Prompt: prompt,
                    WorkingDirectory: working_directory,
                    SessionId: session_id,
                    Skills: skills,
                    AutoApprove: auto_approve));
            LogCompleted("start_agent", invocationId, startedAt, snapshot, includeRequestId: true);
            return JsonSerializer.Serialize(snapshot, AgentJson.Options);
        }
        catch (Exception ex)
        {
            LogFailed("start_agent", invocationId, startedAt, ex, request_id, null);
            CliJson.TraceException(ex);
            throw Wrap("start_agent failed. See the server log for details.", ex);
        }
    }

    [McpServerTool(Name = "get_agent_job"), Description("Get the status or terminal result of a previously started agent job. Does not start or restart work.")]
    public string GetAgentJob(
        [Description("Job id returned by start_agent.")] string job_id)
    {
        var invocationId = Guid.NewGuid().ToString("N");
        var startedAt = Stopwatch.GetTimestamp();
        LogStarted("get_agent_job", invocationId, null, job_id);
        try
        {
            var snapshot = _jobs.Get(job_id);
            LogCompleted("get_agent_job", invocationId, startedAt, snapshot, includeRequestId: false);
            return JsonSerializer.Serialize(snapshot, AgentJson.Options);
        }
        catch (Exception ex)
        {
            LogFailed("get_agent_job", invocationId, startedAt, ex, null, job_id);
            CliJson.TraceException(ex);
            throw Wrap("get_agent_job failed. See the server log for details.", ex);
        }
    }

    [McpServerTool(Name = "cancel_agent_job"), Description("Cancel a running agent job. Terminal jobs are left unchanged.")]
    public string CancelAgentJob(
        [Description("Job id returned by start_agent.")] string job_id)
    {
        var invocationId = Guid.NewGuid().ToString("N");
        var startedAt = Stopwatch.GetTimestamp();
        LogStarted("cancel_agent_job", invocationId, null, job_id);
        try
        {
            var snapshot = _jobs.Cancel(job_id);
            LogCompleted("cancel_agent_job", invocationId, startedAt, snapshot, includeRequestId: false);
            return JsonSerializer.Serialize(snapshot, AgentJson.Options);
        }
        catch (Exception ex)
        {
            LogFailed("cancel_agent_job", invocationId, startedAt, ex, null, job_id);
            CliJson.TraceException(ex);
            throw Wrap("cancel_agent_job failed. See the server log for details.", ex);
        }
    }

    private static void LogStarted(string tool, string invocationId, string? requestId, string? jobId)
    {
        FacadeLog.CreateLogger(FacadeLogging.LoggerCategory).LogInformation(
            "MCP tool={Tool} phase=started invocationId={InvocationId} requestId={RequestId} jobId={JobId}",
            tool, invocationId, requestId ?? "", jobId ?? "");
    }

    private static void LogCompleted(string tool, string invocationId, long startedAt, AgentJobSnapshot snapshot, bool includeRequestId)
    {
        var durationMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var terminal = snapshot.Status is AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled;
        FacadeLog.CreateLogger(FacadeLogging.LoggerCategory).LogInformation(
            "MCP tool={Tool} phase=completed invocationId={InvocationId} requestId={RequestId} jobId={JobId} status={Status} terminal={Terminal} pollAfterMs={PollAfterMs} durationMs={DurationMs}",
            tool, invocationId, includeRequestId ? snapshot.RequestId : "", snapshot.JobId,
            snapshot.Status, terminal ? "true" : "false", snapshot.PollAfterMs, durationMs);
    }

    private static void LogFailed(string tool, string invocationId, long startedAt, Exception exception, string? requestId, string? jobId)
    {
        var durationMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        FacadeLog.CreateLogger(FacadeLogging.LoggerCategory).LogInformation(
            "MCP tool={Tool} phase=failed invocationId={InvocationId} requestId={RequestId} jobId={JobId} errorType={ErrorType} durationMs={DurationMs}",
            tool, invocationId, requestId ?? "", jobId ?? "", exception.GetType().Name, durationMs);
    }

    private static McpException Wrap(string message, Exception exception)
    {
        if (exception is ArgumentException or KeyNotFoundException)
        {
            var mcpException = new McpException(exception.Message, exception);
            CliJson.TraceException(mcpException);
            return mcpException;
        }

        var wrapped = new McpException(message, exception);
        CliJson.TraceException(wrapped);
        return wrapped;
    }
}
