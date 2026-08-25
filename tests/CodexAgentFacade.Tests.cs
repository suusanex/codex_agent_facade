#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property PackAsTool=false
#:property NoWarn=CA2266
#:package Ardalis.SingleFileTestRunner.xUnitV3@1.1.0
#:package Microsoft.Extensions.TimeProvider.Testing@9.0.0
#:include ../src/AgentFacade.cs
#:include ../src/ProcessRunner.cs
#:include ../src/AgentRunLog.cs
#:include ../src/SecretRedactor.cs
#:include ../src/GitHubCopilotDriver.cs
#:include ../src/GrokBuildDriver.cs

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ardalis.SingleFileTestRunner;
using Microsoft.Extensions.Time.Testing;
using Xunit;

return await TestRunner.RunTestsAsync();

internal static class TestRunLogs
{
    public static AgentRunLogFactory CreateFactory(TimeProvider? timeProvider = null)
    {
        var directory = Directory.CreateTempSubdirectory("caf-runlog-").FullName;
        return new AgentRunLogFactory(directory, timeProvider ?? TimeProvider.System);
    }

    public static IAgentRunLog CreateLog(TimeProvider? timeProvider = null)
    {
        return CreateFactory(timeProvider).Start(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "go", Path.GetTempPath(), null, null));
    }

    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class StubProcessLifetime : IProcessLifetime
{
    public int Id { get; set; } = 42;
    public bool HasExited { get; set; }
}

public sealed class RecordingProcessRunner : IProcessRunner
{
    public ProcessRunRequest? LastRequest { get; private set; }
    public ProcessRunResult Result { get; set; } = new(0, "{}", "");
    public Exception? ExceptionToThrow { get; set; }
    public StubProcessLifetime ProcessLifetime { get; } = new();

    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        cancellationToken.ThrowIfCancellationRequested();
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        request.OnProcessStarted?.Invoke(ProcessLifetime);
        request.OnLaunchResolved?.Invoke(new ProcessLaunchInfo(
            request.FileName,
            request.FileName,
            request.FileName,
            request.Arguments,
            UsedWindowsCmdWrapper: false));
        foreach (var line in SplitLines(Result.StandardOutput))
        {
            request.StdoutLineCallback?.Invoke(line);
        }

        foreach (var line in SplitLines(Result.StandardError))
        {
            request.StderrLineCallback?.Invoke(line);
        }

