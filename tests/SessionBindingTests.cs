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

public class LocalSessionRecoveryTests
{
    private const string BoundId = "12121212-1212-1212-1212-121212121212";
    private const string ResumeSuccessId = "12121212-1212-1212-1212-121212121212";

    [Theory]
    [InlineData(AgentFacade.GitHubCopilotAgent, SessionBinding.Preallocated)]
    [InlineData(AgentFacade.GrokBuildAgent, SessionBinding.Preallocated)]
    [InlineData(AgentFacade.CursorAgent, SessionBinding.EarlyStream)]
    [InlineData(AgentFacade.CodexAgent, SessionBinding.EarlyStream)]
    [InlineData(AgentFacade.AntigravityAgent, SessionBinding.EarlyStream)]
    public async Task LocalAgentFailureAfterSessionBoundExposesRecoveryAndAllowsExplicitContinuation(
        string agent,
        string expectedBindingSource)
    {
        var store = Directory.CreateTempSubdirectory("caf-rec-fail-").FullName;
        var logs = Directory.CreateTempSubdirectory("caf-rec-fail-logs-").FullName;
        var workspace = Directory.CreateTempSubdirectory("caf-rec-fail-ws-").FullName;
        try
        {
            var runner = new RecordingProcessRunner();
            var facade = CreateFacade(runner, logs, () => BoundId);
            var service = new AgentJobService(facade, store);

            runner.Result = FailedAfterBindResult(agent, BoundId);
            var initialRequest = new AgentRunRequest(agent, "first attempt", workspace, null, null);
            var started = service.Start("req-fail-initial", initialRequest);
            var waited = await service.WaitAsync(started.JobId, timeoutSeconds: 10);

            Assert.Equal(AgentJobStatus.Failed, waited.Status);
            Assert.NotNull(waited.Failure);
            Assert.NotNull(waited.Recovery);
            Assert.Equal(AgentSessionRecoveryStatus.Bound, waited.Recovery!.Status);
            Assert.Equal(BoundId, waited.Recovery.SessionId);
            Assert.Equal(expectedBindingSource, waited.Recovery.BindingSource);
            Assert.Null(waited.Recovery.Reason);

            var gotten = service.Get(started.JobId);
            Assert.Equal(waited.Recovery, gotten.Recovery);

            // exact retry は同じ failure と bound recovery を返し、CLI を再起動しない。
            var retried = service.Start("req-fail-initial", initialRequest);
            Assert.Equal(started.JobId, retried.JobId);
            Assert.Equal(AgentJobStatus.Failed, retried.Status);
            Assert.Equal(waited.Recovery, retried.Recovery);
            Assert.Equal(1, runner.CallCount);

            // 公開 JSON は recovery を含めつつ、local job 直下の sessionId は複製しない。
            var publicJson = JsonSerializer.Serialize(AgentJobPublicProjection.From(waited), AgentJson.Options);
            using var doc = JsonDocument.Parse(publicJson);
            Assert.False(doc.RootElement.TryGetProperty("sessionId", out _));
            Assert.Equal("bound", doc.RootElement.GetProperty("recovery").GetProperty("status").GetString());
            Assert.Equal(BoundId, doc.RootElement.GetProperty("recovery").GetProperty("sessionId").GetString());

            // ユーザー依頼による明示継続は新しい request_id と recovery.sessionId を使う。
            runner.Result = SucceededResult(agent, BoundId, "continued ok");
            var continuationRequest = new AgentRunRequest(
                agent,
                "continue after failure",
                workspace,
                waited.Recovery.SessionId,
                null);
            var continuedStarted = service.Start("req-fail-continue", continuationRequest);
            var continued = await service.WaitAsync(continuedStarted.JobId, timeoutSeconds: 10);
            Assert.Equal(AgentJobStatus.Completed, continued.Status);
            Assert.Null(continued.Recovery);
            Assert.Equal(BoundId, continued.Result!.SessionId);
            Assert.Equal("continued ok", continued.Result.OutputText);
            Assert.Equal(2, runner.CallCount);
            Assert.Contains(BoundId, runner.LastRequest!.Arguments);
        }
        finally
        {
            DeleteQuietly(store);
            DeleteQuietly(logs);
            DeleteQuietly(workspace);
        }
    }

