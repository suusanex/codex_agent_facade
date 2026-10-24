using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

public class GrokPlanSessionTests
{
    private const string SessionA = "01a16d09-d03d-7d92-b104-cb45cc1d0650";
    private const string SessionB = "01a16d09-d03d-7d92-b104-cb45cc1d0651";
    private const string Plan = "# Probe\n\nThis plan records a single probe sentence and makes no repository changes.\n";

    [Fact]
    public void HeadlessArgumentsStayOnTheExistingGrokPath()
    {
        var request = new AgentRunRequest(AgentFacade.GrokBuildAgent, "hello", Path.GetTempPath(), null, null, Model: "grok-4.7", ReasoningEffort: "low");
        var headless = GrokBuildDriver.BuildArguments(request);
        Assert.Contains("-p", headless);
        Assert.Contains("--reasoning-effort", headless);
        Assert.DoesNotContain("stdio", headless);

        var plan = GrokBuildDriver.BuildPlanArguments(request with { RunMode = "plan", AutoApprove = false });
        Assert.Equal(new[] { "agent", "--no-leader", "--model", "grok-4.7", "stdio" }, plan);
        Assert.DoesNotContain("-p", plan);
        Assert.DoesNotContain("--reasoning-effort", plan);
        Assert.DoesNotContain("--no-auto-update", plan);
        Assert.Contains("--always-approve", GrokBuildDriver.BuildPlanArguments(request with { RunMode = "plan" }));
    }

    [Fact]
    public async Task NewPlanWritesArtifactAndDoesNotUseHeadlessRunner()
    {
        using var directory = new TempWorkspace();
        var factory = new ScriptedGrokAcpFactory();
        var runner = new RecordingProcessRunner();
        var result = await RunAsync(Request(directory.Path, skills: ["review"]), factory, runner);

        Assert.Equal(0, runner.CallCount);
        Assert.Equal(SessionA, result.SessionId);
        Assert.Equal("plan", result.OutputKind);
        Assert.Equal(Plan, result.OutputText);
        Assert.Null(result.ExitCode);
        Assert.Equal(".codex-agent-facade/plans/" + SessionA + ".md", result.PlanPath);
        Assert.Equal(Plan, File.ReadAllText(directory.PlanFile(SessionA)));
        var start = Assert.Single(factory.Starts);
        Assert.Contains("agent", start.Arguments);
        Assert.Contains("stdio", start.Arguments);
        Assert.Contains("--always-approve", start.Arguments);
        Assert.DoesNotContain("-p", start.Arguments);
        Assert.DoesNotContain("--reasoning-effort", start.Arguments);
        var written = string.Join("\n", factory.Last!.Written);
        Assert.Contains("/review\\n", written, StringComparison.Ordinal);
        Assert.Contains("\"method\":\"session/cancel\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"outcome\":\"abandoned\"", written, StringComparison.Ordinal);
        var projected = AgentJobPublicProjection.From(result);
        Assert.Equal(result.PlanPath, projected.PlanPath);
        var json = JsonSerializer.Serialize(projected, AgentJson.Options);
        Assert.Contains("\"outputKind\":\"plan\"", json, StringComparison.Ordinal);
        Assert.Contains("\"planPath\":\"" + result.PlanPath + "\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameSessionUpdatesTheSameFileAndDifferentSessionsDoNotCollide()
    {
        using var directory = new TempWorkspace();
        var firstFactory = new ScriptedGrokAcpFactory();
        var first = await RunAsync(Request(directory.Path), firstFactory, new RecordingProcessRunner());
        Assert.Equal(Plan, File.ReadAllText(directory.PlanFile(SessionA)));

        var revised = Plan + "Revised.\n";
        var secondFactory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { PlanContent = revised },
        };
        var second = await RunAsync(Request(directory.Path, SessionA), secondFactory, new RecordingProcessRunner());
        Assert.Equal(SessionA, second.SessionId);
        Assert.Equal(first.PlanPath, second.PlanPath);
        Assert.Equal(revised, File.ReadAllText(directory.PlanFile(SessionA)));
        Assert.Contains("session/load", string.Join("\n", secondFactory.Last!.Written), StringComparison.Ordinal);
        var loadResponse = secondFactory.Last.Responses.Single(line => line.Contains("\"configOptions\"", StringComparison.Ordinal));
        Assert.DoesNotContain("sessionId", loadResponse, StringComparison.Ordinal);

        var otherFactory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { SessionId = SessionB, PlanContent = "other\n" },
        };
        await RunAsync(Request(directory.Path), otherFactory, new RecordingProcessRunner());
        Assert.Equal(revised, File.ReadAllText(directory.PlanFile(SessionA)));
        Assert.Equal("other\n", File.ReadAllText(directory.PlanFile(SessionB)));
    }

