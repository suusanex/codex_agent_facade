#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property PackAsTool=false
#:property NoWarn=CA2266
#:package Ardalis.SingleFileTestRunner.xUnitV3@1.1.0
#:include ../src/AgentFacade.cs
#:include ../src/ProcessRunner.cs
#:include ../src/GitHubCopilotDriver.cs
#:include ../src/GrokBuildDriver.cs

using Ardalis.SingleFileTestRunner;
using Xunit;

return await TestRunner.RunTestsAsync();

public sealed class RecordingProcessRunner : IProcessRunner
{
    public ProcessRunRequest? LastRequest { get; private set; }
    public ProcessRunResult Result { get; set; } = new(0, "{}", "");
    public Exception? ExceptionToThrow { get; set; }

    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(Result);
    }
}

public class AgentFacadeTests
{
    [Fact]
    public async Task UnknownAgentThrows()
    {
        var facade = CreateFacade(out _);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => facade.RunAsync(
            new AgentRunRequest("unknown", "do work", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None));
        Assert.Contains("Unknown agent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyPromptThrows()
    {
        var facade = CreateFacade(out _);
        await Assert.ThrowsAsync<ArgumentException>(() => facade.RunAsync(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "  ", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None));
    }

    [Fact]
    public async Task EmptyWorkingDirectoryThrows()
    {
        var facade = CreateFacade(out _);
        await Assert.ThrowsAsync<ArgumentException>(() => facade.RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "do work", " ", null, null),
            onStdoutLine: null,
            CancellationToken.None));
    }

    [Fact]
    public async Task RoutesGitHubCopilot()
    {
        var facade = CreateFacade(out var runner);
        runner.Result = new ProcessRunResult(0, """{"type":"assistant","text":"ok","sessionId":"11111111-1111-1111-1111-111111111111"}""", "");
        var result = await facade.RunAsync(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "hello", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None);
        Assert.Equal("copilot", runner.LastRequest!.FileName);
        Assert.Equal(AgentFacade.GitHubCopilotAgent, result.Agent);
        Assert.Equal("ok", result.OutputText);
    }

    [Fact]
    public async Task RoutesGrokBuild()
    {
        var facade = CreateFacade(out var runner);
        runner.Result = new ProcessRunResult(0, """{"text":"hi","session_id":"22222222-2222-2222-2222-222222222222"}""", "");
        var result = await facade.RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "hello", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None);
        Assert.Equal("grok", runner.LastRequest!.FileName);
        Assert.Equal(AgentFacade.GrokBuildAgent, result.Agent);
        Assert.Equal("hi", result.OutputText);
    }

    private static AgentFacade CreateFacade(out RecordingProcessRunner runner)
    {
        runner = new RecordingProcessRunner();
        return new AgentFacade(new GitHubCopilotDriver(runner), new GrokBuildDriver(runner));
    }
}

