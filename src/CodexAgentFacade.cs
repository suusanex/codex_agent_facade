#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property PackAsTool=false
#:property NoWarn=CA2266
#:package ModelContextProtocol@2.2.0
#:package Microsoft.Extensions.Hosting@10.0.0
#:include AgentFacade.cs
#:include ProcessRunner.cs
#:include GitHubCopilotDriver.cs
#:include GrokBuildDriver.cs

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

Trace.Listeners.Add(new ConsoleTraceListener(useErrorStream: true));

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<GitHubCopilotDriver>();
builder.Services.AddSingleton<GrokBuildDriver>();
builder.Services.AddSingleton<AgentFacade>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "codex-agent-facade",
            Version = "0.1.0",
        };
        options.ServerInstructions =
            "Thin messenger from Codex to GitHub Copilot or Grok Build. Call run_agent with agent, prompt, and working_directory. Do not replan or split the user's task. Pass the user prompt through. Reuse session_id to continue the same external agent session.";
    })
    .WithStdioServerTransport()
    .WithTools<AgentTools>();

await builder.Build().RunAsync();

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
        try
        {
            var request = new AgentRunRequest(
                Agent: agent,
                Prompt: prompt,
                WorkingDirectory: working_directory,
                SessionId: session_id,
                Skills: skills,
                AutoApprove: auto_approve);

            var result = await _facade.RunAsync(
                request,
                onStdoutLine: line =>
                {
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = 0,
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
            throw new McpException(ex.Message, ex);
        }
    }

    private static string TruncateProgress(string line)
    {
        const int maxLength = 500;
        return line.Length <= maxLength ? line : line[..maxLength];
    }
}