        return Task.FromResult(Result);
    }

    internal static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0)
        {
            count--;
        }

        for (var i = 0; i < count; i++)
        {
            yield return lines[i];
        }
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
        Assert.False(string.IsNullOrWhiteSpace(result.RunId));
        Assert.True(File.Exists(result.EventsLogPath));
        Assert.True(File.Exists(result.TextLogPath));
    }

    [Fact]
    public async Task RoutesGrokBuild()
    {
        var facade = CreateFacade(out var runner);
        runner.Result = new ProcessRunResult(
            0,
            """
            {"type":"text","data":"hi"}
            {"type":"end","sessionId":"22222222-2222-2222-2222-222222222222"}
            """,
            "");
        var result = await facade.RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "hello", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None);
        Assert.Equal("grok", runner.LastRequest!.FileName);
        Assert.Equal(AgentFacade.GrokBuildAgent, result.Agent);
        Assert.Equal("hi", result.OutputText);
        Assert.Equal("22222222-2222-2222-2222-222222222222", result.SessionId);
    }

    private static AgentFacade CreateFacade(out RecordingProcessRunner runner)
    {
        return CreateFacade(out runner, out _);
    }

    internal static AgentFacade CreateFacade(out RecordingProcessRunner runner, out AgentRunLogFactory factory)
    {
        runner = new RecordingProcessRunner();
        factory = TestRunLogs.CreateFactory();
        return new AgentFacade(new GitHubCopilotDriver(runner), new GrokBuildDriver(runner), factory);
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
    public void BuildArgumentsOmitsAllowAllWhenAutoApproveFalse()
    {
        var args = GitHubCopilotDriver.BuildArguments(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "ask", @"C:\repo", null, null, AutoApprove: false));
        Assert.Equal(["--prompt", "ask", "--output-format", "json"], args);
        Assert.DoesNotContain("--allow-all", args);
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
        Assert.StartsWith("Use the /dotnet-file-based-apps skill.", prompt, StringComparison.Ordinal);
        Assert.Contains("Use the /review skill.", prompt, StringComparison.Ordinal);
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
        var result = await RunAsync(runner);
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", result.SessionId);
        Assert.Equal("first\nsecond", result.OutputText);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Path.GetTempPath(), runner.LastRequest!.WorkingDirectory);
    }

    [Fact]
    public async Task ParsesAssistantMessageAndResultSessionId()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"assistant.message","data":{"content":"pong"}}
                {"type":"result","sessionId":"f3358158-943c-4355-a193-ccb669fe856d","exitCode":0}
                """,
                ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal("pong", result.OutputText);
        Assert.Equal("f3358158-943c-4355-a193-ccb669fe856d", result.SessionId);
    }

    [Fact]
    public async Task IgnoresNonJsonLinesWhenJsonlExists()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                ● Checking my documentation
                {"type":"assistant.message","data":{"content":"pong"}}
                {"type":"result","sessionId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                """,
                ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal("pong", result.OutputText);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.SessionId);
    }

    [Fact]
    public async Task ParsesResumeHint()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"assistant.message","data":{"content":"done"}}
                {"type":"exit","hint":"copilot --resume=cccccccc-cccc-cccc-cccc-cccccccccccc"}
                """,
                ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal("cccccccc-cccc-cccc-cccc-cccccccccccc", result.SessionId);
        Assert.Equal("done", result.OutputText);
    }

    [Fact]
    public async Task ThrowsWhenAssistantTextIsMissing()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """{"type":"result","sessionId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","exitCode":0}""",
                ""),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner));
    }

    [Fact]
    public async Task DoesNotTreatArbitraryUuidAsSessionId()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """{"type":"assistant","text":"see 99999999-9999-9999-9999-999999999999"}""",
                ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal(string.Empty, result.SessionId);
        Assert.Contains("99999999-9999-9999-9999-999999999999", result.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonZeroExitThrows()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(2, "out", "err"),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner));
    }

    [Fact]
    public async Task EmptyStdoutThrows()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, "  ", ""),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner));
    }

    [Fact]
    public async Task StreamsJsonlEventsToRunLog()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"assistant.message","data":{"content":"pong"}}
                {"type":"result","sessionId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                """,
                ""),
        };
        await using var log = TestRunLogs.CreateLog();
        var result = await new GitHubCopilotDriver(runner).RunAsync(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "go", Path.GetTempPath(), null, null),
            log,
            onStdoutLine: null,
            CancellationToken.None);
        var events = TestRunLogs.ReadShared(result.EventsLogPath);
        var text = TestRunLogs.ReadShared(result.TextLogPath);
        Assert.Contains("\"type\":\"assistant.message\"", events, StringComparison.Ordinal);
        Assert.Contains("assistant: pong", text, StringComparison.Ordinal);
        Assert.Contains("result sessionId=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", text, StringComparison.Ordinal);
    }

    private static async Task<AgentRunResult> RunAsync(RecordingProcessRunner runner)
    {
        await using var log = TestRunLogs.CreateLog();
        return await new GitHubCopilotDriver(runner).RunAsync(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "go", Path.GetTempPath(), null, null),
            log,
            onStdoutLine: null,
            CancellationToken.None);
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
            ["--no-auto-update", "-p", "list files", "--cwd", @"D:\ws", "--output-format", "streaming-json", "--always-approve"],
            args);
    }

    [Fact]
    public void BuildArgumentsOmitsAlwaysApproveWhenAutoApproveFalse()
    {
        var args = GrokBuildDriver.BuildArguments(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "ask", @"D:\ws", null, null, AutoApprove: false));
        Assert.DoesNotContain("--always-approve", args);
        Assert.Contains("-p", args);
        Assert.Contains("streaming-json", args);
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
        Assert.Contains("--resume", args);
        Assert.DoesNotContain("--session-id", args);
        Assert.Contains("dddddddd-dddd-dddd-dddd-dddddddddddd", args);
        var prompt = args[args.IndexOf("-p") + 1];
        Assert.Equal("/review\nnext", prompt);
    }

    [Fact]
    public async Task ParsesStreamingJsonTextAndSessionId()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                log line
                {"type":"text","data":"done"}
                {"type":"end","sessionId":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee","stopReason":"end_turn"}
                """,
                ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal("done", result.OutputText);
        Assert.Equal("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", result.SessionId);
        Assert.Equal("grok", runner.LastRequest!.FileName);
    }

    [Fact]
    public async Task ParsesConcatenatedTextChunks()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"text","data":"nes"}
                {"type":"text","data":"ted"}
                {"type":"end","sessionId":"ffffffff-ffff-ffff-ffff-ffffffffffff"}
                """,
                ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal("nested", result.OutputText);
        Assert.Equal("ffffffff-ffff-ffff-ffff-ffffffffffff", result.SessionId);
    }

    [Fact]
    public async Task DoesNotTreatLogUuidAsSessionId()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                session 88888888-8888-8888-8888-888888888888 started
                {"type":"text","data":"ok"}
                {"type":"end","stopReason":"end_turn"}
                """,
                ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal(string.Empty, result.SessionId);
        Assert.Equal("ok", result.OutputText);
    }

    [Fact]
    public async Task ThrowsWhenResponseFieldIsMissing()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, """{"type":"end","sessionId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}""", ""),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner));
    }

    [Fact]
    public async Task NonZeroExitThrows()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(1, "", "boom"),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner));
        Assert.Contains("exited with code 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingJsonThrows()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, "no json here", ""),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner));
    }

    [Fact]
    public async Task LogsToolCallSummaryWithoutHugePayload()
    {
        var huge = new string('x', 4000);
        var stdout =
            """
            {"type":"tool_call","toolCallId":"call_1","title":"Read","kind":"read","status":"in_progress","toolName":"read_file","rawInput":{"path":"src/main.rs"}}
            """
            + "\n{\"type\":\"tool_call_update\",\"toolCallId\":\"call_1\",\"status\":\"completed\",\"rawOutput\":{\"body\":\"" + huge + "\"}}\n"
            + """
            {"type":"text","data":"done"}
            {"type":"end","sessionId":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee","stopReason":"end_turn"}
            """;
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, stdout, ""),
        };
        var result = await RunAsync(runner);
        var events = TestRunLogs.ReadShared(result.EventsLogPath);
        var text = TestRunLogs.ReadShared(result.TextLogPath);
        Assert.Contains("\"type\":\"tool_call\"", events, StringComparison.Ordinal);
        Assert.Contains("read_file", events, StringComparison.Ordinal);
        Assert.Contains(huge, events, StringComparison.Ordinal);
        Assert.Contains("tool start read_file (read)", text, StringComparison.Ordinal);
        Assert.Contains("src/main.rs", text, StringComparison.Ordinal);
        Assert.Contains("tool completed call_1", text, StringComparison.Ordinal);
        Assert.DoesNotContain(huge, text, StringComparison.Ordinal);
        Assert.Equal("done", result.OutputText);
    }

    private static async Task<AgentRunResult> RunAsync(RecordingProcessRunner runner)
    {
        await using var log = TestRunLogs.CreateLog();
        return await new GrokBuildDriver(runner).RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "go", Path.GetTempPath(), null, null),
            log,
            onStdoutLine: null,
            CancellationToken.None);
    }
}