    [Fact]
    public async Task GrokPlanFailureAfterSessionNewExposesRecoveryAndAllowsResume()
    {
        var store = Directory.CreateTempSubdirectory("caf-rec-plan-").FullName;
        var logs = Directory.CreateTempSubdirectory("caf-rec-plan-logs-").FullName;
        var workspace = Directory.CreateTempSubdirectory("caf-rec-plan-ws-").FullName;
        try
        {
            var factory = new ScriptedGrokAcpFactory
            {
                Script = new ScriptedGrokPlanScript
                {
                    SessionId = BoundId,
                    ErrorOn = "session/prompt",
                },
            };
            var facade = new AgentFacade(
                new GitHubCopilotDriver(new RecordingProcessRunner()),
                new GrokBuildDriver(new RecordingProcessRunner(), TimeProvider.System, new ThrowingGrokLogReader(), factory),
                new CursorCliDriver(new RecordingProcessRunner()),
                new CodexCliDriver(new RecordingProcessRunner()),
                new AgentRunLogFactory(logs, TimeProvider.System),
                antigravityCli: new AntigravityCliDriver(new RecordingProcessRunner()));
            var service = new AgentJobService(facade, store);

            var initialRequest = new AgentRunRequest(
                AgentFacade.GrokBuildAgent,
                "draft plan",
                workspace,
                null,
                null,
                RunMode: "plan");
            var started = service.Start("req-plan-fail", initialRequest);
            var waited = await service.WaitAsync(started.JobId, timeoutSeconds: 10);

            Assert.Equal(AgentJobStatus.Failed, waited.Status);
            Assert.Equal(AgentSessionRecoveryStatus.Bound, waited.Recovery!.Status);
            Assert.Equal(BoundId, waited.Recovery.SessionId);
            Assert.Equal(SessionBinding.EarlyStream, waited.Recovery.BindingSource);

            factory.Script = new ScriptedGrokPlanScript
            {
                LoadSessionId = BoundId,
                PlanContent = "# Resumed Plan\nDone",
            };
            var continuedRequest = initialRequest with
            {
                Prompt = "finish plan",
                SessionId = waited.Recovery.SessionId,
            };
            var continuedStarted = service.Start("req-plan-continue", continuedRequest);
            var continued = await service.WaitAsync(continuedStarted.JobId, timeoutSeconds: 10);
            Assert.Equal(AgentJobStatus.Completed, continued.Status);
            Assert.Null(continued.Recovery);
            Assert.Equal(BoundId, continued.Result!.SessionId);
            Assert.Equal("plan", continued.Result.OutputKind);
        }
        finally
        {
            DeleteQuietly(store);
            DeleteQuietly(logs);
            DeleteQuietly(workspace);
        }
    }

