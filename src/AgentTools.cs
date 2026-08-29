using System.ComponentModel;
using System.Text.Json;
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
            return JsonSerializer.Serialize(snapshot, AgentJson.Options);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw Wrap("start_agent failed. See the server log for details.", ex);
        }
    }

    [McpServerTool(Name = "get_agent_job"), Description("Get the status or terminal result of a previously started agent job. Does not start or restart work.")]
    public string GetAgentJob(
        [Description("Job id returned by start_agent.")] string job_id)
    {
        try
        {
            var snapshot = _jobs.Get(job_id);
            return JsonSerializer.Serialize(snapshot, AgentJson.Options);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw Wrap("get_agent_job failed. See the server log for details.", ex);
        }
    }

    [McpServerTool(Name = "cancel_agent_job"), Description("Cancel a running agent job. Terminal jobs are left unchanged.")]
    public string CancelAgentJob(
        [Description("Job id returned by start_agent.")] string job_id)
    {
        try
        {
            var snapshot = _jobs.Cancel(job_id);
            return JsonSerializer.Serialize(snapshot, AgentJson.Options);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw Wrap("cancel_agent_job failed. See the server log for details.", ex);
        }
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
