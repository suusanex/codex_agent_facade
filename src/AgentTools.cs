using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

/// <summary>
/// Codex から見える MCP tools。caller が構成した worker prompt を job として受け、agent 固有処理は持たない。
/// </summary>
[McpServerToolType]
public sealed class AgentTools
{
    private readonly AgentJobService _jobs;

    public AgentTools(AgentJobService jobs)
    {
        _jobs = jobs;
    }

    [McpServerTool(Name = "start_agent"), Description(McpPublicContract.StartAgentDescription)]
    public string StartAgent(
        [Description(McpPublicContract.RequestIdDescription)] string request_id,
        [Description("Target agent. github-copilot, grok-build, or cursor.")] string agent,
        [Description(McpPublicContract.PromptDescription)] string prompt,
        [Description(McpPublicContract.WorkingDirectoryDescription)] string working_directory,
        [Description("Existing external agent session id. Omit to start a new session.")] string? session_id = null,
        [Description(McpPublicContract.SkillsDescription)] string[]? skills = null,
        [Description("When true (default), pass the CLI native non-interactive auto-approve flag. Set false to observe question/permission blocking on this same MCP path.")] bool auto_approve = true,
        [Description(McpPublicContract.ModelDescription)] string? model = null)
    {
        var invocationId = Guid.NewGuid().ToString("N");
        var startedAt = Stopwatch.GetTimestamp();
        LogStarted("start_agent", invocationId, request_id, null, agent);
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
                    AutoApprove: auto_approve,
                    Model: model));
            LogCompleted("start_agent", invocationId, startedAt, snapshot, includeRequestId: true, agent: agent);
            return SerializePublic(snapshot);
        }
        catch (Exception ex)
        {
            LogFailed("start_agent", invocationId, startedAt, ex, request_id, null);
            CliJson.TraceException(ex);
            throw Wrap("start_agent failed. See the server log for details.", ex);
        }
    }

    [McpServerTool(Name = "get_agent_job"), Description(McpPublicContract.GetAgentJobDescription)]
    public string GetAgentJob(
        [Description("Job id returned by start_agent.")] string job_id)
    {
        var invocationId = Guid.NewGuid().ToString("N");
        var startedAt = Stopwatch.GetTimestamp();
        LogStarted("get_agent_job", invocationId, null, job_id, null);
        try
        {
            var snapshot = _jobs.Get(job_id);
            LogCompleted("get_agent_job", invocationId, startedAt, snapshot, includeRequestId: false, agent: null);
            return SerializePublic(snapshot);
        }
        catch (Exception ex)
        {
            LogFailed("get_agent_job", invocationId, startedAt, ex, null, job_id);
            CliJson.TraceException(ex);
            throw Wrap("get_agent_job failed. See the server log for details.", ex);
        }
    }

    [McpServerTool(Name = "wait_agent_job"), Description(McpPublicContract.WaitAgentJobDescription)]
    public async Task<string> WaitAgentJob(
        [Description("Job id returned by start_agent.")] string job_id,
        [Description(McpPublicContract.WaitTimeoutSecondsDescription)] int? timeout_seconds = null,
        CancellationToken cancellationToken = default)
    {
        var invocationId = Guid.NewGuid().ToString("N");
        var startedAt = Stopwatch.GetTimestamp();
        LogStarted("wait_agent_job", invocationId, null, job_id, null);
        try
        {
            var snapshot = await _jobs.WaitAsync(job_id, timeout_seconds ?? AgentJobService.DefaultWaitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            LogCompleted("wait_agent_job", invocationId, startedAt, snapshot, includeRequestId: false, agent: null);
            return SerializePublic(snapshot);
        }
        catch (OperationCanceledException ex)
        {
            LogFailed("wait_agent_job", invocationId, startedAt, ex, null, job_id);
            CliJson.TraceException(ex);
            throw;
        }
        catch (Exception ex)
        {
            LogFailed("wait_agent_job", invocationId, startedAt, ex, null, job_id);
            CliJson.TraceException(ex);
            throw Wrap("wait_agent_job failed. See the server log for details.", ex);
        }
    }

    [McpServerTool(Name = "cancel_agent_job"), Description("Cancel a running agent job. Terminal jobs are left unchanged.")]
    public string CancelAgentJob(
        [Description("Job id returned by start_agent.")] string job_id)
    {
        var invocationId = Guid.NewGuid().ToString("N");
        var startedAt = Stopwatch.GetTimestamp();
        LogStarted("cancel_agent_job", invocationId, null, job_id, null);
        try
        {
            var snapshot = _jobs.Cancel(job_id);
            LogCompleted("cancel_agent_job", invocationId, startedAt, snapshot, includeRequestId: false, agent: null);
            return SerializePublic(snapshot);
        }
        catch (Exception ex)
        {
            LogFailed("cancel_agent_job", invocationId, startedAt, ex, null, job_id);
            CliJson.TraceException(ex);
            throw Wrap("cancel_agent_job failed. See the server log for details.", ex);
        }
    }

    private static string SerializePublic(AgentJobSnapshot snapshot)
    {
        return JsonSerializer.Serialize(AgentJobPublicProjection.From(snapshot), AgentJson.Options);
    }

    private static void LogStarted(string tool, string invocationId, string? requestId, string? jobId, string? agent)
    {
        FacadeLog.CreateLogger(FacadeLogging.LoggerCategory).LogInformation(
            "MCP tool={Tool} phase=started invocationId={InvocationId} requestId={RequestId} jobId={JobId} agent={Agent}",
            SafeLogValue(tool), SafeLogValue(invocationId), SafeLogValue(requestId), SafeLogValue(jobId), SafeLogValue(agent));
    }

    private static void LogCompleted(string tool, string invocationId, long startedAt, AgentJobSnapshot snapshot, bool includeRequestId, string? agent)
    {
        var durationMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var terminal = snapshot.Status is AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled;
        FacadeLog.CreateLogger(FacadeLogging.LoggerCategory).LogInformation(
            "MCP tool={Tool} phase=completed invocationId={InvocationId} requestId={RequestId} jobId={JobId} agent={Agent} status={Status} terminal={Terminal} durationMs={DurationMs}",
            SafeLogValue(tool), SafeLogValue(invocationId), SafeLogValue(includeRequestId ? snapshot.RequestId : null), SafeLogValue(snapshot.JobId),
            SafeLogValue(agent), snapshot.Status, terminal ? "true" : "false", durationMs);
    }

    private static void LogFailed(string tool, string invocationId, long startedAt, Exception exception, string? requestId, string? jobId)
    {
        var durationMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        FacadeLog.CreateLogger(FacadeLogging.LoggerCategory).LogWarning(
            "MCP tool={Tool} phase=failed invocationId={InvocationId} requestId={RequestId} jobId={JobId} errorType={ErrorType} durationMs={DurationMs}",
            SafeLogValue(tool), SafeLogValue(invocationId), SafeLogValue(requestId), SafeLogValue(jobId), exception.GetType().Name, durationMs);
    }

    private static string SafeLogValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new System.Text.StringBuilder(Math.Min(value.Length, 128));
        foreach (var character in value)
        {
            if (builder.Length >= 128)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? '?' : character);
        }

        return builder.ToString();
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