    [Theory]
    [InlineData(AgentFacade.GitHubCopilotAgent, SessionBinding.Preallocated)]
    [InlineData(AgentFacade.CodexAgent, SessionBinding.EarlyStream)]
    [InlineData(AgentFacade.AntigravityAgent, SessionBinding.EarlyStream)]
    public async Task CancelAfterSessionBoundExposesRecoveryAcrossCancelGetRetryAndRestart(
        string agent,
        string expectedBindingSource)
    {
        var store = Directory.CreateTempSubdirectory("caf-rec-cancel-").FullName;
        var logs = Directory.CreateTempSubdirectory("caf-rec-cancel-logs-").FullName;
        var workspace = Directory.CreateTempSubdirectory("caf-rec-cancel-ws-").FullName;
        try
        {
            var runner = new RecordingProcessRunner
            {
                Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var facade = CreateFacade(runner, logs, () => BoundId);
            var service = new AgentJobService(facade, store);

            var request = new AgentRunRequest(agent, "long task", workspace, null, null);
            var started = service.Start("req-cancel-bound", request);
            await runner.StartedTask.WaitAsync(TimeSpan.FromSeconds(10));
            if (agent == AgentFacade.CodexAgent)
            {
                runner.EmitStdout("{\"type\":\"thread.started\",\"thread_id\":\"" + BoundId + "\"}");
            }
            else if (agent == AgentFacade.AntigravityAgent)
            {
                runner.EmitStdout("{\"event\":\"init\",\"conversation_id\":\"" + BoundId + "\",\"init\":{}}");
            }

            // 早期ストリーム経由も session_bound の書き込み完了まで待ってから Cancel する。
            await WaitForSessionBoundEventAsync(logs, started.JobId);

            var cancelled = service.Cancel(started.JobId);
            Assert.Equal(AgentJobStatus.Cancelled, cancelled.Status);
            Assert.NotNull(cancelled.Recovery);
            Assert.Equal(AgentSessionRecoveryStatus.Bound, cancelled.Recovery!.Status);
            Assert.Equal(BoundId, cancelled.Recovery.SessionId);
            Assert.Equal(expectedBindingSource, cancelled.Recovery.BindingSource);

            runner.Gate.TrySetResult(true);
            var gotten = service.Get(started.JobId);
            Assert.Equal(AgentJobStatus.Cancelled, gotten.Status);
            Assert.Equal(cancelled.Recovery, gotten.Recovery);

            var retried = service.Start("req-cancel-bound", request);
            Assert.Equal(AgentJobStatus.Cancelled, retried.Status);
            Assert.Equal(cancelled.Recovery, retried.Recovery);

            // 再起動後の別 AgentJobService インスタンスからも同じ bound recovery を取得できる。
            var restarted = new AgentJobService(CreateFacade(new RecordingProcessRunner(), logs), store);
            var afterRestart = restarted.Get(started.JobId);
            Assert.Equal(AgentJobStatus.Cancelled, afterRestart.Status);
            Assert.Equal(cancelled.Recovery, afterRestart.Recovery);
        }
        finally
        {
            DeleteQuietly(store);
            DeleteQuietly(logs);
            DeleteQuietly(workspace);
        }
    }

    [Fact]
    public async Task CancelBeforeLateBindStillPersistsRecoveryOnDiskWithoutChangingCancelledStatus()
    {
        var store = Directory.CreateTempSubdirectory("caf-rec-race-").FullName;
        var logs = Directory.CreateTempSubdirectory("caf-rec-race-logs-").FullName;
        var workspace = Directory.CreateTempSubdirectory("caf-rec-race-ws-").FullName;
        try
        {
            var runner = new LateBindOnCancelRunner(BoundId);
            var facade = CreateFacade(runner, logs);
            var service = new AgentJobService(facade, store);

            var request = new AgentRunRequest(AgentFacade.CodexAgent, "race task", workspace, null, null);
            var started = service.Start("req-cancel-race", request);
            await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // まだ session_bound 前に Cancel して live 登録が外れた状態を作る。
            var cancelled = service.Cancel(started.JobId);
            Assert.Equal(AgentJobStatus.Cancelled, cancelled.Status);
            Assert.Null(cancelled.Recovery);

            // worker 終了前に遅れて BindSession が発火しても disk 上へ recovery を補完する。
            runner.AllowLateBind.TrySetResult();
            await WaitForSessionBoundEventAsync(logs, started.JobId);

            AgentJobSnapshot? observed = null;
            for (var i = 0; i < 40; i++)
            {
                observed = service.Get(started.JobId);
                if (observed.Recovery?.Status == AgentSessionRecoveryStatus.Bound)
                {
                    break;
                }

                await Task.Delay(25);
            }

            Assert.NotNull(observed);
            Assert.Equal(AgentJobStatus.Cancelled, observed!.Status);
            Assert.Equal(AgentSessionRecoveryStatus.Bound, observed.Recovery?.Status);
            Assert.Equal(BoundId, observed.Recovery?.SessionId);

            var restarted = new AgentJobService(CreateFacade(new RecordingProcessRunner(), logs), store);
            var afterRestart = restarted.Get(started.JobId);
            Assert.Equal(AgentJobStatus.Cancelled, afterRestart.Status);
            Assert.Equal(BoundId, afterRestart.Recovery?.SessionId);
        }
        finally
        {
            DeleteQuietly(store);
            DeleteQuietly(logs);
            DeleteQuietly(workspace);
        }
    }

    [Fact]
    public async Task RestartRecoversBoundSessionFromRunningAndUnfinalizedRecordsEvenWithTruncatedTail()
    {
        var store = Directory.CreateTempSubdirectory("caf-rec-restart-").FullName;
        var logs = Directory.CreateTempSubdirectory("caf-rec-restart-logs-").FullName;
        var workspace = Directory.CreateTempSubdirectory("caf-rec-restart-ws-").FullName;
        try
        {
            var runningRunner = new RecordingProcessRunner
            {
                Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var facade1 = CreateFacade(runningRunner, logs);
            var service1 = new AgentJobService(facade1, store);
            var req = new AgentRunRequest(AgentFacade.CodexAgent, "crash test", workspace, null, null);
            var started = service1.Start("req-crash-bound", req);
            await runningRunner.StartedTask.WaitAsync(TimeSpan.FromSeconds(10));
            runningRunner.EmitStdout("{\"type\":\"thread.started\",\"thread_id\":\"" + BoundId + "\"}");
            var eventsPath = await WaitForSessionBoundEventAsync(logs, started.JobId);

            // 1) running レコードの recovery フィールドが未保存かつ末尾行が途中で切れた状態をシミュレート。
            var recordPath = Directory.EnumerateFiles(store, "req-*.json").Single();
            var stored = JsonSerializer.Deserialize<AgentJobRecord>(File.ReadAllText(recordPath), AgentJson.Options)!;
            var legacyRunning = stored with { Status = AgentJobStatus.Running, Recovery = null, RecoveryFinalized = false };
            File.WriteAllText(recordPath, JsonSerializer.Serialize(legacyRunning, AgentJson.Options));
            using (var stream = new FileStream(eventsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                await writer.WriteAsync("{\"ts\":\"2026-11-15T00:00:00Z\",\"truncated\":");
            }

            var service2 = new AgentJobService(CreateFacade(new RecordingProcessRunner(), logs), store);
            var recoveredRunning = service2.Get(started.JobId);
            Assert.Equal(AgentJobStatus.Failed, recoveredRunning.Status);
            Assert.Equal("facade_interrupted", recoveredRunning.Failure!.Kind);
            Assert.Null(recoveredRunning.Failure.RunId);
            Assert.Equal(AgentSessionRecoveryStatus.Bound, recoveredRunning.Recovery!.Status);
            Assert.Equal(BoundId, recoveredRunning.Recovery.SessionId);
            Assert.Equal(SessionBinding.EarlyStream, recoveredRunning.Recovery.BindingSource);

            // 2) 未完了確定（recoveryFinalized=false）の cancelled レコードでも cancelled のまま events.jsonl から復元する。
            var unfinalizedCancelled = stored with
            {
                Status = AgentJobStatus.Cancelled,
                Error = "cancelled",
                Failure = null,
                Recovery = null,
                RecoveryFinalized = false,
            };
            File.WriteAllText(recordPath, JsonSerializer.Serialize(unfinalizedCancelled, AgentJson.Options));

            var service3 = new AgentJobService(CreateFacade(new RecordingProcessRunner(), logs), store);
            var recoveredCancelled = service3.Get(started.JobId);
            Assert.Equal(AgentJobStatus.Cancelled, recoveredCancelled.Status);
            Assert.Null(recoveredCancelled.Failure);
            Assert.Equal(AgentSessionRecoveryStatus.Bound, recoveredCancelled.Recovery!.Status);
            Assert.Equal(BoundId, recoveredCancelled.Recovery.SessionId);

            runningRunner.Gate.TrySetResult(true);
        }
        finally
        {
            DeleteQuietly(store);
            DeleteQuietly(logs);
            DeleteQuietly(workspace);
        }
    }

    [Fact]
    public void RestartDistinguishesUnboundFromMissingCorruptUnreadableAndConflictingLogs()
    {
        var store = Directory.CreateTempSubdirectory("caf-rec-diag-").FullName;
        var logs = Directory.CreateTempSubdirectory("caf-rec-diag-logs-").FullName;
        try
        {
            var service = new AgentJobService(CreateFacade(new RecordingProcessRunner(), logs), store);

            // 1) 全行が有効 JSON で session_bound が無い running crash -> recovery は null（未確定）
            var unboundJobId = "job-unbound-clean";
            var unboundEvents = Path.Combine(logs, unboundJobId + ".events.jsonl");
            File.WriteAllText(unboundEvents, "{\"ts\":\"2026-11-15T00:00:00Z\",\"runId\":\"" + unboundJobId + "\",\"source\":\"facade\",\"type\":\"started\",\"data\":{}}\n");
            WriteStoredRecord(store, unboundJobId, "req-unbound-clean", unboundEvents);
            var unboundRecovered = service.Get(unboundJobId);
            Assert.Equal(AgentJobStatus.Failed, unboundRecovered.Status);
            Assert.Equal("facade_interrupted", unboundRecovered.Failure!.Kind);
            Assert.Null(unboundRecovered.Recovery);
            Assert.False(JsonDocument.Parse(JsonSerializer.Serialize(AgentJobPublicProjection.From(unboundRecovered), AgentJson.Options))
                .RootElement.TryGetProperty("recovery", out _));

            // 2) run log 欠落 -> unavailable / run_log_missing
            var missingJobId = "job-missing-log";
            var missingEvents = Path.Combine(logs, missingJobId + ".events.jsonl");
            WriteStoredRecord(store, missingJobId, "req-missing-log", missingEvents);
            var missingRecovered = service.Get(missingJobId);
            Assert.Equal(AgentSessionRecoveryStatus.Unavailable, missingRecovered.Recovery!.Status);
            Assert.Equal(AgentSessionRecoveryReason.RunLogMissing, missingRecovered.Recovery.Reason);
            Assert.Null(missingRecovered.Recovery.SessionId);

            // 3) session_bound 無しの破損ログ -> unavailable / run_log_corrupt
            var corruptJobId = "job-corrupt-log";
            var corruptEvents = Path.Combine(logs, corruptJobId + ".events.jsonl");
            File.WriteAllText(corruptEvents, "{not-valid-json\n");
            WriteStoredRecord(store, corruptJobId, "req-corrupt-log", corruptEvents);
            var corruptRecovered = service.Get(corruptJobId);
            Assert.Equal(AgentSessionRecoveryStatus.Unavailable, corruptRecovered.Recovery!.Status);
            Assert.Equal(AgentSessionRecoveryReason.RunLogCorrupt, corruptRecovered.Recovery.Reason);
            Assert.Null(corruptRecovered.Recovery.SessionId);

            // 4) 複数異なる session_bound が残った不整合ログ -> unavailable / session_mismatch
            var conflictJobId = "job-conflict-log";
            var conflictEvents = Path.Combine(logs, conflictJobId + ".events.jsonl");
            File.WriteAllText(
                conflictEvents,
                "{\"ts\":\"2026-11-15T00:00:00Z\",\"runId\":\"" + conflictJobId + "\",\"source\":\"facade\",\"type\":\"session_bound\",\"data\":{\"sessionId\":\"sess-a\",\"bindingSource\":\"preallocated\"}}\n"
                + "{\"ts\":\"2026-11-15T00:00:01Z\",\"runId\":\"" + conflictJobId + "\",\"source\":\"facade\",\"type\":\"session_bound\",\"data\":{\"sessionId\":\"sess-b\",\"bindingSource\":\"early_stream\"}}\n");
            WriteStoredRecord(store, conflictJobId, "req-conflict-log", conflictEvents);
            var conflictRecovered = service.Get(conflictJobId);
            Assert.Equal(AgentSessionRecoveryStatus.Unavailable, conflictRecovered.Recovery!.Status);
            Assert.Equal(AgentSessionRecoveryReason.SessionMismatch, conflictRecovered.Recovery.Reason);
            Assert.Null(conflictRecovered.Recovery.SessionId);

            // 5) 読取不能なログ（共有なしロック中のファイル）-> unavailable / run_log_unreadable
            var unreadableJobId = "job-unreadable-log";
            var unreadableEvents = Path.Combine(logs, unreadableJobId + ".events.jsonl");
            File.WriteAllText(unreadableEvents, "{\"source\":\"facade\",\"type\":\"started\",\"data\":{}}\n");
            WriteStoredRecord(store, unreadableJobId, "req-unreadable-log", unreadableEvents);
            using (var exclusiveLock = new FileStream(unreadableEvents, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var unreadableRecovered = service.Get(unreadableJobId);
                Assert.Equal(AgentSessionRecoveryStatus.Unavailable, unreadableRecovered.Recovery!.Status);
                Assert.Equal(AgentSessionRecoveryReason.RunLogUnreadable, unreadableRecovered.Recovery.Reason);
                Assert.Null(unreadableRecovered.Recovery.SessionId);
            }
        }
        finally
        {
            DeleteQuietly(store);
            DeleteQuietly(logs);
        }
    }

    [Fact]
    public async Task FailureOrCancelBeforeSessionBoundDoesNotFabricateRecoveryId()
    {
        var store = Directory.CreateTempSubdirectory("caf-rec-unbound-").FullName;
        var logs = Directory.CreateTempSubdirectory("caf-rec-unbound-logs-").FullName;
        var workspace = Directory.CreateTempSubdirectory("caf-rec-unbound-ws-").FullName;
        try
        {
            var runner = new RecordingProcessRunner
            {
                Result = new ProcessRunResult(5, "{\"type\":\"assistant.message\",\"data\":{\"content\":\"partial\"}}\n", "boom"),
            };
            var facade = CreateFacade(runner, logs);
            var service = new AgentJobService(facade, store);

            var failedStarted = service.Start("req-unbound-fail", new AgentRunRequest(AgentFacade.CodexAgent, "hello", workspace, null, null));
            var failed = await service.WaitAsync(failedStarted.JobId, timeoutSeconds: 10);
            Assert.Equal(AgentJobStatus.Failed, failed.Status);
            Assert.Null(failed.Recovery);

            runner.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelStarted = service.Start("req-unbound-cancel", new AgentRunRequest(AgentFacade.CursorAgent, "wait", workspace, null, null));
            await runner.StartedTask.WaitAsync(TimeSpan.FromSeconds(10));
            var cancelled = service.Cancel(cancelStarted.JobId);
            runner.Gate.TrySetResult(true);
            Assert.Equal(AgentJobStatus.Cancelled, cancelled.Status);
            Assert.Null(cancelled.Recovery);
        }
        finally
        {
            DeleteQuietly(store);
            DeleteQuietly(logs);
            DeleteQuietly(workspace);
        }
    }

    [Fact]
    public async Task SessionMismatchKeepsFirstBoundRecoveryAndFailureKind()
    {
        var store = Directory.CreateTempSubdirectory("caf-rec-mismatch-").FullName;
        var logs = Directory.CreateTempSubdirectory("caf-rec-mismatch-logs-").FullName;
        var workspace = Directory.CreateTempSubdirectory("caf-rec-mismatch-ws-").FullName;
        try
        {
            var runner = new RecordingProcessRunner
            {
                Result = new ProcessRunResult(
                    0,
                    "{\"type\":\"thread.started\",\"thread_id\":\"other-thread\"}\n"
                    + "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"ok\"}}\n",
                    ""),
            };
            var facade = CreateFacade(runner, logs);
            var service = new AgentJobService(facade, store);

            var started = service.Start(
                "req-mismatch-recovery",
                new AgentRunRequest(AgentFacade.CodexAgent, "resume check", workspace, BoundId, null));
            var failed = await service.WaitAsync(started.JobId, timeoutSeconds: 10);

            Assert.Equal(AgentJobStatus.Failed, failed.Status);
            Assert.Equal("session_mismatch", failed.Failure!.Kind);
            Assert.Equal(AgentSessionRecoveryStatus.Bound, failed.Recovery!.Status);
            Assert.Equal(BoundId, failed.Recovery.SessionId);
            Assert.Equal(SessionBinding.Resume, failed.Recovery.BindingSource);
        }
        finally
        {
            DeleteQuietly(store);
            DeleteQuietly(logs);
            DeleteQuietly(workspace);
        }
    }

    private static AgentFacade CreateFacade(
        IProcessRunner runner,
        string logDirectory,
        Func<string>? allocateSessionId = null)
    {
        return new AgentFacade(
            new GitHubCopilotDriver(runner, allocateSessionId),
            new GrokBuildDriver(runner, TimeProvider.System, new ThrowingGrokLogReader(), allocateSessionId: allocateSessionId),
            new CursorCliDriver(runner),
            new CodexCliDriver(runner),
            new AgentRunLogFactory(logDirectory, TimeProvider.System),
            antigravityCli: new AntigravityCliDriver(runner));
    }

    private sealed class LateBindOnCancelRunner(string threadId) : IProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowLateBind { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            await cancelled.Task.ConfigureAwait(false);
            await AllowLateBind.Task.ConfigureAwait(false);
            request.StdoutLineCallback?.Invoke("{\"type\":\"thread.started\",\"thread_id\":\"" + threadId + "\"}");
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static ProcessRunResult FailedAfterBindResult(string agent, string sessionId)
    {
        return agent switch
        {
            AgentFacade.GitHubCopilotAgent => new ProcessRunResult(1, "not-json\n", "copilot failed"),
            AgentFacade.GrokBuildAgent => new ProcessRunResult(1, "not-json\n", "grok failed"),
            AgentFacade.CursorAgent => new ProcessRunResult(
                1,
                "{\"type\":\"system\",\"session_id\":\"" + sessionId + "\"}\n",
                "cursor failed"),
            AgentFacade.CodexAgent => new ProcessRunResult(
                1,
                "{\"type\":\"thread.started\",\"thread_id\":\"" + sessionId + "\"}\n",
                "codex failed"),
            AgentFacade.AntigravityAgent => new ProcessRunResult(
                1,
                "{\"event\":\"init\",\"conversation_id\":\"" + sessionId + "\",\"init\":{}}\n",
                "antigravity failed"),
            _ => throw new ArgumentOutOfRangeException(nameof(agent), agent, null),
        };
    }

    private static ProcessRunResult SucceededResult(string agent, string sessionId, string text)
    {
        return agent switch
        {
            AgentFacade.GitHubCopilotAgent => new ProcessRunResult(
                0,
                "{\"type\":\"assistant.turn_start\"}\n{\"type\":\"assistant.message\",\"data\":{\"content\":\"" + text + "\"}}\n{\"type\":\"assistant.turn_end\"}\n{\"type\":\"result\",\"sessionId\":\"" + sessionId + "\"}\n",
                ""),
            AgentFacade.GrokBuildAgent => new ProcessRunResult(
                0,
                "{\"type\":\"text\",\"data\":\"" + text + "\"}\n{\"type\":\"end\",\"sessionId\":\"" + sessionId + "\"}\n",
                ""),
            AgentFacade.CursorAgent => new ProcessRunResult(
                0,
                "{\"type\":\"system\",\"session_id\":\"" + sessionId + "\"}\n{\"type\":\"result\",\"is_error\":false,\"session_id\":\"" + sessionId + "\",\"result\":\"" + text + "\"}\n",
                ""),
            AgentFacade.CodexAgent => new ProcessRunResult(
                0,
                "{\"type\":\"thread.started\",\"thread_id\":\"" + sessionId + "\"}\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"" + text + "\"}}\n{\"type\":\"turn.completed\"}\n",
                ""),
            AgentFacade.AntigravityAgent => new ProcessRunResult(
                0,
                "{\"event\":\"init\",\"conversation_id\":\"" + sessionId + "\",\"init\":{}}\n{\"event\":\"result\",\"result\":{\"conversation_id\":\"" + sessionId + "\",\"status\":\"SUCCESS\",\"response\":\"" + text + "\"}}\n",
                ""),
            _ => throw new ArgumentOutOfRangeException(nameof(agent), agent, null),
        };
    }

    private static async Task<string> WaitForSessionBoundEventAsync(string logDirectory, string jobId)
    {
        var eventsPath = Path.Combine(logDirectory, jobId + ".events.jsonl");
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(eventsPath))
            {
                using var stream = new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync();
                if (text.Contains("\"session_bound\"", StringComparison.Ordinal))
                {
                    return eventsPath;
                }
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Timed out waiting for session_bound in " + eventsPath);
    }

    private static void WriteStoredRecord(string storeDirectory, string jobId, string requestId, string? eventsLogPath)
    {
        var record = new AgentJobRecord(
            jobId,
            requestId,
            AgentJobStatus.Running,
            "fp-" + jobId,
            null,
            null,
            null,
            EventsLogPath: eventsLogPath);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestId))).ToLowerInvariant();
        File.WriteAllText(
            Path.Combine(storeDirectory, "req-" + hash + ".json"),
            JsonSerializer.Serialize(record, AgentJson.Options));
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }
    }
}
