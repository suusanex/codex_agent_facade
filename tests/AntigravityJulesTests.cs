using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

/// <summary>
/// Antigravity と Jules の stub 検証。実 CLI と実 Jules API は呼ばない。
/// </summary>
[Collection("global-facade-logger")]
public sealed class AntigravityJulesTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Secret = "super-secret-jules-key-42";
    private const string PatchOne = "diff --git a/A b/A\n+hello \n";
    private const string PatchTwo = "diff --git a/B b/B\n+second\n";

    [Fact]
    public void AntigravityArgumentsOmitUnspecifiedOptions()
    {
        var bare = AntigravityCliDriver.BuildArguments(Request(AgentFacade.AntigravityAgent, "hello", autoApprove: false));
        Assert.Equal(["-p", "hello", "--output-format", "stream-json", "--print-timeout", "0s"], bare);

        var full = AntigravityCliDriver.BuildArguments(new AgentRunRequest(
            AgentFacade.AntigravityAgent,
            "hello",
            Path.GetTempPath(),
            " conv ",
            null,
            AutoApprove: true,
            Model: "gemini",
            ReasoningEffort: "high"));
        Assert.Equal(
            [
                "-p", "hello", "--output-format", "stream-json", "--print-timeout", "0s",
                "--conversation", "conv", "--model", "gemini", "--effort", "high",
                "--dangerously-skip-permissions",
            ],
            full);
    }

    [Fact]
    public async Task AntigravitySuccessRequiresSuccessStatusAndExitZero()
    {
        var result = await RunAntigravityAsync(SuccessStream("conv-1", "SUCCESS", "pong"), 0);
        Assert.Equal("conv-1", result.SessionId);
        Assert.Equal("pong", result.OutputText);
        Assert.Equal("final_response", result.OutputKind);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.CompletionReason);
    }

    [Theory]
    [InlineData("ERROR")]
    [InlineData("INVALID")]
    [InlineData("CANCELED")]
    [InlineData("INTERRUPTED")]
    public async Task AntigravityFailureStatusesDoNotSucceed(string status)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAntigravityAsync(SuccessStream("conv-1", status, "nope"), 0));
        Assert.Equal("agent_error", ex.Data[CliJson.FailureKindKey]);
    }

    [Theory]
    [InlineData("WAITING")]
    [InlineData("RUNNING")]
    public async Task AntigravityIncompleteStatusesFail(string status)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAntigravityAsync(SuccessStream("conv-1", status, "later"), 0));
        Assert.Equal("incomplete_run", ex.Data[CliJson.FailureKindKey]);
    }

    [Fact]
    public async Task AntigravityCanceledStatusIsCancelledOnlyWhenFacadeCancelled()
    {
        using var cts = new CancellationTokenSource();
        var runner = new ScriptedProcessRunner(SuccessStream("conv-1", "CANCELED", ""), _ => cts.Cancel());
        var driver = new AntigravityCliDriver(runner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.RunAsync(
            Request(AgentFacade.AntigravityAgent, "hello"),
            TestRunLogs.CreateLog(),
            null,
            cts.Token));
    }

    [Fact]
    public async Task AntigravityRejectsBrokenStreamsAndSessionMismatch()
    {
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAntigravityAsync(
            """{"event":"init","conversation_id":"conv-1","init":{}}""",
            0));
        Assert.Equal("output_parse_failed", missing.Data[CliJson.FailureKindKey]);

        var broken = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAntigravityAsync("not-json", 0));
        Assert.Equal("output_parse_failed", broken.Data[CliJson.FailureKindKey]);

        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAntigravityAsync(
            SuccessStream("other", "SUCCESS", "pong"),
            0,
            sessionId: "conv-1"));
        Assert.Equal("session_mismatch", mismatch.Data[CliJson.FailureKindKey]);

        var exit = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAntigravityAsync(SuccessStream("conv-1", "SUCCESS", "pong"), 7));
        Assert.Equal("non_zero_exit", exit.Data[CliJson.FailureKindKey]);
    }

    [Fact]
    public async Task AntigravitySoftDenyKeepsSuccessAndSetsCompletionReason()
    {
        var fromStderr = await RunAntigravityAsync(
            SuccessStream("conv-1", "SUCCESS", "pong"),
            0,
            standardError: "Permission was auto-denied by the policy.");
        Assert.Equal(AntigravityCliDriver.PermissionSoftDeniedReason, fromStderr.CompletionReason);

        var stream = """
            {"event":"init","conversation_id":"conv-1","init":{}}
            {"event":"step_update","step_update":{"conversation_id":"conv-1","denied_actions":[{"name":"write"}]}}
            {"event":"result","result":{"conversation_id":"conv-1","status":"SUCCESS","response":"pong"}}
            """;
        var fromEvent = await RunAntigravityAsync(stream, 0);
        Assert.Equal(AntigravityCliDriver.PermissionSoftDeniedReason, fromEvent.CompletionReason);
        Assert.Equal("pong", fromEvent.OutputText);
    }

    [Fact]
    public async Task AntigravityWaitTimeoutDoesNotCancelTheWorker()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingProcessRunner { Gate = gate };
        var jobs = JobsFor(runner, out var store);
        var started = jobs.Start("req-agy-wait", Request(AgentFacade.AntigravityAgent, "hello"));
        await runner.StartedTask.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = await jobs.WaitAsync(started.JobId, 1);
        Assert.Equal(AgentJobStatus.Running, waiting.Status);
        Assert.False(runner.LastCancellationToken.IsCancellationRequested);

        var cancelled = jobs.Cancel(started.JobId);
        Assert.Equal(AgentJobStatus.Cancelled, cancelled.Status);
        Assert.True(Directory.Exists(store));
    }

    [Fact]
    public void AntigravityAndJulesRejectUnsupportedOptions()
    {
        Assert.Throws<ArgumentException>(() => AgentFacade.Validate(Request(AgentFacade.AntigravityAgent, "hello", fast: false)));
        Assert.Throws<ArgumentException>(() => AgentFacade.Validate(Request(AgentFacade.AntigravityAgent, "hello", skills: ["skill"])));
        Assert.Throws<ArgumentException>(() => AgentFacade.Validate(Request(AgentFacade.AntigravityAgent, "hello", runMode: "plan")));
        Assert.Throws<ArgumentException>(() => AgentFacade.Validate(Request(AgentFacade.JulesAgent, "hello", fast: true)));
        Assert.Throws<ArgumentException>(() => AgentFacade.Validate(Request(AgentFacade.JulesAgent, "hello", skills: ["skill"])));
        Assert.Throws<ArgumentException>(() => AgentFacade.Validate(Request(AgentFacade.JulesAgent, "hello", runMode: "plan")));
        Assert.Throws<ArgumentException>(() => AgentFacade.Validate(Request(AgentFacade.JulesAgent, "hello", model: "gemini")));
        Assert.Throws<ArgumentException>(() => AgentFacade.Validate(Request(AgentFacade.JulesAgent, "hello", effort: "low")));
        Assert.Throws<ArgumentException>(() => AgentFacade.Validate(Request(AgentFacade.JulesAgent, "hello", autoApprove: false)));
    }

    [Fact]
    public async Task CliRejectsJulesBeforeLaunchAndRunsAntigravity()
    {
        var runner = new RecordingProcessRunner();
        var facade = Facade(runner, new JulesHttpScript(), new FakeGitWorktree());
        await using var rejected = new StringWriter();
        var rejectedCode = await AgentCli.ExecuteAsync(
            facade,
            JsonSerializer.Serialize(new
            {
                agent = AgentFacade.JulesAgent,
                prompt = "hello",
                working_directory = Path.GetTempPath(),
            }),
            rejected,
            CancellationToken.None,
            @"C:\facade-cli-test\cli.4242.log");
        Assert.Equal(AgentCli.ExitInvalidRequest, rejectedCode);
        Assert.Contains("MCP job host", rejected.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, runner.CallCount);

        runner.Result = new ProcessRunResult(0, SuccessStream("conv-1", "SUCCESS", "pong"), "");
        await using var accepted = new StringWriter();
        var acceptedCode = await AgentCli.ExecuteAsync(
            facade,
            JsonSerializer.Serialize(new
            {
                agent = AgentFacade.AntigravityAgent,
                prompt = "hello",
                working_directory = Path.GetTempPath(),
                session_id = "conv-1",
            }),
            accepted,
            CancellationToken.None,
            @"C:\facade-cli-test\cli.4242.log");
        Assert.Equal(AgentCli.ExitCompleted, acceptedCode);
        Assert.Equal(AntigravityCliDriver.FileName, runner.LastRequest!.FileName);
        Assert.Contains("--conversation", runner.LastRequest.Arguments);
        Assert.DoesNotContain("--model", runner.LastRequest.Arguments);
    }

    [Fact]
    public async Task JulesCreateCompletesWithoutUserMessageOrPatch()
    {
        using var harness = Harness();
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-done", user: null, agent: "pong", completed: true)));
            }

            if (path.Contains("/sessions", StringComparison.Ordinal))
            {
                return Json(Session("sess-1", "COMPLETED"));
            }

            return Status(HttpStatusCode.NotFound);
        };

        var done = await harness.WaitAsync("req-create", Request(AgentFacade.JulesAgent, "hello"));
        AssertCompleted(done, "pong");
        Assert.Equal("sess-1", done.Result!.SessionId);
        Assert.NotEqual("not-the-path-id", done.Result.SessionId);
        Assert.Null(done.Result.ExitCode);
        Assert.True(done.Result.Artifacts is null || done.Result.Artifacts.Count == 0);
        var create = harness.Http.Bodies.Single(call => call.Method == HttpMethod.Post.Method && call.Path.EndsWith("/sessions", StringComparison.Ordinal));
        Assert.Contains("\"requirePlanApproval\":false", create.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("automationMode", create.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Serialize(done), StringComparison.Ordinal);
        AssertNoSecret(harness.Store);
    }

    [Fact]
    public async Task JulesCompletedSessionWithoutCompletedActivityWaitsUntilTheActivityListIsStable()
    {
        using var harness = Harness();
        var sessionReads = 0;
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-agent", user: null, agent: "pong", completed: false)));
            }

            if (path.Contains("/sessions/sess-1", StringComparison.Ordinal))
            {
                sessionReads++;
                return Json(Session("sess-1", "COMPLETED"));
            }

            return Json(Session("sess-1", "COMPLETED"));
        };

        var done = await harness.WaitAsync("req-stable-complete", Request(AgentFacade.JulesAgent, "hello"));
        AssertCompleted(done, "pong");
        Assert.True(sessionReads >= 2);
        Assert.Null(done.Result!.ExitCode);
    }

    [Fact]
    public async Task JulesContinuationCompletesWithoutSessionCompletedActivity()
    {
        using var harness = Harness();
        var prompt = "SAME-TEXT-MARKER";
        var sessionReads = 0;
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains(":sendMessage", StringComparison.Ordinal))
            {
                return Status(HttpStatusCode.OK);
            }

            var sent = harness.Http.Bodies.Any(call => call.Path.Contains(":sendMessage", StringComparison.Ordinal));
            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return sent
                    ? Json(Activities(
                        Activity("act-old", null, "which word?", completed: false),
                        Activity("act-user", prompt, null, completed: false),
                        Activity("act-agent", null, "still waiting", completed: false)))
                    : Json(Activities(Activity("act-old", null, "which word?", completed: false)));
            }

            if (path.Contains("/sessions/sess-1", StringComparison.Ordinal))
            {
                sessionReads++;
            }

            return Json(Session("sess-1", sent ? "COMPLETED" : "AWAITING_USER_FEEDBACK"));
        };

        var done = await harness.WaitAsync("req-continue-stable", Request(AgentFacade.JulesAgent, prompt, sessionId: "sess-1"));
        AssertCompleted(done, "still waiting");
        Assert.Equal(1, harness.Http.Bodies.Count(call => call.Path.Contains(":sendMessage", StringComparison.Ordinal)));
        Assert.True(sessionReads >= 3);
    }

    [Fact]
    public async Task JulesProgressPatchWithoutDiffDoesNotFailTheTurn()
    {
        using var harness = Harness();
        var prompt = "TWO";
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains(":sendMessage", StringComparison.Ordinal))
            {
                return Status(HttpStatusCode.OK);
            }

            var sent = harness.Http.Bodies.Any(call => call.Path.Contains(":sendMessage", StringComparison.Ordinal));
            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return sent
                    ? Json(Activities(
                        Activity("act-old", null, "which file?", completed: false),
                        Activity("act-user", prompt, null, completed: false),
                        Activity("act-partial", null, null, completed: false, patches: (Commit, null)),
                        Activity("act-full", null, "wrote two", completed: false, patches: (Commit, PatchOne))))
                    : Json(Activities(Activity("act-old", null, "which file?", completed: false)));
            }

            return Json(Session("sess-1", sent ? "AWAITING_USER_FEEDBACK" : "AWAITING_USER_FEEDBACK"));
        };

        var waiting = await harness.WaitAsync("req-partial-patch", Request(AgentFacade.JulesAgent, prompt, sessionId: "sess-1"));
        Assert.Equal(AgentJobStatus.WaitingForInput, waiting.Status);
        Assert.Equal("wrote two", waiting.Result!.OutputText);
        var artifact = Assert.Single(waiting.Result.Artifacts!);
        Assert.Equal("act-full", artifact.ActivityId);
        Assert.Equal(Commit, artifact.BaseCommitId);
        Assert.Equal(PatchOne, File.ReadAllText(artifact.PatchPath));
    }

    [Fact]
    public async Task JulesActivityNotFoundRightAfterCreateKeepsPollingUntilTheQuestion()
    {
        using var harness = Harness();
        var activityReads = 0;
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                activityReads++;
                if (activityReads == 1)
                {
                    return Status(HttpStatusCode.NotFound);
                }

                return Json(Activities(Activity("act-ask", user: null, agent: "which word?", completed: false)));
            }

            if (path.Contains("/sessions", StringComparison.Ordinal))
            {
                return Json(Session("sess-1", "AWAITING_USER_FEEDBACK"));
            }

            return Status(HttpStatusCode.NotFound);
        };

        var waiting = await harness.WaitAsync("req-activity-404", Request(AgentFacade.JulesAgent, "hello"));
        Assert.Equal(AgentJobStatus.WaitingForInput, waiting.Status);
        Assert.Equal("sess-1", waiting.Result!.SessionId);
        Assert.True(activityReads >= 2);
    }

    [Fact]
    public async Task JulesMissingSessionAfterCreateStaysRejected()
    {
        using var harness = Harness();
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-done", user: null, agent: "pong", completed: true)));
            }

            if (path.Contains("/sessions/sess-1", StringComparison.Ordinal))
            {
                return Status(HttpStatusCode.NotFound);
            }

            if (path.Contains("/sessions", StringComparison.Ordinal))
            {
                return Json(Session("sess-1", "IN_PROGRESS"));
            }

            return Status(HttpStatusCode.NotFound);
        };

        var failed = await harness.WaitAsync("req-session-404", Request(AgentFacade.JulesAgent, "hello"));
        Assert.Equal("remote_rejected", failed.Failure!.Kind);
        Assert.DoesNotContain(harness.Http.Bodies, call => call.Path.Contains("/activities", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JulesSourcePaginationSelectsTheLaterPage()
    {
        using var harness = Harness();
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return path.Contains("pageToken=next", StringComparison.Ordinal)
                    ? Json(Sources(null, ("sources/src-2", "Suusanex", "Codex_Agent_Facade")))
                    : Json(Sources("next", ("sources/other", "other", "repo")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-done", null, "pong", completed: true)));
            }

            return Json(Session("sess-1", "COMPLETED"));
        };

        var done = await harness.WaitAsync("req-page", Request(AgentFacade.JulesAgent, "hello"));
        AssertCompleted(done, "pong");
        Assert.Contains("sources/src-2", harness.Http.Bodies.Single(call => call.Method == HttpMethod.Post.Method).Body, StringComparison.Ordinal);
        Assert.Equal(2, harness.Http.Bodies.Count(call => call.Path.Contains("/sources", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task JulesRepeatedPageTokenFailsBeforeCreate()
    {
        using var harness = Harness();
        harness.Http.Respond = path => Json(Sources("again", ("sources/src-1", "suusanex", "codex_agent_facade")));
        var failed = await harness.WaitAsync("req-loop", Request(AgentFacade.JulesAgent, "hello"));
        Assert.Equal("remote_rejected", failed.Failure!.Kind);
        Assert.DoesNotContain(harness.Http.Bodies, call => call.Method == HttpMethod.Post.Method);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "submission_unknown")]
    [InlineData(HttpStatusCode.BadRequest, "remote_rejected")]
    public async Task JulesCreateResponseBoundaryDoesNotRetry(HttpStatusCode status, string kind)
    {
        using var harness = Harness();
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            return Status(status, "body " + Secret);
        };

        var request = Request(AgentFacade.JulesAgent, "hello");
        var failed = await harness.WaitAsync("req-create-boundary", request);
        Assert.Equal(kind, failed.Failure!.Kind);
        var again = harness.Jobs.Start("req-create-boundary", request);
        Assert.Equal(kind, again.Failure!.Kind);
        Assert.Equal(1, harness.Http.Bodies.Count(call => call.Method == HttpMethod.Post.Method));
        AssertNoSecret(harness.Store);
    }

    [Fact]
    public async Task JulesEmptyCreateBodyIsSubmissionUnknown()
    {
        using var harness = Harness();
        harness.Http.Respond = path => path.Contains("/sources", StringComparison.Ordinal)
            ? Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")))
            : Status(HttpStatusCode.OK);
        var failed = await harness.WaitAsync("req-empty-create", Request(AgentFacade.JulesAgent, "hello"));
        Assert.Equal("submission_unknown", failed.Failure!.Kind);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    public async Task JulesUnreadableCreateBodyIsSubmissionUnknown(string body)
    {
        using var harness = Harness();
        harness.Http.Respond = path => path.Contains("/sources", StringComparison.Ordinal)
            ? Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")))
            : Status(HttpStatusCode.OK, body);
        var request = Request(AgentFacade.JulesAgent, "hello");
        var failed = await harness.WaitAsync("req-unreadable-create", request);
        Assert.Equal("submission_unknown", failed.Failure!.Kind);
        var again = harness.Jobs.Start("req-unreadable-create", request);
        Assert.Equal("submission_unknown", again.Failure!.Kind);
        Assert.Equal(1, harness.Http.Bodies.Count(call => call.Method == HttpMethod.Post.Method));
    }

    [Fact]
    public async Task JulesMissingCredentialFailsOnlyThatJob()
    {
        using var harness = Harness(() => null);
        var failed = await harness.WaitAsync("req-no-key", Request(AgentFacade.JulesAgent, "hello"));
        Assert.Equal("credential_missing", failed.Failure!.Kind);
        Assert.Empty(harness.Http.Bodies);
        AssertNoSecret(harness.Store);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("no-upstream")]
    [InlineData("tip-mismatch")]
    [InlineData("detached-none")]
    [InlineData("detached-ambiguous")]
    [InlineData("no-source")]
    public async Task JulesRepositoryPolicyRejectsNewSession(string policy)
    {
        using var harness = Harness();
        harness.Git.Apply(policy);
        harness.Http.Respond = _ => Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
        var failed = await harness.WaitAsync("req-" + policy, Request(AgentFacade.JulesAgent, "hello"));
        Assert.Equal("repository_rejected", failed.Failure!.Kind);
        Assert.DoesNotContain(harness.Http.Bodies, call => call.Method == HttpMethod.Post.Method);
    }

    [Fact]
    public async Task JulesDetachedHeadUsesTheUniqueMatchingBranch()
    {
        using var harness = Harness();
        harness.Git.Apply("detached-unique");
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-done", null, "pong", completed: true)));
            }

            return Json(Session("sess-1", "COMPLETED"));
        };
        var done = await harness.WaitAsync("req-detached", Request(AgentFacade.JulesAgent, "hello"));
        AssertCompleted(done, "pong");
        Assert.Contains("\"startingBranch\":\"feature\"", harness.Http.Bodies.Single(call => call.Method == HttpMethod.Post.Method).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JulesQuestionAnswerKeepsEveryPatchAndIgnoresOlderActivity()
    {
        using var harness = Harness();
        var prompt = "Option A";
        var sent = 0;
        var polls = 0;
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains(":sendMessage", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref sent);
                return Status(HttpStatusCode.OK);
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                if (sent == 0)
                {
                    return Json(Activities(Activity("act-old", null, "Choose A or B", completed: false, patches: (Commit, PatchOne))));
                }

                var seen = Interlocked.Increment(ref polls);
                if (seen == 1)
                {
                    return Json(Activities(Activity("act-old", null, "Choose A or B", completed: false, patches: (Commit, PatchOne))));
                }

                return Json(Activities(
                    Activity("act-old", null, "Choose A or B", completed: false, patches: (Commit, PatchOne)),
                    Activity("act-user", prompt, null, completed: false),
                    Activity(
                        "act-new",
                        null,
                        "accepted",
                        completed: true,
                        patches:
                        [
                            (Commit, PatchOne),
                            ("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", PatchTwo),
                        ])));
            }

            if (sent == 0)
            {
                return Json(Session("sess-1", "AWAITING_USER_FEEDBACK"));
            }

            return Json(Session("sess-1", polls >= 2 ? "COMPLETED" : "IN_PROGRESS"));
        };

        var question = await harness.WaitAsync("req-question", Request(AgentFacade.JulesAgent, "Ask"));
        Assert.Equal(AgentJobStatus.WaitingForInput, question.Status);
        Assert.Equal("Choose A or B", question.InputPrompt);
        Assert.Equal("Choose A or B", question.Result!.OutputText);
        Assert.NotNull(question.Artifacts);
        Assert.Single(question.Artifacts!);
        Assert.Equal(PatchOne, File.ReadAllText(question.Artifacts![0].PatchPath));

        harness.Git.Inspection = harness.Git.Inspection with { IsDirty = true };
        var answered = await harness.WaitAsync("req-answer", Request(AgentFacade.JulesAgent, prompt, sessionId: "sess-1"));
        AssertCompleted(answered, "accepted");
        Assert.DoesNotContain("Choose A or B", answered.Result!.OutputText, StringComparison.Ordinal);
        Assert.Equal(2, answered.Artifacts!.Count);
        Assert.Equal(PatchOne, File.ReadAllText(answered.Artifacts[0].PatchPath));
        Assert.Equal(PatchTwo, File.ReadAllText(answered.Artifacts[1].PatchPath));
        Assert.Equal(Commit, answered.Artifacts[0].BaseCommitId);
        Assert.Equal("act-new", answered.Artifacts[1].ActivityId);
        Assert.Equal(1, sent);
        Assert.True(polls >= 2);
    }

    [Fact]
    public async Task JulesContinuationRejectsCompletedSessionAndWrongRepository()
    {
        using var harness = Harness();
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-old", null, "done", completed: true)));
            }

            return Json(Session("sess-1", "COMPLETED"));
        };
        var completed = await harness.WaitAsync("req-closed", Request(AgentFacade.JulesAgent, "more", sessionId: "sess-1"));
        Assert.Equal("invalid_request", completed.Failure!.Kind);
        Assert.DoesNotContain(harness.Http.Bodies, call => call.Path.Contains(":sendMessage", StringComparison.Ordinal));

        harness.Git.Inspection = harness.Git.Inspection with
        {
            Remotes = [new GitRemoteEndpoint("origin", "https://github.com/other/repo.git", "other", "repo")],
        };
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-old", null, "Choose", completed: false)));
            }

            return Json(Session("sess-1", "AWAITING_USER_FEEDBACK"));
        };
        var wrong = await harness.WaitAsync("req-wrong-repo", Request(AgentFacade.JulesAgent, "more", sessionId: "sess-1"));
        Assert.Equal("repository_rejected", wrong.Failure!.Kind);
        Assert.DoesNotContain(harness.Http.Bodies, call => call.Path.Contains(":sendMessage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JulesDuplicateUserMessageDoesNotComplete()
    {
        using var harness = Harness();
        var prompt = "same text";
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains(":sendMessage", StringComparison.Ordinal))
            {
                return Status(HttpStatusCode.OK);
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return harness.Http.Bodies.Any(call => call.Path.Contains(":sendMessage", StringComparison.Ordinal))
                    ? Json(Activities(
                        Activity("act-old", null, "Choose", completed: false),
                        Activity("act-1", prompt, "one", completed: false),
                        Activity("act-2", prompt, "two", completed: true)))
                    : Json(Activities(Activity("act-old", null, "Choose", completed: false)));
            }

            return Json(Session("sess-1", "AWAITING_USER_FEEDBACK"));
        };

        var failed = await harness.WaitAsync("req-dup", Request(AgentFacade.JulesAgent, prompt, sessionId: "sess-1"));
        Assert.Equal("activity_correlation_failed", failed.Failure!.Kind);
        Assert.Equal(1, harness.Http.Bodies.Count(call => call.Path.Contains(":sendMessage", StringComparison.Ordinal)));
        var stored = File.ReadAllText(Directory.EnumerateFiles(harness.Store, "req-*.json").Single());
        Assert.Contains("\"sessionLockHeld\":true", stored, StringComparison.Ordinal);
        var blocked = await harness.WaitAsync("req-dup-other", Request(AgentFacade.JulesAgent, "other", sessionId: "sess-1"));
        Assert.Equal("session_locked", blocked.Failure!.Kind);
        Assert.Equal(1, harness.Http.Bodies.Count(call => call.Path.Contains(":sendMessage", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task JulesPlanApprovalAndArtifactFailureStayFailed()
    {
        using var harness = Harness();
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-plan", null, "approve?", completed: false)));
            }

            return Json(Session("sess-1", "AWAITING_PLAN_APPROVAL"));
        };
        var plan = await harness.WaitAsync("req-plan", Request(AgentFacade.JulesAgent, "hello"));
        Assert.Equal("agent_error", plan.Failure!.Kind);

        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains(":sendMessage", StringComparison.Ordinal))
            {
                return Status(HttpStatusCode.OK);
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return harness.Http.Bodies.Any(call => call.Path.Contains(":sendMessage", StringComparison.Ordinal))
                    ? Json(Activities(
                        Activity("act-old", null, "Choose", completed: false),
                        Activity("act-user", "answer", null, completed: false),
                        Activity("act-bad", null, "patch", completed: true, includeCreateTime: false, patches: (Commit, PatchOne))))
                    : Json(Activities(Activity("act-old", null, "Choose", completed: false)));
            }

            return Json(Session("sess-1", "AWAITING_USER_FEEDBACK"));
        };
        var artifact = await harness.WaitAsync("req-artifact", Request(AgentFacade.JulesAgent, "answer", sessionId: "sess-1"));
        Assert.Equal("artifact_persist_failed", artifact.Failure!.Kind);
        var artifactRecord = File.ReadAllText(Directory.EnumerateFiles(harness.Store, "req-*.json").Single(path => path.Contains(HashText("req-artifact"), StringComparison.Ordinal)));
        Assert.Contains("\"sessionLockHeld\":false", artifactRecord, StringComparison.Ordinal);
        var lockText = File.ReadAllText(Path.Combine(harness.Store, "jules-locks", HashText("sess-1") + ".lock"));
        Assert.True(string.IsNullOrWhiteSpace(lockText));
    }

    [Fact]
    public async Task JulesSendUnknownKeepsTheLockUntilAUniqueActivity()
    {
        using var harness = Harness();
        var prompt = "follow up";
        var sendAttempts = 0;
        var reveal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Http.RespondAsync = async (path, _) =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains(":sendMessage", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref sendAttempts);
                return Status(HttpStatusCode.InternalServerError, Secret);
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Volatile.Read(ref sendAttempts) == 0
                    ? Json(Activities(Activity("act-old", null, "Choose", completed: false)))
                    : Json(Activities(
                        Activity("act-old", null, "Choose", completed: false),
                        Activity("act-user", prompt, null, completed: false),
                        Activity("act-done", null, "done", completed: true)));
            }

            if (Volatile.Read(ref sendAttempts) > 0 && !reveal.Task.IsCompleted)
            {
                await reveal.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }

            return Json(Session("sess-1", reveal.Task.IsCompleted ? "COMPLETED" : "AWAITING_USER_FEEDBACK"));
        };

        var started = harness.Jobs.Start("req-send-unknown", Request(AgentFacade.JulesAgent, prompt, sessionId: "sess-1"));
        Assert.True(await WaitUntilAsync(() => JobFileContains(harness.Store, "req-send-unknown", "\"remotePhase\":\"submission_unknown\"")));
        var unknown = harness.Jobs.Get(started.JobId);
        Assert.Equal(AgentJobStatus.Running, unknown.Status);
        Assert.Equal(JulesRemotePhase.SubmissionUnknown, unknown.RemotePhase);
        AssertNoSecret(harness.Store);

        var blocked = await harness.WaitAsync("req-send-other", Request(AgentFacade.JulesAgent, "other", sessionId: "sess-1"));
        Assert.Equal("session_locked", blocked.Failure!.Kind);
        Assert.Equal(1, sendAttempts);
        reveal.TrySetResult();

        var resumed = await harness.Jobs.WaitAsync(started.JobId, 5);
        AssertCompleted(resumed, "done");
        Assert.Equal(1, sendAttempts);
    }

    [Fact]
    public async Task JulesCompletedSessionWithoutTheUserMessageStaysOpenUntilItAppears()
    {
        using var harness = Harness();
        var prompt = "follow up";
        var sends = 0;
        var activityReads = 0;
        var reveal = 0;
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains(":sendMessage", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref sends);
                return Status(HttpStatusCode.InternalServerError);
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref activityReads);
                return Volatile.Read(ref reveal) == 0
                    ? Json(Activities(Activity("act-old", null, "Choose", completed: false)))
                    : Json(Activities(
                        Activity("act-old", null, "Choose", completed: false),
                        Activity("act-user", prompt, null, completed: false),
                        Activity("act-done", null, "done", completed: true)));
            }

            return Json(Session("sess-1", Volatile.Read(ref sends) == 0 ? "AWAITING_USER_FEEDBACK" : "COMPLETED"));
        };

        var started = harness.Jobs.Start("req-late-user", Request(AgentFacade.JulesAgent, prompt, sessionId: "sess-1"));
        var observedReads = -1;
        Assert.True(await WaitUntilAsync(() =>
        {
            var reads = Volatile.Read(ref activityReads);
            var unresolved = JobFileContains(harness.Store, "req-late-user", "\"status\":\"running\"")
                && JobFileContains(harness.Store, "req-late-user", "\"remotePhase\":\"submission_unknown\"")
                && JobFileContains(harness.Store, "req-late-user", "\"sessionLockHeld\":true");
            if (!unresolved || reads < 3)
            {
                observedReads = -1;
                return false;
            }

            if (observedReads != reads)
            {
                observedReads = reads;
                return false;
            }

            return true;
        }));
        Assert.Equal(1, Volatile.Read(ref sends));
        Volatile.Write(ref reveal, 1);

        var done = await harness.Jobs.WaitAsync(started.JobId, 5);
        AssertCompleted(done, "done");
        Assert.Equal(1, Volatile.Read(ref sends));
    }

    [Fact]
    public async Task JulesPollFailureStaysRunningAndResumes()
    {
        using var harness = Harness();
        var polls = 0;
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-done", null, "pong", completed: true)));
            }

            if (path.EndsWith("/sessions", StringComparison.Ordinal))
            {
                return Json(Session("sess-1", "IN_PROGRESS"));
            }

            var seen = Interlocked.Increment(ref polls);
            return seen == 1
                ? Status(HttpStatusCode.BadGateway, Secret)
                : Json(Session("sess-1", "COMPLETED"));
        };

        var started = harness.Jobs.Start("req-poll", Request(AgentFacade.JulesAgent, "hello"));
        Assert.True(await WaitUntilAsync(() => JobFileContains(harness.Store, "req-poll", "\"status\":\"running\"")
            && JobFileContains(harness.Store, "req-poll", "\"remotePhase\":\"communication_error\"")));
        var done = await harness.Jobs.WaitAsync(started.JobId, 5);
        AssertCompleted(done, "pong");
        AssertNoSecret(harness.Store);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.Forbidden, "")]
    [InlineData(HttpStatusCode.OK, "{")]
    public async Task JulesObserverReadFailureStaysRunning(HttpStatusCode status, string body)
    {
        using var harness = Harness();
        var polls = 0;
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-done", null, "pong", completed: true)));
            }

            if (path.EndsWith("/sessions", StringComparison.Ordinal))
            {
                return Json(Session("sess-1", "IN_PROGRESS"));
            }

            var seen = Interlocked.Increment(ref polls);
            return seen == 1
                ? Status(status, body.Length == 0 ? null : body)
                : Json(Session("sess-1", "COMPLETED"));
        };

        var started = harness.Jobs.Start("req-observer", Request(AgentFacade.JulesAgent, "hello"));
        Assert.True(await WaitUntilAsync(() => JobFileContains(harness.Store, "req-observer", "\"status\":\"running\"")
            && JobFileContains(harness.Store, "req-observer", "\"remotePhase\":\"communication_error\"")));
        var done = await harness.Jobs.WaitAsync(started.JobId, 5);
        AssertCompleted(done, "pong");
        Assert.True(polls >= 2);
    }

    [Fact]
    public async Task JulesCrashBeforeSessionIdBecomesSubmissionUnknown()
    {
        using var harness = Harness();
        var request = Request(AgentFacade.JulesAgent, "hello");
        WriteRecord(harness.Store, new AgentJobRecord(
            "job-create-crash",
            "req-create-crash",
            AgentJobStatus.Running,
            AgentJobService.ComputeRequestFingerprint(request),
            null,
            null,
            null,
            new JulesJobState(JulesRemotePhase.SubmittingCreate, JulesDriver.HashPrompt(request.Prompt), [], false, false)));
        var recovered = harness.Jobs.Start("req-create-crash", request);
        Assert.Equal("submission_unknown", recovered.Failure!.Kind);
        Assert.Empty(harness.Http.Bodies);
        var again = harness.Jobs.Start("req-create-crash", request);
        Assert.Equal(recovered.JobId, again.JobId);
        Assert.Equal("submission_unknown", again.Failure!.Kind);
    }

    [Fact]
    public async Task JulesRestartResumesSavedSessionWithoutAnotherPost()
    {
        using var harness = Harness();
        var prompt = "hello";
        WriteRecord(harness.Store, new AgentJobRecord(
            "job-resume",
            "req-resume",
            AgentJobStatus.Running,
            "stored-fingerprint",
            null,
            null,
            null,
            new JulesJobState(
                JulesRemotePhase.Polling,
                JulesDriver.HashPrompt(prompt),
                [],
                false,
                false,
                "sess-1",
                "https://jules.google.com/session/sess-1")));
        harness.Http.Respond = path => path.Contains("/activities", StringComparison.Ordinal)
            ? Json(Activities(Activity("act-done", null, "pong", completed: true)))
            : Json(Session("sess-1", "COMPLETED"));
        var done = await harness.Jobs.WaitAsync("job-resume", 5);
        AssertCompleted(done, "pong");
        Assert.DoesNotContain(harness.Http.Bodies, call => call.Method == HttpMethod.Post.Method);
    }

    [Fact]
    public async Task JulesRestartKeepsSessionLock()
    {
        using var harness = Harness();
        WriteRecord(harness.Store, new AgentJobRecord(
            "job-locked",
            "req-locked",
            AgentJobStatus.Running,
            "stored-fingerprint",
            null,
            null,
            null,
            new JulesJobState(
                JulesRemotePhase.SubmissionUnknown,
                JulesDriver.HashPrompt("answer"),
                ["act-old"],
                true,
                true,
                "sess-1")));
        var blocked = await harness.WaitAsync("req-new-lock", Request(AgentFacade.JulesAgent, "other", sessionId: "sess-1"));
        Assert.Equal("session_locked", blocked.Failure!.Kind);
        Assert.Empty(harness.Http.Bodies);
    }

    [Fact]
    public async Task JulesRunningCreateBlocksContinuationOnTheSameSession()
    {
        using var harness = Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Http.RespondAsync = async (path, _) =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-done", null, "pong", completed: true)));
            }

            if (path.Contains(":sendMessage", StringComparison.Ordinal))
            {
                return Status(HttpStatusCode.OK);
            }

            if (path.EndsWith("/sessions", StringComparison.Ordinal))
            {
                return Json(Session("sess-1", "IN_PROGRESS"));
            }

            await release.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return Json(Session("sess-1", "COMPLETED"));
        };

        var started = harness.Jobs.Start("req-create-open", Request(AgentFacade.JulesAgent, "hello"));
        Assert.True(await WaitUntilAsync(() => JobFileContains(harness.Store, "req-create-open", "\"sessionId\":\"sess-1\"")
            && JobFileContains(harness.Store, "req-create-open", "\"status\":\"running\"")));
        var blocked = await harness.WaitAsync("req-create-continue", Request(AgentFacade.JulesAgent, "more", sessionId: "sess-1"));
        Assert.Equal("session_locked", blocked.Failure!.Kind);
        Assert.DoesNotContain(harness.Http.Bodies, call => call.Path.Contains(":sendMessage", StringComparison.Ordinal));
        release.TrySetResult();
        var done = await harness.Jobs.WaitAsync(started.JobId, 10);
        AssertCompleted(done, "pong");
    }

    [Fact]
    public async Task JulesStaleLockFileDoesNotBlockASettledOwner()
    {
        using var harness = Harness();
        WriteRecord(harness.Store, new AgentJobRecord(
            "job-stale",
            "req-stale",
            AgentJobStatus.Failed,
            "stored-fingerprint",
            null,
            null,
            null,
            new JulesJobState(
                JulesRemotePhase.Polling,
                JulesDriver.HashPrompt("old"),
                [],
                true,
                false,
                "sess-1")));
        var lockDirectory = Path.Combine(harness.Store, "jules-locks");
        Directory.CreateDirectory(lockDirectory);
        File.WriteAllText(
            Path.Combine(lockDirectory, HashText("sess-1") + ".lock"),
            """{"sessionId":"sess-1","jobId":"job-stale","requestId":"req-stale"}""");
        var prompt = "answer";
        harness.Http.Respond = path =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains(":sendMessage", StringComparison.Ordinal))
            {
                return Status(HttpStatusCode.OK);
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return harness.Http.Bodies.Any(call => call.Path.Contains(":sendMessage", StringComparison.Ordinal))
                    ? Json(Activities(
                        Activity("act-old", null, "Choose", completed: false),
                        Activity("act-user", prompt, null, completed: false),
                        Activity("act-done", null, "done", completed: true)))
                    : Json(Activities(Activity("act-old", null, "Choose", completed: false)));
            }

            return Json(Session(
                "sess-1",
                harness.Http.Bodies.Any(call => call.Path.Contains(":sendMessage", StringComparison.Ordinal))
                    ? "COMPLETED"
                    : "AWAITING_USER_FEEDBACK"));
        };

        var done = await harness.WaitAsync("req-after-stale", Request(AgentFacade.JulesAgent, prompt, sessionId: "sess-1"));
        AssertCompleted(done, "done");
        Assert.Equal(1, harness.Http.Bodies.Count(call => call.Path.Contains(":sendMessage", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task JulesResumeWithoutCredentialDoesNotFailTheRemoteSession()
    {
        using var harness = Harness(() => null);
        WriteRecord(harness.Store, new AgentJobRecord(
            "job-no-key",
            "req-no-key-resume",
            AgentJobStatus.Running,
            "stored-fingerprint",
            null,
            null,
            null,
            new JulesJobState(JulesRemotePhase.Polling, JulesDriver.HashPrompt("hello"), [], false, false, "sess-1")));
        _ = harness.Jobs.Get("job-no-key");
        Assert.True(await WaitUntilAsync(() => JobFileContains(harness.Store, "req-no-key-resume", "\"remotePhase\":\"communication_error\"")));
        var suspended = harness.Jobs.Get("job-no-key");
        Assert.Equal(AgentJobStatus.Running, suspended.Status);
        Assert.Equal(JulesRemotePhase.CommunicationError, suspended.RemotePhase);
        Assert.Empty(harness.Http.Bodies);
    }

    [Fact]
    public async Task JulesFileLockBlocksASecondSend()
    {
        using var harness = Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = 0;
        harness.Http.RespondAsync = async (path, _) =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Volatile.Read(ref sent) == 0
                    ? Json(Activities(Activity("act-old", null, "Choose", completed: false)))
                    : Json(Activities(
                        Activity("act-old", null, "Choose", completed: false),
                        Activity("act-user", "first", null, completed: false),
                        Activity("act-done", null, "done", completed: true)));
            }

            if (path.Contains(":sendMessage", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref sent);
                return Status(HttpStatusCode.OK);
            }

            if (harness.Http.Bodies.Count(call => call.Path.Contains("/sessions/sess-1", StringComparison.Ordinal) && !call.Path.Contains("/activities", StringComparison.Ordinal)) == 1)
            {
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }

            return Json(Session("sess-1", Volatile.Read(ref sent) == 0 ? "AWAITING_USER_FEEDBACK" : "COMPLETED"));
        };

        var first = harness.Jobs.Start("req-lock-1", Request(AgentFacade.JulesAgent, "first", sessionId: "sess-1"));
        var sawSession = await WaitUntilAsync(() => harness.Http.Bodies.Any(call => call.Path.Contains("/sessions/sess-1", StringComparison.Ordinal)));
        Assert.True(sawSession);
        var second = await harness.WaitAsync("req-lock-2", Request(AgentFacade.JulesAgent, "second", sessionId: "sess-1"));
        Assert.Equal("session_locked", second.Failure!.Kind);
        release.TrySetResult();
        var done = await harness.Jobs.WaitAsync(first.JobId, 10);
        AssertCompleted(done, "done");
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task JulesCancelIsUnsupportedAndLeavesTheJob()
    {
        using var harness = Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Http.RespondAsync = async (path, _) =>
        {
            if (path.Contains("/sources", StringComparison.Ordinal))
            {
                return Json(Sources(null, ("sources/src-1", "suusanex", "codex_agent_facade")));
            }

            await release.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            if (path.Contains("/activities", StringComparison.Ordinal))
            {
                return Json(Activities(Activity("act-done", null, "pong", completed: true)));
            }

            return Json(Session("sess-1", "COMPLETED"));
        };
        var started = harness.Jobs.Start("req-cancel", Request(AgentFacade.JulesAgent, "hello"));
        var ex = Assert.Throws<InvalidOperationException>(() => harness.Jobs.Cancel(started.JobId));
        Assert.Contains("unsupported", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentJobStatus.Running, harness.Jobs.Get(started.JobId).Status);
        release.TrySetResult();
    }

    [Fact]
    public void JulesCancelOfStoredSessionDoesNotRewriteIt()
    {
        using var harness = Harness();
        WriteRecord(harness.Store, new AgentJobRecord(
            "job-cancel-stored",
            "req-cancel-stored",
            AgentJobStatus.Running,
            "stored-fingerprint",
            null,
            null,
            null,
            new JulesJobState(JulesRemotePhase.Polling, JulesDriver.HashPrompt("hello"), [], false, false, "sess-1")));
        var before = File.ReadAllText(Directory.EnumerateFiles(harness.Store, "req-*.json").Single());
        var ex = Assert.Throws<InvalidOperationException>(() => harness.Jobs.Cancel("job-cancel-stored"));
        Assert.Contains("unsupported", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(Directory.EnumerateFiles(harness.Store, "req-*.json").Single()));
    }

    [Fact]
    public void OldRunningRecordWithoutJulesCheckpointIsInterrupted()
    {
        using var harness = Harness();
        File.WriteAllText(
            Path.Combine(harness.Store, "req-manual.json"),
            """{"jobId":"job-old","requestId":"req-old","status":"running","requestFingerprint":"abc"}""");
        var recovered = harness.Jobs.Get("job-old");
        Assert.Equal(AgentJobStatus.Failed, recovered.Status);
        Assert.Equal("facade_interrupted", recovered.Failure!.Kind);
        Assert.Null(recovered.SessionId);
    }

    [Fact]
    public void PublicSnapshotDoesNotCopyLocalSessionIdToTheTopLevel()
    {
        var result = new AgentRunResult(
            AgentFacade.CodexAgent,
            "sess-local",
            0,
            "ok",
            "raw",
            "run",
            "events",
            "text");
        var json = JsonSerializer.Serialize(
            AgentJobPublicProjection.From(new AgentJobSnapshot("job", "req", AgentJobStatus.Completed, result, null, null)),
            AgentJson.Options);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("sessionId", out _));
        Assert.Equal("sess-local", document.RootElement.GetProperty("result").GetProperty("sessionId").GetString());
    }

    [Fact]
    public void RedactsJulesApiKeyAssignment()
    {
        var redacted = SecretRedactor.RedactText("JULES_API_KEY=" + Secret);
        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Replacement, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/suusanex/codex_agent_facade.git", "suusanex", "codex_agent_facade")]
    [InlineData("git@github.com:suusanex/codex_agent_facade.git", "suusanex", "codex_agent_facade")]
    [InlineData("ssh://git@github.com/suusanex/codex_agent_facade.git", "suusanex", "codex_agent_facade")]
    [InlineData("https://example.com/suusanex/codex_agent_facade.git", null, null)]
    public void ParsesGitHubRemoteUrls(string url, string? owner, string? repository)
    {
        var parsed = GitWorktreeInspector.TryParseGitHubRepository(url, out var parsedOwner, out var parsedRepository);
        Assert.Equal(owner is not null, parsed);
        Assert.Equal(owner, parsedOwner);
        Assert.Equal(repository, parsedRepository);
    }

    private static async Task<AgentRunResult> RunAntigravityAsync(
        string stdout,
        int exitCode,
        string? sessionId = null,
        string standardError = "")
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(exitCode, stdout, standardError),
        };
        var driver = new AntigravityCliDriver(runner);
        return await driver.RunAsync(
            Request(AgentFacade.AntigravityAgent, "hello", sessionId: sessionId),
            TestRunLogs.CreateLog(),
            null,
            CancellationToken.None);
    }

    private static string SuccessStream(string conversationId, string status, string response)
    {
        return "{\"event\":\"init\",\"conversation_id\":\"" + conversationId + "\",\"init\":{}}\n"
            + "{\"event\":\"result\",\"result\":{\"conversation_id\":\"" + conversationId + "\",\"status\":\"" + status + "\",\"response\":\"" + response + "\"}}";
    }

    private static AgentRunRequest Request(
        string agent,
        string prompt,
        string? sessionId = null,
        bool autoApprove = true,
        string? model = null,
        string? effort = null,
        bool? fast = null,
        string? runMode = null,
        IReadOnlyList<string>? skills = null)
    {
        return new AgentRunRequest(agent, prompt, Path.GetTempPath(), sessionId, skills, autoApprove, model, effort, fast, runMode);
    }

    private static AgentJobService JobsFor(RecordingProcessRunner runner, out string store)
    {
        store = Directory.CreateTempSubdirectory("caf-agy-jobs-").FullName;
        return new AgentJobService(Facade(runner, new JulesHttpScript(), new FakeGitWorktree()), store);
    }

    private static AgentFacade Facade(RecordingProcessRunner runner, JulesHttpScript http, FakeGitWorktree git, Func<string?>? apiKey = null)
    {
        return new AgentFacade(
            new GitHubCopilotDriver(runner),
            new GrokBuildDriver(runner),
            new CursorCliDriver(runner),
            new CodexCliDriver(runner),
            TestRunLogs.CreateFactory(),
            NullRunLogViewerLauncher.Instance,
            new AntigravityCliDriver(runner),
            new JulesDriver(http, git, _ => Task.CompletedTask, apiKey ?? (() => Secret)));
    }

    private static JulesHarness Harness(Func<string?>? apiKey = null)
    {
        return new JulesHarness(apiKey);
    }

    private static void AssertCompleted(AgentJobSnapshot snapshot, string output)
    {
        Assert.True(
            snapshot.Status == AgentJobStatus.Completed,
            snapshot.Status + " " + snapshot.Failure?.Kind + " " + snapshot.Failure?.Summary);
        Assert.Equal(output, snapshot.Result!.OutputText);
        Assert.Equal("final_response", snapshot.Result.OutputKind);
        Assert.Null(snapshot.Result.ExitCode);
    }

    private static string Serialize(AgentJobSnapshot snapshot)
    {
        return JsonSerializer.Serialize(AgentJobPublicProjection.From(snapshot), AgentJson.Options);
    }

    private static void AssertNoSecret(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(Secret, File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    private static void WriteRecord(string store, AgentJobRecord record)
    {
        Directory.CreateDirectory(store);
        var path = Path.Combine(store, "req-" + HashText(record.RequestId) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(record, AgentJson.Options));
    }

    private static string HashText(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static bool JobFileContains(string store, string requestId, string text)
    {
        var path = Path.Combine(store, "req-" + HashText(requestId) + ".json");
        return File.Exists(path) && File.ReadAllText(path).Contains(text, StringComparison.Ordinal);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> ready)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(5);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (ready())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    private static string Sources(string? nextPageToken, params (string Name, string Owner, string Repository)[] sources)
    {
        return JsonSerializer.Serialize(
            new
            {
                sources = sources.Select(source => new
                {
                    name = source.Name,
                    githubRepo = new { owner = source.Owner, repo = source.Repository },
                }).ToArray(),
                nextPageToken,
            },
            JsonOptions);
    }

    private static string Session(string sessionId, string state)
    {
        return JsonSerializer.Serialize(new
        {
            name = "sessions/" + sessionId,
            id = "not-the-path-id",
            state,
            url = "https://jules.google.com/session/" + sessionId,
            sourceContext = new
            {
                source = "sources/src-1",
                githubRepoContext = new { startingBranch = "main" },
            },
        });
    }

    private static string Activities(params string[] items)
    {
        return "{\"activities\":[" + string.Join(',', items) + "]}";
    }

    private static string Activity(
        string id,
        string? user,
        string? agent,
        bool completed,
        bool includeCreateTime = true,
        params (string? Commit, string? Diff)[] patches)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["createTime"] = includeCreateTime ? "2026-10-29T01:02:03Z" : null,
            ["userMessaged"] = user is null ? null : new Dictionary<string, string> { ["userMessage"] = user },
            ["agentMessaged"] = agent is null ? null : new Dictionary<string, string> { ["agentMessage"] = agent },
            ["sessionCompleted"] = completed ? new Dictionary<string, string>() : null,
            ["artifacts"] = patches.Length == 0
                ? null
                : patches.Select(patch => new Dictionary<string, object?>
                {
                    ["changeSet"] = new Dictionary<string, object?>
                    {
                        ["gitPatch"] = new Dictionary<string, string?>
                        {
                            ["baseCommitId"] = patch.Commit,
                            ["unidiffPatch"] = patch.Diff,
                        },
                    },
                }).ToArray(),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage Status(HttpStatusCode status, string? body = null)
    {
        return new HttpResponseMessage(status)
        {
            Content = body is null ? null : new StringContent(body, Encoding.UTF8, "text/plain"),
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class JulesHarness : IDisposable
    {
        public JulesHarness(Func<string?>? apiKey)
        {
            Http = new JulesHttpScript();
            Git = new FakeGitWorktree();
            Store = Directory.CreateTempSubdirectory("caf-jules-jobs-").FullName;
            var runner = new RecordingProcessRunner();
            Jobs = new AgentJobService(Facade(runner, Http, Git, apiKey), Store);
        }

        public JulesHttpScript Http { get; }

        public FakeGitWorktree Git { get; }

        public string Store { get; }

        public AgentJobService Jobs { get; }

        public async Task<AgentJobSnapshot> WaitAsync(string requestId, AgentRunRequest request)
        {
            var started = Jobs.Start(requestId, request);
            return await Jobs.WaitAsync(started.JobId, 30);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Store, recursive: true);
            }
            catch (Exception ex)
            {
                CliJson.TraceException(ex);
            }
        }
    }

    private sealed class JulesHttpScript : HttpMessageHandler
    {
        public List<CapturedCall> Bodies { get; } = [];

        public Func<string, HttpResponseMessage>? Respond { get; set; }

        public Func<string, CancellationToken, Task<HttpResponseMessage>>? RespondAsync { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (Bodies)
            {
                Bodies.Add(new CapturedCall(request.Method.Method, path, body));
            }

            if (RespondAsync is not null)
            {
                return await RespondAsync(path, cancellationToken).ConfigureAwait(false);
            }

            return Respond is null ? Status(HttpStatusCode.NotFound) : Respond(path);
        }
    }

    private sealed record CapturedCall(string Method, string Path, string? Body);

    private sealed class FakeGitWorktree : IGitWorktreeInspector
    {
        public GitWorktreeInspection Inspection { get; set; } = Clean();

        public string? RemoteTip { get; set; } = Commit;

        public IReadOnlyList<GitRemoteBranchTip> Tips { get; set; } = [];

        public GitWorktreeInspection Inspect(string workingDirectory)
        {
            return Inspection;
        }

        public string? ReadRemoteTip(string remoteUrl, string branch)
        {
            return RemoteTip;
        }

        public IReadOnlyList<GitRemoteBranchTip> ListBranchTips(string remoteUrl)
        {
            return Tips;
        }

        public void Apply(string policy)
        {
            switch (policy)
            {
                case "dirty":
                    Inspection = Inspection with { IsDirty = true };
                    break;
                case "no-upstream":
                    Inspection = Inspection with { UpstreamRemote = null, UpstreamBranch = null };
                    break;
                case "tip-mismatch":
                    RemoteTip = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
                    break;
                case "detached-none":
                    Inspection = Inspection with { IsDetached = true, BranchName = null, UpstreamRemote = null, UpstreamBranch = null };
                    Tips = [new GitRemoteBranchTip("other", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")];
                    break;
                case "detached-ambiguous":
                    Inspection = Inspection with { IsDetached = true, BranchName = null, UpstreamRemote = null, UpstreamBranch = null };
                    Tips =
                    [
                        new GitRemoteBranchTip("one", Commit),
                        new GitRemoteBranchTip("two", Commit),
                    ];
                    break;
                case "detached-unique":
                    Inspection = Inspection with { IsDetached = true, BranchName = null, UpstreamRemote = null, UpstreamBranch = null };
                    Tips = [new GitRemoteBranchTip("feature", Commit)];
                    break;
                case "no-source":
                    Inspection = Inspection with
                    {
                        Remotes = [new GitRemoteEndpoint("origin", "https://github.com/missing/repo.git", "missing", "repo")],
                    };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown git policy.");
            }
        }

        private static GitWorktreeInspection Clean()
        {
            return new GitWorktreeInspection(
                true,
                false,
                false,
                Commit,
                "main",
                "origin",
                "main",
                [new GitRemoteEndpoint("origin", "https://github.com/suusanex/codex_agent_facade.git", "suusanex", "codex_agent_facade")]);
        }
    }

    private sealed class ScriptedProcessRunner : IProcessRunner
    {
        private readonly string _stdout;
        private readonly Action<CancellationToken> _beforeReturn;

        public ScriptedProcessRunner(string stdout, Action<CancellationToken> beforeReturn)
        {
            _stdout = stdout;
            _beforeReturn = beforeReturn;
        }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            foreach (var line in RecordingProcessRunner.SplitLines(_stdout))
            {
                request.StdoutLineCallback?.Invoke(line);
            }

            _beforeReturn(cancellationToken);
            return Task.FromResult(new ProcessRunResult(0, _stdout, ""));
        }
    }
}
