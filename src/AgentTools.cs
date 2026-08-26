using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>
/// Codex から見える唯一の MCP tool。agent 固有処理は持たない。
/// </summary>
[McpServerToolType]
public sealed class AgentTools
{
    private readonly AgentFacade _facade;

    public AgentTools(AgentFacade facade)
    {
        _facade = facade;
    }

    [McpServerTool(Name = "run_agent"), Description("Run a coding agent (github-copilot or grok-build) with the given prompt and return its response. This facade does not plan or split the task.")]
    public async Task<string> RunAgent(
        [Description("Target agent. github-copilot or grok-build.")] string agent,
        [Description("User prompt forwarded to the selected agent without reinterpretation.")] string prompt,
        [Description("Working directory or worktree for the agent process.")] string working_directory,
        [Description("Existing external agent session id. Omit to start a new session.")] string? session_id = null,
        [Description("Codex-format skill names. Each driver converts them to that agent's native invocation.")] string[]? skills = null,
        [Description("When true (default), pass the CLI native non-interactive auto-approve flag. Set false to observe question/permission blocking on this same MCP path.")] bool auto_approve = true,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        PeriodicTimer? heartbeatTimer = null;
        CancellationTokenSource? heartbeatCts = null;
        Task heartbeatTask = Task.CompletedTask;
        var progressCount = new StrongBox<int>(0);
        try
        {
            var request = new AgentRunRequest(
                Agent: agent,
                Prompt: prompt,
                WorkingDirectory: working_directory,
                SessionId: session_id,
                Skills: skills,
                AutoApprove: auto_approve);

            if (progress is not null)
            {
                heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeatTimer = new PeriodicTimer(AgentRunLog.HeartbeatInterval);
                heartbeatTask = ReportProgressHeartbeatAsync(heartbeatTimer, progress, progressCount, heartbeatCts.Token);
            }

            var result = await _facade.RunAsync(
                request,
                onStdoutLine: line =>
                {
                    var count = Interlocked.Increment(ref progressCount.Value);
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = count,
                        Message = TruncateProgress(line),
                    });
                },
                cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(result, AgentJson.Options);
        }
        catch (OperationCanceledException ex)
        {
            CliJson.TraceException(ex);
            throw;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            var mcpException = new McpException("run_agent failed. See server traces for details.", ex);
            CliJson.TraceException(mcpException);
            throw mcpException;
        }
        finally
        {
            if (heartbeatCts is not null)
            {
                await heartbeatCts.CancelAsync().ConfigureAwait(false);
            }

            heartbeatTimer?.Dispose();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CliJson.TraceException(ex);
                throw;
            }
            finally
            {
                heartbeatCts?.Dispose();
            }
        }
    }

    private static async Task ReportProgressHeartbeatAsync(
        PeriodicTimer timer,
        IProgress<ProgressNotificationValue> progress,
        StrongBox<int> progressCount,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var count = Interlocked.Increment(ref progressCount.Value);
                progress.Report(new ProgressNotificationValue
                {
                    Progress = count,
                    Message = "heartbeat",
                });
            }
        }
        catch (OperationCanceledException ex)
        {
            CliJson.TraceException(ex);
        }
    }

    private static string TruncateProgress(string line)
    {
        const int maxLength = 500;
        return line.Length <= maxLength ? line : line[..maxLength];
    }
}
