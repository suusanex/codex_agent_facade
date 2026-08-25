#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property PackAsTool=false
#:property NoWarn=CA2266
#:include AgentFacade.cs
#:include ProcessRunner.cs
#:include AgentRunLog.cs
#:include SecretRedactor.cs
#:include GitHubCopilotDriver.cs
#:include GrokBuildDriver.cs

var workspace = Directory.CreateTempSubdirectory("codex-agent-facade-poc-");
File.WriteAllText(Path.Combine(workspace.FullName, "NOTE.txt"), "poc observation workspace. do not keep.");
Console.WriteLine("workspace=" + workspace.FullName);

var facade = new AgentFacade(
    new GitHubCopilotDriver(new ProcessRunner()),
    new GrokBuildDriver(new ProcessRunner()),
    new AgentRunLogFactory());
const string prompt = "Reply with only the word pong. Do not create, edit, or delete any files.";
const string followUp = "Reply with only the word pingpong. Do not create, edit, or delete any files.";
const string question = "Ask me to choose option A or option B, then stop and wait for my answer. Do not choose for me. Do not modify files.";
const string skillPrompt = "If a skill was invoked, name it in one short sentence. Do not modify files.";

var copilot = await RunTrial(facade, "copilot-auto", AgentFacade.GitHubCopilotAgent, workspace.FullName, prompt, autoApprove: true, sessionId: null, skills: null, timeoutSeconds: 180);
var grok = await RunTrial(facade, "grok-auto", AgentFacade.GrokBuildAgent, workspace.FullName, prompt, autoApprove: true, sessionId: null, skills: null, timeoutSeconds: 180);

if (!string.IsNullOrWhiteSpace(copilot))
{
    await RunTrial(facade, "copilot-continue", AgentFacade.GitHubCopilotAgent, workspace.FullName, followUp, autoApprove: true, sessionId: copilot, skills: null, timeoutSeconds: 180);
}

if (!string.IsNullOrWhiteSpace(grok))
{
    await RunTrial(facade, "grok-continue", AgentFacade.GrokBuildAgent, workspace.FullName, followUp, autoApprove: true, sessionId: grok, skills: null, timeoutSeconds: 180);
}

await RunTrial(facade, "copilot-no-approve", AgentFacade.GitHubCopilotAgent, workspace.FullName, question, autoApprove: false, sessionId: null, skills: null, timeoutSeconds: 45);
await RunTrial(facade, "grok-no-approve", AgentFacade.GrokBuildAgent, workspace.FullName, question, autoApprove: false, sessionId: null, skills: null, timeoutSeconds: 45);
await RunTrial(facade, "copilot-skill", AgentFacade.GitHubCopilotAgent, workspace.FullName, skillPrompt, autoApprove: true, sessionId: null, skills: ["$dotnet-file-based-apps"], timeoutSeconds: 120);
await RunTrial(facade, "grok-skill", AgentFacade.GrokBuildAgent, workspace.FullName, skillPrompt, autoApprove: true, sessionId: null, skills: ["$dotnet-file-based-apps"], timeoutSeconds: 120);

static async Task<string?> RunTrial(
    AgentFacade facade,
    string name,
    string agent,
    string cwd,
    string prompt,
    bool autoApprove,
    string? sessionId,
    IReadOnlyList<string>? skills,
    int timeoutSeconds)
{
    Console.WriteLine("----- " + name + " -----");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
    try
    {
        var result = await facade.RunAsync(
            new AgentRunRequest(agent, prompt, cwd, sessionId, skills, autoApprove),
            line => Console.WriteLine("[stdout-line] " + Truncate(line)),
            cts.Token);
        Console.WriteLine("exitCode=" + result.ExitCode);
        Console.WriteLine("sessionId=" + result.SessionId);
        Console.WriteLine("outputText=" + Truncate(result.OutputText));
        Console.WriteLine("rawOutput=" + Truncate(result.RawOutput));
        return string.IsNullOrWhiteSpace(result.SessionId) ? null : result.SessionId;
    }
    catch (Exception ex)
    {
        Console.WriteLine("exceptionType=" + ex.GetType().FullName);
        Console.WriteLine("exception=" + Truncate(ex.ToString()));
        return null;
    }
}

static string Truncate(string text)
{
    const int max = 4000;
    return text.Length <= max ? text : text[..max] + "...";
}
