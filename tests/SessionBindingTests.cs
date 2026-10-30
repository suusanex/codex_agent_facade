using System.Text.Json;
using Xunit;

[Collection("global-facade-logger")]
public class SessionBindingTests
{
    private const string Allocated = "11111111-1111-1111-1111-111111111111";
    private const string OtherId = "33333333-3333-3333-3333-333333333333";
    private const string ResumeId = "22222222-2222-2222-2222-222222222222";
    private const string CursorA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string CursorB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string PlanA = "01a16d09-d03d-7d92-b104-cb45cc1d0650";
    private const string PlanB = "01a16d09-d03d-7d92-b104-cb45cc1d0651";

    [Fact]
    public async Task BindSessionIsIdempotentAndADifferentIdFails()
    {
        await using var log = TestRunLogs.CreateLog();
        log.BindSession("thread-1", SessionBinding.Preallocated);
        log.BindSession("thread-1", SessionBinding.EarlyStream);
        RequireSingleBound(log.EventsPath, "thread-1", SessionBinding.Preallocated);

        var failure = Assert.Throws<InvalidOperationException>(() => log.BindSession("thread-2", SessionBinding.Resume));
        Assert.Equal("session_mismatch", failure.Data[CliJson.FailureKindKey]);
        Assert.Equal("thread-1", log.BoundSessionId);
        RequireSingleBound(log.EventsPath, "thread-1", SessionBinding.Preallocated);
        AssertNoTerminal(log.EventsPath);

        log.WriteCancelled();
        Assert.Equal("thread-1", log.BoundSessionId);
        Assert.Contains(ReadEvents(log.EventsPath), item => item.Type == "cancelled");
        Assert.DoesNotContain(ReadEvents(log.EventsPath), item => item.Type == "completed");
    }

    [Fact]
    public async Task FailedRunKeepsTheBoundSession()
    {
        await using var log = TestRunLogs.CreateLog();
        log.BindSession("kept", SessionBinding.EarlyStream);
        log.WriteFailed(new InvalidOperationException("boom"));
        Assert.Equal("kept", log.BoundSessionId);
        var types = ReadEvents(log.EventsPath).Select(item => item.Type).ToList();
        Assert.Contains("session_bound", types);
        Assert.Contains("failed", types);
        Assert.DoesNotContain("completed", types);
    }

    [Fact]
    public void EmptySessionIdIsRejected()
    {
        using var log = TestRunLogs.CreateLog();
        Assert.Throws<ArgumentException>(() => log.BindSession(" ", SessionBinding.Preallocated));
        Assert.Throws<ArgumentException>(() => log.BindSession("id", " "));
        Assert.Null(log.BoundSessionId);
    }

    [Fact]
    public void NullLogDoesNotStoreASharedSessionId()
    {
        Assert.Null(NullAgentRunLog.Instance.BoundSessionId);
        NullAgentRunLog.Instance.BindSession("first", SessionBinding.Resume);
        NullAgentRunLog.Instance.BindSession("second", SessionBinding.EarlyStream);
        Assert.Null(NullAgentRunLog.Instance.BoundSessionId);
        Assert.Throws<ArgumentException>(() => NullAgentRunLog.Instance.BindSession(" ", SessionBinding.Resume));
    }