    [Fact]
    public async Task MissingPlanContentDoesNotOverwriteAnExistingFile()
    {
        using var directory = new TempWorkspace();
        var existing = directory.PlanFile(SessionA);
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "OLD PLAN\n");
        var factory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { PlanContent = " \n\t" },
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Request(directory.Path, SessionA), factory, new RecordingProcessRunner()));
        Assert.Equal("internal_error", failure.Data[CliJson.FailureKindKey]);
        Assert.Contains("did not return plan content", failure.Message, StringComparison.Ordinal);
        Assert.Equal("OLD PLAN\n", File.ReadAllText(existing));
    }

    [Fact]
    public async Task UnsafeSessionIdsFailWithoutWriting()
    {
        using var directory = new TempWorkspace();
        foreach (var sessionId in new[] { "../evil", ".\\evil", "a:b", ".", ".." })
        {
            var factory = new ScriptedGrokAcpFactory();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Request(directory.Path, sessionId), factory, new RecordingProcessRunner()));
            Assert.Equal("internal_error", failure.Data[CliJson.FailureKindKey]);
            Assert.Empty(factory.Starts);
        }

        var returned = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { SessionId = "../evil" },
        };
        var returnedFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Request(directory.Path), returned, new RecordingProcessRunner()));
        Assert.Equal("internal_error", returnedFailure.Data[CliJson.FailureKindKey]);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(directory.Path, "..", "evil.md"))));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, ".codex-agent-facade")));
    }

    [Fact]
    public async Task LoadSessionMismatchFailsWithoutWriting()
    {
        using var directory = new TempWorkspace();
        var factory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { LoadSessionId = SessionB },
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Request(directory.Path, SessionA), factory, new RecordingProcessRunner()));
        Assert.Equal("session_mismatch", failure.Data[CliJson.FailureKindKey]);
        Assert.False(File.Exists(directory.PlanFile(SessionA)));
        Assert.False(File.Exists(directory.PlanFile(SessionB)));
    }

    [Fact]
    public async Task EndTurnAfterPlanCancelIsNotSuccess()
    {
        using var directory = new TempWorkspace();
        var factory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { StopReason = "end_turn" },
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Request(directory.Path), factory, new RecordingProcessRunner()));
        Assert.Equal("internal_error", failure.Data[CliJson.FailureKindKey]);
        Assert.Contains("end_turn", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(directory.PlanFile(SessionA)));
    }

    [Fact]
    public async Task ToolCallAfterAbandonFailsWithoutWriting()
    {
        using var directory = new TempWorkspace();
        var factory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { ToolCallAfterAbandon = true },
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Request(directory.Path), factory, new RecordingProcessRunner()));
        Assert.Equal("internal_error", failure.Data[CliJson.FailureKindKey]);
        Assert.Contains("tool call", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(directory.PlanFile(SessionA)));
    }

    [Fact]
    public async Task CompletedToolCallUpdateDoesNotFailThePlan()
    {
        using var directory = new TempWorkspace();
        var factory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { ToolCallUpdateAfterAbandon = true },
        };
        var result = await RunAsync(Request(directory.Path), factory, new RecordingProcessRunner());
        Assert.Equal(Plan, result.OutputText);
        Assert.Equal(Plan, File.ReadAllText(directory.PlanFile(SessionA)));
    }

    [Fact]
    public async Task UnsupportedReverseRequestFailsWithoutWriting()
    {
        using var directory = new TempWorkspace();
        var factory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { ReverseMethodInsteadOfExit = "session/request_permission" },
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Request(directory.Path), factory, new RecordingProcessRunner()));
        Assert.Equal("internal_error", failure.Data[CliJson.FailureKindKey]);
        Assert.Contains("session/request_permission", failure.Message, StringComparison.Ordinal);
        Assert.Contains("\"code\":-32601", string.Join("\n", factory.Last!.Written), StringComparison.Ordinal);
        Assert.False(File.Exists(directory.PlanFile(SessionA)));
    }

    [Fact]
    public async Task ConfigOptionUsesAJsonStringAndMismatchDoesNotWrite()
    {
        using var directory = new TempWorkspace();
        var applied = new ScriptedGrokAcpFactory();
        await RunAsync(Request(directory.Path, model: "grok-4.5", effort: "low"), applied, new RecordingProcessRunner());
        var written = string.Join("\n", applied.Last!.Written);
        Assert.Contains("\"configId\":\"model\"", written, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"grok-4.5\"", written, StringComparison.Ordinal);
        Assert.Contains("\"configId\":\"reasoning_effort\"", written, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"low\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"value\":{", written, StringComparison.Ordinal);

        var matched = new ScriptedGrokAcpFactory();
        await RunAsync(Request(directory.Path, model: "grok-4.7"), matched, new RecordingProcessRunner());
        Assert.DoesNotContain("session/set_config_option", string.Join("\n", matched.Last!.Written), StringComparison.Ordinal);

        var mismatch = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { MismatchAppliedValue = true },
        };
        var mismatchFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            Request(directory.Path, effort: "low"),
            mismatch,
            new RecordingProcessRunner()));
        Assert.Equal("option_not_applied", mismatchFailure.Data[CliJson.FailureKindKey]);
        Assert.Equal(Plan, File.ReadAllText(directory.PlanFile(SessionA)));

        var missing = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { OmitEffortOption = true },
        };
        var missingDirectory = Directory.CreateTempSubdirectory("caf-plan-missing-");
        try
        {
            var missingFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
                Request(missingDirectory.FullName, effort: "low"),
                missing,
                new RecordingProcessRunner()));
            Assert.Equal("option_not_applied", missingFailure.Data[CliJson.FailureKindKey]);
            Assert.False(Directory.Exists(Path.Combine(missingDirectory.FullName, ".codex-agent-facade")));
        }
        finally
        {
            missingDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task IgnoredEffortOnStderrFailsWithoutWriting()
    {
        using var directory = new TempWorkspace();
        var factory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { StandardError = "ignoring --effort/--reasoning-effort" },
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            Request(directory.Path, effort: "low"),
            factory,
            new RecordingProcessRunner()));
        Assert.Equal("option_not_applied", failure.Data[CliJson.FailureKindKey]);
        Assert.False(File.Exists(directory.PlanFile(SessionA)));
    }

    [Fact]
    public async Task ModelChangeUsesTheUpdatedReasoningEffortList()
    {
        using var directory = new TempWorkspace();
        var refreshed = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { Effort = "low", EffortAfterModelSet = "high" },
        };
        await RunAsync(Request(directory.Path, model: "grok-4.5", effort: "low"), refreshed, new RecordingProcessRunner());
        var written = string.Join("\n", refreshed.Last!.Written);
        Assert.Contains("\"configId\":\"model\"", written, StringComparison.Ordinal);
        Assert.Contains("\"configId\":\"reasoning_effort\"", written, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"low\"", written, StringComparison.Ordinal);

        var dropped = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript { OmitEffortAfterModel = true },
        };
        var droppedDirectory = Directory.CreateTempSubdirectory("caf-plan-effort-");
        try
        {
            var droppedFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
                Request(droppedDirectory.FullName, model: "grok-4.5", effort: "low"),
                dropped,
                new RecordingProcessRunner()));
            Assert.Equal("option_not_applied", droppedFailure.Data[CliJson.FailureKindKey]);
            Assert.Contains("reasoning_effort", droppedFailure.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(droppedDirectory.FullName, ".codex-agent-facade")));
        }
        finally
        {
            droppedDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task CancelledPlanDoesNotStartTheProcess()
    {
        using var directory = new TempWorkspace();
        var factory = new ScriptedGrokAcpFactory();
        var runner = new RecordingProcessRunner();
        var driver = new GrokBuildDriver(runner, TimeProvider.System, FileGrokUnifiedLogReader.CreateDefault(), factory);
        await using var log = TestRunLogs.CreateLog();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.RunAsync(
            Request(directory.Path),
            log,
            null,
            cancelled.Token));
        Assert.Empty(factory.Starts);
        Assert.False(File.Exists(directory.PlanFile(SessionA)));
    }

    [Fact]
    public async Task PlanFailureWritesStderrToTheRunLog()
    {
        using var directory = new TempWorkspace();
        var factory = new ScriptedGrokAcpFactory
        {
            Script = new ScriptedGrokPlanScript
            {
                PlanContent = " ",
                StandardError = "not authenticated",
            },
        };
        var runner = new RecordingProcessRunner();
        var driver = new GrokBuildDriver(runner, TimeProvider.System, FileGrokUnifiedLogReader.CreateDefault(), factory);
        await using var log = TestRunLogs.CreateLog();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunAsync(
            Request(directory.Path),
            log,
            null,
            CancellationToken.None));
        Assert.Equal("internal_error", failure.Data[CliJson.FailureKindKey]);
        Assert.Contains("not authenticated", TestRunLogs.ReadShared(log.EventsPath), StringComparison.Ordinal);
        Assert.False(File.Exists(directory.PlanFile(SessionA)));
    }

    [Fact]
    public async Task CliPlanJsonReturnsPlanFieldsAndUnknownModeIsInvalid()
    {
        using var directory = new TempWorkspace();
        var runner = new RecordingProcessRunner();
        var factory = new ScriptedGrokAcpFactory();
        var facade = Facade(runner, factory);
        await using var writer = new StringWriter();
        var code = await AgentCli.ExecuteAsync(
            facade,
            JsonSerializer.Serialize(new
            {
                agent = AgentFacade.GrokBuildAgent,
                prompt = "Write a plan.",
                working_directory = directory.Path,
                run_mode = "plan",
            }),
            writer,
            CancellationToken.None,
            @"C:\facade-cli-test\cli.4242.log");
        Assert.Equal(AgentCli.ExitCompleted, code);
        using var document = JsonDocument.Parse(writer.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("plan", result.GetProperty("outputKind").GetString());
        Assert.Equal(SessionA, result.GetProperty("sessionId").GetString());
        Assert.Equal(".codex-agent-facade/plans/" + SessionA + ".md", result.GetProperty("planPath").GetString());
        Assert.Equal(Plan, result.GetProperty("outputText").GetString());
        Assert.False(result.TryGetProperty("exitCode", out _));
        Assert.Equal(0, runner.CallCount);
        Assert.Equal(Plan, File.ReadAllText(directory.PlanFile(SessionA)));

        var unknownRunner = new RecordingProcessRunner();
        var unknownFactory = new ScriptedGrokAcpFactory();
        await using var unknownWriter = new StringWriter();
        var unknown = await AgentCli.ExecuteAsync(
            Facade(unknownRunner, unknownFactory),
            JsonSerializer.Serialize(new
            {
                agent = AgentFacade.GrokBuildAgent,
                prompt = "Write a plan.",
                working_directory = directory.Path,
                run_mode = "execute",
            }),
            unknownWriter,
            CancellationToken.None,
            @"C:\facade-cli-test\cli.4242.log");
        Assert.Equal(AgentCli.ExitInvalidRequest, unknown);
        Assert.Equal(0, unknownRunner.CallCount);
        Assert.Empty(unknownFactory.Starts);

        var headlessRunner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, "{\"type\":\"text\",\"data\":\"ok\"}\n{\"type\":\"end\",\"sessionId\":\"" + SessionA + "\"}", ""),
        };
        var headlessFactory = new ScriptedGrokAcpFactory();
        await using var headlessWriter = new StringWriter();
        var headless = await AgentCli.ExecuteAsync(
            Facade(headlessRunner, headlessFactory),
            JsonSerializer.Serialize(new
            {
                agent = AgentFacade.GrokBuildAgent,
                prompt = "hello",
                working_directory = directory.Path,
            }),
            headlessWriter,
            CancellationToken.None,
            @"C:\facade-cli-test\cli.4242.log");
        Assert.Equal(AgentCli.ExitCompleted, headless);
        Assert.Contains("-p", headlessRunner.LastRequest!.Arguments);
        Assert.Empty(headlessFactory.Starts);
    }

    private static async Task<AgentRunResult> RunAsync(AgentRunRequest request, ScriptedGrokAcpFactory factory, RecordingProcessRunner runner)
    {
        var driver = new GrokBuildDriver(runner, TimeProvider.System, FileGrokUnifiedLogReader.CreateDefault(), factory);
        await using var log = TestRunLogs.CreateLog();
        return await driver.RunAsync(request, log, null, CancellationToken.None);
    }

    private static AgentFacade Facade(RecordingProcessRunner runner, ScriptedGrokAcpFactory factory)
    {
        return new AgentFacade(
            new GitHubCopilotDriver(runner),
            new GrokBuildDriver(runner, TimeProvider.System, FileGrokUnifiedLogReader.CreateDefault(), factory),
            new CursorCliDriver(runner),
            new CodexCliDriver(runner),
            TestRunLogs.CreateFactory(),
            NullRunLogViewerLauncher.Instance);
    }

    private static AgentRunRequest Request(
        string directory,
        string? sessionId = null,
        string? model = null,
        string? effort = null,
        string[]? skills = null)
    {
        return new AgentRunRequest(
            AgentFacade.GrokBuildAgent,
            "Write a plan.",
            directory,
            sessionId,
            skills,
            Model: model,
            ReasoningEffort: effort,
            RunMode: "plan");
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = Directory.CreateTempSubdirectory("caf-plan-").FullName;
        }

        public string Path { get; }

        public string PlanFile(string sessionId)
        {
            return System.IO.Path.Combine(Path, ".codex-agent-facade", "plans", sessionId + ".md");
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}