public class SkillConversionTests
{
    [Fact]
    public void CopilotLeavesPromptUnchangedWithoutSkills()
    {
        Assert.Equal("body", GitHubCopilotDriver.ApplyCopilotSkills("body", null));
        Assert.Equal("body", GitHubCopilotDriver.ApplyCopilotSkills("body", []));
    }

    [Fact]
    public void CopilotConvertsCodexSkillPrefixToSlash()
    {
        Assert.Equal("/dotnet-file-based-apps", GitHubCopilotDriver.ToSlashName("$dotnet-file-based-apps"));
        Assert.Equal("/review", GitHubCopilotDriver.ToSlashName("review"));
        Assert.Equal(
            "Use the /review skill.\nbody",
            GitHubCopilotDriver.ApplyCopilotSkills("body", ["review"]));
    }

    [Fact]
    public void GrokConvertsCodexSkillPrefixToSlash()
    {
        Assert.Equal("/dotnet-file-based-apps", GrokBuildDriver.ToSlashInvocation("$dotnet-file-based-apps"));
        Assert.Equal("/review", GrokBuildDriver.ToSlashInvocation("/review"));
    }

    [Fact]
    public void EmptySkillNameThrowsOnEachDriver()
    {
        Assert.Throws<ArgumentException>(() => GitHubCopilotDriver.ToSlashName("$"));
        Assert.Throws<ArgumentException>(() => GrokBuildDriver.ToSlashInvocation("$"));
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
    public async Task ReportsResolvedLaunchPath()
    {
        ProcessLaunchInfo? launch = null;
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(
            new ProcessRunRequest(
                "dotnet",
                ["--version"],
                Directory.GetCurrentDirectory(),
                OnLaunchResolved: info => launch = info),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(launch);
        Assert.True(Path.IsPathRooted(launch!.ResolvedExecutable));
        Assert.Contains("dotnet", launch.ResolvedExecutable, StringComparison.OrdinalIgnoreCase);
        Assert.False(launch.UsedWindowsCmdWrapper);
    }

    [Fact]
    public async Task CancelledTokenThrowsBeforeStartingProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new ProcessRunner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new ProcessRunRequest("codex-agent-facade-missing-cli-xyz", [], Directory.GetCurrentDirectory()),
            cts.Token));
    }

    [Fact]
    public async Task StdoutCallbackFailureStopsTheProcess()
    {
        var runner = new ProcessRunner();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new ProcessRunRequest(
                "dotnet",
                ["--info"],
                Directory.GetCurrentDirectory(),
                StdoutLineCallback: _ => throw new InvalidOperationException("log volume full")),
            CancellationToken.None));
        Assert.Equal("log volume full", ex.Message);
    }

    [Fact]
    public void MissingExecutableThrows()
    {
        Assert.Throws<FileNotFoundException>(() => ExecutableResolver.Resolve("codex-agent-facade-missing-cli-xyz"));
    }

    [Fact]
    public void CmdCommandQuotesMetacharacters()
    {
        var command = WindowsCmd.BuildCommand(
            @"C:\tools\copilot.cmd",
            ["--prompt", "foo&whoami", "--output-format", "json"]);
        Assert.Equal(
            "\"C:\\tools\\copilot.cmd\" \"--prompt\" \"foo&whoami\" \"--output-format\" \"json\"",
            command);
        Assert.Equal("\"a\"\"b\"", WindowsCmd.QuoteArgument("a\"b"));
        Assert.Equal("\"%%PATH%%\"", WindowsCmd.QuoteArgument("%PATH%"));
    }
}