public class GitHubCopilotDriverTests
{
    [Fact]
    public void BuildArgumentsIncludeNonInteractiveFlags()
    {
        var args = GitHubCopilotDriver.BuildArguments(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "fix the bug", @"C:\repo", null, null));
        Assert.Equal(
            ["--prompt", "fix the bug", "--output-format", "json", "--allow-all"],
            args);
    }

    [Fact]
    public void BuildArgumentsResumeAndSkills()
    {
        var args = GitHubCopilotDriver.BuildArguments(
            new AgentRunRequest(
                AgentFacade.GitHubCopilotAgent,
                "continue",
                @"C:\repo",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ["$dotnet-file-based-apps", "review"]));
        Assert.Contains("--resume", args);
        Assert.Contains("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", args);
        var prompt = args[args.IndexOf("--prompt") + 1];
        Assert.StartsWith("/dotnet-file-based-apps", prompt, StringComparison.Ordinal);
        Assert.Contains("/review", prompt, StringComparison.Ordinal);
        Assert.EndsWith("continue", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParsesJsonlSessionAndText()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"session","sessionId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}
                {"type":"assistant","data":{"text":"first"}}
                {"type":"assistant","text":"second"}
                """,
                ""),
        };
        var driver = new GitHubCopilotDriver(runner);
        var result = await driver.RunAsync(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "go", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None);
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", result.SessionId);
        Assert.Equal("first\nsecond", result.OutputText);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Path.GetTempPath(), runner.LastRequest!.WorkingDirectory);
    }

    [Fact]
    public async Task ParsesResumeHint()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """{"type":"exit","hint":"copilot --resume=cccccccc-cccc-cccc-cccc-cccccccccccc"}""",
                ""),
        };
        var driver = new GitHubCopilotDriver(runner);
        var result = await driver.RunAsync(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "go", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None);
        Assert.Equal("cccccccc-cccc-cccc-cccc-cccccccccccc", result.SessionId);
    }

    [Fact]
    public async Task NonZeroExitThrows()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(2, "out", "err"),
        };
        var driver = new GitHubCopilotDriver(runner);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunAsync(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "go", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None));
        Assert.Contains("exited with code 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("err", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidJsonlThrows()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, "not-json", ""),
        };
        var driver = new GitHubCopilotDriver(runner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunAsync(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "go", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None));
    }

    [Fact]
    public async Task EmptyStdoutThrows()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, "  ", ""),
        };
        var driver = new GitHubCopilotDriver(runner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunAsync(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "go", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None));
    }
}

public class GrokBuildDriverTests
{
    [Fact]
    public void BuildArgumentsIncludeNonInteractiveFlags()
    {
        var args = GrokBuildDriver.BuildArguments(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "list files", @"D:\ws", null, null));
        Assert.Equal(
            ["--no-auto-update", "-p", "list files", "--cwd", @"D:\ws", "--output-format", "json", "--always-approve"],
            args);
    }

    [Fact]
    public void BuildArgumentsSessionAndSkills()
    {
        var args = GrokBuildDriver.BuildArguments(
            new AgentRunRequest(
                AgentFacade.GrokBuildAgent,
                "next",
                @"D:\ws",
                "dddddddd-dddd-dddd-dddd-dddddddddddd",
                ["/review"]));
        Assert.Contains("--session-id", args);
        Assert.Contains("dddddddd-dddd-dddd-dddd-dddddddddddd", args);
        var prompt = args[args.IndexOf("-p") + 1];
        Assert.Equal("/review\nnext", prompt);
    }

    [Fact]
    public async Task ParsesTrailingJson()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                "log line\n{\"result\":\"done\",\"session_id\":\"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee\"}",
                ""),
        };
        var driver = new GrokBuildDriver(runner);
        var result = await driver.RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "go", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None);
        Assert.Equal("done", result.OutputText);
        Assert.Equal("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", result.SessionId);
        Assert.Equal("grok", runner.LastRequest!.FileName);
    }

    [Fact]
    public async Task ParsesNestedTrailingJson()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """prefix {"result":{"text":"nested"},"sessionId":"ffffffff-ffff-ffff-ffff-ffffffffffff"}""",
                ""),
        };
        var driver = new GrokBuildDriver(runner);
        var result = await driver.RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "go", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None);
        Assert.Equal("nested", result.OutputText);
        Assert.Equal("ffffffff-ffff-ffff-ffff-ffffffffffff", result.SessionId);
    }

    [Fact]
    public async Task NonZeroExitThrows()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(1, "", "boom"),
        };
        var driver = new GrokBuildDriver(runner);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "go", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None));
        Assert.Contains("exited with code 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingJsonThrows()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, "no json here", ""),
        };
        var driver = new GrokBuildDriver(runner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "go", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None));
    }
}

public class AgentPromptTests
{
    [Fact]
    public void EmptySkillsLeavesPromptUnchanged()
    {
        Assert.Equal("body", AgentPrompt.ApplySkills("body", null));
        Assert.Equal("body", AgentPrompt.ApplySkills("body", []));
    }

    [Fact]
    public void ConvertsCodexSkillPrefix()
    {
        Assert.Equal("/dotnet-file-based-apps", AgentPrompt.ToNativeSkillInvocation("$dotnet-file-based-apps"));
        Assert.Equal("/review", AgentPrompt.ToNativeSkillInvocation("review"));
        Assert.Equal("/review", AgentPrompt.ToNativeSkillInvocation("/review"));
    }

    [Fact]
    public void EmptySkillNameThrows()
    {
        Assert.Throws<ArgumentException>(() => AgentPrompt.ToNativeSkillInvocation("$"));
    }
}

public class ProcessRunnerTests
{
    [Fact]
    public async Task CapturesDotnetVersionWithoutChangingOs()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(
            new ProcessRunRequest("dotnet", ["--version"], Directory.GetCurrentDirectory()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
    }

    [Fact]
    public void MissingExecutableThrows()
    {
        Assert.Throws<FileNotFoundException>(() => ExecutableResolver.Resolve("codex-agent-facade-missing-cli-xyz"));
    }
}