internal sealed class ScriptedGrokPlanScript
{
    public string SessionId { get; set; } = "01a16d09-d03d-7d92-b104-cb45cc1d0650";

    public string? LoadSessionId { get; set; }

    public string PlanContent { get; set; } = "# Probe\n\nThis plan records a single probe sentence and makes no repository changes.\n";

    public string StopReason { get; set; } = "cancelled";

    public bool ToolCallAfterAbandon { get; set; }

    public bool ToolCallUpdateAfterAbandon { get; set; }

    public string? ReverseMethodInsteadOfExit { get; set; }

    public string Model { get; set; } = "grok-4.7";

    public string Effort { get; set; } = "high";

    public bool OmitEffortOption { get; set; }

    public string? EffortAfterModelSet { get; set; }

    public bool OmitEffortAfterModel { get; set; }

    public bool MismatchAppliedValue { get; set; }

    public string StandardError { get; set; } = "";
}

internal sealed class ScriptedGrokAcpFactory : IGrokAcpProcessFactory
{
    public List<GrokAcpStartInfo> Starts { get; } = new();

    public ScriptedGrokPlanScript Script { get; set; } = new();

    public ScriptedGrokAcpPeer? Last { get; private set; }

    public IGrokAcpProcess Start(GrokAcpStartInfo info)
    {
        Starts.Add(info);
        Last = new ScriptedGrokAcpPeer(Script, info.OnStandardError);
        return Last;
    }
}