    [Theory]
    [InlineData(AgentFacade.GrokBuildAgent)]
    [InlineData(AgentFacade.GitHubCopilotAgent)]
    public async Task PreallocatedIdIsFlushedBeforeCompletion(string agent)
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, SuccessStdout(agent, OtherId), "");
        var result = await harness.RunGated(async () =>
        {
            var sessionId = TestRunLogs.RequiredArgument(harness.Runner.LastRequest!.Arguments, "--session-id");
            Assert.True(Guid.TryParseExact(sessionId, "D", out _));
            Assert.DoesNotContain("--resume", harness.Runner.LastRequest.Arguments);
            AssertStartedSession(harness.EventsPath(), null);
            RequireSingleBound(harness.EventsPath(), sessionId, SessionBinding.Preallocated);
            AssertNoTerminal(harness.EventsPath());
            return sessionId;
        }, Request(agent));

        Assert.NotNull(result.Result);
        Assert.Equal(result.Observed, result.Result.SessionId);
        var events = ReadEvents(harness.EventsPath());
        Assert.Single(events, item => item.Type == "session_bound");
        Assert.True(IndexOf(events, "session_bound") < IndexOf(events, "completed"));
    }

    [Theory]
    [InlineData(AgentFacade.GrokBuildAgent)]
    [InlineData(AgentFacade.GitHubCopilotAgent)]
    public async Task PreallocatedIdSurvivesProcessStartFailure(string agent)
    {
        using var harness = new Harness();
        harness.Runner.ExceptionToThrow = new ProcessStartException("down", new InvalidOperationException("down"));
        var failure = await Assert.ThrowsAsync<ProcessStartException>(() => harness.Facade.RunAsync(
            Request(agent),
            null,
            CancellationToken.None));
        Assert.Equal("process_start_failed", failure.Data[CliJson.FailureKindKey]);
        var path = Assert.IsType<string>(failure.Data[CliJson.FailureEventsLogPathKey]);
        var sessionId = TestRunLogs.RequiredArgument(harness.Runner.LastRequest!.Arguments, "--session-id");
        Assert.True(Guid.TryParseExact(sessionId, "D", out _));
        RequireSingleBound(path, sessionId, SessionBinding.Preallocated);
        var types = ReadEvents(path).Select(item => item.Type).ToList();
        Assert.Contains("failed", types);
        Assert.DoesNotContain("completed", types);
    }

    [Theory]
    [InlineData(AgentFacade.GrokBuildAgent)]
    [InlineData(AgentFacade.GitHubCopilotAgent)]
    public async Task CancelAfterPreallocationKeepsTheBoundId(string agent)
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        string? sessionId = null;
        await harness.RunGated(async () =>
        {
            sessionId = TestRunLogs.RequiredArgument(harness.Runner.LastRequest!.Arguments, "--session-id");
            RequireSingleBound(harness.EventsPath(), sessionId, SessionBinding.Preallocated);
            await cancellation.CancelAsync();
            return sessionId;
        }, Request(agent), cancellation.Token);

        var events = ReadEvents(harness.EventsPath());
        Assert.Equal(sessionId, Assert.Single(events, item => item.Type == "session_bound").SessionId);
        Assert.Contains(events, item => item.Type == "cancelled");
        Assert.DoesNotContain(events, item => item.Type == "completed");
    }

    [Theory]
    [InlineData(AgentFacade.GrokBuildAgent)]
    [InlineData(AgentFacade.GitHubCopilotAgent)]
    public async Task ResumeUsesTheRequestedIdAndDoesNotAllocate(string agent)
    {
        var calls = 0;
        using var harness = new Harness(() =>
        {
            calls++;
            return Allocated;
        });
        harness.Runner.Result = new ProcessRunResult(0, SuccessStdout(agent, ResumeId), "");
        var result = await harness.Facade.RunAsync(Request(agent, ResumeId), null, CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(ResumeId, result.SessionId);
        Assert.Equal(ResumeId, TestRunLogs.RequiredArgument(harness.Runner.LastRequest!.Arguments, "--resume"));
        Assert.DoesNotContain("--session-id", harness.Runner.LastRequest.Arguments);
        AssertStartedSession(harness.EventsPath(), ResumeId);
        RequireSingleBound(harness.EventsPath(), ResumeId, SessionBinding.Resume);
    }

    [Theory]
    [InlineData(AgentFacade.GrokBuildAgent)]
    [InlineData(AgentFacade.GitHubCopilotAgent)]
    public async Task ResumeWithoutAProviderIdKeepsTheRequestedId(string agent)
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, SuccessStdout(agent, sessionId: null), "");
        var result = await harness.Facade.RunAsync(Request(agent, ResumeId), null, CancellationToken.None);
        Assert.Equal(ResumeId, result.SessionId);
        RequireSingleBound(harness.EventsPath(), ResumeId, SessionBinding.Resume);
    }

    [Theory]
    [InlineData(AgentFacade.GrokBuildAgent)]
    [InlineData(AgentFacade.GitHubCopilotAgent)]
    public async Task DifferentProviderIdDoesNotReplaceThePreallocatedId(string agent)
    {
        using var harness = new Harness(() => Allocated);
        harness.Runner.HonorPreallocatedSessionId = false;
        harness.Runner.Result = new ProcessRunResult(0, ConflictingStdout(agent), "");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Facade.RunAsync(
            Request(agent),
            null,
            CancellationToken.None));
        Assert.Equal("session_mismatch", failure.Data[CliJson.FailureKindKey]);
        var path = Assert.IsType<string>(failure.Data[CliJson.FailureEventsLogPathKey]);
        RequireSingleBound(path, Allocated, SessionBinding.Preallocated);
        Assert.DoesNotContain(OtherId, ReadEvents(path).Select(item => item.SessionId));
    }

    [Theory]
    [InlineData(AgentFacade.GrokBuildAgent)]
    [InlineData(AgentFacade.GitHubCopilotAgent)]
    public async Task EmptyAllocationFailsBeforeBind(string agent)
    {
        using var harness = new Harness(() => " ");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Facade.RunAsync(
            Request(agent),
            null,
            CancellationToken.None));
        Assert.Contains("empty", failure.Message, StringComparison.OrdinalIgnoreCase);
        var path = Assert.IsType<string>(failure.Data[CliJson.FailureEventsLogPathKey]);
        Assert.DoesNotContain(ReadEvents(path), item => item.Type == "session_bound");
    }

    [Fact]
    public async Task CursorBindsInitBeforeATerminalResult()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, CursorResult(CursorA), "");
        var result = await harness.RunGated(async () =>
        {
            Assert.DoesNotContain(ReadEvents(harness.EventsPath()), item => item.Type == "session_bound");
            harness.Runner.EmitStdout(CursorInit(CursorA));
            RequireSingleBound(harness.EventsPath(), CursorA, SessionBinding.EarlyStream);
            AssertNoTerminal(harness.EventsPath());
            return CursorA;
        }, Request(AgentFacade.CursorAgent));

        Assert.NotNull(result.Result);
        Assert.Equal(CursorA, result.Result.SessionId);
        var events = ReadEvents(harness.EventsPath());
        Assert.Single(events, item => item.Type == "session_bound");
        Assert.True(IndexOf(events, "session_bound") < IndexOf(events, "completed"));
    }

    [Fact]
    public async Task CursorCancelAfterInitKeepsTheBoundIdWithoutAResult()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        await harness.RunGated(async () =>
        {
            harness.Runner.EmitStdout(CursorInit(CursorA));
            RequireSingleBound(harness.EventsPath(), CursorA, SessionBinding.EarlyStream);
            AssertNoTerminal(harness.EventsPath());
            await cancellation.CancelAsync();
            return CursorA;
        }, Request(AgentFacade.CursorAgent), cancellation.Token);

        var events = ReadEvents(harness.EventsPath());
        Assert.Equal(CursorA, Assert.Single(events, item => item.Type == "session_bound").SessionId);
        Assert.Contains(events, item => item.Type == "cancelled");
        Assert.DoesNotContain(events, item => item.Type == "completed");
    }

    [Fact]
    public async Task CursorRejectsADifferentFinalSessionId()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, CursorInit(CursorA) + "\n" + CursorResult(CursorB), "");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Facade.RunAsync(
            Request(AgentFacade.CursorAgent),
            null,
            CancellationToken.None));
        Assert.Equal("session_mismatch", failure.Data[CliJson.FailureKindKey]);
        var path = Assert.IsType<string>(failure.Data[CliJson.FailureEventsLogPathKey]);
        RequireSingleBound(path, CursorA, SessionBinding.EarlyStream);
    }

    [Fact]
    public async Task CursorResumeBindsTheRequestedIdBeforeTheProcessStarts()
    {
        using var harness = new Harness();
        harness.Runner.Gate = NewGate();
        harness.Runner.Result = new ProcessRunResult(0, CursorInit(CursorA) + "\n" + CursorResult(CursorA), "");
        var task = harness.Facade.RunAsync(Request(AgentFacade.CursorAgent, CursorA), null, CancellationToken.None);
        try
        {
            await harness.Runner.StartedTask.WaitAsync(TimeSpan.FromSeconds(10));
            RequireSingleBound(harness.EventsPath(), CursorA, SessionBinding.Resume);
            AssertNoTerminal(harness.EventsPath());
            Assert.Equal(CursorA, TestRunLogs.RequiredArgument(harness.Runner.LastRequest!.Arguments, "--resume"));
            Assert.DoesNotContain("--session-id", harness.Runner.LastRequest.Arguments);
        }
        finally
        {
            harness.Runner.Gate.TrySetResult(true);
        }

        var result = await task;
        Assert.Equal(CursorA, result.SessionId);
        RequireSingleBound(harness.EventsPath(), CursorA, SessionBinding.Resume);
    }

    [Fact]
    public async Task CursorResumeWithoutAProviderIdKeepsTheRequestedId()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(
            0,
            """{"type":"result","subtype":"success","result":"pong"}""",
            "");
        var result = await harness.Facade.RunAsync(Request(AgentFacade.CursorAgent, CursorA), null, CancellationToken.None);
        Assert.Equal(CursorA, result.SessionId);
        RequireSingleBound(harness.EventsPath(), CursorA, SessionBinding.Resume);
    }

    [Fact]
    public async Task CodexBindsThreadStartedBeforeTurnCompleted()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, CodexDone(), "");
        var result = await harness.RunGated(async () =>
        {
            harness.Runner.EmitStdout(CodexStarted("thread-1"));
            RequireSingleBound(harness.EventsPath(), "thread-1", SessionBinding.EarlyStream);
            AssertNoTerminal(harness.EventsPath());
            return "thread-1";
        }, Request(AgentFacade.CodexAgent));

        Assert.NotNull(result.Result);
        Assert.Equal("thread-1", result.Result.SessionId);
        var events = ReadEvents(harness.EventsPath());
        Assert.Single(events, item => item.Type == "session_bound");
        Assert.True(IndexOf(events, "session_bound") < IndexOf(events, "completed"));
    }

    [Fact]
    public async Task CodexCancelAfterThreadStartedKeepsTheBoundId()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        await harness.RunGated(async () =>
        {
            harness.Runner.EmitStdout(CodexStarted("thread-1"));
            RequireSingleBound(harness.EventsPath(), "thread-1", SessionBinding.EarlyStream);
            AssertNoTerminal(harness.EventsPath());
            await cancellation.CancelAsync();
            return "thread-1";
        }, Request(AgentFacade.CodexAgent), cancellation.Token);

        var events = ReadEvents(harness.EventsPath());
        Assert.Equal("thread-1", Assert.Single(events, item => item.Type == "session_bound").SessionId);
        Assert.Contains(events, item => item.Type == "cancelled");
        Assert.DoesNotContain(events, item => item.Type == "completed");
    }

    [Fact]
    public async Task CodexRejectsADifferentFinalThreadId()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, CodexStarted("thread-1") + "\n" + CodexDone("thread-2"), "");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Facade.RunAsync(
            Request(AgentFacade.CodexAgent),
            null,
            CancellationToken.None));
        Assert.Equal("session_mismatch", failure.Data[CliJson.FailureKindKey]);
        var path = Assert.IsType<string>(failure.Data[CliJson.FailureEventsLogPathKey]);
        RequireSingleBound(path, "thread-1", SessionBinding.EarlyStream);
    }

    [Fact]
    public async Task CodexResumeBindsTheRequestedIdAndChecksTheProviderId()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, CodexStarted("thread-1") + "\n" + CodexDone(), "");
        var result = await harness.Facade.RunAsync(Request(AgentFacade.CodexAgent, "thread-1"), null, CancellationToken.None);
        Assert.Equal("thread-1", result.SessionId);
        Assert.Contains("resume", harness.Runner.LastRequest!.Arguments);
        Assert.Contains("thread-1", harness.Runner.LastRequest.Arguments);
        RequireSingleBound(harness.EventsPath(), "thread-1", SessionBinding.Resume);
    }

    [Fact]
    public async Task CodexResumeWithoutAProviderIdKeepsTheRequestedId()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, CodexDone(), "");
        var result = await harness.Facade.RunAsync(Request(AgentFacade.CodexAgent, "thread-1"), null, CancellationToken.None);
        Assert.Equal("thread-1", result.SessionId);
        RequireSingleBound(harness.EventsPath(), "thread-1", SessionBinding.Resume);
    }

    [Fact]
    public async Task CodexNewRunWithoutAThreadIdFails()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, CodexDone(), "");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Facade.RunAsync(
            Request(AgentFacade.CodexAgent),
            null,
            CancellationToken.None));
        Assert.Contains("session id", failure.Message, StringComparison.OrdinalIgnoreCase);
        var path = Assert.IsType<string>(failure.Data[CliJson.FailureEventsLogPathKey]);
        Assert.DoesNotContain(ReadEvents(path), item => item.Type == "session_bound");
    }

    [Fact]
    public async Task AntigravityBindsInitBeforeAResult()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, AntigravityResult("conv-1"), "");
        var result = await harness.RunGated(async () =>
        {
            harness.Runner.EmitStdout(AntigravityInit("conv-1"));
            RequireSingleBound(harness.EventsPath(), "conv-1", SessionBinding.EarlyStream);
            AssertNoTerminal(harness.EventsPath());
            return "conv-1";
        }, Request(AgentFacade.AntigravityAgent));

        Assert.NotNull(result.Result);
        Assert.Equal("conv-1", result.Result.SessionId);
        var events = ReadEvents(harness.EventsPath());
        Assert.Single(events, item => item.Type == "session_bound");
        Assert.True(IndexOf(events, "session_bound") < IndexOf(events, "completed"));
    }

    [Fact]
    public async Task AntigravityCancelAfterInitKeepsTheBoundIdWithoutAResult()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        await harness.RunGated(async () =>
        {
            harness.Runner.EmitStdout(AntigravityInit("conv-1"));
            RequireSingleBound(harness.EventsPath(), "conv-1", SessionBinding.EarlyStream);
            AssertNoTerminal(harness.EventsPath());
            await cancellation.CancelAsync();
            return "conv-1";
        }, Request(AgentFacade.AntigravityAgent), cancellation.Token);

        var events = ReadEvents(harness.EventsPath());
        Assert.Equal("conv-1", Assert.Single(events, item => item.Type == "session_bound").SessionId);
        Assert.Contains(events, item => item.Type == "cancelled");
        Assert.DoesNotContain(events, item => item.Type == "completed");
    }

    [Fact]
    public async Task AntigravityRejectsADifferentFinalConversationId()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, AntigravityInit("conv-1") + "\n" + AntigravityResult("conv-2"), "");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Facade.RunAsync(
            Request(AgentFacade.AntigravityAgent),
            null,
            CancellationToken.None));
        Assert.Equal("session_mismatch", failure.Data[CliJson.FailureKindKey]);
        var path = Assert.IsType<string>(failure.Data[CliJson.FailureEventsLogPathKey]);
        RequireSingleBound(path, "conv-1", SessionBinding.EarlyStream);
    }

    [Fact]
    public async Task AntigravityResumeBindsTheTrimmedIdBeforeTheProcessStarts()
    {
        using var harness = new Harness();
        harness.Runner.Gate = NewGate();
        harness.Runner.Result = new ProcessRunResult(0, AntigravityInit("conv-1") + "\n" + AntigravityResult("conv-1"), "");
        var task = harness.Facade.RunAsync(Request(AgentFacade.AntigravityAgent, " conv-1 "), null, CancellationToken.None);
        try
        {
            await harness.Runner.StartedTask.WaitAsync(TimeSpan.FromSeconds(10));
            RequireSingleBound(harness.EventsPath(), "conv-1", SessionBinding.Resume);
            AssertNoTerminal(harness.EventsPath());
            Assert.Equal("conv-1", TestRunLogs.RequiredArgument(harness.Runner.LastRequest!.Arguments, "--conversation"));
        }
        finally
        {
            harness.Runner.Gate.TrySetResult(true);
        }

        var result = await task;
        Assert.Equal("conv-1", result.SessionId);
        RequireSingleBound(harness.EventsPath(), "conv-1", SessionBinding.Resume);
    }

    [Fact]
    public async Task AntigravityResumeWithoutAProviderIdKeepsTheRequestedId()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, AntigravityInit(null) + "\n" + AntigravityResult(null), "");
        var result = await harness.Facade.RunAsync(Request(AgentFacade.AntigravityAgent, "conv-1"), null, CancellationToken.None);
        Assert.Equal("conv-1", result.SessionId);
        RequireSingleBound(harness.EventsPath(), "conv-1", SessionBinding.Resume);
    }

    [Fact]
    public async Task AntigravityNewRunWithoutAConversationIdFails()
    {
        using var harness = new Harness();
        harness.Runner.Result = new ProcessRunResult(0, AntigravityInit(null) + "\n" + AntigravityResult(null), "");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Facade.RunAsync(
            Request(AgentFacade.AntigravityAgent),
            null,
            CancellationToken.None));
        Assert.Contains("conversation id", failure.Message, StringComparison.OrdinalIgnoreCase);
        var path = Assert.IsType<string>(failure.Data[CliJson.FailureEventsLogPathKey]);
        Assert.DoesNotContain(ReadEvents(path), item => item.Type == "session_bound");
    }

    [Fact]
    public async Task GrokPlanBindsTheSessionReturnedBySessionNew()
    {
        using var workspace = new Workspace();
        var factory = new ScriptedGrokAcpFactory();
        var driver = PlanDriver(factory);
        await using var log = TestRunLogs.CreateLog();
        var result = await driver.RunAsync(PlanRequest(workspace.Path, null), log, null, CancellationToken.None);
        Assert.Equal(PlanA, result.SessionId);
        Assert.DoesNotContain("--session-id", Assert.Single(factory.Starts).Arguments);
        RequireSingleBound(log.EventsPath, PlanA, SessionBinding.EarlyStream);
    }

    [Fact]
    public async Task GrokPlanKeepsTheNewSessionWhenThePromptFails()
    {
        using var workspace = new Workspace();
        var factory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { ErrorOn = "session/prompt" },
        };
        await using var log = TestRunLogs.CreateLog();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PlanDriver(factory).RunAsync(
            PlanRequest(workspace.Path, null),
            log,
            null,
            CancellationToken.None));
        Assert.Equal(PlanA, log.BoundSessionId);
        RequireSingleBound(log.EventsPath, PlanA, SessionBinding.EarlyStream);
        AssertNoTerminal(log.EventsPath);
    }

    [Fact]
    public async Task GrokPlanResumeBindsBeforeTheProcessStarts()
    {
        using var workspace = new Workspace();
        var factory = new ThrowingGrokAcpFactory();
        await using var log = TestRunLogs.CreateLog();
        var failure = await Assert.ThrowsAsync<ProcessStartException>(() => PlanDriver(factory).RunAsync(
            PlanRequest(workspace.Path, PlanA),
            log,
            null,
            CancellationToken.None));
        Assert.Equal("process_start_failed", failure.Data[CliJson.FailureKindKey]);
        Assert.Equal(1, factory.Starts);
        RequireSingleBound(log.EventsPath, PlanA, SessionBinding.Resume);
        AssertNoTerminal(log.EventsPath);
        Assert.DoesNotContain("--session-id", TestRunLogs.ReadShared(log.EventsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrokPlanResumeBindsAnUnsafeIdBeforeRejectingIt()
    {
        using var workspace = new Workspace();
        var factory = new ScriptedGrokAcpFactory();
        await using var log = TestRunLogs.CreateLog();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => PlanDriver(factory).RunAsync(
            PlanRequest(workspace.Path, "bad/id"),
            log,
            null,
            CancellationToken.None));
        Assert.Equal("internal_error", failure.Data[CliJson.FailureKindKey]);
        Assert.Empty(factory.Starts);
        RequireSingleBound(log.EventsPath, "bad/id", SessionBinding.Resume);
    }

    [Fact]
    public async Task GrokPlanResumeRejectsADifferentLoadedSession()
    {
        using var workspace = new Workspace();
        var factory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { LoadSessionId = PlanB },
        };
        await using var log = TestRunLogs.CreateLog();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => PlanDriver(factory).RunAsync(
            PlanRequest(workspace.Path, PlanA),
            log,
            null,
            CancellationToken.None));
        Assert.Equal("session_mismatch", failure.Data[CliJson.FailureKindKey]);
        Assert.Equal(PlanA, log.BoundSessionId);
        RequireSingleBound(log.EventsPath, PlanA, SessionBinding.Resume);
    }

    [Fact]
    public async Task CancelledNewGrokPlanDoesNotInventASessionId()
    {
        using var workspace = new Workspace();
        var factory = new ScriptedGrokAcpFactory();
        await using var log = TestRunLogs.CreateLog();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlanDriver(factory).RunAsync(
            PlanRequest(workspace.Path, null),
            log,
            null,
            cancellation.Token));
        Assert.Empty(factory.Starts);
        Assert.DoesNotContain(ReadEvents(log.EventsPath), item => item.Type == "session_bound");
    }

    private static GrokBuildDriver PlanDriver(IGrokAcpProcessFactory factory)
    {
        return new GrokBuildDriver(
            new RecordingProcessRunner(),
            TimeProvider.System,
            new ThrowingGrokLogReader(),
            factory);
    }

    private static AgentRunRequest Request(string agent, string? sessionId = null)
    {
        return new AgentRunRequest(agent, "hello", Path.GetTempPath(), sessionId, null);
    }

    private static AgentRunRequest PlanRequest(string directory, string? sessionId)
    {
        return new AgentRunRequest(AgentFacade.GrokBuildAgent, "hello", directory, sessionId, null, RunMode: "plan");
    }

    private static string SuccessStdout(string agent, string? sessionId)
    {
        if (agent == AgentFacade.GrokBuildAgent)
        {
            var session = sessionId is null ? string.Empty : ",\"sessionId\":\"" + sessionId + "\"";
            return "{\"type\":\"text\",\"data\":\"pong\"}\n{\"type\":\"end\",\"stopReason\":\"end_turn\"" + session + "}";
        }

        var sessionField = sessionId is null ? string.Empty : ",\"sessionId\":\"" + sessionId + "\"";
        return "{\"type\":\"assistant.message\",\"data\":{\"content\":\"pong\"}}\n{\"type\":\"result\"" + sessionField + "}";
    }

    private static string ConflictingStdout(string agent)
    {
        if (agent == AgentFacade.GrokBuildAgent)
        {
            return "{\"type\":\"text\",\"data\":\"pong\",\"sessionId\":\"" + Allocated + "\"}\n"
                + "{\"type\":\"end\",\"sessionId\":\"" + OtherId + "\",\"stopReason\":\"end_turn\"}";
        }

        return "{\"type\":\"assistant.message\",\"data\":{\"content\":\"pong\"},\"sessionId\":\"" + Allocated + "\"}\n"
            + "{\"type\":\"result\",\"sessionId\":\"" + OtherId + "\"}";
    }

    private static string CursorInit(string sessionId)
    {
        return "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + sessionId + "\"}";
    }

    private static string CursorResult(string sessionId)
    {
        return "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"pong\",\"session_id\":\"" + sessionId + "\"}";
    }

    private static string CodexStarted(string threadId)
    {
        return "{\"type\":\"thread.started\",\"thread_id\":\"" + threadId + "\"}";
    }

    private static string CodexDone(string? threadId = null)
    {
        var turn = threadId is null
            ? "{\"type\":\"turn.completed\"}"
            : "{\"type\":\"turn.completed\",\"thread_id\":\"" + threadId + "\"}";
        return "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"pong\"}}\n" + turn;
    }

    private static string AntigravityInit(string? conversationId)
    {
        return conversationId is null
            ? "{\"event\":\"init\",\"init\":{}}"
            : "{\"event\":\"init\",\"conversation_id\":\"" + conversationId + "\",\"init\":{}}";
    }

    private static string AntigravityResult(string? conversationId)
    {
        var id = conversationId is null ? string.Empty : "\"conversation_id\":\"" + conversationId + "\",";
        return "{\"event\":\"result\",\"result\":{" + id + "\"status\":\"SUCCESS\",\"response\":\"pong\"}}";
    }

    private static TaskCompletionSource<bool> NewGate()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static List<RunEvent> ReadEvents(string path)
    {
        var events = new List<RunEvent>();
        var text = TestRunLogs.ReadShared(path);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            string? sessionId = null;
            string? bindingSource = null;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String)
                {
                    sessionId = session.GetString();
                }

                if (data.TryGetProperty("bindingSource", out var binding) && binding.ValueKind == JsonValueKind.String)
                {
                    bindingSource = binding.GetString();
                }
            }

            events.Add(new RunEvent(root.GetProperty("type").GetString() ?? string.Empty, sessionId, bindingSource));
        }

        return events;
    }

    private static void RequireSingleBound(string path, string sessionId, string bindingSource)
    {
        var bound = Assert.Single(ReadEvents(path), item => item.Type == "session_bound");
        Assert.Equal(sessionId, bound.SessionId);
        Assert.Equal(bindingSource, bound.BindingSource);
    }

    private static void AssertNoTerminal(string path)
    {
        var types = ReadEvents(path).Select(item => item.Type).ToList();
        Assert.DoesNotContain("completed", types);
        Assert.DoesNotContain("failed", types);
        Assert.DoesNotContain("cancelled", types);
    }

    private static void AssertStartedSession(string path, string? sessionId)
    {
        var started = Assert.Single(ReadEvents(path), item => item.Type == "started");
        Assert.Equal(sessionId, started.SessionId);
    }

    private static int IndexOf(List<RunEvent> events, string type)
    {
        var index = events.FindIndex(item => item.Type == type);
        Assert.True(index >= 0);
        return index;
    }

    private sealed record RunEvent(string Type, string? SessionId, string? BindingSource);

    private sealed class Harness : IDisposable
    {
        private readonly string _directory;

        public Harness(Func<string>? allocateSessionId = null)
        {
            _directory = Directory.CreateTempSubdirectory("caf-bind-").FullName;
            Runner = new RecordingProcessRunner();
            Facade = new AgentFacade(
                new GitHubCopilotDriver(Runner, allocateSessionId),
                new GrokBuildDriver(Runner, TimeProvider.System, new ThrowingGrokLogReader(), allocateSessionId: allocateSessionId),
                new CursorCliDriver(Runner),
                new CodexCliDriver(Runner),
                new AgentRunLogFactory(_directory, TimeProvider.System),
                antigravityCli: new AntigravityCliDriver(Runner));
        }

        public RecordingProcessRunner Runner { get; }

        public AgentFacade Facade { get; }

        public string EventsPath()
        {
            return Assert.Single(Directory.GetFiles(_directory, "*.events.jsonl"));
        }

        public async Task<GatedResult> RunGated(
            Func<Task<string>> whileRunning,
            AgentRunRequest request,
            CancellationToken cancellationToken = default)
        {
            Runner.Gate = NewGate();
            var task = Facade.RunAsync(request, null, cancellationToken);
            try
            {
                await Runner.StartedTask.WaitAsync(TimeSpan.FromSeconds(10));
                var observed = await whileRunning();
                if (cancellationToken.IsCancellationRequested)
                {
                    Runner.Gate.TrySetCanceled(cancellationToken);
                }
                else
                {
                    Runner.Gate.TrySetResult(true);
                }

                try
                {
                    return new GatedResult(await task.WaitAsync(TimeSpan.FromSeconds(10)), observed);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new GatedResult(null, observed);
                }
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Runner.Gate.TrySetCanceled(cancellationToken);
                }
                else
                {
                    Runner.Gate.TrySetResult(true);
                }

                if (!task.IsCompleted)
                {
                    try
                    {
                        await task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch (Exception ex)
                    {
                        CliJson.TraceException(ex);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }

    private sealed record GatedResult(AgentRunResult? Result, string Observed);

    private sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Path = Directory.CreateTempSubdirectory("caf-bind-plan-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    private sealed class ThrowingGrokAcpFactory : IGrokAcpProcessFactory
    {
        public int Starts { get; private set; }

        public IGrokAcpProcess Start(GrokAcpStartInfo info)
        {
            Starts++;
            throw new ProcessStartException("simulated plan start failure", new InvalidOperationException("simulated"));
        }
    }
}