public class AgentRunLogTests
{
    [Fact]
    public async Task WritesPairedFilesOutsideWorkingDirectory()
    {
        var work = Directory.CreateTempSubdirectory("caf-work-").FullName;
        var facade = AgentFacadeTests.CreateFacade(out var runner, out var factory);
        runner.Result = new ProcessRunResult(
            0,
            """
            {"type":"text","data":"hi"}
            {"type":"end","sessionId":"22222222-2222-2222-2222-222222222222"}
            """,
            "");
        var result = await facade.RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "hello", work, null, ["$review"], AutoApprove: true),
            onStdoutLine: null,
            CancellationToken.None);

        Assert.Equal(result.RunId + ".events.jsonl", Path.GetFileName(result.EventsLogPath));
        Assert.Equal(result.RunId + ".log", Path.GetFileName(result.TextLogPath));
        Assert.Equal(factory.LogDirectory, Path.GetDirectoryName(result.EventsLogPath));
        Assert.False(result.EventsLogPath.StartsWith(work, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(result.EventsLogPath));
        Assert.True(File.Exists(result.TextLogPath));

        var events = TestRunLogs.ReadShared(result.EventsLogPath);
        Assert.Contains("\"type\":\"started\"", events, StringComparison.Ordinal);
        Assert.Contains("\"prompt\":\"/review\\nhello\"", events, StringComparison.Ordinal);
        Assert.Contains("\"autoApprove\":true", events, StringComparison.Ordinal);
        Assert.Contains("$review", events, StringComparison.Ordinal);
        Assert.Contains("streaming-json", events, StringComparison.Ordinal);
        Assert.Contains("--always-approve", events, StringComparison.Ordinal);
        Assert.DoesNotContain("environmentVariables", events, StringComparison.Ordinal);
        Assert.DoesNotContain("XAI_API_KEY", events, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"completed\"", events, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartedIncludesConvertedPromptAndDoesNotDumpSecrets()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """{"type":"assistant","text":"ok"}""",
                ""),
        };
        await using var log = TestRunLogs.CreateLog();
        var result = await new GitHubCopilotDriver(runner).RunAsync(
            new AgentRunRequest(
                AgentFacade.GitHubCopilotAgent,
                "do work",
                Path.GetTempPath(),
                null,
                ["$dotnet-file-based-apps"],
                AutoApprove: false),
            log,
            onStdoutLine: null,
            CancellationToken.None);
        var events = TestRunLogs.ReadShared(result.EventsLogPath);
        Assert.Contains("Use the /dotnet-file-based-apps skill.", events, StringComparison.Ordinal);
        Assert.Contains("\"autoApprove\":false", events, StringComparison.Ordinal);
        Assert.DoesNotContain("--allow-all", events, StringComparison.Ordinal);
        Assert.DoesNotContain("GH_TOKEN", events, StringComparison.Ordinal);
        Assert.DoesNotContain("environmentVariables", events, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedactsSecretsFromPromptPayloadAndFailedDetail()
    {
        const string xai = "xai-supersecrettokenvalue";
        const string github = "ghp_abcdefghijklmnopqrstuvwxyz1234567890";
        var prompt = "keep going with " + xai + " and GH_TOKEN=" + github;
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"tool_call","toolCallId":"call_1","toolName":"read_file","kind":"read","rawInput":{"path":"src/main.rs","apiKey":"literal-secret"}}
                {"type":"text","data":"done"}
                {"type":"end","sessionId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                """,
                "Authorization: Bearer " + xai),
        };
        string eventsPath;
        string textPath;
        await using (var log = TestRunLogs.CreateLog())
        {
            var result = await new GrokBuildDriver(runner).RunAsync(
                new AgentRunRequest(AgentFacade.GrokBuildAgent, prompt, Path.GetTempPath(), null, null),
                log,
                onStdoutLine: null,
                CancellationToken.None);
            eventsPath = result.EventsLogPath;
            textPath = result.TextLogPath;
        }

        var events = TestRunLogs.ReadShared(eventsPath);
        var text = TestRunLogs.ReadShared(textPath);
        Assert.DoesNotContain(xai, events, StringComparison.Ordinal);
        Assert.DoesNotContain(github, events, StringComparison.Ordinal);
        Assert.DoesNotContain("literal-secret", events, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Replacement, events, StringComparison.Ordinal);
        Assert.DoesNotContain(xai, text, StringComparison.Ordinal);
        Assert.DoesNotContain(github, text, StringComparison.Ordinal);
        Assert.Contains("tool start read_file (read) src/main.rs", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRunAndTraceDoNotKeepSecrets()
    {
        const string xai = "xai-supersecrettokenvalue";
        const string basic = "Authorization: Basic dXNlcjpwYXNz";
        const string pem = "-----BEGIN PRIVATE KEY-----\nMIISECRETKEYMATERIAL\n-----END PRIVATE KEY-----";
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            var facade = AgentFacadeTests.CreateFacade(out var runner, out var factory);
            runner.Result = new ProcessRunResult(1, "token=" + xai, basic + "\n" + pem);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => facade.RunAsync(
                new AgentRunRequest(AgentFacade.GrokBuildAgent, "go", Path.GetTempPath(), null, null),
                onStdoutLine: null,
                CancellationToken.None));
            Assert.DoesNotContain(xai, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("dXNlcjpwYXNz", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("MIISECRETKEYMATERIAL", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(xai, listener.Buffer.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("dXNlcjpwYXNz", listener.Buffer.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("MIISECRETKEYMATERIAL", listener.Buffer.ToString(), StringComparison.Ordinal);

            var events = TestRunLogs.ReadShared(Directory.GetFiles(factory.LogDirectory, "*.events.jsonl").Single());
            var text = TestRunLogs.ReadShared(Directory.GetFiles(factory.LogDirectory, "*.log").Single());
            Assert.Contains("\"type\":\"failed\"", events, StringComparison.Ordinal);
            Assert.DoesNotContain(xai, events, StringComparison.Ordinal);
            Assert.DoesNotContain("dXNlcjpwYXNz", events, StringComparison.Ordinal);
            Assert.DoesNotContain("MIISECRETKEYMATERIAL", events, StringComparison.Ordinal);
            Assert.DoesNotContain(xai, text, StringComparison.Ordinal);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task HumanLogSummarizesCopilotToolAndGrokMode()
    {
        var copilotRunner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"tool","toolName":"bash","status":"in_progress","data":{"command":"dotnet test"}}
                {"type":"assistant.message","data":{"content":"ok"}}
                """,
                ""),
        };
        await using var copilotLog = TestRunLogs.CreateLog();
        var copilot = await new GitHubCopilotDriver(copilotRunner).RunAsync(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "go", Path.GetTempPath(), null, null),
            copilotLog,
            onStdoutLine: null,
            CancellationToken.None);
        var copilotText = TestRunLogs.ReadShared(copilot.TextLogPath);
        Assert.Contains("tool start bash (execute) dotnet test", copilotText, StringComparison.Ordinal);

        var grokRunner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"lifecycle","mode":"plan","status":"entered"}
                {"type":"text","data":"done"}
                {"type":"end","sessionId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                """,
                ""),
        };
        await using var grokLog = TestRunLogs.CreateLog();
        var grok = await new GrokBuildDriver(grokRunner).RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "go", Path.GetTempPath(), null, null),
            grokLog,
            onStdoutLine: null,
            CancellationToken.None);
        var grokText = TestRunLogs.ReadShared(grok.TextLogPath);
        Assert.Contains("lifecycle", grokText, StringComparison.Ordinal);
        Assert.Contains("mode=plan", grokText, StringComparison.Ordinal);
        Assert.DoesNotContain("event lifecycle", grokText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchEventRecordsResolvedExecutable()
    {
        var facade = AgentFacadeTests.CreateFacade(out var runner, out _);
        runner.Result = new ProcessRunResult(
            0,
            """
            {"type":"text","data":"hi"}
            {"type":"end","sessionId":"22222222-2222-2222-2222-222222222222"}
            """,
            "");
        var result = await facade.RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "hello", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None);
        var events = TestRunLogs.ReadShared(result.EventsLogPath);
        var text = TestRunLogs.ReadShared(result.TextLogPath);
        Assert.Contains("\"type\":\"launch\"", events, StringComparison.Ordinal);
        Assert.Contains("\"resolvedExecutable\":\"grok\"", events, StringComparison.Ordinal);
        Assert.Contains("launch resolved=grok", text, StringComparison.Ordinal);
        Assert.Contains("wrapper=none", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeartbeatRecordsElapsedProcessAliveAndLastOutput()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
        await using var log = (AgentRunLog)TestRunLogs.CreateLog(time);
        log.AttachProcess(new StubProcessLifetime { Id = 7, HasExited = false });
        time.Advance(TimeSpan.FromSeconds(3));
        log.NoteExternalOutput();
        time.Advance(TimeSpan.FromSeconds(2));
        log.WriteHeartbeat();

        var events = TestRunLogs.ReadShared(log.EventsPath);
        var text = TestRunLogs.ReadShared(log.TextLogPath);
        using var started = JsonDocument.Parse(FindLastEvent(events, "heartbeat"));
        var data = started.RootElement.GetProperty("data");
        Assert.Equal(5, data.GetProperty("elapsedSeconds").GetDouble(), 1);
        Assert.True(data.GetProperty("processAlive").GetBoolean());
        Assert.Equal(2, data.GetProperty("lastOutputAgoSeconds").GetDouble(), 1);
        Assert.Equal(7, data.GetProperty("processId").GetInt32());
        Assert.Contains("heartbeat elapsed=5.0s processAlive=True lastOutputAgo=2.0s", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeartbeatTimerWritesAfterInterval()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
        await using var log = (AgentRunLog)TestRunLogs.CreateLog(time);
        log.AttachProcess(new StubProcessLifetime { HasExited = false });
        await WaitForHeartbeatLoopAsync();
        time.Advance(AgentRunLog.HeartbeatInterval);

        var found = false;
        for (var i = 0; i < 20; i++)
        {
            var events = TestRunLogs.ReadShared(log.EventsPath);
            if (events.Contains("\"type\":\"heartbeat\"", StringComparison.Ordinal))
            {
                found = true;
                break;
            }

            await Task.Delay(20);
        }

        Assert.True(found);
    }

    [Fact]
    public async Task CancelWritesCancelledEvent()
    {
        var facade = AgentFacadeTests.CreateFacade(out var runner, out var factory);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => facade.RunAsync(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "go", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            cts.Token));
        var logFile = Directory.GetFiles(factory.LogDirectory, "*.log").Single();
        var eventsFile = Directory.GetFiles(factory.LogDirectory, "*.events.jsonl").Single();
        Assert.Contains("cancelled", TestRunLogs.ReadShared(logFile), StringComparison.Ordinal);
        Assert.Contains("\"type\":\"cancelled\"", TestRunLogs.ReadShared(eventsFile), StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"cancelled\"", TestRunLogs.ReadShared(eventsFile), StringComparison.Ordinal);
        Assert.DoesNotContain("\"reason\":\"canceled\"", TestRunLogs.ReadShared(eventsFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonZeroExitWritesFailedEvent()
    {
        var facade = AgentFacadeTests.CreateFacade(out var runner, out var factory);
        runner.Result = new ProcessRunResult(1, "", "boom");
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "go", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None));
        var events = TestRunLogs.ReadShared(Directory.GetFiles(factory.LogDirectory, "*.events.jsonl").Single());
        var text = TestRunLogs.ReadShared(Directory.GetFiles(factory.LogDirectory, "*.log").Single());
        Assert.Contains("\"type\":\"failed\"", events, StringComparison.Ordinal);
        Assert.Contains("failed", text, StringComparison.Ordinal);
        Assert.Contains("started", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StderrLinesBecomeProcessEvents()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"text","data":"ok"}
                {"type":"end","sessionId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                """,
                "boom-stderr"),
        };
        string eventsPath;
        string textPath;
        await using (var log = TestRunLogs.CreateLog())
        {
            var result = await new GrokBuildDriver(runner).RunAsync(
                new AgentRunRequest(AgentFacade.GrokBuildAgent, "go", Path.GetTempPath(), null, null),
                log,
                onStdoutLine: null,
                CancellationToken.None);
            eventsPath = result.EventsLogPath;
            textPath = result.TextLogPath;
        }

        var events = File.ReadAllText(eventsPath);
        var text = File.ReadAllText(textPath);
        Assert.Contains("\"type\":\"stderr\"", events, StringComparison.Ordinal);
        Assert.Contains("boom-stderr", events, StringComparison.Ordinal);
        Assert.Contains("stderr: boom-stderr", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsFileCanBeReadWhileWriting()
    {
        await using var log = TestRunLogs.CreateLog();
        log.WriteStarted(new AgentRunStartedInfo(
            AgentFacade.GrokBuildAgent,
            Path.GetTempPath(),
            null,
            true,
            null,
            "hello",
            "grok",
            ["-p", "hello"]));
        using (var reader = new FileStream(log.EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var text = new StreamReader(reader))
        {
            var contents = text.ReadToEnd();
            Assert.Contains("\"type\":\"started\"", contents, StringComparison.Ordinal);
        }

        log.WriteCancelled();
        Assert.Contains("cancelled", TestRunLogs.ReadShared(log.TextLogPath), StringComparison.Ordinal);
    }

    private static string FindLastEvent(string jsonl, string type)
    {
        string? match = null;
        foreach (var line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("\"type\":\"" + type + "\"", StringComparison.Ordinal))
            {
                match = line;
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(match));
        return match!;
    }

    [Fact]
    public async Task HeartbeatCancellationIsTraced()
    {
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            await using (var log = TestRunLogs.CreateLog())
            {
                await Task.Delay(20);
            }

            Assert.Contains("OperationCanceledException", listener.Buffer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task CombinesThoughtFragmentsIntoOneHumanLine()
    {
        string[] parts = ["The", " file", "-", "based", " app", " has", " a", " bug", ":"];
        await using var log = (AgentRunLog)TestRunLogs.CreateLog();
        foreach (var part in parts)
        {
            WriteThoughtEvent(log, part);
        }

        var events = TestRunLogs.ReadShared(log.EventsPath);
        var thoughtEvents = events.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains("\"type\":\"thought\"", StringComparison.Ordinal));
        Assert.Equal(parts.Length, thoughtEvents);

        await log.DisposeAsync();
        var text = TestRunLogs.ReadShared(log.TextLogPath);
        var thoughtLines = HumanLines(text, "thought:");
        Assert.Single(thoughtLines);
        Assert.Contains("thought: The file-based app has a bug:", thoughtLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlushesHumanFragmentsOnNewlines()
    {
        await using var log = (AgentRunLog)TestRunLogs.CreateLog();
        WriteThoughtEvent(log, "first line\nsecond");
        WriteThoughtEvent(log, " line\nthird");
        var beforeDispose = HumanLines(TestRunLogs.ReadShared(log.TextLogPath), "thought:");
        Assert.Equal(2, beforeDispose.Count);
        Assert.Contains("thought: first line", beforeDispose[0], StringComparison.Ordinal);
        Assert.Contains("thought: second line", beforeDispose[1], StringComparison.Ordinal);
        Assert.DoesNotContain("thought: third", string.Join('\n', beforeDispose), StringComparison.Ordinal);

        await log.DisposeAsync();
        var afterDispose = HumanLines(TestRunLogs.ReadShared(log.TextLogPath), "thought:");
        Assert.Equal(3, afterDispose.Count);
        Assert.Contains("thought: third", afterDispose[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlushesHumanFragmentsWhenKindChanges()
    {
        await using var log = (AgentRunLog)TestRunLogs.CreateLog();
        WriteThoughtEvent(log, "aaa");
        var payload = JsonSerializer.SerializeToElement(new { type = "text", data = "bbb" });
        log.WriteAgentEvent("text", payload, null);
        log.AppendHumanFragment("assistant", "bbb");

        var text = TestRunLogs.ReadShared(log.TextLogPath);
        var thoughtLines = HumanLines(text, "thought:");
        Assert.Single(thoughtLines);
        Assert.Contains("thought: aaa", thoughtLines[0], StringComparison.Ordinal);
        Assert.Empty(HumanLines(text, "assistant:"));

        await log.DisposeAsync();
        text = TestRunLogs.ReadShared(log.TextLogPath);
        Assert.Single(HumanLines(text, "assistant:"));
        Assert.Contains("assistant: bbb", HumanLines(text, "assistant:")[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlushesHumanFragmentsBeforeHeartbeatAndCompleted()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        await using var log = (AgentRunLog)TestRunLogs.CreateLog(time);
        WriteThoughtEvent(log, "partial");
        time.Advance(TimeSpan.FromSeconds(15));
        log.WriteHeartbeat();

        var text = TestRunLogs.ReadShared(log.TextLogPath);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var thoughtIndex = Array.FindIndex(lines, line => line.Contains(" thought:", StringComparison.Ordinal));
        var heartbeatIndex = Array.FindIndex(lines, line => line.Contains(" heartbeat ", StringComparison.Ordinal));
        Assert.True(thoughtIndex >= 0);
        Assert.True(heartbeatIndex > thoughtIndex);
        Assert.StartsWith("2026-08-26T12:00:00.000Z thought:", lines[thoughtIndex], StringComparison.Ordinal);
        Assert.Contains("heartbeat", lines[heartbeatIndex], StringComparison.Ordinal);

        log.WriteCompleted(new AgentRunResult(
            AgentFacade.GrokBuildAgent,
            "sid",
            0,
            "ok",
            "raw",
            log.RunId,
            log.EventsPath,
            log.TextLogPath));
        text = TestRunLogs.ReadShared(log.TextLogPath);
        Assert.Contains("completed exitCode=0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlushesHumanFragmentsOnFailedAndCancelled()
    {
        await using (var failedLog = (AgentRunLog)TestRunLogs.CreateLog())
        {
            WriteThoughtEvent(failedLog, "boom-thought");
            failedLog.WriteFailed(new InvalidOperationException("nope"));
            var text = TestRunLogs.ReadShared(failedLog.TextLogPath);
            Assert.Contains("thought: boom-thought", text, StringComparison.Ordinal);
            Assert.Contains("failed", text, StringComparison.Ordinal);
        }

        await using var cancelledLog = (AgentRunLog)TestRunLogs.CreateLog();
        WriteThoughtEvent(cancelledLog, "wait-thought");
        cancelledLog.WriteCancelled();
        var cancelledText = TestRunLogs.ReadShared(cancelledLog.TextLogPath);
        Assert.Contains("thought: wait-thought", cancelledText, StringComparison.Ordinal);
        Assert.Contains("cancelled", cancelledText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlushesHumanFragmentsWhenBufferExceedsLimit()
    {
        await using var log = (AgentRunLog)TestRunLogs.CreateLog();
        var huge = new string('a', AgentRunLog.MaxFragmentBufferChars + 8);
        WriteThoughtEvent(log, huge);
        var text = TestRunLogs.ReadShared(log.TextLogPath);
        Assert.Single(HumanLines(text, "thought:"));
        Assert.Contains("thought: " + huge, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrokDriverKeepsJsonlFragmentsButCombinesHumanThought()
    {
        var stdout =
            """
            {"type":"thought","data":"The"}
            {"type":"thought","data":" file"}
            {"type":"thought","data":"-"}
            {"type":"thought","data":"based"}
            {"type":"text","data":"done"}
            {"type":"end","sessionId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","stopReason":"end_turn"}
            """;
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, stdout, ""),
        };
        await using var log = TestRunLogs.CreateLog();
        var result = await new GrokBuildDriver(runner).RunAsync(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "go", Path.GetTempPath(), null, null),
            log,
            onStdoutLine: null,
            CancellationToken.None);
        var events = TestRunLogs.ReadShared(result.EventsLogPath);
        Assert.Equal(4, events.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains("\"type\":\"thought\"", StringComparison.Ordinal)));
        var text = TestRunLogs.ReadShared(result.TextLogPath);
        Assert.Single(HumanLines(text, "thought:"));
        Assert.Contains("thought: The file-based", text, StringComparison.Ordinal);
        Assert.Contains("assistant: done", text, StringComparison.Ordinal);
        Assert.Equal("done", result.OutputText);
    }

    private static void WriteThoughtEvent(IAgentRunLog log, string data)
    {
        var payload = JsonSerializer.SerializeToElement(new { type = "thought", data });
        log.WriteAgentEvent("thought", payload, null);
        log.AppendHumanFragment("thought", data);
    }

    private static List<string> HumanLines(string text, string marker)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(" " + marker, StringComparison.Ordinal)
                || line.Contains(" " + marker.TrimEnd(':') + ":", StringComparison.Ordinal))
            .ToList();
    }

    private static async Task WaitForHeartbeatLoopAsync()
    {
        await Task.Delay(50);
    }
}

public class AgentRunLogDirectoryTests
{
    private static readonly object EnvironmentLock = new();

    [Fact]
    public void GetDefaultLogDirectoryUsesUserProfileWhenOverrideIsAbsent()
    {
        lock (EnvironmentLock)
        {
            using (OverrideLogDirectory(null))
            {
                var expected = Path.GetFullPath(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".codex-agent-facade",
                        "runs"));
                Assert.Equal(expected, AgentRunLogFactory.GetDefaultLogDirectory());
            }
        }
    }

    [Fact]
    public void GetDefaultLogDirectoryPrefersEnvironmentOverride()
    {
        lock (EnvironmentLock)
        {
            var overrideDir = Directory.CreateTempSubdirectory("caf-logdir-").FullName;
            using (OverrideLogDirectory(overrideDir))
            {
                Assert.Equal(Path.GetFullPath(overrideDir), AgentRunLogFactory.GetDefaultLogDirectory());
            }
        }
    }

    [Fact]
    public void GetDefaultLogDirectoryIgnoresWhitespaceOverride()
    {
        lock (EnvironmentLock)
        {
            using (OverrideLogDirectory("   "))
            {
                var expected = Path.GetFullPath(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".codex-agent-facade",
                        "runs"));
                Assert.Equal(expected, AgentRunLogFactory.GetDefaultLogDirectory());
            }
        }
    }

    [Fact]
    public void GetDefaultLogDirectoryExpandsEnvironmentVariables()
    {
        lock (EnvironmentLock)
        {
            using (OverrideLogDirectory("%USERPROFILE%\\.codex-agent-facade-override-test"))
            {
                var expected = Path.GetFullPath(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".codex-agent-facade-override-test"));
                Assert.Equal(expected, AgentRunLogFactory.GetDefaultLogDirectory());
            }
        }
    }

    [Fact]
    public void GetDefaultLogDirectoryResolvesRelativeOverrideAgainstUserProfile()
    {
        lock (EnvironmentLock)
        {
            using (OverrideLogDirectory("relative-caf-log-dir"))
            {
                var expected = Path.GetFullPath(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "relative-caf-log-dir"));
                Assert.Equal(expected, AgentRunLogFactory.GetDefaultLogDirectory());
            }
        }
    }

    [Fact]
    public void ExplicitConstructorDirectoryIgnoresEnvironmentOverride()
    {
        lock (EnvironmentLock)
        {
            var injected = Directory.CreateTempSubdirectory("caf-injected-").FullName;
            using (OverrideLogDirectory(Directory.CreateTempSubdirectory("caf-env-").FullName))
            {
                var factory = new AgentRunLogFactory(injected, TimeProvider.System);
                Assert.Equal(Path.GetFullPath(injected), factory.LogDirectory);
            }
        }
    }

    private static IDisposable OverrideLogDirectory(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(AgentRunLogFactory.LogDirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(AgentRunLogFactory.LogDirectoryEnvironmentVariable, value);
        return new EnvironmentVariableRestore(AgentRunLogFactory.LogDirectoryEnvironmentVariable, previous);
    }

    private sealed class EnvironmentVariableRestore : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableRestore(string name, string? previous)
        {
            _name = name;
            _previous = previous;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previous);
        }
    }
}

public class SecretRedactorTests
{
    [Fact]
    public void RedactTextCoversModernGitHubAndOpenAiPrefixes()
    {
        var gho = "gho_" + new string('a', 36);
        var ghu = "ghu_" + new string('b', 36);
        var ghs = "ghs_" + new string('c', 36);
        var ghr = "ghr_" + new string('d', 36);
        const string projectKey = "sk-proj-abcdefghijklmnopqrstuvwxyz";
        var text = "tokens " + gho + " " + ghu + " " + ghs + " " + ghr + " " + projectKey;
        var redacted = SecretRedactor.RedactText(text);
        Assert.DoesNotContain(gho, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(ghu, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(ghs, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(ghr, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(projectKey, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Replacement, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactTextRedactsJsonSecretPropertyValues()
    {
        const string input = """{"apiKey":"literal-secret","path":"src/main.rs"}""";
        var redacted = SecretRedactor.RedactText(input);
        Assert.DoesNotContain("literal-secret", redacted, StringComparison.Ordinal);
        Assert.Contains("\"apiKey\":\"" + SecretRedactor.Replacement + "\"", redacted, StringComparison.Ordinal);
        Assert.Contains("src/main.rs", redacted, StringComparison.Ordinal);
    }
}

internal sealed class CapturingTraceListener : TraceListener
{
    public StringBuilder Buffer { get; } = new();

    public override void Write(string? message)
    {
        Buffer.Append(message);
    }

    public override void WriteLine(string? message)
    {
        Buffer.AppendLine(message);
    }
}