internal sealed class ScriptedGrokAcpPeer : IGrokAcpProcess
{
    private readonly ScriptedGrokPlanScript _script;
    private readonly Action<string>? _onStandardError;
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private readonly List<string> _responses = new();
    private string? _promptId;

    public ScriptedGrokAcpPeer(ScriptedGrokPlanScript script, Action<string>? onStandardError)
    {
        _script = script;
        _onStandardError = onStandardError;
    }

    public List<string> Written { get; } = new();

    public IReadOnlyList<string> Responses => _responses;

    public ProcessLaunchInfo Launch { get; } = new(
        "grok",
        "grok",
        "grok",
        ["agent", "stdio"],
        ["agent", "stdio"],
        false);

    public bool ExitObserved => true;

    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        Written.Add(line);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("method", out var method))
        {
            return Task.CompletedTask;
        }

        var name = method.GetString();
        if (name == "session/cancel")
        {
            if (_script.ToolCallAfterAbandon)
            {
                Enqueue("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"update\":{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"edit-1\"}}}");
            }
            else
            {
                if (_script.ToolCallUpdateAfterAbandon)
                {
                    Enqueue("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"update\":{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"exit\"}}}");
                }

                Enqueue("{\"jsonrpc\":\"2.0\",\"id\":" + (_promptId ?? "1") + ",\"result\":{\"stopReason\":" + GrokPlanSession.Quote(_script.StopReason) + "}}");
            }

            return Task.CompletedTask;
        }

        var id = root.GetProperty("id").GetRawText();
        switch (name)
        {
            case "initialize":
                Enqueue("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":1}}");
                break;
            case "session/new":
                Enqueue("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"sessionId\":" + GrokPlanSession.Quote(_script.SessionId) + ",\"configOptions\":" + Config() + "}}");
                break;
            case "session/load":
                var loaded = new StringBuilder();
                loaded.Append("{\"configOptions\":").Append(Config());
                if (_script.LoadSessionId is not null)
                {
                    loaded.Append(",\"sessionId\":").Append(GrokPlanSession.Quote(_script.LoadSessionId));
                }

                loaded.Append('}');
                Enqueue("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + loaded + "}");
                break;
            case "session/set_config_option":
                var configId = root.GetProperty("params").GetProperty("configId").GetString();
                var requested = root.GetProperty("params").GetProperty("value").GetString() ?? string.Empty;
                var applied = _script.MismatchAppliedValue ? "wrong" : requested;
                var model = configId == "model" ? applied : _script.Model;
                var effort = configId == "reasoning_effort"
                    ? applied
                    : _script.EffortAfterModelSet ?? _script.Effort;
                var options = configId == "model" && _script.OmitEffortAfterModel
                    ? "[{\"id\":\"model\",\"currentValue\":" + GrokPlanSession.Quote(model) + "}]"
                    : "[{\"id\":\"model\",\"currentValue\":" + GrokPlanSession.Quote(model) + "},{\"id\":\"reasoning_effort\",\"currentValue\":" + GrokPlanSession.Quote(effort) + "}]";
                Enqueue("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"configOptions\":" + options + "}}");
                break;
            case "session/set_mode":
                Enqueue("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{}}");
                break;
            case "session/prompt":
                _promptId = id;
                Enqueue("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"update\":{\"sessionUpdate\":\"current_mode_update\",\"currentModeId\":\"plan\"}}}");
                if (_script.ReverseMethodInsteadOfExit is not null)
                {
                    Enqueue("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":" + GrokPlanSession.Quote(_script.ReverseMethodInsteadOfExit) + ",\"params\":{}}");
                }
                else
                {
                    Enqueue("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"_x.ai/exit_plan_mode\",\"params\":{\"sessionId\":" + GrokPlanSession.Quote(_script.SessionId) + ",\"toolCallId\":\"call-1\",\"planContent\":" + GrokPlanSession.Quote(_script.PlanContent) + "}}");
                }

                break;
        }

        return Task.CompletedTask;
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public string CopyStandardError()
    {
        return _script.StandardError;
    }

    public void Kill()
    {
    }

    public ValueTask DisposeAsync()
    {
        if (!string.IsNullOrEmpty(_script.StandardError))
        {
            foreach (var line in _script.StandardError.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                _onStandardError?.Invoke(line);
            }
        }

        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void Enqueue(string line)
    {
        _responses.Add(line);
        _inbound.Writer.TryWrite(line);
    }

    private string Config()
    {
        var model = "{\"id\":\"model\",\"currentValue\":" + GrokPlanSession.Quote(_script.Model) + "}";
        if (_script.OmitEffortOption)
        {
            return "[" + model + "]";
        }

        return "[" + model + ",{\"id\":\"reasoning_effort\",\"currentValue\":" + GrokPlanSession.Quote(_script.Effort) + "}]";
    }
}
