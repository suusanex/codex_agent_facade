using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

/// <summary>
/// Issue #35 の実測3イベントを、機密と実在パスを除いた fixture として固定する。
/// 時刻とプロセス停止はスタブで行い、実 Grok と実ユーザーログは読まない。
/// </summary>
public class GrokInternalCompletionTests
{
    internal const int Pid = 56368;
    internal const string SessionId = "01a15136-771a-7363-924b-9e5bb063b27b";
    internal const string PromptId = "ca33ed71-5085-487c-98d4-360d23921419";
    internal const string Version = "1.0.62";
    internal const string OtherVersion = "9.9.9";
    internal const string SampleCwd = @"D:\work\sample";
    internal static readonly DateTimeOffset Launch = Parse("2026-10-18T22:51:24.557Z");
    internal static readonly DateTimeOffset DoneAt = Parse("2026-10-19T00:59:04.377Z");

    [Fact]
    public void RecoveryTimingIsInternalAndFixed()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), GrokBuildDriver.IdleBeforeLogCheck);
        Assert.Equal(TimeSpan.FromSeconds(15), GrokBuildDriver.LogRecheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), ProcessRunner.ForcedTerminationBudget);
        Assert.Equal("grok_internal_prompt_done", GrokBuildDriver.InternalPromptDoneReason);
    }

    [Fact]
    public async Task MeasuredEventsRescueReportWithoutEndOrExitCode()
    {
        using var harness = new GrokRescueHarness();
        var result = await harness.RescueAsync(Request(), MeasuredLog());

        Assert.Equal("report", result.OutputText);
        Assert.Equal("assistant_transcript", result.OutputKind);
        Assert.Equal(SessionId, result.SessionId);
        Assert.Null(result.ExitCode);
        Assert.Equal(GrokBuildDriver.InternalPromptDoneReason, result.CompletionReason);
        Assert.Equal(new GrokCompletionEvidence(Pid, SessionId, PromptId, DoneAt, Version), result.CompletionEvidence);
        Assert.Equal(new ProcessTerminationInfo(ProcessTerminationStatus.Unconfirmed, false), result.ProcessTermination);
        Assert.DoesNotContain(SampleCwd, result.OutputText, StringComparison.Ordinal);
        Assert.True(harness.Runner.LastRequest!.Completion!.IsSignaled);
        var events = TestRunLogs.ReadShared(result.EventsLogPath);
        Assert.DoesNotContain(SampleCwd, events, StringComparison.Ordinal);
        Assert.DoesNotContain("shell.handle_prompt.done", events, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DifferentGrokVersionStillRescuesWhenShapeMatches()
    {
        using var harness = new GrokRescueHarness();
        var result = await harness.RescueAsync(Request(effort: null), MeasuredLog(version: OtherVersion));
        Assert.Equal(OtherVersion, result.CompletionEvidence!.GrokVersion);
        Assert.Equal(PromptId, result.CompletionEvidence.PromptId);
    }

    [Fact]
    public async Task ResumeMatchesSessionLoadedRecord()
    {
        using var harness = new GrokRescueHarness();
        var result = await harness.RescueAsync(Request(sessionId: SessionId), MeasuredLog(sessionMessage: "session loaded"));
        Assert.Equal(SessionId, result.SessionId);
        Assert.Equal(PromptId, result.CompletionEvidence!.PromptId);
    }

    [Fact]
    public async Task SessionRecordedBeforeTheWatchStartsStillRescues()
    {
        using var harness = new GrokRescueHarness();
        harness.Runner.StartedAtClock = () => Launch;
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        var result = await harness.RescueAsync(Request(), MeasuredLog());
        Assert.Equal(PromptId, result.CompletionEvidence!.PromptId);
        Assert.Equal(GrokBuildDriver.InternalPromptDoneReason, result.CompletionReason);
    }

    [Fact]
    public async Task DoesNotRescueBeforeFiveIdleMinutes()
    {
        using var harness = new GrokRescueHarness();
        var run = harness.Start(Request());
        await harness.StartedAsync();
        harness.EmitReport();
        harness.Append(MeasuredLog());
        harness.Time.Advance(GrokBuildDriver.IdleBeforeLogCheck - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.False(run.IsCompleted);
        Assert.Equal(0, harness.Log.Reads);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var result = await harness.AwaitRunAsync(run);
        Assert.Equal("report", result.OutputText);
        Assert.Equal(GrokBuildDriver.InternalPromptDoneReason, result.CompletionReason);
    }

    [Fact]
    public async Task LaterCompletionIsSeenOnTheNextRecheck()
    {
        using var harness = new GrokRescueHarness();
        var run = harness.Start(Request());
        await harness.StartedAsync();
        harness.EmitReport();
        harness.Append(MeasuredLog(includeDone: false));
        await harness.AdvanceUntilReadAsync(run, 1);
        Assert.False(run.IsCompleted);
        harness.Append(DoneLine() + "\n");
        harness.Time.Advance(GrokBuildDriver.LogRecheckInterval);
        var result = await harness.AwaitRunAsync(run);
        Assert.Equal(PromptId, result.CompletionEvidence!.PromptId);
        Assert.True(harness.Log.Reads >= 2);
    }

    [Fact]
    public async Task ProcessingProgressRestartsTheIdleWindow()
    {
        using var harness = new GrokRescueHarness();
        var run = harness.Start(Request());
        await harness.StartedAsync();
        harness.EmitReport();
        harness.Append(MeasuredLog());
        harness.Time.Advance(TimeSpan.FromMinutes(4));
        harness.Runner.EmitStdout("""{"type":"tool_call","toolName":"read_file"}""");
        harness.Runner.EmitStdout("""{"type":"plan","entries":[]}""");
        await Task.Delay(30);
        harness.Time.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(30);
        Assert.False(run.IsCompleted);
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        var result = await harness.AwaitRunAsync(run);
        Assert.Equal("report", result.OutputText);
    }

    [Fact]
    public async Task BackgroundOutputDoesNotDelayTheFirstCheck()
    {
        using var harness = new GrokRescueHarness();
        var run = harness.Start(Request());
        await harness.StartedAsync();
        harness.EmitReport();
        harness.Append(MeasuredLog());
        harness.Time.Advance(TimeSpan.FromMinutes(4));
        harness.Runner.EmitStdout("""{"type":"available_commands","tools":["read_file"]}""");
        harness.Runner.EmitStdout("""{"type":"usage","usage":{"input_tokens":1}}""");
        harness.Runner.EmitStdout("""{"type":"thought","data":""}""");
        harness.Runner.EmitStdout("""{"type":"status","data":"still"}""");
        harness.Runner.EmitStdout("   ");
        harness.Runner.EmitStderr("background");
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        var result = await harness.AwaitRunAsync(run);
        Assert.Equal("report", result.OutputText);
        Assert.Equal(GrokBuildDriver.InternalPromptDoneReason, result.CompletionReason);
    }

    [Fact]
    public async Task PartialDoneLineIsCompletedOnTheNextRead()
    {
        using var harness = new GrokRescueHarness();
        var run = harness.Start(Request());
        await harness.StartedAsync();
        harness.EmitReport();
        var done = DoneLine();
        harness.Append(MeasuredLog(includeDone: false) + done[..^4]);
        await harness.AdvanceUntilReadAsync(run, 1);
        Assert.False(run.IsCompleted);
        harness.Append(done[^4..] + "\n");
        harness.Time.Advance(GrokBuildDriver.LogRecheckInterval);
        var result = await harness.AwaitRunAsync(run);
        Assert.Equal(DoneAt, result.CompletionEvidence!.CompletedAt);
    }

    [Fact]
    public async Task OutputAfterConfirmationDoesNotChangeTheReport()
    {
        using var harness = new GrokRescueHarness();
        harness.Runner.LinesAfterSignal = ["""{"type":"text","data":"MORE"}"""];
        var result = await harness.RescueAsync(Request(), MeasuredLog());
        Assert.Equal("report", result.OutputText);
        Assert.Equal(string.Empty, result.RawOutput);
    }

    [Fact]
    public async Task NonZeroKillCodeStaysCompleted()
    {
        using var harness = new GrokRescueHarness();
        harness.Runner.CompletionResult = new ProcessRunResult(
            1,
            string.Empty,
            string.Empty,
            new ProcessTerminationInfo(ProcessTerminationStatus.Exited, true));
        var result = await harness.RescueAsync(Request(), MeasuredLog());
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(ProcessTerminationStatus.Exited, result.ProcessTermination!.Status);
        Assert.True(result.ProcessTermination.OutputRecoveryCompleted);
        Assert.Equal("report", result.OutputText);
    }

    [Fact]
    public async Task AlreadyExitedProcessIsNotRescued()
    {
        using var harness = new GrokRescueHarness();
        harness.Runner.ProcessLifetime.HasExited = true;
        var run = harness.Start(Request());
        await harness.StartedAsync();
        harness.EmitReport();
        harness.Append(MeasuredLog());
        await harness.AdvanceUntilReadAsync(run, 1);
        Assert.False(harness.Runner.LastRequest!.Completion!.IsSignaled);
        harness.Runner.CompleteNaturally(new ProcessRunResult(1, """{"type":"text","data":"natural"}""", "exited"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AwaitRunAsync(run));
        Assert.Equal("non_zero_exit", ex.Data[CliJson.FailureKindKey]);
    }

    [Fact]
    public async Task UnacceptedRescueKeepsNaturalNonZeroExit()
    {
        using var harness = new GrokRescueHarness();
        harness.Runner.CompletionResult = new ProcessRunResult(1, string.Empty, "exited");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RescueAsync(Request(), MeasuredLog()));
        Assert.Equal("non_zero_exit", ex.Data[CliJson.FailureKindKey]);
        Assert.True(harness.Runner.LastRequest!.Completion!.IsSignaled);
    }

    [Fact]
    public async Task StopFailureKeepsTheReportAndOmitsExitCode()
    {
        using var harness = new GrokRescueHarness();
        harness.Runner.CompletionResult = new ProcessRunResult(
            null,
            string.Empty,
            string.Empty,
            new ProcessTerminationInfo(ProcessTerminationStatus.Failed, false));
        var result = await harness.RescueAsync(Request(), MeasuredLog());
        Assert.Null(result.ExitCode);
        Assert.Equal(ProcessTerminationStatus.Failed, result.ProcessTermination!.Status);
        Assert.False(result.ProcessTermination.OutputRecoveryCompleted);
        Assert.Equal("report", result.OutputText);
        Assert.Equal(GrokBuildDriver.InternalPromptDoneReason, result.CompletionReason);
    }

    [Fact]
    public async Task CancellationAfterConfirmationIsNotAUserCancel()
    {
        using var harness = new GrokRescueHarness();
        harness.Runner.ExceptionAfterSignal = new OperationCanceledException("internal stop");
        var result = await harness.RescueAsync(Request(), MeasuredLog());
        Assert.Equal("report", result.OutputText);
        Assert.Null(result.ExitCode);
        Assert.Equal(ProcessTerminationStatus.Unconfirmed, result.ProcessTermination!.Status);
        Assert.False(result.ProcessTermination.OutputRecoveryCompleted);
    }

    [Fact]
    public async Task NaturalExitBeforeConfirmationKeepsTheExistingPath()
    {
        using var harness = new GrokRescueHarness();
        var run = harness.Start(Request());
        await harness.StartedAsync();
        harness.Runner.CompleteNaturally(new ProcessRunResult(
            0,
            """
            {"type":"text","data":"natural"}
            {"type":"end","sessionId":"22222222-2222-2222-2222-222222222222"}
            """,
            ""));
        var result = await harness.AwaitRunAsync(run);
        Assert.Equal("natural", result.OutputText);
        Assert.Equal("22222222-2222-2222-2222-222222222222", result.SessionId);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.CompletionReason);
        Assert.Null(result.CompletionEvidence);
        Assert.Null(result.ProcessTermination);
        Assert.False(harness.Runner.LastRequest!.Completion!.IsSignaled);
    }

    [Fact]
    public async Task UserCancelBeforeConfirmationStaysCancelled()
    {
        using var harness = new GrokRescueHarness();
        using var cancellation = new CancellationTokenSource();
        var run = harness.Start(Request(), cancellation.Token);
        await harness.StartedAsync();
        harness.Append(MeasuredLog());
        harness.EmitReport();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.AwaitRunAsync(run));
        Assert.False(harness.Runner.LastRequest!.Completion!.IsSignaled);
    }

    [Fact]
    public async Task IgnoredReasoningEffortIsNotRescued()
    {
        using var harness = new GrokRescueHarness();
        var run = harness.Start(Request());
        await harness.StartedAsync();
        harness.EmitReport();
        harness.Runner.EmitStderr("ignoring --effort/--reasoning-effort high");
        harness.Append(MeasuredLog());
        harness.Time.Advance(GrokBuildDriver.IdleBeforeLogCheck);
        await Task.Delay(50);
        Assert.False(run.IsCompleted);
        Assert.False(harness.Runner.LastRequest!.Completion!.IsSignaled);
        harness.Runner.CompleteNaturally(new ProcessRunResult(
            0,
            """
            {"type":"text","data":"report"}
            {"type":"end","sessionId":"22222222-2222-2222-2222-222222222222"}
            """,
            "ignoring --effort/--reasoning-effort high"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AwaitRunAsync(run));
        Assert.Equal("option_not_applied", ex.Data[CliJson.FailureKindKey]);
    }

    [Fact]
    public async Task ReturnedEffortWarningFailsAfterInternalSuccess()
    {
        using var harness = new GrokRescueHarness();
        harness.Runner.CompletionResult = new ProcessRunResult(
            null,
            string.Empty,
            "ignoring --effort/--reasoning-effort high",
            new ProcessTerminationInfo(ProcessTerminationStatus.Exited, true));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RescueAsync(Request(), MeasuredLog()));
        Assert.Equal("option_not_applied", ex.Data[CliJson.FailureKindKey]);
    }

    [Theory]
    [InlineData("other-pid")]
    [InlineData("other-session")]
    [InlineData("other-prompt")]
    [InlineData("before-launch")]
    [InlineData("queue-only")]
    [InlineData("second-start")]
    [InlineData("ok-false")]
    [InlineData("ok-string")]
    [InlineData("no-done")]
    [InlineData("version-changed")]
    [InlineData("resume-mismatch")]
    [InlineData("invalid-json")]
    [InlineData("empty-body")]
    public async Task DoesNotRescueUnrelatedOrIncompleteEvidence(string kind)
    {
        using var harness = new GrokRescueHarness();
        var run = harness.Start(Request(sessionId: kind == "resume-mismatch" ? SessionId : null));
        await harness.StartedAsync();
        if (kind != "empty-body")
        {
            harness.EmitReport();
        }

        harness.Append(RejectedLog(kind));
        await harness.AdvanceUntilReadAsync(run, 1);
        harness.Time.Advance(GrokBuildDriver.LogRecheckInterval);
        await Task.Delay(40);
        Assert.False(run.IsCompleted);
        Assert.False(harness.Runner.LastRequest!.Completion!.IsSignaled);
        harness.Runner.CompleteNaturally(new ProcessRunResult(
            0,
            """
            {"type":"text","data":"natural"}
            {"type":"end","sessionId":"22222222-2222-2222-2222-222222222222"}
            """,
            ""));
        var result = await harness.AwaitRunAsync(run);
        Assert.Null(result.CompletionReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("natural", result.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PastLogBeforeTheCursorIsIgnored()
    {
        var directory = Directory.CreateTempSubdirectory("caf-grok-old-").FullName;
        var path = Path.Combine(directory, "unified.jsonl");
        await File.WriteAllTextAsync(path, MeasuredLog());
        using var harness = new GrokRescueHarness(path);
        var run = harness.Start(Request());
        await harness.StartedAsync();
        harness.EmitReport();
        await harness.AdvanceUntilReadAsync(run, 1);
        harness.Time.Advance(GrokBuildDriver.LogRecheckInterval);
        await Task.Delay(40);
        Assert.False(run.IsCompleted);
        harness.Runner.CompleteNaturally(new ProcessRunResult(
            0,
            """
            {"type":"text","data":"natural"}
            {"type":"end","sessionId":"22222222-2222-2222-2222-222222222222"}
            """,
            ""));
        var result = await harness.AwaitRunAsync(run);
        Assert.Null(result.CompletionReason);
    }

    [Fact]
    public async Task ReplacedLogDisablesRecovery()
    {
        var directory = Directory.CreateTempSubdirectory("caf-grok-replaced-").FullName;
        var path = Path.Combine(directory, "unified.jsonl");
        await File.WriteAllTextAsync(path, new string('x', 64) + "\n");
        using var harness = new GrokRescueHarness(path);
        var run = harness.Start(Request());
        await harness.StartedAsync();
        harness.EmitReport();
        await File.WriteAllTextAsync(path, "short");
        await harness.AdvanceUntilReadAsync(run, 1);
        Assert.False(run.IsCompleted);
        harness.Runner.CompleteNaturally(NaturalResult());
        var result = await harness.AwaitRunAsync(run);
        Assert.Null(result.CompletionReason);
    }

    [Fact]
    public async Task ReadExceptionDisablesRecoveryWithoutFailingTheRun()
    {
        var reader = new ThrowingGrokLogReader();
        var time = new FakeTimeProvider(Launch);
        var runner = NewRunner();
        var driver = new GrokBuildDriver(runner, time, reader);
        var run = driver.RunAsync(Request(effort: null), TestRunLogs.CreateLog(time), null, CancellationToken.None);
        await runner.StartedTask.WaitAsync(TimeSpan.FromSeconds(5));
        runner.EmitStdout("""{"type":"text","data":"report"}""");
        time.Advance(GrokBuildDriver.IdleBeforeLogCheck);
        await Task.Delay(50);
        Assert.False(run.IsCompleted);
        Assert.True(reader.Reads >= 1);
        runner.CompleteNaturally(NaturalResult());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result.CompletionReason);
        Assert.Contains("natural", result.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpCliAndJobStoreShareTheRescueContract()
    {
        using var harness = new GrokRescueHarness();
        harness.Runner.CompletionResult = new ProcessRunResult(
            1,
            string.Empty,
            string.Empty,
            new ProcessTerminationInfo(ProcessTerminationStatus.Exited, false));
        var store = Directory.CreateTempSubdirectory("caf-grok-jobs-").FullName;
        var service = new AgentJobService(harness.CreateFacade(), store, harness.Time);
        var tools = new AgentTools(service, NullLoggerFactory.Instance);
        var request = Request();
        var startedJson = tools.StartAgent(
            "req-rescue",
            request.Agent,
            request.Prompt,
            request.WorkingDirectory,
            reasoning_effort: request.ReasoningEffort);
        using (var startedDocument = JsonDocument.Parse(startedJson))
        {
            Assert.Equal(AgentJobStatus.Running, startedDocument.RootElement.GetProperty("status").GetString());
            Assert.False(startedDocument.RootElement.TryGetProperty("result", out _));
        }

        await harness.StartedAsync();
        harness.EmitReport();
        harness.Append(MeasuredLog());
        harness.Time.Advance(GrokBuildDriver.IdleBeforeLogCheck);
        var jobId = JsonDocument.Parse(startedJson).RootElement.GetProperty("jobId").GetString()!;
        var waitedJson = await tools.WaitAgentJob(jobId, timeout_seconds: 30);
        var gotJson = tools.GetAgentJob(jobId);
        AssertRescueJson(waitedJson, exitCode: 1, outputRecovery: false, status: ProcessTerminationStatus.Exited);
        AssertRescueJson(gotJson, exitCode: 1, outputRecovery: false, status: ProcessTerminationStatus.Exited);

        var reloaded = new AgentJobService(harness.CreateFacade(), store, harness.Time)
            .Start("req-rescue", request);
        Assert.Equal(AgentJobStatus.Completed, reloaded.Status);
        Assert.Equal(1, reloaded.Result!.ExitCode);
        Assert.Equal(PromptId, reloaded.Result.CompletionEvidence!.PromptId);
        var saved = await File.ReadAllTextAsync(Directory.EnumerateFiles(store, "req-*.json").Single());
        Assert.Contains("grok_internal_prompt_done", saved, StringComparison.Ordinal);
        Assert.DoesNotContain(SampleCwd, saved, StringComparison.Ordinal);
        Assert.DoesNotContain("shell.handle_prompt", saved, StringComparison.Ordinal);

        var events = TestRunLogs.ReadShared(reloaded.Result.EventsLogPath);
        var text = TestRunLogs.ReadShared(reloaded.Result.TextLogPath);
        Assert.Contains("\"completionReason\":\"grok_internal_prompt_done\"", events, StringComparison.Ordinal);
        Assert.Contains(PromptId, events, StringComparison.Ordinal);
        Assert.Contains("completionReason=grok_internal_prompt_done", text, StringComparison.Ordinal);
        Assert.Contains("exitCode=1", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SampleCwd, events, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmittedExitCodeIsAbsentFromPublicJson()
    {
        using var harness = new GrokRescueHarness();
        var stdout = new StringWriter();
        var facade = harness.CreateFacade();
        var executing = AgentCli.ExecuteAsync(
            facade,
            CliRequest(Request(effort: null)),
            stdout,
            CancellationToken.None,
            Path.Combine(harness.Root, "cli.log"));
        await harness.StartedAsync();
        harness.EmitReport();
        harness.Append(MeasuredLog());
        harness.Time.Advance(GrokBuildDriver.IdleBeforeLogCheck);
        Assert.Equal(AgentCli.ExitCompleted, await executing.WaitAsync(TimeSpan.FromSeconds(5)));
        using var document = JsonDocument.Parse(stdout.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("exitCode", out _));
        Assert.Equal("report", result.GetProperty("outputText").GetString());
        Assert.Equal(ProcessTerminationStatus.Unconfirmed, result.GetProperty("processTermination").GetProperty("status").GetString());
    }

    [Fact]
    public async Task OtherAgentsDoNotReceiveACompletionSignal()
    {
        var cases = new (string Agent, string Stdout)[]
        {
            (AgentFacade.GitHubCopilotAgent, """{"type":"assistant","text":"ok","sessionId":"11111111-1111-1111-1111-111111111111"}"""),
            (AgentFacade.CursorAgent, """{"type":"system","subtype":"init","session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}""" + "\n" + """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"ok"}]}}""" + "\n" + """{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}"""),
            (AgentFacade.CodexAgent, """{"type":"thread.started","thread_id":"thread-2"}""" + "\n" + """{"type":"item.completed","item":{"type":"agent_message","text":"ok"}}""" + "\n" + """{"type":"turn.completed"}"""),
        };
        foreach (var (agent, stdout) in cases)
        {
            var facade = AgentFacadeTests.CreateFacade(out var runner, out _);
            runner.Result = new ProcessRunResult(0, stdout, "");
            var result = await facade.RunAsync(
                new AgentRunRequest(agent, "hello", Path.GetTempPath(), null, null),
                null,
                CancellationToken.None);
            Assert.Null(runner.LastRequest!.Completion);
            Assert.Null(result.CompletionReason);
            Assert.Null(result.ProcessTermination);
            Assert.Equal(0, result.ExitCode);
        }
    }

    [Fact]
    public void OldSavedResultWithoutRescueFieldsStillLoads()
    {
        const string json = """
            {"jobId":"job-old","requestId":"req-old","status":"completed","requestFingerprint":"abc","result":{"agent":"github-copilot","sessionId":"sess-old","exitCode":0,"outputText":"old","rawOutput":"","runId":"run-old","eventsLogPath":"events","textLogPath":"text","outputKind":"final_response"}}
            """;
        var record = JsonSerializer.Deserialize<AgentJobRecord>(json, AgentJson.Options);
        Assert.NotNull(record);
        Assert.Equal(0, record!.Result!.ExitCode);
        Assert.Null(record.Result.CompletionReason);
        Assert.Null(record.Result.CompletionEvidence);
        Assert.Null(record.Result.ProcessTermination);
        var projected = JsonSerializer.Serialize(AgentJobPublicProjection.From(record.Result), AgentJson.Options);
        using var document = JsonDocument.Parse(projected);
        Assert.Equal(0, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("completionReason", out _));
        Assert.False(document.RootElement.TryGetProperty("completionEvidence", out _));
        Assert.False(document.RootElement.TryGetProperty("processTermination", out _));
    }

    private static void AssertRescueJson(string json, int exitCode, bool outputRecovery, string status)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(AgentJobStatus.Completed, root.GetProperty("status").GetString());
        var result = root.GetProperty("result");
        Assert.Equal(exitCode, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("report", result.GetProperty("outputText").GetString());
        Assert.Equal("assistant_transcript", result.GetProperty("outputKind").GetString());
        Assert.Equal(GrokBuildDriver.InternalPromptDoneReason, result.GetProperty("completionReason").GetString());
        var evidence = result.GetProperty("completionEvidence");
        Assert.Equal(Pid, evidence.GetProperty("pid").GetInt32());
        Assert.Equal(SessionId, evidence.GetProperty("sessionId").GetString());
        Assert.Equal(PromptId, evidence.GetProperty("promptId").GetString());
        Assert.Equal(Version, evidence.GetProperty("grokVersion").GetString());
        Assert.Equal(DoneAt, evidence.GetProperty("completedAt").GetDateTimeOffset());
        var termination = result.GetProperty("processTermination");
        Assert.Equal(status, termination.GetProperty("status").GetString());
        Assert.Equal(outputRecovery, termination.GetProperty("outputRecoveryCompleted").GetBoolean());
        Assert.DoesNotContain(SampleCwd, json, StringComparison.Ordinal);
        Assert.False(result.TryGetProperty("rawOutput", out _));
    }

    private static string CliRequest(AgentRunRequest request)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["agent"] = request.Agent,
            ["prompt"] = request.Prompt,
            ["working_directory"] = request.WorkingDirectory,
            ["auto_approve"] = request.AutoApprove,
            ["reasoning_effort"] = request.ReasoningEffort,
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static AgentRunRequest Request(string? sessionId = null, string? effort = "high")
    {
        return new AgentRunRequest(
            AgentFacade.GrokBuildAgent,
            "go",
            Path.GetTempPath(),
            sessionId,
            null,
            ReasoningEffort: effort);
    }

    private static ProcessRunResult NaturalResult()
    {
        return new ProcessRunResult(
            0,
            """
            {"type":"text","data":"natural"}
            {"type":"end","sessionId":"22222222-2222-2222-2222-222222222222"}
            """,
            "");
    }

    private static RecordingProcessRunner NewRunner()
    {
        var runner = new RecordingProcessRunner
        {
            HoldForCompletion = true,
            CompletionResult = new ProcessRunResult(
                null,
                string.Empty,
                string.Empty,
                new ProcessTerminationInfo(ProcessTerminationStatus.Unconfirmed, false)),
            StartedAtClock = () => Launch,
        };
        runner.ProcessLifetime.Id = Pid;
        return runner;
    }

    private static string RejectedLog(string kind)
    {
        return kind switch
        {
            "other-pid" => MeasuredLog(pid: Pid + 1),
            "other-session" => MeasuredLog(includeDone: false)
                + Shell("2026-10-19T00:59:04.377Z", "shell.handle_prompt.done", Pid, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Version, new { prompt_id = PromptId, ok = true })
                + "\n",
            "other-prompt" => MeasuredLog(includeDone: false)
                + DoneLine(promptId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
                + "\n",
            "before-launch" => MeasuredLog(
                sessionTs: "2026-10-18T22:51:24.000Z",
                startTs: "2026-10-18T22:51:24.100Z",
                doneTs: "2026-10-18T22:51:24.200Z"),
            "queue-only" => Shell("2026-10-18T22:51:27.300Z", "shell.prompt.queued", Pid, SessionId, Version, new { prompt_id = "task-completed-sample-a" }) + "\n",
            "second-start" => MeasuredLog(includeDone: false)
                + Shell("2026-10-18T22:51:28.000Z", "shell.handle_prompt.start", Pid, SessionId, Version, new { prompt_id = "cccccccc-cccc-cccc-cccc-cccccccccccc", block_count = 1 })
                + "\n"
                + DoneLine()
                + "\n",
            "ok-false" => MeasuredLog(ok: false),
            "ok-string" => MeasuredLog(includeDone: false) + """{"ts":"2026-10-19T00:59:04.377Z","src":"shell","pid":56368,"ver":"1.0.62","lvl":"info","sid":"01a15136-771a-7363-924b-9e5bb063b27b","msg":"shell.handle_prompt.done","ctx":{"prompt_id":"ca33ed71-5085-487c-98d4-360d23921419","ok":"true"}}""" + "\n",
            "no-done" => MeasuredLog(includeDone: false),
            "version-changed" => MeasuredLog(includeDone: false) + DoneLine(version: OtherVersion) + "\n",
            "resume-mismatch" => MeasuredLog(sessionId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "invalid-json" => "{not-json}\n",
            "empty-body" => MeasuredLog(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown rejection fixture."),
        };
    }

    internal static string MeasuredLog(
        string version = Version,
        int pid = Pid,
        string sessionId = SessionId,
        string promptId = PromptId,
        bool ok = true,
        bool includeQueue = true,
        bool includeDone = true,
        string sessionMessage = "session created",
        string sessionTs = "2026-10-18T22:51:27.253Z",
        string startTs = "2026-10-18T22:51:27.427Z",
        string doneTs = "2026-10-19T00:59:04.377Z")
    {
        var builder = new StringBuilder();
        builder.AppendLine(Shell(sessionTs, sessionMessage, pid, sessionId, version, new { cwd = SampleCwd }));
        if (includeQueue)
        {
            builder.AppendLine(Shell("2026-10-18T22:51:27.300Z", "shell.prompt.queued", pid, sessionId, version, new { prompt_id = "task-completed-sample-a" }));
            builder.AppendLine(Shell("2026-10-18T22:51:27.310Z", "shell.prompt.queued", pid, sessionId, version, new { prompt_id = "task-completed-sample-b" }));
        }

        builder.AppendLine(Shell(startTs, "shell.handle_prompt.start", pid, sessionId, version, new { prompt_id = promptId, block_count = 1 }));
        if (includeDone)
        {
            builder.AppendLine(DoneLine(doneTs, pid, sessionId, promptId, version, ok));
        }

        return builder.ToString();
    }

    private static string DoneLine(
        string doneTs = "2026-10-19T00:59:04.377Z",
        int pid = Pid,
        string sessionId = SessionId,
        string promptId = PromptId,
        string version = Version,
        bool ok = true)
    {
        return Shell(doneTs, "shell.handle_prompt.done", pid, sessionId, version, new { prompt_id = promptId, ok });
    }

    private static string Shell(string timestamp, string message, int pid, string sessionId, string version, object? context)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ts"] = timestamp,
            ["src"] = "shell",
            ["pid"] = pid,
            ["ver"] = version,
            ["lvl"] = "info",
            ["sid"] = sessionId,
            ["msg"] = message,
        };
        if (context is not null)
        {
            payload["ctx"] = context;
        }

        return JsonSerializer.Serialize(payload);
    }

    private static DateTimeOffset Parse(string text)
    {
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}

public class GrokPromptCorrelationTests
{
    [Fact]
    public void MeasuredFixtureBindsTheFirstPromptAndIgnoresQueueIds()
    {
        var evidence = Accept(Lines(GrokInternalCompletionTests.MeasuredLog()));
        Assert.Equal(GrokInternalCompletionTests.PromptId, evidence.PromptId);
        Assert.Equal(GrokInternalCompletionTests.SessionId, evidence.SessionId);
        Assert.Equal(GrokInternalCompletionTests.Pid, evidence.Pid);
        Assert.Equal(GrokInternalCompletionTests.Version, evidence.GrokVersion);
        Assert.DoesNotContain("task-completed", evidence.PromptId, StringComparison.Ordinal);
    }

    [Fact]
    public void PidReuseBeforeLaunchDoesNotBlockTheNewPrompt()
    {
        var oldDone = """{"ts":"2026-10-18T22:51:20.000Z","src":"shell","pid":56368,"ver":"1.0.62","lvl":"info","sid":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","msg":"shell.handle_prompt.done","ctx":{"prompt_id":"old","ok":true}}""";
        var evidence = Accept([oldDone, .. Lines(GrokInternalCompletionTests.MeasuredLog())]);
        Assert.Equal(GrokInternalCompletionTests.PromptId, evidence.PromptId);
    }

    [Fact]
    public void FalseOkDoesNotFlipWhenALaterDoneSaysTrue()
    {
        var lines = Lines(GrokInternalCompletionTests.MeasuredLog(ok: false)).ToList();
        lines.Add("""{"ts":"2026-10-19T01:00:00.000Z","src":"shell","pid":56368,"ver":"1.0.62","lvl":"info","sid":"01a15136-771a-7363-924b-9e5bb063b27b","msg":"shell.handle_prompt.done","ctx":{"prompt_id":"ca33ed71-5085-487c-98d4-360d23921419","ok":true}}""");
        var correlation = Observe(lines);
        Assert.False(correlation.IsAbandoned);
        Assert.Null(correlation.Success);
    }

    [Fact]
    public void OtherSourceAndOtherPidAreIgnored()
    {
        var other = """{"ts":"2026-10-19T00:59:04.377Z","src":"app","pid":56368,"ver":"1.0.62","lvl":"info","sid":"01a15136-771a-7363-924b-9e5bb063b27b","msg":"shell.handle_prompt.done","ctx":{"prompt_id":"ca33ed71-5085-487c-98d4-360d23921419","ok":true}}""";
        var evidence = Accept([other, .. Lines(GrokInternalCompletionTests.MeasuredLog())]);
        Assert.Equal(GrokInternalCompletionTests.PromptId, evidence.PromptId);
    }

    [Theory]
    [InlineData("start-before-session")]
    [InlineData("done-before-start")]
    [InlineData("second-session")]
    [InlineData("missing-envelope")]
    public void InconsistentRecordsAbandon(string kind)
    {
        var correlation = Observe(Inconsistent(kind));
        Assert.True(correlation.IsAbandoned);
        Assert.Null(correlation.Success);
        Assert.False(string.IsNullOrWhiteSpace(correlation.AbandonReason));
    }

    private static GrokCompletionEvidence Accept(IEnumerable<string> lines)
    {
        var correlation = Observe(lines);
        Assert.False(correlation.IsAbandoned, correlation.AbandonReason);
        Assert.NotNull(correlation.Success);
        return correlation.Success!;
    }

    private static GrokPromptCorrelation Observe(IEnumerable<string> lines, string? resume = null)
    {
        var correlation = new GrokPromptCorrelation();
        foreach (var line in lines)
        {
            correlation.Observe(line, GrokInternalCompletionTests.Pid, GrokInternalCompletionTests.Launch, resume);
        }

        return correlation;
    }

    private static IEnumerable<string> Lines(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IEnumerable<string> Inconsistent(string kind)
    {
        var start = """{"ts":"2026-10-18T22:51:27.427Z","src":"shell","pid":56368,"ver":"1.0.62","lvl":"info","sid":"01a15136-771a-7363-924b-9e5bb063b27b","msg":"shell.handle_prompt.start","ctx":{"prompt_id":"ca33ed71-5085-487c-98d4-360d23921419"}}""";
        var session = """{"ts":"2026-10-18T22:51:27.253Z","src":"shell","pid":56368,"ver":"1.0.62","lvl":"info","sid":"01a15136-771a-7363-924b-9e5bb063b27b","msg":"session created"}""";
        var done = """{"ts":"2026-10-19T00:59:04.377Z","src":"shell","pid":56368,"ver":"1.0.62","lvl":"info","sid":"01a15136-771a-7363-924b-9e5bb063b27b","msg":"shell.handle_prompt.done","ctx":{"prompt_id":"ca33ed71-5085-487c-98d4-360d23921419","ok":true}}""";
        return kind switch
        {
            "start-before-session" => [start, session],
            "done-before-start" => [session, done],
            "second-session" => [session, """{"ts":"2026-10-18T22:51:27.500Z","src":"shell","pid":56368,"ver":"1.0.62","lvl":"info","sid":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","msg":"session created"}"""],
            "missing-envelope" => ["""{"msg":"session created"}"""],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown inconsistent fixture."),
        };
    }
}

public class GrokUnifiedLogReaderTests
{
    [Fact]
    public void ReadsOnlyBytesAfterTheCursorAndKeepsAPartialLine()
    {
        var path = TempFile();
        File.WriteAllText(path, "old\n");
        var reader = new FileGrokUnifiedLogReader(path);
        var cursor = reader.CaptureCursor();
        File.AppendAllText(path, "partial");
        var pending = reader.Read(cursor);
        Assert.Empty(pending.Lines);
        File.AppendAllText(path, "-tail\nnext");
        var first = reader.Read(pending.Cursor);
        Assert.Equal(["partial-tail"], first.Lines);
        Assert.False(first.Cursor.Abandoned);
        File.AppendAllText(path, "\n");
        var second = reader.Read(first.Cursor);
        Assert.Equal(["next"], second.Lines);
        Assert.Empty(second.Cursor.Pending);
    }

    [Fact]
    public void BlankLinesAreSkippedAndCrlfIsOneLine()
    {
        var path = TempFile();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("\n\r\nhello\r\n"));
        var reader = new FileGrokUnifiedLogReader(path);
        var read = reader.Read(reader.CaptureCursorAtStart());
        Assert.Equal(["hello"], read.Lines);
    }

    [Fact]
    public void MissingReplacedAndUndecodableLogsStopCorrelation()
    {
        var path = TempFile();
        File.WriteAllText(path, "kept\n");
        var reader = new FileGrokUnifiedLogReader(path);
        var cursor = reader.CaptureCursor();
        File.Delete(path);
        var missing = reader.Read(cursor);
        Assert.True(missing.Cursor.Abandoned);
        Assert.Equal("missing", missing.Cursor.Problem);
        Assert.Empty(missing.Lines);

        File.WriteAllText(path, new string('x', 32));
        var replacedCursor = reader.CaptureCursor();
        File.WriteAllText(path, "y");
        var replaced = reader.Read(replacedCursor);
        Assert.Equal("replaced", replaced.Cursor.Problem);

        File.WriteAllText(path, "alpha\n");
        var sameLengthCursor = new FileGrokUnifiedLogReader(path).CaptureCursor();
        Assert.NotNull(sameLengthCursor.Continuity);
        File.WriteAllText(path, "bravo\n");
        var sameLength = new FileGrokUnifiedLogReader(path).Read(sameLengthCursor);
        Assert.Equal("replaced", sameLength.Cursor.Problem);
        Assert.Empty(sameLength.Lines);

        File.WriteAllText(path, "start\n");
        var regrowthCursor = new FileGrokUnifiedLogReader(path).CaptureCursor();
        File.WriteAllText(path, "other-session-start\ndone-for-old-prompt\n");
        var regrown = new FileGrokUnifiedLogReader(path).Read(regrowthCursor);
        Assert.Equal("replaced", regrown.Cursor.Problem);
        Assert.Empty(regrown.Lines);

        File.WriteAllBytes(path, [0xFF, (byte)'\n']);
        var undecodable = new FileGrokUnifiedLogReader(path).Read(GrokLogCursor.At(0));
        Assert.Equal("unreadable", undecodable.Cursor.Problem);
    }

    [Fact]
    public void LockedAndOverlongInputsStopWithoutReturningAPartialLine()
    {
        var path = TempFile();
        using (var locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var cursor = new FileGrokUnifiedLogReader(path).CaptureCursor();
            Assert.True(cursor.Abandoned);
            Assert.Equal("unreadable", cursor.Problem);
        }

        var overlong = new byte[FileGrokUnifiedLogReader.MaxPendingBytes + 1];
        Array.Fill(overlong, (byte)'a');
        File.WriteAllBytes(path, overlong);
        var read = new FileGrokUnifiedLogReader(path).Read(GrokLogCursor.At(0));
        Assert.Equal("line-too-long", read.Cursor.Problem);
        Assert.Empty(read.Lines);
    }

    private static string TempFile()
    {
        var directory = Directory.CreateTempSubdirectory("caf-grok-reader-").FullName;
        return Path.Combine(directory, "unified.jsonl");
    }
}

public class GrokForcedStopTests
{
    [Fact]
    public async Task CompletedExitAndReadsAreExited()
    {
        var outcome = await ForcedProcessStop.ExecuteAsync(
            () => { },
            Task.CompletedTask,
            Task.CompletedTask,
            Task.CompletedTask,
            new FakeTimeProvider(GrokInternalCompletionTests.Launch));
        Assert.Equal(ProcessTerminationStatus.Exited, outcome.Status);
        Assert.True(outcome.ExitObserved);
        Assert.True(outcome.OutputRecoveryCompleted);
    }

    [Fact]
    public async Task KillExceptionKeepsExitObservation()
    {
        var outcome = await ForcedProcessStop.ExecuteAsync(
            () => throw new InvalidOperationException("kill failed"),
            Task.CompletedTask,
            Task.CompletedTask,
            Task.CompletedTask,
            new FakeTimeProvider(GrokInternalCompletionTests.Launch));
        Assert.Equal(ProcessTerminationStatus.Failed, outcome.Status);
        Assert.True(outcome.ExitObserved);
        Assert.True(outcome.OutputRecoveryCompleted);
    }

    [Fact]
    public async Task BudgetLeavesExitAndPipesUnconfirmed()
    {
        var time = new FakeTimeProvider(GrokInternalCompletionTests.Launch);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = ForcedProcessStop.ExecuteAsync(() => { }, pending.Task, pending.Task, pending.Task, time);
        time.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(30);
        Assert.False(task.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        var outcome = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProcessTerminationStatus.Unconfirmed, outcome.Status);
        Assert.False(outcome.ExitObserved);
        Assert.False(outcome.OutputRecoveryCompleted);
    }

    [Fact]
    public async Task HungPipesAfterExitDoNotExtendTheBudget()
    {
        var time = new FakeTimeProvider(GrokInternalCompletionTests.Launch);
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = ForcedProcessStop.ExecuteAsync(() => { }, Task.CompletedTask, hung.Task, hung.Task, time);
        time.Advance(ProcessRunner.ForcedTerminationBudget);
        var outcome = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProcessTerminationStatus.Exited, outcome.Status);
        Assert.True(outcome.ExitObserved);
        Assert.False(outcome.OutputRecoveryCompleted);
    }

    [Fact]
    public void SnapshotDuringAppendDoesNotTearOrThrow()
    {
        var buffer = new ProcessOutputBuffer();
        var errors = 0;
        var writers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 200; index++)
            {
                try
                {
                    buffer.Append($"line-{worker}-{index}");
                }
                catch (Exception ex)
                {
                    CliJson.TraceException(ex);
                    Interlocked.Increment(ref errors);
                }
            }
        })).ToArray();
        var reader = Task.Run(() =>
        {
            for (var index = 0; index < 400; index++)
            {
                try
                {
                    foreach (var line in buffer.Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!line.StartsWith("line-", StringComparison.Ordinal) || line.Split('-').Length != 3)
                        {
                            Interlocked.Increment(ref errors);
                        }
                    }
                }
                catch (Exception ex)
                {
                    CliJson.TraceException(ex);
                    Interlocked.Increment(ref errors);
                }
            }
        });
        Task.WaitAll([.. writers, reader]);
        Assert.Equal(0, errors);
        Assert.Equal(1600, buffer.Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task FaultedExitWaitIsUnconfirmed()
    {
        var outcome = await ForcedProcessStop.ExecuteAsync(
            () => { },
            Task.FromException(new InvalidOperationException("exit failed")),
            Task.CompletedTask,
            Task.CompletedTask,
            new FakeTimeProvider(GrokInternalCompletionTests.Launch));
        Assert.Equal(ProcessTerminationStatus.Unconfirmed, outcome.Status);
        Assert.False(outcome.ExitObserved);
    }
}

public class GrokProcessStopTests
{
    [Fact]
    public async Task ClosedStreamsStillHonorTheCompletionSignal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf-grok-closed-").FullName;
        var script = Path.Combine(directory, "close-out.ps1");
        var marker = Path.Combine(directory, "closed.txt");
        await File.WriteAllTextAsync(script, """
            $bytes = [Text.Encoding]::ASCII.GetBytes("tick`n")
            $stdout = [Console]::OpenStandardOutput()
            $stdout.Write($bytes, 0, $bytes.Length)
            $stdout.Flush()
            Add-Type -TypeDefinition @"
            using System;
            using System.Runtime.InteropServices;
            public static class CafPipeClose {
              [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n);
              [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
            }
            "@
            [void][CafPipeClose]::CloseHandle([CafPipeClose]::GetStdHandle(-11))
            [void][CafPipeClose]::CloseHandle([CafPipeClose]::GetStdHandle(-12))
            Set-Content -LiteralPath $args[0] -Value closed -Encoding ascii
            Start-Sleep -Seconds 120
            """, new UTF8Encoding(false));
        var signal = new ProcessCompletionSignal();
        var pid = 0;
        var runner = new ProcessRunner(NullProcessJobGuard.Instance);
        try
        {
            var run = runner.RunAsync(
                new ProcessRunRequest(
                    script,
                    [marker],
                    directory,
                    OnProcessStarted: process => pid = process.Id,
                    Completion: signal),
                CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(marker) && !run.IsCompleted && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.True(File.Exists(marker));
            // 読取完了が終了待ちへ移るまで待つ。通知が早いと、ストリーム残存の経路しか踏めない。
            await Task.Delay(500);
            Assert.False(run.IsCompleted);
            signal.Notify();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(result.Termination);
            Assert.Contains("tick", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            KillPid(pid);
        }
    }

    [Fact]
    public async Task SignalKillsTheRemainingProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf-grok-kill-").FullName;
        var script = Path.Combine(directory, "sleep.ps1");
        await File.WriteAllTextAsync(script, """
            Write-Output 'tick'
            Start-Sleep -Seconds 60
            """, new UTF8Encoding(false));
        var signal = new ProcessCompletionSignal();
        var runner = new ProcessRunner(NullProcessJobGuard.Instance);
        var result = await runner.RunAsync(
            new ProcessRunRequest(
                script,
                [],
                directory,
                StdoutLineCallback: line =>
                {
                    if (line.Contains("tick", StringComparison.Ordinal))
                    {
                        signal.Notify();
                    }
                },
                Completion: signal),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ProcessTerminationStatus.Exited, result.Termination!.Status);
        Assert.NotNull(result.ExitCode);
        Assert.Contains("tick", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(result.Termination.OutputRecoveryCompleted);
    }

    [Fact]
    public async Task KillExceptionReturnsFailedWithoutAnExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf-grok-kill-fail-").FullName;
        var script = Path.Combine(directory, "sleep.ps1");
        await File.WriteAllTextAsync(script, """
            Write-Output 'tick'
            Start-Sleep -Seconds 120
            """, new UTF8Encoding(false));
        var signal = new ProcessCompletionSignal();
        var time = new FakeTimeProvider(GrokInternalCompletionTests.Launch);
        var pid = 0;
        var runner = new ProcessRunner(
            NullProcessJobGuard.Instance,
            SystemProcessEncodingProvider.Instance,
            time,
            _ => throw new InvalidOperationException("kill failed"));
        try
        {
            var run = runner.RunAsync(
                new ProcessRunRequest(
                    script,
                    [],
                    directory,
                    StdoutLineCallback: line =>
                    {
                        if (line.Contains("tick", StringComparison.Ordinal))
                        {
                            signal.Notify();
                        }
                    },
                    OnProcessStarted: process => pid = process.Id,
                    Completion: signal),
                CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!run.IsCompleted && DateTime.UtcNow < deadline)
            {
                time.Advance(ProcessRunner.ForcedTerminationBudget);
                await Task.Delay(20);
            }

            var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ProcessTerminationStatus.Failed, result.Termination!.Status);
            Assert.Null(result.ExitCode);
            Assert.False(result.Termination.OutputRecoveryCompleted);
        }
        finally
        {
            KillPid(pid);
        }
    }

    [Fact]
    public async Task SignalAfterNaturalExitDoesNotForceStop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf-grok-natural-").FullName;
        var script = Path.Combine(directory, "exit.ps1");
        await File.WriteAllTextAsync(script, """
            Write-Output 'done'
            exit 0
            """, new UTF8Encoding(false));
        IProcessLifetime? lifetime = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new ProcessCompletionSignal();
        var run = new ProcessRunner(NullProcessJobGuard.Instance).RunAsync(
            new ProcessRunRequest(
                script,
                [],
                directory,
                OnProcessStarted: process =>
                {
                    lifetime = process;
                    started.TrySetResult();
                },
                Completion: signal),
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (lifetime is not null && !lifetime.HasExited && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(lifetime!.HasExited);
        signal.Notify();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result.Termination);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RescueBeforeExitStillCapsOpenPipes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf-grok-rescue-first-").FullName;
        var script = Path.Combine(directory, "hold.ps1");
        await File.WriteAllTextAsync(script, """
            Write-Output 'tick'
            $pwsh = (Get-Process -Id $PID).Path
            Start-Process -FilePath $pwsh -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30' -NoNewWindow
            Start-Sleep -Seconds 30
            exit 1
            """, new UTF8Encoding(false));
        var signal = new ProcessCompletionSignal();
        var pid = 0;
        var runner = new ProcessRunner(NullProcessJobGuard.Instance);
        try
        {
            var result = await runner.RunAsync(
                new ProcessRunRequest(
                    script,
                    [],
                    directory,
                    OnProcessStarted: process =>
                    {
                        pid = process.Id;
                        signal.Notify();
                    },
                    Completion: signal),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(result.Termination);
        }
        finally
        {
            KillPid(pid);
        }
    }

    [Fact]
    public async Task ExitBeforeRescueKeepsTheNaturalNonZeroResult()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf-grok-exit-first-").FullName;
        var script = Path.Combine(directory, "hold.ps1");
        await File.WriteAllTextAsync(script, """
            Write-Output 'tick'
            Start-Sleep -Seconds 1
            $pwsh = (Get-Process -Id $PID).Path
            Start-Process -FilePath $pwsh -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 4' -NoNewWindow
            exit 1
            """, new UTF8Encoding(false));
        IProcessLifetime? lifetime = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new ProcessCompletionSignal();
        var pid = 0;
        var runner = new ProcessRunner(NullProcessJobGuard.Instance);
        try
        {
            var run = runner.RunAsync(
                new ProcessRunRequest(
                    script,
                    [],
                    directory,
                    OnProcessStarted: process =>
                    {
                        lifetime = process;
                        pid = process.Id;
                        started.TrySetResult();
                    },
                    Completion: signal),
                CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (lifetime is not null && !lifetime.HasExited && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.True(lifetime!.HasExited);
            // ExitTime の分解能より後に通知し、終了が先だったことを残す。
            await Task.Delay(TimeSpan.FromSeconds(2));
            signal.Notify();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(result.Termination);
            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            KillPid(pid);
        }
    }

    private static void KillPid(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }
    }
}

internal sealed class GrokRescueHarness : IDisposable
{
    private readonly string _ownedRoot;

    public GrokRescueHarness(string? existingLogPath = null)
    {
        if (existingLogPath is null)
        {
            _ownedRoot = Directory.CreateTempSubdirectory("caf-grok-rescue-").FullName;
            LogPath = Path.Combine(_ownedRoot, "unified.jsonl");
            File.WriteAllText(LogPath, string.Empty);
        }
        else
        {
            LogPath = existingLogPath;
            _ownedRoot = string.Empty;
        }

        Root = string.IsNullOrEmpty(_ownedRoot) ? Path.GetDirectoryName(LogPath)! : _ownedRoot;
        Time = new FakeTimeProvider(GrokInternalCompletionTests.Launch);
        Runner = new RecordingProcessRunner
        {
            HoldForCompletion = true,
            CompletionResult = new ProcessRunResult(
                null,
                string.Empty,
                string.Empty,
                new ProcessTerminationInfo(ProcessTerminationStatus.Unconfirmed, false)),
        };
        Runner.ProcessLifetime.Id = GrokInternalCompletionTests.Pid;
        Runner.StartedAtClock = () => Time.GetUtcNow();
        Log = new CountingGrokLogReader(LogPath);
        Driver = new GrokBuildDriver(Runner, Time, Log);
    }

    public string Root { get; }

    public string LogPath { get; }

    public FakeTimeProvider Time { get; }

    public RecordingProcessRunner Runner { get; }

    public CountingGrokLogReader Log { get; }

    public GrokBuildDriver Driver { get; }

    public AgentFacade CreateFacade()
    {
        return new AgentFacade(
            new GitHubCopilotDriver(Runner),
            Driver,
            new CursorCliDriver(Runner),
            new CodexCliDriver(Runner),
            TestRunLogs.CreateFactory(Time));
    }

    public Task<AgentRunResult> Start(AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        return Driver.RunAsync(request, TestRunLogs.CreateLog(Time), null, cancellationToken);
    }

    public async Task StartedAsync()
    {
        await Runner.StartedTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void EmitReport()
    {
        Runner.EmitStdout("""{"type":"text","data":"re"}""");
        Runner.EmitStdout("""{"type":"text","data":"port"}""");
    }

    public void Append(string text)
    {
        File.AppendAllText(LogPath, text);
    }

    public async Task<AgentRunResult> RescueAsync(AgentRunRequest request, string log)
    {
        var run = Start(request);
        await StartedAsync();
        EmitReport();
        Append(log);
        Time.Advance(GrokBuildDriver.IdleBeforeLogCheck);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(2)));
        if (finished != run)
        {
            Time.Advance(GrokBuildDriver.LogRecheckInterval);
        }

        return await AwaitRunAsync(run);
    }

    public async Task AdvanceUntilReadAsync(Task run, int minimumReads)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Log.Reads < minimumReads && !run.IsCompleted && DateTime.UtcNow < deadline)
        {
            Time.Advance(GrokBuildDriver.IdleBeforeLogCheck);
            await Task.Delay(20);
        }

        if (Log.Reads < minimumReads)
        {
            throw new TimeoutException("The completion watch did not read the internal log. reads=" + Log.Reads);
        }
    }

    public async Task<AgentRunResult> AwaitRunAsync(Task<AgentRunResult> run)
    {
        try
        {
            return await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("Grok run did not finish. reads=" + Log.Reads, ex);
        }
    }

    public void Dispose()
    {
        if (string.IsNullOrEmpty(_ownedRoot))
        {
            return;
        }

        try
        {
            Directory.Delete(_ownedRoot, recursive: true);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }
    }
}

internal sealed class CountingGrokLogReader : IGrokUnifiedLogReader
{
    private readonly FileGrokUnifiedLogReader _inner;
    private int _reads;

    public CountingGrokLogReader(string path)
    {
        _inner = new FileGrokUnifiedLogReader(path);
    }

    public int Reads => Volatile.Read(ref _reads);

    public GrokLogCursor CaptureCursor()
    {
        return _inner.CaptureCursor();
    }

    public GrokLogRead Read(GrokLogCursor cursor)
    {
        Interlocked.Increment(ref _reads);
        return _inner.Read(cursor);
    }
}

internal sealed class ThrowingGrokLogReader : IGrokUnifiedLogReader
{
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public GrokLogCursor CaptureCursor()
    {
        return GrokLogCursor.At(0);
    }

    public GrokLogRead Read(GrokLogCursor cursor)
    {
        Interlocked.Increment(ref _reads);
        throw new IOException("simulated unreadable Grok log");
    }
}

internal static class GrokLogCursorStart
{
    public static GrokLogCursor CaptureCursorAtStart(this FileGrokUnifiedLogReader reader)
    {
        _ = reader;
        return GrokLogCursor.At(0);
    }
}
