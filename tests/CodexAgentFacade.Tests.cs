#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property PackAsTool=false
#:property NoWarn=CA2266
#:package Ardalis.SingleFileTestRunner.xUnitV3@1.1.0
#:package Microsoft.Extensions.TimeProvider.Testing@9.0.0
#:package ModelContextProtocol.AspNetCore@2.2.0
#:package NLog.Extensions.Logging@6.2.0
#:include ../src/AgentFacade.cs
#:include ../src/ProcessRunner.cs
#:include ../src/AgentRunLog.cs
#:include ../src/SecretRedactor.cs
#:include ../src/FacadeLogging.cs
#:include ../src/GitHubCopilotDriver.cs
#:include ../src/GrokBuildDriver.cs
#:include ../src/CursorCliDriver.cs
#:include ../src/AgentTools.cs
#:include ../src/McpPublicContract.cs
#:include ../src/AgentJob.cs
#:include ../src/AgentJobService.cs
#:include ../src/McpHttpHost.cs

using System.Diagnostics;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Ardalis.SingleFileTestRunner;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NLog.Targets;
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
    public TaskCompletionSource<bool>? Gate { get; set; }
    public CancellationToken LastCancellationToken { get; private set; }
    private int _callCount;
    public int CallCount => _callCount;
    public StubProcessLifetime ProcessLifetime { get; } = new();

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        Interlocked.Increment(ref _callCount);
        LastCancellationToken = cancellationToken;
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
            request.Arguments,
            UsedWindowsCmdWrapper: false));

        if (Gate is not null)
        {
            using var registration = cancellationToken.Register(() => Gate.TrySetCanceled(cancellationToken));
            await Gate.Task.ConfigureAwait(false);
        }

        foreach (var line in SplitLines(Result.StandardOutput))
        {
            request.StdoutLineCallback?.Invoke(line);
        }

        foreach (var line in SplitLines(Result.StandardError))
        {
            request.StderrLineCallback?.Invoke(line);
        }

        return Result;
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
        Assert.Contains(AgentFacade.GitHubCopilotAgent, ex.Message, StringComparison.Ordinal);
        Assert.Contains(AgentFacade.GrokBuildAgent, ex.Message, StringComparison.Ordinal);
        Assert.Contains(AgentFacade.CursorAgent, ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task RoutesCursor()
    {
        var facade = CreateFacade(out var runner);
        runner.Result = new ProcessRunResult(
            0,
            """
            {"type":"system","subtype":"init","session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"pong"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"pong","session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}
            """,
            "");
        var result = await facade.RunAsync(
            new AgentRunRequest(AgentFacade.CursorAgent, "hello", Path.GetTempPath(), null, null),
            onStdoutLine: null,
            CancellationToken.None);
        Assert.Equal(CursorCliDriver.FileName, runner.LastRequest!.FileName);
        Assert.Equal(AgentFacade.CursorAgent, result.Agent);
        Assert.Equal("pong", result.OutputText);
        Assert.Equal("c6b62c6f-7ead-4fd6-9922-e952131177ff", result.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(result.RunId));
        Assert.True(File.Exists(result.EventsLogPath));
        Assert.True(File.Exists(result.TextLogPath));
    }

    private static AgentFacade CreateFacade(out RecordingProcessRunner runner)
    {
        return CreateFacade(out runner, out _);
    }

    internal static AgentFacade CreateFacade(out RecordingProcessRunner runner, out AgentRunLogFactory factory)
    {
        runner = new RecordingProcessRunner();
        factory = TestRunLogs.CreateFactory();
        return new AgentFacade(
            new GitHubCopilotDriver(runner),
            new GrokBuildDriver(runner),
            new CursorCliDriver(runner),
            factory);
    }
}

public class AgentJobServiceTests
{
    [Fact]
    public void StartReturnsRunningBeforeProcessCompletes()
    {
        var service = CreateService(out var runner, grokText: "ok");
        runner.Gate = NewGate();
        var started = service.Start("req-1", Grok("hello"));
        Assert.Equal(AgentJobStatus.Running, started.Status);
        Assert.Equal("req-1", started.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(started.JobId));
        Assert.Equal(1, runner.CallCount);
        runner.Gate.SetResult(true);
    }

    [Fact]
    public async Task SameRequestIdDoesNotStartSecondWorker()
    {
        var service = CreateService(out var runner, grokText: "ok");
        runner.Gate = NewGate();
        var first = service.Start("req-dup", Grok("hello"));
        var second = service.Start("req-dup", Grok("hello"));
        Assert.Equal(first.JobId, second.JobId);
        Assert.Equal(1, runner.CallCount);
        runner.Gate.SetResult(true);
        var completed = await WaitAsync(service, first.JobId);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);
        Assert.Equal("ok", completed.Result!.OutputText);
        Assert.Equal(first.JobId, completed.Result.RunId);
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task SameRequestIdWithDifferentPromptFailsAndKeepsOriginalJob()
    {
        var service = CreateService(out var runner, grokText: "ok");
        runner.Gate = NewGate();
        var first = service.Start("req-conflict", Grok("hello"));
        var ex = Assert.Throws<ArgumentException>(() => service.Start("req-conflict", Grok("other")));
        Assert.Contains("already bound", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal(AgentJobStatus.Running, service.Get(first.JobId).Status);
        runner.Gate.SetResult(true);
        var completed = await WaitAsync(service, first.JobId);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);
    }

    [Fact]
    public void UnspecifiedModelMatchesFingerprintFromBeforeModelField()
    {
        var request = new AgentRunRequest(
            AgentFacade.CursorAgent,
            "hello",
            @"C:\repo",
            "sess",
            ["review"],
            AutoApprove: false);
        var historical = HistoricalFingerprintWithoutModel(request);
        Assert.Equal(historical, AgentJobService.ComputeRequestFingerprint(request));
        Assert.Equal(historical, AgentJobService.ComputeRequestFingerprint(request with { Model = null }));
        Assert.Equal(historical, AgentJobService.ComputeRequestFingerprint(request with { Model = "" }));
        Assert.Equal(historical, AgentJobService.ComputeRequestFingerprint(request with { Model = " \t " }));
        var composer = AgentJobService.ComputeRequestFingerprint(request with { Model = "composer-2" });
        var gpt = AgentJobService.ComputeRequestFingerprint(request with { Model = "gpt-5" });
        Assert.NotEqual(historical, composer);
        Assert.NotEqual(composer, gpt);
    }

    [Fact]
    public void ModelFingerprintDoesNotMatchSkillTextThatLooksLikeModelMarker()
    {
        var baseRequest = new AgentRunRequest(
            AgentFacade.CursorAgent,
            "hello",
            @"C:\repo",
            null,
            null,
            AutoApprove: true);
        var specified = baseRequest with { Model = "composer-2" };
        var skillNamedLikeMarker = baseRequest with { Skills = ["model=composer-2"] };
        var skillNamedLikeSuffix = baseRequest with { Skills = ["\u0001model=composer-2"] };
        var skillAndModel = baseRequest with { Skills = ["review"], Model = "composer-2" };
        var skillsOnly = baseRequest with { Skills = ["review", "model=composer-2"] };

        var specifiedFingerprint = AgentJobService.ComputeRequestFingerprint(specified);
        Assert.NotEqual(specifiedFingerprint, AgentJobService.ComputeRequestFingerprint(skillNamedLikeMarker));
        Assert.NotEqual(specifiedFingerprint, AgentJobService.ComputeRequestFingerprint(skillNamedLikeSuffix));
        Assert.NotEqual(
            AgentJobService.ComputeRequestFingerprint(skillAndModel),
            AgentJobService.ComputeRequestFingerprint(skillsOnly));
        Assert.NotEqual(specifiedFingerprint, AgentJobService.ComputeRequestFingerprint(baseRequest));
    }

    [Fact]
    public async Task ExactRetryWithSameModelDoesNotStartSecondWorker()
    {
        var service = CreateService(out var runner, grokText: "ok");
        runner.Gate = NewGate();
        var request = Grok("hello") with { Model = "grok-4" };
        var first = service.Start("req-model-retry", request);
        var second = service.Start("req-model-retry", request);
        Assert.Equal(first.JobId, second.JobId);
        Assert.Equal(1, runner.CallCount);
        runner.Gate.SetResult(true);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StoredRequestWithoutModelAcceptsUnspecifiedModelOnRetry()
    {
        var store = Directory.CreateTempSubdirectory("caf-jobs-model-").FullName;
        var first = CreateService(out _, grokText: "once", store);
        var started = first.Start("req-model-compat", Grok("hello"));
        var completed = await WaitAsync(first, started.JobId);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);

        var second = CreateService(out var runner2, grokText: "other", store);
        foreach (var model in new string?[] { null, "", "   " })
        {
            var recovered = second.Start("req-model-compat", Grok("hello") with { Model = model });
            Assert.Equal(started.JobId, recovered.JobId);
            Assert.Equal(AgentJobStatus.Completed, recovered.Status);
            Assert.Equal("once", recovered.Result!.OutputText);
        }

        var conflict = Assert.Throws<ArgumentException>(() =>
            second.Start("req-model-compat", Grok("hello") with { Model = "grok-4" }));
        Assert.Contains("already bound", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner2.CallCount);
    }

    [Fact]
    public void ModelLineBreakIsRejectedBeforeLaunch()
    {
        var service = CreateService(out var runner, grokText: "ok");
        var ex = Assert.Throws<ArgumentException>(() =>
            service.Start("req-model-nl", Grok("hello") with { Model = "a\nb" }));
        Assert.Contains("line breaks", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task ConcurrentSameRequestIdStartsOnce()
    {
        var service = CreateService(out var runner, grokText: "ok");
        runner.Gate = NewGate();
        var request = Grok("hello");
        var first = Task.Run(() => service.Start("req-race", request));
        var second = Task.Run(() => service.Start("req-race", request));
        await Task.WhenAll(first, second);
        Assert.Equal(first.Result.JobId, second.Result.JobId);
        Assert.Equal(1, runner.CallCount);
        runner.Gate.SetResult(true);
        await WaitAsync(service, first.Result.JobId);
    }

    [Fact]
    public async Task GetIsIdempotentAfterCompletion()
    {
        var service = CreateService(out var runner, grokText: "done");
        var started = service.Start("req-get", Grok("hello"));
        var first = await WaitAsync(service, started.JobId);
        var second = service.Get(started.JobId);
        Assert.Equal(AgentJobStatus.Completed, first.Status);
        Assert.Equal(first.Result!.OutputText, second.Result!.OutputText);
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public void UnknownJobThrows()
    {
        var service = CreateService(out _, grokText: "ok");
        var ex = Assert.Throws<KeyNotFoundException>(() => service.Get("missing"));
        Assert.Contains("Unknown job", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRequestIdThrowsWithoutStarting()
    {
        var service = CreateService(out var runner, grokText: "ok");
        Assert.Throws<ArgumentException>(() => service.Start("  ", Grok("hello")));
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task CancelStopsRunningJob()
    {
        var service = CreateService(out var runner, grokText: "ok");
        runner.Gate = NewGate();
        var started = service.Start("req-cancel", Grok("hello"));
        var cancelled = service.Cancel(started.JobId);
        Assert.Equal(AgentJobStatus.Cancelled, cancelled.Status);
        await WaitCanceledAsync(runner);
        var snapshot = service.Get(started.JobId);
        Assert.Equal(AgentJobStatus.Cancelled, snapshot.Status);
        Assert.True(runner.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StartDoesNotUseCallerCancellationToken()
    {
        var service = CreateService(out var runner, grokText: "ok");
        runner.Gate = NewGate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var started = service.Start("req-ct", Grok("hello"));
        Assert.Equal(AgentJobStatus.Running, started.Status);
        Assert.False(runner.LastCancellationToken.IsCancellationRequested);
        runner.Gate.SetResult(true);
        var completed = await WaitAsync(service, started.JobId);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task RestartReturnsCompletedResultForSameRequestId()
    {
        var store = Directory.CreateTempSubdirectory("caf-jobs-").FullName;
        var first = CreateService(out var runner, grokText: "once", store);
        var started = first.Start("req-persist", Grok("hello"));
        var completed = await WaitAsync(first, started.JobId);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);

        var second = CreateService(out var runner2, grokText: "other", store);
        var recovered = second.Start("req-persist", Grok("hello"));
        Assert.Equal(started.JobId, recovered.JobId);
        Assert.Equal(AgentJobStatus.Completed, recovered.Status);
        Assert.Equal("once", recovered.Result!.OutputText);
        Assert.Equal(0, runner2.CallCount);
        Assert.Equal("once", second.Get(started.JobId).Result!.OutputText);
    }

    [Fact]
    public async Task RestartIgnoresLegacyPollAfterMsInSavedJobRecord()
    {
        var store = Directory.CreateTempSubdirectory("caf-jobs-legacy-").FullName;
        var first = CreateService(out var runner, grokText: "legacy", store);
        var started = first.Start("req-legacy-poll", Grok("hello"));
        await WaitAsync(first, started.JobId);

        var path = Directory.EnumerateFiles(store, "req-*.json").Single();
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var objectBuilder = new Dictionary<string, object?>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            objectBuilder[property.Name] = property.Value.Clone();
        }

        objectBuilder["pollAfterMs"] = 2000;
        File.WriteAllText(path, JsonSerializer.Serialize(objectBuilder, AgentJson.Options));

        var second = AgentFacadeTests.CreateFacade(out var runner2, out _);
        var recovered = new AgentJobService(second, store).Start("req-legacy-poll", Grok("hello"));
        Assert.Equal(AgentJobStatus.Completed, recovered.Status);
        Assert.Equal("legacy", recovered.Result!.OutputText);
        Assert.Equal(0, runner2.CallCount);
    }

    [Fact]
    public void RestartDoesNotRerunInterruptedJob()
    {
        var store = Directory.CreateTempSubdirectory("caf-jobs-").FullName;
        var first = CreateService(out var runner, grokText: "live", store);
        runner.Gate = NewGate();
        var started = first.Start("req-crash", Grok("hello"));
        Assert.Equal(AgentJobStatus.Running, started.Status);
        Assert.Equal(1, runner.CallCount);

        var second = CreateService(out var runner2, grokText: "rerun", store);
        var recovered = second.Start("req-crash", Grok("hello"));
        Assert.Equal(started.JobId, recovered.JobId);
        Assert.Equal(AgentJobStatus.Failed, recovered.Status);
        Assert.Equal(AgentJobService.InterruptedError, recovered.Error);
        Assert.Equal(0, runner2.CallCount);
        runner.Gate.TrySetResult(true);
    }

    [Fact]
    public async Task PersistFailureUnregistersJobSoRetryCanStart()
    {
        var parent = Directory.CreateTempSubdirectory("caf-jobs-parent-").FullName;
        var store = Path.Combine(parent, "jobs");
        var service = CreateService(out var runner, grokText: "ok", store);
        Directory.Delete(store, recursive: true);
        File.WriteAllText(store, "not-a-directory");

        var ex = Assert.ThrowsAny<Exception>(() => service.Start("req-persist-fail", Grok("hello")));
        Assert.False(ex is ArgumentException);
        Assert.Equal(0, runner.CallCount);

        File.Delete(store);
        Directory.CreateDirectory(store);
        var started = service.Start("req-persist-fail", Grok("hello"));
        var completed = started.Status == AgentJobStatus.Running
            ? await WaitAsync(service, started.JobId)
            : started;
        Assert.Equal(AgentJobStatus.Completed, completed.Status);
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task CancelWithoutDiskRecordKeepsRequestFingerprint()
    {
        var store = Directory.CreateTempSubdirectory("caf-jobs-").FullName;
        var first = CreateService(out var runner, grokText: "ok", store);
        runner.Gate = NewGate();
        var started = first.Start("req-cancel-fp", Grok("hello"));
        foreach (var file in Directory.EnumerateFiles(store, "req-*.json"))
        {
            File.Delete(file);
        }

        var cancelled = first.Cancel(started.JobId);
        Assert.Equal(AgentJobStatus.Cancelled, cancelled.Status);

        var second = CreateService(out var runner2, grokText: "other", store);
        var recovered = second.Start("req-cancel-fp", Grok("hello"));
        Assert.Equal(started.JobId, recovered.JobId);
        Assert.Equal(AgentJobStatus.Cancelled, recovered.Status);
        Assert.Equal(0, runner2.CallCount);
        runner.Gate.TrySetCanceled();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TerminalJobIsReloadedFromDiskAfterEviction()
    {
        var store = Directory.CreateTempSubdirectory("caf-jobs-").FullName;
        var service = CreateService(out var runner, grokText: "done", store);
        var started = service.Start("req-evict", Grok("hello"));
        var completed = await WaitAsync(service, started.JobId);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);

        var again = service.Start("req-evict", Grok("hello"));
        Assert.Equal(started.JobId, again.JobId);
        Assert.Equal("done", again.Result!.OutputText);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal("done", service.Get(started.JobId).Result!.OutputText);
        Assert.False(string.IsNullOrEmpty(again.Result.RawOutput));
    }

    [Fact]
    public async Task WaitAsyncReleasesWhenRunningJobCompletes()
    {
        var service = CreateService(out var runner, grokText: "wait-ok");
        runner.Gate = NewGate();
        var started = service.Start("req-wait-complete", Grok("hello"));
        var wait = service.WaitAsync(started.JobId, 30, CancellationToken.None);
        Assert.False(wait.IsCompleted);
        runner.Gate.SetResult(true);
        var completed = await wait;
        Assert.Equal(AgentJobStatus.Completed, completed.Status);
        Assert.Equal("wait-ok", completed.Result!.OutputText);
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task WaitAsyncReturnsImmediatelyWhenAlreadyTerminal()
    {
        var service = CreateService(out var runner, grokText: "already");
        var started = service.Start("req-wait-terminal", Grok("hello"));
        var completed = await WaitAsync(service, started.JobId);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);

        var immediate = await service.WaitAsync(started.JobId, 30, CancellationToken.None);
        Assert.Equal(AgentJobStatus.Completed, immediate.Status);
        Assert.Equal("already", immediate.Result!.OutputText);
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task WaitAsyncTimeoutReturnsRunningWithoutCancellingWorker()
    {
        var service = CreateService(out var runner, grokText: "later");
        runner.Gate = NewGate();
        var started = service.Start("req-wait-timeout", Grok("hello"));
        var timedOut = await service.WaitAsync(started.JobId, 1, CancellationToken.None);
        Assert.Equal(AgentJobStatus.Running, timedOut.Status);
        Assert.False(runner.LastCancellationToken.IsCancellationRequested);
        Assert.Equal(1, runner.CallCount);

        runner.Gate.SetResult(true);
        var completed = await WaitAsync(service, started.JobId);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);
        Assert.Equal("later", completed.Result!.OutputText);
    }

    [Fact]
    public async Task WaitAsyncCallerCancelDoesNotCancelWorker()
    {
        var service = CreateService(out var runner, grokText: "kept");
        runner.Gate = NewGate();
        var started = service.Start("req-wait-ct", Grok("hello"));
        using var cts = new CancellationTokenSource();
        var wait = service.WaitAsync(started.JobId, 30, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.False(runner.LastCancellationToken.IsCancellationRequested);
        Assert.Equal(AgentJobStatus.Running, service.Get(started.JobId).Status);

        runner.Gate.SetResult(true);
        var completed = await WaitAsync(service, started.JobId);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);
        Assert.Equal("kept", completed.Result!.OutputText);
    }

    [Fact]
    public async Task CancelReleasesWaitersAsTerminal()
    {
        var service = CreateService(out var runner, grokText: "nope");
        runner.Gate = NewGate();
        var started = service.Start("req-wait-cancel", Grok("hello"));
        var wait = service.WaitAsync(started.JobId, 30, CancellationToken.None);
        var cancelled = service.Cancel(started.JobId);
        Assert.Equal(AgentJobStatus.Cancelled, cancelled.Status);
        var waited = await wait;
        Assert.Equal(AgentJobStatus.Cancelled, waited.Status);
        await WaitCanceledAsync(runner);
        Assert.True(runner.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task MultipleWaitersShareOneWorker()
    {
        var service = CreateService(out var runner, grokText: "shared");
        runner.Gate = NewGate();
        var started = service.Start("req-wait-multi", Grok("hello"));
        var first = service.WaitAsync(started.JobId, 30, CancellationToken.None);
        var second = service.WaitAsync(started.JobId, 30, CancellationToken.None);
        Assert.Equal(1, runner.CallCount);
        runner.Gate.SetResult(true);
        var results = await Task.WhenAll(first, second);
        Assert.All(results, snapshot => Assert.Equal(AgentJobStatus.Completed, snapshot.Status));
        Assert.All(results, snapshot => Assert.Equal("shared", snapshot.Result!.OutputText));
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task WaitAsyncRejectsOutOfRangeTimeout()
    {
        var service = CreateService(out var runner, grokText: "ok");
        await Assert.ThrowsAsync<ArgumentException>(() => service.WaitAsync("job", 0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.WaitAsync("job", 86401, CancellationToken.None));
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task WaitAsyncDefaultsTo300SecondsWhenOmitted()
    {
        var service = CreateService(out var runner, grokText: "default-timeout");
        var started = service.Start("req-default-timeout", Grok("hello"));
        var completed = await service.WaitAsync(started.JobId);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);
        Assert.Equal("default-timeout", completed.Result!.OutputText);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal(300, AgentJobService.DefaultWaitTimeoutSeconds);
    }

    [Fact]
    public async Task FailureProjectionClassifiesAndPersistsAllKinds()
    {
        var cases = new (string Kind, Func<RecordingProcessRunner, AgentRunRequest> Configure)[]
        {
            ("process_start_failed", runner =>
            {
                runner.ExceptionToThrow = new ProcessStartException(
                    "start xai-process-secret-value",
                    new InvalidOperationException("missing executable"));
                return Grok("start");
            }),
            ("non_zero_exit", runner =>
            {
                runner.Result = new ProcessRunResult(7, "stdout-secret", "stderr\r\nxai-01234567890123456789 " + new string('x', 700));
                return Grok("exit");
            }),
            ("output_parse_failed", runner =>
            {
                runner.Result = new ProcessRunResult(0, "not-json", "");
                return Grok("parse");
            }),
            ("internal_error", runner =>
            {
                runner.ExceptionToThrow = new InvalidOperationException("worker state is uncertain");
                return Grok("internal");
            }),
        };

        foreach (var (kind, configure) in cases)
        {
            var store = Directory.CreateTempSubdirectory("caf-failure-").FullName;
            var facade = AgentFacadeTests.CreateFacade(out var runner, out _);
            runner.Result = GrokStdout("ok", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var service = new AgentJobService(facade, store);
            var request = configure(runner);
            var started = service.Start("req-failure-" + kind, request);
            var failed = await WaitAsync(service, started.JobId);

            Assert.Equal(AgentJobStatus.Failed, failed.Status);
            Assert.Equal("agent job failed. See the server log for details.", failed.Error);
            Assert.NotNull(failed.Failure);
            Assert.Equal(kind, failed.Failure!.Kind);
            Assert.NotEmpty(failed.Failure.Summary);
            Assert.True(failed.Failure.Summary.Length <= 512);
            Assert.DoesNotContain('\r', failed.Failure.Summary);
            Assert.DoesNotContain('\n', failed.Failure.Summary);
            Assert.DoesNotContain("xai-01234567890123456789", failed.Failure.Summary, StringComparison.Ordinal);
            Assert.NotNull(failed.Failure.RunId);
            Assert.NotNull(failed.Failure.EventsLogPath);
            Assert.NotNull(failed.Failure.TextLogPath);
            Assert.True(File.Exists(failed.Failure.EventsLogPath!));
            Assert.True(File.Exists(failed.Failure.TextLogPath!));
            if (kind == "non_zero_exit")
            {
                Assert.Equal(7, failed.Failure.ExitCode);
                Assert.Equal(512, failed.Failure.Summary.Length);
            }
            else
            {
                Assert.Null(failed.Failure.ExitCode);
            }

            var record = Directory.EnumerateFiles(store, "req-*.json").Single();
            var persisted = File.ReadAllText(record);
            Assert.Contains("\"failure\"", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("rawOutput", JsonSerializer.Serialize(AgentJobPublicProjection.From(failed), AgentJson.Options), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RestartPreservesFailureProjectionWithoutRerunning()
    {
        var store = Directory.CreateTempSubdirectory("caf-failure-restart-").FullName;
        var first = AgentFacadeTests.CreateFacade(out var runner, out _);
        runner.Result = new ProcessRunResult(4, "out", "bad");
        var service = new AgentJobService(first, store);
        var started = service.Start("req-failure-restart", Grok("hello"));
        var failed = await WaitAsync(service, started.JobId);
        Assert.Equal("non_zero_exit", failed.Failure!.Kind);

        var second = AgentFacadeTests.CreateFacade(out var runner2, out _);
        var recovered = new AgentJobService(second, store).Start("req-failure-restart", Grok("hello"));
        Assert.Equal(started.JobId, recovered.JobId);
        Assert.Equal(failed.Failure, recovered.Failure);
        Assert.Equal(0, runner2.CallCount);
    }

    [Fact]
    public void RestartClassifiesRunningRecordAsFacadeInterrupted()
    {
        var store = Directory.CreateTempSubdirectory("caf-failure-interrupted-").FullName;
        var first = AgentFacadeTests.CreateFacade(out var runner, out _);
        runner.Gate = NewGate();
        var service = new AgentJobService(first, store);
        var started = service.Start("req-failure-interrupted", Grok("hello"));
        Assert.Equal(AgentJobStatus.Running, started.Status);

        var second = AgentFacadeTests.CreateFacade(out var runner2, out _);
        var recovered = new AgentJobService(second, store).Start("req-failure-interrupted", Grok("hello"));
        Assert.Equal(AgentJobStatus.Failed, recovered.Status);
        Assert.Equal("facade_interrupted", recovered.Failure!.Kind);
        Assert.Equal(AgentJobService.InterruptedError, recovered.Failure.Summary);
        Assert.Null(recovered.Failure.ExitCode);
        Assert.Null(recovered.Failure.RunId);
        Assert.Equal(0, runner2.CallCount);
        runner.Gate.TrySetResult(true);
    }

    private static AgentJobService CreateService(out RecordingProcessRunner runner, string grokText, string? storeDirectory = null)
    {
        var facade = AgentFacadeTests.CreateFacade(out runner, out _);
        runner.Result = GrokStdout(grokText, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        return new AgentJobService(
            facade,
            storeDirectory ?? Directory.CreateTempSubdirectory("caf-jobs-").FullName);
    }

    private static AgentRunRequest Grok(string prompt)
    {
        return new AgentRunRequest(AgentFacade.GrokBuildAgent, prompt, Path.GetTempPath(), null, null);
    }

    private static string HistoricalFingerprintWithoutModel(AgentRunRequest request)
    {
        var builder = new StringBuilder();
        builder.Append(request.Agent).Append('\n');
        builder.Append(request.Prompt).Append('\n');
        builder.Append(request.WorkingDirectory).Append('\n');
        builder.Append(request.SessionId ?? string.Empty).Append('\n');
        builder.Append(request.AutoApprove ? "1" : "0").Append('\n');
        if (request.Skills is not null)
        {
            foreach (var skill in request.Skills)
            {
                builder.Append(skill).Append('\n');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static ProcessRunResult GrokStdout(string text, string sessionId)
    {
        var stdout = "{\"type\":\"text\",\"data\":" + JsonSerializer.Serialize(text)
            + "}\n{\"type\":\"end\",\"sessionId\":" + JsonSerializer.Serialize(sessionId) + "}";
        return new ProcessRunResult(0, stdout, "");
    }

    private static TaskCompletionSource<bool> NewGate()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task<AgentJobSnapshot> WaitAsync(AgentJobService service, string jobId)
    {
        for (var i = 0; i < 100; i++)
        {
            var snapshot = service.Get(jobId);
            if (snapshot.Status != AgentJobStatus.Running)
            {
                return snapshot;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("job did not finish: " + jobId);
    }

    private static async Task WaitCanceledAsync(RecordingProcessRunner runner)
    {
        for (var i = 0; i < 100; i++)
        {
            if (runner.LastCancellationToken.CanBeCanceled
                && runner.LastCancellationToken.IsCancellationRequested
                && runner.Gate!.Task.IsCompleted)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("runner was not cancelled.");
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
            ["--output-format", "json", "--allow-all"],
            args);
        Assert.DoesNotContain("--prompt", args);
    }

    [Fact]
    public void BuildArgumentsOmitsAllowAllWhenAutoApproveFalse()
    {
        var args = GitHubCopilotDriver.BuildArguments(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "ask", @"C:\repo", null, null, AutoApprove: false));
        Assert.Equal(["--output-format", "json"], args);
        Assert.DoesNotContain("--prompt", args);
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
        Assert.Equal(
            ["--output-format", "json", "--allow-all", "--resume", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"],
            args);
        Assert.Contains("--resume", args);
        Assert.Contains("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", args);
        Assert.DoesNotContain("--prompt", args);
    }

    [Fact]
    public void BuildArgumentsPassesModelOnResumeWithoutPuttingItInThePrompt()
    {
        const string prompt = "Mention model gpt-5 in the notes.";
        var args = GitHubCopilotDriver.BuildArguments(
            new AgentRunRequest(
                AgentFacade.GitHubCopilotAgent,
                prompt,
                @"C:\repo",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                null,
                Model: "gpt-5.4"));
        Assert.Equal(
            [
                "--output-format",
                "json",
                "--allow-all",
                "--model",
                "gpt-5.4",
                "--resume",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            ],
            args);
        Assert.DoesNotContain(prompt, args);
        Assert.DoesNotContain("gpt-5", args.Where(arg => arg != "gpt-5.4"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildArgumentsOmitsModelWhenUnspecified(string? model)
    {
        var args = GitHubCopilotDriver.BuildArguments(
            new AgentRunRequest(AgentFacade.GitHubCopilotAgent, "ask", @"C:\repo", null, null, Model: model));
        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public async Task RunSendsMultilineSkillPromptThroughStandardInput()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """{"type":"assistant.message","data":{"content":"ok"}}""",
                ""),
        };
        await using var log = TestRunLogs.CreateLog();
        await new GitHubCopilotDriver(runner).RunAsync(
            new AgentRunRequest(
                AgentFacade.GitHubCopilotAgent,
                "issue #25 を調査してください。",
                Path.GetTempPath(),
                null,
                ["github-copilot"]),
            log,
            onStdoutLine: null,
            CancellationToken.None);

        var prompt = runner.LastRequest!.StandardInputText;
        Assert.NotNull(prompt);
        Assert.Equal(
            GitHubCopilotDriver.ApplyCopilotSkills(
                "issue #25 を調査してください。",
                ["github-copilot"]),
                prompt!);
        Assert.Contains('\n', prompt!);
        Assert.Equal("copilot", runner.LastRequest.FileName);
        Assert.DoesNotContain("--prompt", runner.LastRequest.Arguments);
        Assert.Equal(prompt, runner.LastRequest.StandardInputText);
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
        Assert.Equal("assistant_transcript", result.OutputKind);
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
    public async Task UsesLastCompletedAssistantTurnWhenLifecycleIsAvailable()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"assistant.turn_start"}
                {"type":"assistant.message","data":{"content":"old turn"}}
                {"type":"assistant.turn_end"}
                {"type":"tool_call","name":"read"}
                {"type":"assistant.turn_start"}
                {"type":"assistant.message","data":{"content":"final turn"}}
                {"type":"assistant.turn_end"}
                {"type":"result","sessionId":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"}
                """,
                ""),
        };

        var result = await RunAsync(runner);
        Assert.Equal("final turn", result.OutputText);
        Assert.Equal("final_response", result.OutputKind);
        Assert.DoesNotContain("old turn", result.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepsTranscriptWhenAssistantTurnIsNotCompleted()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"assistant.turn_start"}
                {"type":"assistant.message","data":{"content":"unfinished report"}}
                {"type":"result","sessionId":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"}
                """,
                ""),
        };

        var result = await RunAsync(runner);
        Assert.Equal("unfinished report", result.OutputText);
        Assert.Equal("assistant_transcript", result.OutputKind);
    }

    [Fact]
    public async Task IgnoresUnrelatedCompletedStatusAsAssistantLifecycle()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"assistant.message","data":{"content":"first"}}
                {"type":"tool.execution_complete","status":"completed"}
                {"type":"assistant.message","data":{"content":"second"}}
                {"type":"result","sessionId":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"}
                """,
                ""),
        };

        var result = await RunAsync(runner);
        Assert.Equal("first\nsecond", result.OutputText);
        Assert.Equal("assistant_transcript", result.OutputKind);
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
    public void BuildArgumentsPassesModelOnResumeWithoutRewritingPrompt()
    {
        const string prompt = "Mention model grok-3 in the notes.";
        var args = GrokBuildDriver.BuildArguments(
            new AgentRunRequest(
                AgentFacade.GrokBuildAgent,
                prompt,
                @"D:\ws",
                "dddddddd-dddd-dddd-dddd-dddddddddddd",
                null,
                Model: "grok-4"));
        Assert.Equal(
            [
                "--no-auto-update",
                "-p",
                prompt,
                "--cwd",
                @"D:\ws",
                "--output-format",
                "streaming-json",
                "--always-approve",
                "--model",
                "grok-4",
                "--resume",
                "dddddddd-dddd-dddd-dddd-dddddddddddd",
            ],
            args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildArgumentsOmitsModelWhenUnspecified(string? model)
    {
        var args = GrokBuildDriver.BuildArguments(
            new AgentRunRequest(AgentFacade.GrokBuildAgent, "ask", @"D:\ws", null, null, Model: model));
        Assert.DoesNotContain("--model", args);
        Assert.Equal("ask", args[args.IndexOf("-p") + 1]);
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

public class CursorCliDriverTests
{
    [Fact]
    public void BuildArgumentsIncludeNonInteractiveFlags()
    {
        var args = CursorCliDriver.BuildArguments(
            new AgentRunRequest(AgentFacade.CursorAgent, "Reply with exactly: pong", @"C:\repo", null, null));
        Assert.Equal(
            [
                "--print",
                "--output-format",
                "stream-json",
                "--trust",
                "--workspace",
                @"C:\repo",
                "--force",
                "Reply with exactly: pong",
            ],
            args);
        Assert.DoesNotContain("--continue", args);
        Assert.DoesNotContain("--yolo", args);
        Assert.DoesNotContain("--stream-partial-output", args);
    }

    [Fact]
    public void BuildArgumentsOmitsForceWhenAutoApproveFalse()
    {
        var args = CursorCliDriver.BuildArguments(
            new AgentRunRequest(AgentFacade.CursorAgent, "ask", @"C:\repo", null, null, AutoApprove: false));
        Assert.Equal(
            [
                "--print",
                "--output-format",
                "stream-json",
                "--trust",
                "--workspace",
                @"C:\repo",
                "ask",
            ],
            args);
        Assert.DoesNotContain("--force", args);
        Assert.DoesNotContain("--yolo", args);
        Assert.Contains("--trust", args);
    }

    [Fact]
    public void BuildArgumentsResumeSessionAndWorkingDirectory()
    {
        var args = CursorCliDriver.BuildArguments(
            new AgentRunRequest(
                AgentFacade.CursorAgent,
                "continue",
                @"D:\ws",
                "c6b62c6f-7ead-4fd6-9922-e952131177ff",
                ["$dotnet-file-based-apps"]));
        Assert.Contains("--resume", args);
        Assert.Contains("c6b62c6f-7ead-4fd6-9922-e952131177ff", args);
        Assert.Contains("--workspace", args);
        Assert.Contains(@"D:\ws", args);
        Assert.Equal("continue", args[^1]);
        Assert.DoesNotContain("/dotnet-file-based-apps", args);
        Assert.DoesNotContain("--continue", args);
    }

    [Fact]
    public void FileNameAvoidsWindowsCmdWrapper()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cursor-agent.ps1", CursorCliDriver.FileName);
        }
        else
        {
            Assert.Equal("cursor-agent", CursorCliDriver.FileName);
        }
    }

    [Fact]
    public async Task RunPassesMultilinePromptAsSingleArgument()
    {
        var prompt = "Reply with exactly: pong\nSecond line.";
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """{"type":"result","subtype":"success","result":"pong","session_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}""",
                ""),
        };
        await using var log = TestRunLogs.CreateLog();
        await new CursorCliDriver(runner).RunAsync(
            new AgentRunRequest(AgentFacade.CursorAgent, prompt, Path.GetTempPath(), null, null),
            log,
            onStdoutLine: null,
            CancellationToken.None);
        Assert.Equal(CursorCliDriver.FileName, runner.LastRequest!.FileName);
        Assert.Equal(prompt, runner.LastRequest.Arguments[^1]);
        Assert.Contains('\n', runner.LastRequest.Arguments[^1]);
        Assert.Null(runner.LastRequest.StandardInputText);
    }

    [Fact]
    public void SkillsDoNotRewritePrompt()
    {
        var args = CursorCliDriver.BuildArguments(
            new AgentRunRequest(
                AgentFacade.CursorAgent,
                "body",
                @"C:\repo",
                null,
                ["review", "$dotnet-file-based-apps"]));
        Assert.Equal("body", args[^1]);
        Assert.DoesNotContain("/review", args);
        Assert.DoesNotContain("Use the /review skill.", args);
    }

    [Fact]
    public void BuildArgumentsPassesModelOnResumeWithoutRewritingPrompt()
    {
        const string prompt = "Mention model gpt-5 in the notes.";
        var args = CursorCliDriver.BuildArguments(
            new AgentRunRequest(
                AgentFacade.CursorAgent,
                prompt,
                @"C:\repo",
                "c6b62c6f-7ead-4fd6-9922-e952131177ff",
                null,
                Model: "composer-2"));
        Assert.Equal(
            [
                "--print",
                "--output-format",
                "stream-json",
                "--trust",
                "--workspace",
                @"C:\repo",
                "--force",
                "--model",
                "composer-2",
                "--resume",
                "c6b62c6f-7ead-4fd6-9922-e952131177ff",
                prompt,
            ],
            args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildArgumentsOmitsModelWhenUnspecified(string? model)
    {
        var args = CursorCliDriver.BuildArguments(
            new AgentRunRequest(AgentFacade.CursorAgent, "ask", @"C:\repo", null, null, Model: model));
        Assert.DoesNotContain("--model", args);
        Assert.Equal("ask", args[^1]);
    }

    [Fact]
    public async Task ParsesOfficialStreamJsonResultAndSessionId()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, OfficialStreamJsonFixture(), ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal("Done! I've created the summary in summary.txt", result.OutputText);
        Assert.Equal("c6b62c6f-7ead-4fd6-9922-e952131177ff", result.SessionId);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("final_response", result.OutputKind);
        Assert.Equal(CursorCliDriver.FileName, runner.LastRequest!.FileName);
        Assert.Equal(Path.GetTempPath(), runner.LastRequest.WorkingDirectory);
    }

    [Fact]
    public async Task PrefersResultEventOverThinkingAndToolProgress()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"system","subtype":"init","session_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                {"type":"thinking","text":"I should inspect the repo"}
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I'll look at the files"}]}}
                {"type":"tool_call","subtype":"started","call_id":"toolu_1","tool_call":{"readToolCall":{"args":{"path":"README.md"}}},"session_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                {"type":"tool_call","subtype":"completed","call_id":"toolu_1","tool_call":{"readToolCall":{"args":{"path":"README.md"},"result":{"success":{"content":"# Project"}}}}}
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"pong"}]}}
                {"type":"result","subtype":"success","is_error":false,"result":"pong","session_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                """,
                ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal("pong", result.OutputText);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.SessionId);
        Assert.DoesNotContain("I should inspect the repo", result.OutputText, StringComparison.Ordinal);
        Assert.DoesNotContain("I'll look at the files", result.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallsBackToAssistantTextWhenResultEventIsMissing()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"system","subtype":"init","session_id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hello"}]}}
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"world"}]}}
                """,
                ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal("hello\nworld", result.OutputText);
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", result.SessionId);
        Assert.Equal("assistant_transcript", result.OutputKind);
    }

    [Fact]
    public async Task KeepsTerminalResultAsTranscriptWhenItsCompositionCannotBeVerified()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"progress"}]}}
                {"type":"result","subtype":"success","result":"different terminal report","session_id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}
                """,
                ""),
        };

        var result = await RunAsync(runner);
        Assert.Equal("different terminal report", result.OutputText);
        Assert.Equal("assistant_transcript", result.OutputKind);
    }

    [Fact]
    public async Task TreatsResultOnlyOutputAsTranscriptBecauseFinalReportCannotBeVerified()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """{"type":"result","subtype":"success","result":"terminal text","session_id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}""",
                ""),
        };

        var result = await RunAsync(runner);
        Assert.Equal("terminal text", result.OutputText);
        Assert.Equal("assistant_transcript", result.OutputKind);
    }

    [Fact]
    public async Task CoalescesRegularAssistantEventsInHumanLogWithoutDuplicatingResult()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I'll "}]}}
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"read the README"}]}}
                {"type":"result","subtype":"success","result":"I'll read the README","session_id":"dddddddd-dddd-dddd-dddd-dddddddddddd"}
                """,
                ""),
        };
        await using var log = TestRunLogs.CreateLog();

        var result = await new CursorCliDriver(runner).RunAsync(
            new AgentRunRequest(AgentFacade.CursorAgent, "go", Path.GetTempPath(), null, null),
            log,
            onStdoutLine: null,
            CancellationToken.None);

        await log.DisposeAsync();
        var text = TestRunLogs.ReadShared(result.TextLogPath);
        var assistantLines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(" assistant:", StringComparison.Ordinal))
            .ToList();
        Assert.Single(assistantLines);
        Assert.Contains("assistant: I'll read the README", assistantLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("result: I'll read the README", text, StringComparison.Ordinal);
        Assert.Equal("I'll read the README", result.OutputText);
    }

    [Fact]
    public async Task IgnoresUnknownEventsAndDuplicateAssistantFlush()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"system","subtype":"init","session_id":"cccccccc-cccc-cccc-cccc-cccccccccccc"}
                {"type":"future_event","payload":{"ignored":true}}
                {"type":"assistant","timestamp_ms":1,"message":{"role":"assistant","content":[{"type":"text","text":"po"}]}}
                {"type":"assistant","timestamp_ms":2,"model_call_id":"call_1","message":{"role":"assistant","content":[{"type":"text","text":"po"}]}}
                {"type":"assistant","timestamp_ms":3,"message":{"role":"assistant","content":[{"type":"text","text":"ng"}]}}
                {"type":"result","subtype":"success","result":"pong","session_id":"cccccccc-cccc-cccc-cccc-cccccccccccc"}
                """,
                ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal("pong", result.OutputText);
        Assert.Equal("cccccccc-cccc-cccc-cccc-cccccccccccc", result.SessionId);
    }

    [Fact]
    public async Task DoesNotTreatRequestIdOrArbitraryUuidAsSessionId()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"see 99999999-9999-9999-9999-999999999999"}]}}
                {"type":"result","subtype":"success","result":"see 99999999-9999-9999-9999-999999999999","request_id":"10e11780-df2f-45dc-a1ff-4540af32e9c0"}
                """,
                ""),
        };
        var result = await RunAsync(runner);
        Assert.Equal(string.Empty, result.SessionId);
        Assert.Equal("see 99999999-9999-9999-9999-999999999999", result.OutputText);
    }

    [Fact]
    public async Task MixedNonJsonLineFailsEvenWhenResultExists()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                warning: not json
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"pong"}]}}
                {"type":"result","subtype":"success","result":"pong","session_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                """,
                ""),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner));
        Assert.Contains("not a JSON object event", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MixedNonObjectJsonFailsEvenWhenResultExists()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                [1,2]
                {"type":"result","subtype":"success","result":"pong","session_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                """,
                ""),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner));
        Assert.Contains("not a JSON object event", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedJsonThrowsWhenNoJsonObjectExists()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(0, "not-json\n{oops", ""),
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
    public async Task ThrowsWhenAssistantTextIsMissing()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """{"type":"system","subtype":"init","session_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}""",
                ""),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner));
    }

    [Fact]
    public async Task NonZeroExitThrows()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(1, "", "Authentication required"),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner));
        Assert.Contains("exited with code 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Authentication required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamsEventsToRunLog()
    {
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessRunResult(
                0,
                """
                {"type":"system","subtype":"init","session_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","model":"Composer"}
                {"type":"thinking","text":"plan"}
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"pong"}]}}
                {"type":"result","subtype":"success","result":"pong","session_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
                """,
                "warn"),
        };
        await using var log = TestRunLogs.CreateLog();
        var result = await new CursorCliDriver(runner).RunAsync(
            new AgentRunRequest(AgentFacade.CursorAgent, "go", Path.GetTempPath(), null, null),
            log,
            onStdoutLine: null,
            CancellationToken.None);
        var events = TestRunLogs.ReadShared(result.EventsLogPath);
        var text = TestRunLogs.ReadShared(result.TextLogPath);
        Assert.Contains("\"type\":\"system\"", events, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"assistant\"", events, StringComparison.Ordinal);
        Assert.Contains("assistant: pong", text, StringComparison.Ordinal);
        Assert.Contains("thought: plan", text, StringComparison.Ordinal);
        Assert.Contains("stderr", text, StringComparison.Ordinal);
        Assert.Contains("warn", text, StringComparison.Ordinal);
        Assert.Equal("pong", result.OutputText);
    }

    private static async Task<AgentRunResult> RunAsync(RecordingProcessRunner runner)
    {
        await using var log = TestRunLogs.CreateLog();
        return await new CursorCliDriver(runner).RunAsync(
            new AgentRunRequest(AgentFacade.CursorAgent, "go", Path.GetTempPath(), null, null),
            log,
            onStdoutLine: null,
            CancellationToken.None);
    }

    private static string OfficialStreamJsonFixture()
    {
        return """
            {"type":"system","subtype":"init","apiKeySource":"login","cwd":"/Users/user/project","session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff","model":"Claude 4 Sonnet","permissionMode":"default"}
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"Read README.md and create a summary"}]},"session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I'll read the README.md file"}]},"session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}
            {"type":"tool_call","subtype":"started","call_id":"toolu_vrtx_01NnjaR886UcE8whekg2MGJd","tool_call":{"readToolCall":{"args":{"path":"README.md"}}},"session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}
            {"type":"tool_call","subtype":"completed","call_id":"toolu_vrtx_01NnjaR886UcE8whekg2MGJd","tool_call":{"readToolCall":{"args":{"path":"README.md"},"result":{"success":{"content":"# Project\n\nThis is a sample project...","isEmpty":false,"exceededLimit":false,"totalLines":54,"totalChars":1254}}}},"session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Based on the README, I'll create a summary"}]},"session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}
            {"type":"tool_call","subtype":"started","call_id":"toolu_vrtx_01Q3VHVnWFSKygaRPT7WDxrv","tool_call":{"writeToolCall":{"args":{"path":"summary.txt","fileText":"# README Summary\n\nThis project contains...","toolCallId":"toolu_vrtx_01Q3VHVnWFSKygaRPT7WDxrv"}}},"session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}
            {"type":"tool_call","subtype":"completed","call_id":"toolu_vrtx_01Q3VHVnWFSKygaRPT7WDxrv","tool_call":{"writeToolCall":{"args":{"path":"summary.txt","fileText":"# README Summary\n\nThis project contains...","toolCallId":"toolu_vrtx_01Q3VHVnWFSKygaRPT7WDxrv"},"result":{"success":{"path":"/Users/user/project/summary.txt","linesCreated":19,"fileSize":942}}}},"session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done! I've created the summary in summary.txt"}]},"session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}
            {"type":"result","subtype":"success","duration_ms":5234,"duration_api_ms":5234,"is_error":false,"result":"I'll read the README.md fileBased on the README, I'll create a summaryDone! I've created the summary in summary.txt","session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff","request_id":"10e11780-df2f-45dc-a1ff-4540af32e9c0"}
            """;
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

/// <summary>
/// Facade 委譲 Skill が relay-only 契約を失っていないことを検証する。
/// 全文一致ではなく、Codex が対象作業を行わないこと、Skill 後本文が外部 agent 用 payload であること、
/// 既存の start_agent 契約が残っていることを確認する。apm-packages 配下の新規同種 Skill も対象にする。
/// </summary>
public class FacadeDelegationSkillContractTests
{
    private static readonly string[] KnownFacadeDelegationSkillNames =
    [
        "github-copilot",
        "grok-build",
        "cursor",
    ];

    private static readonly (string Name, string SessionLabel)[] KnownAgentSessionLabels =
    [
        ("github-copilot", "Copilot session"),
        ("grok-build", "Grok session"),
        ("cursor", "Cursor session"),
    ];

    private static readonly string[] RequiredRelayContractPhrases =
    [
        "Codex 自身は対象作業を実行しない",
        "薄い UI shell / relay",
        "planner / executor / reviewer / orchestrator",
        "外部 agent に渡す作業 payload",
        "Codex 自身への作業実行指示として扱わない",
        "`prompt` としてそのまま外部 agent に渡す",
        "補足、要約、再構成、再計画、分割をしない",
        "`request_id`",
        "`start_agent`",
        "`wait_agent_job`",
        "`get_agent_job`",
        "`working_directory`",
        "`session_id`",
        "`skills`",
        "`result.outputText`",
        "`result.sessionId`",
        "repository / source code の調査",
        "Issue / PR 等の内容取得・分析",
        "memory の検索",
        "web 検索",
        "shell command の実行",
        "独自のプラン作成",
        "実装・ファイル編集",
        "テスト・検証",
        "レビュー",
        "外部 agent と並行した独自調査",
        "外部 agent の結果を材料にした独自の再計画・補完",
        "外部 agent を走らせながら Codex 側でも同じ Issue とコードを調査する動きは不正である",
        "独自回答を追加しない",
        "この Skill の役割は relay である",
        "ユーザーへの主たる応答である",
    ];

    private static readonly string[] RequiredHeadings =
    [
        "## ユーザー本文の意味",
        "## request_id と session_id の lifetime",
        "## start_agent は毎回 full request を再構成する",
        "## Codex が行ってよい処理",
        "## start_agent 直前の preflight",
        "## Exact retry",
        "## Follow-up continuation",
        "## Codex が行ってはならない処理",
        "## 外部 agent 結果の中継",
    ];

    private static readonly string[] ForbiddenLegacyMultiTurnPhrases =
    [
        "この作業用の `request_id` を UUID で一度だけ作り",
        "失っても同じ値を使う。新しい id で `start_agent` を打ち直さない",
    ];

    [Fact]
    public void DiscoversKnownFacadeDelegationSkillsWithoutHardcodingOnlyThoseFiles()
    {
        var skills = LoadFacadeDelegationSkills();
        var names = skills.Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in KnownFacadeDelegationSkillNames)
        {
            Assert.True(names.Contains(expected), "Missing Facade delegation Skill: " + expected);
        }
    }

    [Fact]
    public void ExternalAgentOrchestrationSkillDefinesParentContract()
    {
        var path = EnumerateSkillFiles(LocateRepoRoot())
            .Single(path => path.Contains("external-agent-orchestration", StringComparison.Ordinal));
        var text = File.ReadAllText(path);
        var skill = ParseSkill(path, text);
        var frontmatterEnd = text.IndexOf("\n---", StringComparison.Ordinal);
        var frontmatter = text[..frontmatterEnd];

        Assert.Equal("external-agent-orchestration", skill.Name);
        Assert.Equal("true", ReadFrontmatterValue(frontmatter, "user-invocable"));
        Assert.False(IsRelayOnlyFacadeDelegationSkill(text));
        Assert.Contains("親は目的・制約・受入条件の理解", text, StringComparison.Ordinal);
        Assert.Contains("成果の統合判断、レビュー、受入", text, StringComparison.Ordinal);
        Assert.Contains("修正可能な不足は、根拠・期待結果・修正範囲を添えてworkerへ戻す", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Codex 自身は対象作業を実行しない", text, StringComparison.Ordinal);
        Assert.DoesNotContain("薄い UI shell / relay", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FacadeDelegationSkillsShareRelayOnlyContract()
    {
        var skills = LoadFacadeDelegationSkills();
        Assert.NotEmpty(skills);
        foreach (var skill in skills)
        {
            foreach (var heading in RequiredHeadings)
            {
                Assert.True(
                    skill.Text.Contains(heading, StringComparison.Ordinal),
                    skill.Name + " is missing heading: " + heading);
            }

            foreach (var phrase in RequiredRelayContractPhrases)
            {
                Assert.True(
                    skill.Text.Contains(phrase, StringComparison.Ordinal),
                    skill.Name + " is missing relay-only contract phrase: " + phrase);
            }

            Assert.StartsWith(
                "Codex 自身は対象作業を実行しない。",
                skill.Description,
                StringComparison.Ordinal);
            Assert.Contains("委譲", skill.Description, StringComparison.Ordinal);
            Assert.Contains("中継", skill.Description, StringComparison.Ordinal);
            Assert.Contains("薄い UI shell / relay", skill.Description, StringComparison.Ordinal);
            Assert.Contains("- `agent`: `" + skill.Name + "`", skill.Text, StringComparison.Ordinal);
            Assert.Contains("通常は `timeout_seconds` を指定せず", skill.Text, StringComparison.Ordinal);
            Assert.Contains("上書きする理由がある場合だけ指定する", skill.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FacadeDelegationSkillsKeepAgentSpecificIdentity()
    {
        var skills = LoadFacadeDelegationSkills().ToDictionary(skill => skill.Name, StringComparer.Ordinal);
        foreach (var (name, sessionLabel) in KnownAgentSessionLabels)
        {
            Assert.True(skills.ContainsKey(name), "Missing Skill for agent: " + name);
            var text = skills[name].Text;
            Assert.Contains("- `agent`: `" + name + "`", text, StringComparison.Ordinal);
            Assert.Contains(sessionLabel, text, StringComparison.Ordinal);
            foreach (var other in KnownFacadeDelegationSkillNames.Where(candidate => candidate != name))
            {
                Assert.DoesNotContain("- `agent`: `" + other + "`", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void RelayContractTreatsIssuePlanningPromptAsExternalPayloadNotCodexWork()
    {
        // `$grok-build` のあとに Issue URL とプラン作成依頼を書いても、
        // Codex は Facade tool だけを呼び、Issue / code 調査と plan は外部 agent の作業である。
        foreach (var skill in LoadFacadeDelegationSkills())
        {
            Assert.Contains("外部 agent に渡す作業 payload", skill.Text, StringComparison.Ordinal);
            Assert.Contains("Codex 自身への作業実行指示として扱わない", skill.Text, StringComparison.Ordinal);
            Assert.Contains(
                "外部 agent を走らせながら Codex 側でも同じ Issue とコードを調査する動きは不正である",
                skill.Text,
                StringComparison.Ordinal);
            Assert.Contains("独自のプラン作成", skill.Text, StringComparison.Ordinal);
            Assert.Contains("外部 agent の結果そのものがユーザーへの主たる応答である", skill.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NonDelegationSkillsDoNotReceiveRelayOnlyConstraint()
    {
        foreach (var path in EnumerateSkillFiles(LocateRepoRoot()))
        {
            var text = File.ReadAllText(path);
            if (IsRelayOnlyFacadeDelegationSkill(text))
            {
                continue;
            }

            Assert.DoesNotContain("Codex 自身は対象作業を実行しない", text, StringComparison.Ordinal);
            Assert.DoesNotContain("薄い UI shell / relay", text, StringComparison.Ordinal);
            Assert.DoesNotContain("外部 agent に渡す作業 payload", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PackageReadmesDescribeRelayOnlyRole()
    {
        var repoRoot = LocateRepoRoot();
        foreach (var skill in LoadFacadeDelegationSkills())
        {
            var readme = Path.Combine(repoRoot, "apm-packages", skill.Name, "README.md");
            Assert.True(File.Exists(readme), "Missing package README for " + skill.Name);
            var text = File.ReadAllText(readme);
            Assert.Contains("Codex 自身は対象作業を実行せず", text, StringComparison.Ordinal);
            Assert.Contains("中継する", text, StringComparison.Ordinal);
            Assert.Contains("作業 payload", text, StringComparison.Ordinal);
            Assert.Contains("Codex 自身への作業実行指示ではない", text, StringComparison.Ordinal);
            Assert.Contains("新しい `request_id`", text, StringComparison.Ordinal);
            Assert.Contains("`session_id`", text, StringComparison.Ordinal);
            Assert.Contains("required fields を毎回指定する", text, StringComparison.Ordinal);
            Assert.Contains("facade-options", text, StringComparison.Ordinal);
            Assert.Contains("本文中のモデル名は起動設定にしない", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FacadeDelegationSkillsSeparateModelOptionFromPrompt()
    {
        foreach (var skill in LoadFacadeDelegationSkills())
        {
            AssertContains(skill.Name, skill.Text, "facade-options");
            AssertContains(skill.Name, skill.Text, "prompt 本文に現れたモデル名から起動用の `model` を推測しない");
            AssertContains(skill.Name, skill.Text, "`model` は渡さない");
            var lifetime = ReadHeadingSection(skill.Text, "## request_id と session_id の lifetime");
            AssertContains(skill.Name, lifetime, "`auto_approve`, `model`");
            var followUp = ReadHeadingSection(skill.Text, "## Follow-up continuation");
            var actions = ReadActionBlock(skill.Name, followUp, "Follow-up continuation");
            AssertContains(skill.Name, actions, "前回 session のモデルは継承しない");
        }
    }

    [Fact]
    public void CursorSkillDocumentsThatSkillsAreNotConvertedToNativeInvoke()
    {
        var skills = LoadFacadeDelegationSkills().ToDictionary(skill => skill.Name, StringComparer.Ordinal);
        Assert.True(skills.ContainsKey("cursor"), "Missing Skill for agent: cursor");
        var text = skills["cursor"].Text;
        Assert.Contains(
            "Cursor Driver は現在この値を明示 invoke へ変換しない",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "ユーザー本文側に Cursor native 形式の `/skill-name` を含める",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ユーザーが通し指定した Skill 名だけ。Codex 形式のまま渡す",
            text,
            StringComparison.Ordinal);

        foreach (var other in KnownFacadeDelegationSkillNames.Where(name => name != "cursor"))
        {
            Assert.Contains(
                "ユーザーが通し指定した Skill 名だけ。Codex 形式のまま渡す",
                skills[other].Text,
                StringComparison.Ordinal);
        }

        var readme = File.ReadAllText(Path.Combine(LocateRepoRoot(), "apm-packages", "cursor", "README.md"));
        Assert.Contains("明示 invoke へ変換しない", readme, StringComparison.Ordinal);
        Assert.Contains("/skill-name", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void FacadeDelegationSkillsSeparateRequestIdAndSessionIdLifetimes()
    {
        foreach (var skill in LoadFacadeDelegationSkills())
        {
            foreach (var phrase in ForbiddenLegacyMultiTurnPhrases)
            {
                Assert.DoesNotContain(phrase, skill.Text, StringComparison.Ordinal);
            }

            var lifetime = ReadHeadingSection(skill.Text, "## request_id と session_id の lifetime");
            AssertContains(skill.Name, lifetime, "冪等キー");
            AssertContains(skill.Name, lifetime, "Codex thread");
            AssertContains(skill.Name, lifetime, "外部 agent session");
            AssertContains(skill.Name, lifetime, "再利用しない");
            AssertContains(skill.Name, lifetime, "Exact retry");
            Assert.DoesNotContain("この作業用の `request_id`", lifetime, StringComparison.Ordinal);

            var fullRequest = ReadHeadingSection(skill.Text, "## start_agent は毎回 full request を再構成する");
            AssertContains(skill.Name, fullRequest, "完全な RPC");
            AssertContains(skill.Name, fullRequest, "`request_id`");
            AssertContains(skill.Name, fullRequest, "`agent`");
            AssertContains(skill.Name, fullRequest, "`prompt`");
            AssertContains(skill.Name, fullRequest, "`working_directory`");
            AssertContains(skill.Name, fullRequest, "暗黙継承はしない");

            var preflight = ReadHeadingSection(skill.Text, "## start_agent 直前の preflight");
            AssertContains(skill.Name, preflight, "`start_agent` を呼ぶ前");
            AssertContains(skill.Name, preflight, "`working_directory`");
            AssertContains(skill.Name, preflight, "required field が欠けている場合は `start_agent` を呼ばず");

            var exactRetry = ReadHeadingSection(skill.Text, "## Exact retry");
            var followUp = ReadHeadingSection(skill.Text, "## Follow-up continuation");
            var exactRetryActions = ReadActionBlock(skill.Name, exactRetry, "Exact retry");
            var followUpActions = ReadActionBlock(skill.Name, followUp, "Follow-up continuation");

            AssertContains(skill.Name, exactRetryActions, "同じ `request_id`");
            AssertContains(skill.Name, exactRetryActions, "完全一致");
            Assert.DoesNotContain("新しい `request_id`", exactRetryActions, StringComparison.Ordinal);
            Assert.DoesNotContain("新しい `prompt`", exactRetryActions, StringComparison.Ordinal);

            AssertContains(skill.Name, followUpActions, "新しい `request_id`");
            AssertContains(skill.Name, followUpActions, "新しい `prompt`");
            AssertContains(skill.Name, followUpActions, "`agent`");
            AssertContains(skill.Name, followUpActions, "`working_directory`");
            AssertContains(skill.Name, followUpActions, "省略しない");
            AssertContains(skill.Name, followUpActions, "`sessionId`");
            AssertContains(skill.Name, followUpActions, "`session_id`");
            Assert.DoesNotContain("同じ `request_id`", followUpActions, StringComparison.Ordinal);
        }
    }

    private static void AssertContains(string skillName, string text, string phrase)
    {
        Assert.True(
            text.Contains(phrase, StringComparison.Ordinal),
            skillName + " is missing phrase in scoped section: " + phrase);
    }

    private static string ReadHeadingSection(string text, string heading)
    {
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "Missing heading: " + heading);
        var contentStart = start + heading.Length;
        var next = text.IndexOf("\n## ", contentStart, StringComparison.Ordinal);
        return next < 0 ? text[contentStart..] : text[contentStart..next];
    }

    private static string ReadActionBlock(string skillName, string section, string flowName)
    {
        const string marker = "動作:";
        var start = section.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, skillName + " is missing 動作: in " + flowName);
        var rest = section[(start + marker.Length)..];
        using var reader = new StringReader(rest);
        var bullets = new StringBuilder();
        var seenBullet = false;
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                if (seenBullet)
                {
                    break;
                }

                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                seenBullet = true;
                bullets.AppendLine(trimmed);
                continue;
            }

            if (seenBullet)
            {
                break;
            }
        }

        var block = bullets.ToString();
        Assert.False(string.IsNullOrWhiteSpace(block), skillName + " has empty 動作 list in " + flowName);
        return block;
    }

    private static List<FacadeDelegationSkillFile> LoadFacadeDelegationSkills()
    {
        var skills = new List<FacadeDelegationSkillFile>();
        foreach (var path in EnumerateSkillFiles(LocateRepoRoot()))
        {
            var text = File.ReadAllText(path);
            if (!IsRelayOnlyFacadeDelegationSkill(text))
            {
                continue;
            }

            skills.Add(ParseSkill(path, text));
        }

        return skills;
    }

    private static bool IsRelayOnlyFacadeDelegationSkill(string text)
    {
        return text.Contains("Codex 自身は対象作業を実行しない。", StringComparison.Ordinal)
            && text.Contains("薄い UI shell / relay", StringComparison.Ordinal)
            && text.Contains("外部 agent に渡す作業 payload", StringComparison.Ordinal);
    }

    private static FacadeDelegationSkillFile ParseSkill(string path, string text)
    {
        var frontmatterEnd = text.IndexOf("\n---", StringComparison.Ordinal);
        Assert.True(frontmatterEnd >= 0, "Skill is missing YAML frontmatter: " + path);
        var frontmatter = text[..frontmatterEnd];
        var name = ReadFrontmatterValue(frontmatter, "name");
        var description = ReadFrontmatterValue(frontmatter, "description");
        Assert.False(string.IsNullOrWhiteSpace(name), "Skill name is missing: " + path);
        Assert.False(string.IsNullOrWhiteSpace(description), "Skill description is missing: " + path);
        return new FacadeDelegationSkillFile(path, name, description, text);
    }

    private static string ReadFrontmatterValue(string frontmatter, string key)
    {
        var prefix = key + ":";
        using var reader = new StringReader(frontmatter);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return "";
    }

    private static IEnumerable<string> EnumerateSkillFiles(string repoRoot)
    {
        var packagesRoot = Path.Combine(repoRoot, "apm-packages");
        if (Directory.Exists(packagesRoot))
        {
            foreach (var packageDir in Directory.EnumerateDirectories(packagesRoot))
            {
                var nestedSkills = Path.Combine(packageDir, ".apm", "skills");
                if (!Directory.Exists(nestedSkills))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(nestedSkills, "SKILL.md", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }

        var localSkills = Path.Combine(repoRoot, ".agents", "skills");
        if (!Directory.Exists(localSkills))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(localSkills, "SKILL.md", SearchOption.AllDirectories))
        {
            yield return file;
        }
    }

    private static string LocateRepoRoot([CallerFilePath] string? callerFile = null)
    {
        var seeds = new List<string> { Directory.GetCurrentDirectory() };
        if (!string.IsNullOrEmpty(callerFile))
        {
            var testsDir = Path.GetDirectoryName(callerFile);
            if (!string.IsNullOrEmpty(testsDir))
            {
                seeds.Add(Path.GetFullPath(Path.Combine(testsDir, "..")));
            }
        }

        foreach (var seed in seeds)
        {
            for (var dir = new DirectoryInfo(seed); dir is not null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "apm-packages"))
                    && File.Exists(Path.Combine(dir.FullName, "README.md")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new InvalidOperationException(
            "Could not locate the repository root from " + Directory.GetCurrentDirectory() + ".");
    }

    private sealed record FacadeDelegationSkillFile(string Path, string Name, string Description, string Text);
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
    public async Task JobGuardFailureKillsStartedProcess()
    {
        var guard = new ThrowingProcessJobGuard();
        var runner = new ProcessRunner(guard);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new ProcessRunRequest("dotnet", ["--version"], Directory.GetCurrentDirectory()),
            CancellationToken.None));
        Assert.Contains("job assign failed", ex.Message, StringComparison.Ordinal);
        Assert.True(guard.AssignCount >= 1);
        Assert.True(guard.ProcessId > 0);
        Assert.True(ProcessHasExited(guard.ProcessId));
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
        Assert.Equal(["--version"], launch.LogicalArguments);
        Assert.Null(launch.RawArguments);
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
                StandardInputText: new string('x', 128 * 1024),
                StdoutLineCallback: _ => throw new InvalidOperationException("log volume full")),
            CancellationToken.None));
        Assert.Equal("log volume full", ex.Message);
    }

    [Fact]
    public async Task StdoutCallbackFailureKillsLongRunningProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf callback fixture ").FullName;
        var script = Path.Combine(directory, "long-running.ps1");
        await File.WriteAllTextAsync(
            script,
            """
            while ($true) {
                Write-Output 'tick'
                Start-Sleep -Seconds 1
            }
            """,
            new UTF8Encoding(false));

        var runTask = new ProcessRunner().RunAsync(
            new ProcessRunRequest(
                script,
                [],
                directory,
                StdoutLineCallback: _ => throw new InvalidOperationException("long-running callback failure")),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("long-running callback failure", ex.Message);
    }

    [Fact]
    public void MissingExecutableThrows()
    {
        Assert.Throws<FileNotFoundException>(() => ExecutableResolver.Resolve("codex-agent-facade-missing-cli-xyz"));
    }

    [Fact]
    public void CmdCommandBuildUsesRawArgumentContract()
    {
        var launch = WindowsCmd.BuildCommandWithEnvironment(
            @"C:\tools\copilot.cmd",
            ["--prompt", "foo&whoami", "--output-format", "json"]);
        Assert.Contains("\"--prompt\"", launch.Command, StringComparison.Ordinal);
        Assert.Contains("\"foo&whoami\"", launch.Command, StringComparison.Ordinal);
        Assert.Single(launch.EnvironmentVariables);
        Assert.Equal("%", launch.EnvironmentVariables.Single().Value);
        var quoteLaunch = WindowsCmd.BuildCommandWithEnvironment(
            @"C:\tools\copilot.cmd",
            ["a\"b", "%PATH%"]);
        Assert.Contains("\"a\"\"b\"", quoteLaunch.Command, StringComparison.Ordinal);
        var percentVariable = quoteLaunch.EnvironmentVariables.Single().Key;
        Assert.Contains("%" + percentVariable + "%PATH%" + percentVariable + "%", quoteLaunch.Command, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => WindowsCmd.BuildCommandWithEnvironment(
            @"C:\tools\copilot.cmd",
            ["nul\0value"]));
        Assert.Throws<ArgumentException>(() => WindowsCmd.BuildCommandWithEnvironment(
            @"C:\tools\copilot.cmd",
            ["line\r\nbreak"]));
    }

    [Fact]
    public async Task RunsCmdAndBatFixturesWithoutDelayedExpansion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf cmd fixture ").FullName;
        var helper = Path.Combine(directory, "capture-arguments.cs");
        await File.WriteAllTextAsync(
            helper,
            """
            #:property TargetFramework=net10.0
            using System.Text;
            foreach (var argument in args)
            {
                Console.Error.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(argument)));
            }
            Console.WriteLine("{\"type\":\"assistant\",\"text\":\"cmd-ok\"}");
            """,
            new UTF8Encoding(false));

        var marker = Path.Combine(directory, "injection-marker.txt");
        var logicalArguments = new List<string>
        {
            "--plain",
            "with spaces",
            "%PATH%",
            "a\"b",
            "a\\\"b",
            @"trailing\",
            "a&b",
            "a|b",
            "a^b",
            "日本語と空白",
            "!bang!",
            "(parentheses)",
            "<input> >output",
            $"a&echo injected>{marker}",
            $"a\"&echo injected>{marker}",
        };

        var cases = new (string FileName, string Body, IReadOnlyList<string> Arguments)[]
        {
            (
                "default.cmd",
                $"""
                @echo off
                dotnet run --file "{helper}" -- %*
                exit /b %ERRORLEVEL%
                """,
                logicalArguments),
            (
                "disabled.cmd",
                $"""
                @echo off
                setlocal DisableDelayedExpansion
                dotnet run --file "{helper}" -- %*
                exit /b %ERRORLEVEL%
                """,
                logicalArguments),
            (
                "direct-first.cmd",
                $"""
                @echo off
                dotnet run --file "{helper}" -- %1
                exit /b %ERRORLEVEL%
                """,
                [logicalArguments[3]]),
            (
                "forward.bat",
                $"""
                @echo off
                setlocal DisableDelayedExpansion
                dotnet run --file "{helper}" -- %*
                exit /b %ERRORLEVEL%
                """,
                logicalArguments),
        };

        foreach (var testCase in cases)
        {
            var script = Path.Combine(directory, testCase.FileName);
            await File.WriteAllTextAsync(script, testCase.Body, new UTF8Encoding(false));
            ProcessLaunchInfo? launch = null;
            var result = await new ProcessRunner().RunAsync(
                new ProcessRunRequest(
                    script,
                    testCase.Arguments,
                    directory,
                    OnLaunchResolved: info => launch = info),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                "{\"type\":\"assistant\",\"text\":\"cmd-ok\"}",
                result.StandardOutput);
            var actualLaunch = launch ?? throw new InvalidOperationException("Launch information was not captured.");
            Assert.Equal(testCase.Arguments, actualLaunch.LogicalArguments);
            Assert.Equal(["/d", "/v:off", "/s", "/c"], actualLaunch.ProcessArguments);
            Assert.True(actualLaunch.UsedWindowsCmdWrapper);
            Assert.Equal(Path.GetExtension(script), Path.GetExtension(actualLaunch.ResolvedExecutable), ignoreCase: true);

            var actualArguments = result.StandardError
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line)))
                .ToArray();
            Assert.Equal(testCase.Arguments, actualArguments);
            Assert.False(File.Exists(marker));
        }

        var multilinePrompt = GitHubCopilotDriver.ApplyCopilotSkills(
            "issue #25 を調査してください。",
            ["github-copilot"]);
        Assert.Contains('\n', multilinePrompt);
        var defaultScript = Path.Combine(directory, "default.cmd");
        await Assert.ThrowsAsync<ArgumentException>(() => new ProcessRunner().RunAsync(
            new ProcessRunRequest(defaultScript, ["--prompt", multilinePrompt], directory),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => new ProcessRunner().RunAsync(
            new ProcessRunRequest(defaultScript, ["line1\r\nline2"], directory),
            CancellationToken.None));
    }

    [Fact]
    public async Task RunsPowerShellFixtureWithArgumentList()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf ps fixture ").FullName;
        var script = Path.Combine(directory, "fixture with spaces.ps1");
        await File.WriteAllTextAsync(
            script,
            """
            $ErrorActionPreference = 'Stop'
            foreach ($argument in $args) {
                [Console]::Error.WriteLine(
                    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($argument)))
            }
            [Console]::Out.WriteLine('{"type":"assistant","text":"ps-ok"}')
            """,
            new UTF8Encoding(false));

        var marker = Path.Combine(directory, "injection-marker.txt");
        var logicalArguments = new[]
        {
            "--prompt",
            "%PATH%",
            "a\"b",
            "a!b",
            "a&b",
            "a|b",
            "a^b",
            "a<b",
            "a>b",
            "(parentheses)",
            @"trailing\",
            "日本語と空白",
            "line1\nline2",
            "line1\r\nline2",
            $"a&echo injected>{marker}",
        };

        ProcessLaunchInfo? launch = null;
        var result = await new ProcessRunner().RunAsync(
            new ProcessRunRequest(
                script,
                logicalArguments,
                directory,
                OnLaunchResolved: info => launch = info),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "{\"type\":\"assistant\",\"text\":\"ps-ok\"}",
            result.StandardOutput);
        var actualLaunch = launch ?? throw new InvalidOperationException("Launch information was not captured.");
        Assert.Equal(script, actualLaunch.ResolvedExecutable);
        Assert.Equal(
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-File",
                script,
                .. logicalArguments,
            ],
            actualLaunch.ProcessArguments);
        Assert.Equal(logicalArguments, actualLaunch.LogicalArguments);
        Assert.Equal("powershell", actualLaunch.Wrapper);
        Assert.False(actualLaunch.UsedWindowsCmdWrapper);
        Assert.Null(actualLaunch.RawArguments);
        Assert.Equal("pwsh.exe", Path.GetFileName(actualLaunch.ProcessFileName));

        var actualArguments = result.StandardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line)))
            .ToArray();
        Assert.Equal(logicalArguments, actualArguments);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task WritesExactStandardInputToPowerShellAndCmdFixtures()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf stdin fixture ").FullName;
        var psScript = Path.Combine(directory, "capture-stdin.ps1");
        await File.WriteAllTextAsync(
            psScript,
            """
            [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
            $bytes = [Text.Encoding]::UTF8.GetBytes([Console]::In.ReadToEnd())
            [Console]::Error.WriteLine('B64:' + [Convert]::ToBase64String($bytes))
            [Console]::Out.WriteLine('stdin-ok')
            """,
            new UTF8Encoding(false));
        var helper = Path.Combine(directory, "capture-stdin.cs");
        await File.WriteAllTextAsync(
            helper,
            """
            #:property TargetFramework=net10.0
            using System.Text;
            var bytes = Encoding.UTF8.GetBytes(Console.In.ReadToEnd());
            Console.Error.WriteLine("B64:" + Convert.ToBase64String(bytes));
            Console.WriteLine("stdin-ok");
            """,
            new UTF8Encoding(false));
        var cmdScript = Path.Combine(directory, "forward-stdin.cmd");
        await File.WriteAllTextAsync(
            cmdScript,
            $"""
            @echo off
            dotnet run --file "{helper}" --
            exit /b %ERRORLEVEL%
            """,
            new UTF8Encoding(false));

        var input = "Use the /github-copilot skill.\nissue #25 を調査してください。\r\n"
            + "%PATH% a\"b a!b a&b a|b a^b <input> (parentheses) 日本語 "
            + @"trailing\"
            + "\0終端"
            + new string('x', 128 * 1024);

        ProcessLaunchInfo? powerShellLaunch = null;
        var powerShellResult = await new ProcessRunner().RunAsync(
            new ProcessRunRequest(
                psScript,
                [],
                directory,
                OnLaunchResolved: info => powerShellLaunch = info,
                    StandardInputText: input),
                CancellationToken.None);
        Assert.Equal(0, powerShellResult.ExitCode);
        Assert.Equal("stdin-ok", powerShellResult.StandardOutput);
        Assert.Equal(input, DecodeSingleBase64Line(powerShellResult.StandardError));
        var actualPowerShellLaunch = powerShellLaunch ?? throw new InvalidOperationException("PowerShell launch was not captured.");
        Assert.True(actualPowerShellLaunch.HasStandardInput);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(input), actualPowerShellLaunch.StandardInputByteCount);
        Assert.Equal("powershell", actualPowerShellLaunch.Wrapper);
        Assert.Null(actualPowerShellLaunch.RawArguments);

        ProcessLaunchInfo? cmdLaunch = null;
        var cmdResult = await new ProcessRunner().RunAsync(
            new ProcessRunRequest(
                cmdScript,
                [],
                directory,
                OnLaunchResolved: info => cmdLaunch = info,
                StandardInputText: input),
            CancellationToken.None);
        Assert.Equal(0, cmdResult.ExitCode);
        Assert.Equal("stdin-ok", cmdResult.StandardOutput);
        Assert.Equal(input, DecodeSingleBase64Line(cmdResult.StandardError));
        var actualCmdLaunch = cmdLaunch ?? throw new InvalidOperationException("cmd launch was not captured.");
        Assert.True(actualCmdLaunch.HasStandardInput);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(input), actualCmdLaunch.StandardInputByteCount);
        Assert.Equal("windows-cmd", actualCmdLaunch.Wrapper);
        Assert.Equal(["/d", "/v:off", "/s", "/c"], actualCmdLaunch.ProcessArguments);
        Assert.Empty(actualCmdLaunch.LogicalArguments);
        Assert.NotNull(actualCmdLaunch.RawArguments);
        Assert.DoesNotContain(input, actualCmdLaunch.RawArguments!, StringComparison.Ordinal);

        ProcessLaunchInfo? emptyLaunch = null;
        var emptyResult = await new ProcessRunner().RunAsync(
            new ProcessRunRequest(
                psScript,
                [],
                directory,
                OnLaunchResolved: info => emptyLaunch = info),
            CancellationToken.None);
        Assert.Equal(0, emptyResult.ExitCode);
        Assert.Equal(string.Empty, DecodeSingleBase64Line(emptyResult.StandardError));
        var actualEmptyLaunch = emptyLaunch ?? throw new InvalidOperationException("empty stdin launch was not captured.");
        Assert.False(actualEmptyLaunch.HasStandardInput);
        Assert.Null(actualEmptyLaunch.StandardInputByteCount);
    }

    [Fact]
    public async Task CancellationKillsProcessWhileStandardInputIsConfigured()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf stdin cancel ").FullName;
        var script = Path.Combine(directory, "wait.ps1");
        await File.WriteAllTextAsync(
            script,
            """
            Start-Sleep -Seconds 30
            """,
            new UTF8Encoding(false));

        using var cts = new CancellationTokenSource();
        var runTask = new ProcessRunner().RunAsync(
            new ProcessRunRequest(
                script,
                [],
                directory,
                StandardInputText: new string('x', 128 * 1024)),
            cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RejectsNulCmdArgumentBeforeStartingProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("caf cmd nul ").FullName;
        var script = Path.Combine(directory, "reject-nul.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nexit /b 0\r\n");
        var runner = new ProcessRunner();
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            new ProcessRunRequest(script, ["nul\0value"], directory),
            CancellationToken.None));
    }

    [Fact]
    public void DecodesStrictUtf8AndInjectedOemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var provider = new StubProcessEncodingProvider(Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback));
        Assert.Equal("日本語", StderrDecoder.Decode(
            Encoding.UTF8.GetBytes("日本語"),
            windowsCmdWrapper: false,
            provider));
        Assert.Equal("日本語", StderrDecoder.Decode(
            provider.Encoding.GetBytes("日本語"),
            windowsCmdWrapper: true,
            provider));
    }

    [Fact]
    public void WindowsCmdWrapperUsesOemForUtf8AmbiguousBytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var provider = new StubProcessEncodingProvider(Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback));
        var bytes = Convert.FromHexString("EAA3A1");

        Assert.Equal("凜｡", StderrDecoder.Decode(bytes, windowsCmdWrapper: true, provider));
    }

    [Fact]
    public void RejectsBytesThatAreInvalidInBothEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var provider = new StubProcessEncodingProvider(Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback));
        Assert.Throws<DecoderFallbackException>(() => StderrDecoder.Decode(
            [0x81],
            windowsCmdWrapper: true,
            provider));
    }

    [Fact]
    public void RejectsInvalidUtf8ForNonWrapper()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var provider = new StubProcessEncodingProvider(Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback));
        Assert.Throws<DecoderFallbackException>(() => StderrDecoder.Decode(
            [0xFF],
            windowsCmdWrapper: false,
            provider));
    }

    [Fact]
    public void PropagatesOemDecodeFailure()
    {
        var expected = new InvalidOperationException("OEM decode failed.");
        var provider = new ThrowingProcessEncodingProvider(expected);
        var actual = Assert.Throws<InvalidOperationException>(() => StderrDecoder.Decode(
            [0xFF],
            windowsCmdWrapper: true,
            provider));
        Assert.Same(expected, actual);
    }

    [Fact]
    public void WindowsOemEncodingIsCached()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new SystemProcessEncodingProvider();
        Assert.Same(provider.GetWindowsOemEncoding(), provider.GetWindowsOemEncoding());
    }

    private static string DecodeSingleBase64Line(string standardError)
    {
        var lines = standardError.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.StartsWith("B64:", lines[0], StringComparison.Ordinal);
        return Encoding.UTF8.GetString(Convert.FromBase64String(lines[0]["B64:".Length..]));
    }

    private static bool ProcessHasExited(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return process.HasExited;
        }

        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}

internal sealed class StubProcessEncodingProvider : IProcessEncodingProvider
{
    public StubProcessEncodingProvider(Encoding encoding)
    {
        Encoding = encoding;
    }

    public Encoding Encoding { get; }

    public Encoding GetWindowsOemEncoding()
    {
        return Encoding;
    }
}

internal sealed class ThrowingProcessEncodingProvider : IProcessEncodingProvider
{
    private readonly Exception _exception;

    public ThrowingProcessEncodingProvider(Exception exception)
    {
        _exception = exception;
    }

    public Encoding GetWindowsOemEncoding()
    {
        throw _exception;
    }
}

public sealed class ThrowingProcessJobGuard : IProcessJobGuard
{
    public int AssignCount { get; private set; }

    public int ProcessId { get; private set; }

    public IDisposable? Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        AssignCount++;
        ProcessId = process.Id;
        throw new InvalidOperationException("job assign failed");
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
        var capturing = new CapturingLoggerFactory();
        using (FacadeLog.UseLoggerFactory(capturing))
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
            Assert.DoesNotContain(xai, capturing.Logger.Buffer.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("dXNlcjpwYXNz", capturing.Logger.Buffer.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("MIISECRETKEYMATERIAL", capturing.Logger.Buffer.ToString(), StringComparison.Ordinal);

            var events = TestRunLogs.ReadShared(Directory.GetFiles(factory.LogDirectory, "*.events.jsonl").Single());
            var text = TestRunLogs.ReadShared(Directory.GetFiles(factory.LogDirectory, "*.log").Single());
            Assert.Contains("\"type\":\"failed\"", events, StringComparison.Ordinal);
            Assert.DoesNotContain(xai, events, StringComparison.Ordinal);
            Assert.DoesNotContain("dXNlcjpwYXNz", events, StringComparison.Ordinal);
            Assert.DoesNotContain("MIISECRETKEYMATERIAL", events, StringComparison.Ordinal);
            Assert.DoesNotContain(xai, text, StringComparison.Ordinal);
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
    public async Task LaunchEventRedactsLogicalAndRawArguments()
    {
        var token = "gho_" + new string('a', 36);
        var humanPrefix = "launch resolved=" + @"C:\tools\fixture.cmd"
            + " processFileName=cmd.exe wrapper=windows-cmd rawArguments=";
        var rawArguments = new string(
            'x',
            AgentRunLog.HumanSummaryMaxLength - humanPrefix.Length - 3) + " " + token;
        await using var log = TestRunLogs.CreateLog();
        log.WriteLaunch(new ProcessLaunchInfo(
            "fixture.cmd",
            @"C:\tools\fixture.cmd",
            "cmd.exe",
            ["/d", "/s", "/c"],
            [token],
            UsedWindowsCmdWrapper: true,
            RawArguments: rawArguments,
            HasStandardInput: true,
            StandardInputByteCount: 42));

        var events = TestRunLogs.ReadShared(log.EventsPath);
        var text = TestRunLogs.ReadShared(log.TextLogPath);
        Assert.Contains("\"logicalArguments\"", events, StringComparison.Ordinal);
        Assert.Contains("\"rawArguments\"", events, StringComparison.Ordinal);
        Assert.DoesNotContain(token, events, StringComparison.Ordinal);
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(token[..2], text, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Replacement, events, StringComparison.Ordinal);
        Assert.DoesNotContain("rawArguments=", text, StringComparison.Ordinal);
        Assert.Contains("\"hasStandardInput\":true", events, StringComparison.Ordinal);
        Assert.Contains("\"standardInputByteCount\":42", events, StringComparison.Ordinal);
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
    public async Task HeartbeatDisposeDoesNotTraceNormalCompletion()
    {
        var capturing = new CapturingLoggerFactory();
        using (FacadeLog.UseLoggerFactory(capturing))
        {
            await using (var log = TestRunLogs.CreateLog())
            {
                await Task.Delay(20);
            }

            Assert.DoesNotContain("OperationCanceledException", capturing.Logger.Buffer.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task HeartbeatProcessProbeFailureIsTraced()
    {
        var capturing = new CapturingLoggerFactory();
        using (FacadeLog.UseLoggerFactory(capturing))
        {
            await using var log = (AgentRunLog)TestRunLogs.CreateLog();
            log.AttachProcess(new ThrowingProcessLifetime());
            log.WriteHeartbeat();
            await log.DisposeAsync();
        }

        Assert.DoesNotContain("OperationCanceledException", capturing.Logger.Buffer.ToString(), StringComparison.Ordinal);
        Assert.Contains("heartbeat process probe failed", capturing.Logger.Buffer.ToString(), StringComparison.Ordinal);
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
    public async Task DoesNotTreatSplitCrLfAsTwoNewlines()
    {
        await using var log = (AgentRunLog)TestRunLogs.CreateLog();
        WriteThoughtEvent(log, "hello\r");
        Assert.Empty(HumanLines(TestRunLogs.ReadShared(log.TextLogPath), "thought:"));

        WriteThoughtEvent(log, "\nworld");
        var text = TestRunLogs.ReadShared(log.TextLogPath);
        var thoughtLines = HumanLines(text, "thought:");
        Assert.Single(thoughtLines);
        Assert.Contains("thought: hello", thoughtLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("thought: \n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("thought:\n", text, StringComparison.Ordinal);
        Assert.False(thoughtLines.Any(line => line.TrimEnd().EndsWith("thought:", StringComparison.Ordinal)));

        await log.DisposeAsync();
        thoughtLines = HumanLines(TestRunLogs.ReadShared(log.TextLogPath), "thought:");
        Assert.Equal(2, thoughtLines.Count);
        Assert.Contains("thought: world", thoughtLines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemainderAfterNewlineUsesCurrentFragmentTimestamp()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        await using var log = (AgentRunLog)TestRunLogs.CreateLog(time);
        WriteThoughtEvent(log, "aaa");
        time.Advance(TimeSpan.FromSeconds(5));
        WriteThoughtEvent(log, "bbb\nccc");
        await log.DisposeAsync();

        var thoughtLines = HumanLines(TestRunLogs.ReadShared(log.TextLogPath), "thought:");
        Assert.Equal(2, thoughtLines.Count);
        Assert.StartsWith("2026-08-26T12:00:00.000Z thought: aaabbb", thoughtLines[0], StringComparison.Ordinal);
        Assert.StartsWith("2026-08-26T12:00:05.000Z thought: ccc", thoughtLines[1], StringComparison.Ordinal);
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

internal sealed class ThrowingProcessLifetime : IProcessLifetime
{
    public int Id => throw new InvalidOperationException("heartbeat process probe failed");

    public bool HasExited => throw new InvalidOperationException("heartbeat process probe failed");
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
    public void NormalizeLogDirectoryDoesNotKeepWindowsDriveRelativePaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var expected = Path.GetFullPath(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "logs"));
        Assert.Equal(expected, AgentRunLogFactory.NormalizeLogDirectory("C:logs"));
        Assert.Equal(expected, AgentRunLogFactory.NormalizeLogDirectory(@"\logs"));
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

/// <summary>
/// MCP 公開契約が「丸投げ必須」へ後退していないことを検証する。
/// 全文一致ではなく、caller の plan / split / worker prompt 構成を禁止しないこと、
/// Facade は worker task を意味的に書き換えないこと、skills は Driver が変換する場合があること、
/// request_id が distinct job 単位であること、start_agent が毎回 complete RPC であることを見る。
/// </summary>
[Collection("http-host")]
public class McpPublicContractTests
{
    [Fact]
    public void SourceContractAllowsCallerConstructedWorkerJobs()
    {
        AssertWorkerDelegationContract(McpPublicContract.ServerInstructions);
        AssertWorkerDelegationContract(McpPublicContract.StartAgentDescription);
        AssertWaitFirstContract(McpPublicContract.ServerInstructions);
        AssertWaitFirstContract(McpPublicContract.StartAgentDescription);
        AssertWaitFirstContract(McpPublicContract.GetAgentJobDescription);
        AssertNormalWaitContract(McpPublicContract.ServerInstructions);
        AssertNormalWaitContract(McpPublicContract.StartAgentDescription);
        AssertNormalWaitContract(McpPublicContract.WaitAgentJobDescription);
        AssertNormalWaitContract(McpPublicContract.WaitTimeoutSecondsDescription);
        Assert.Contains("timeout_seconds", McpPublicContract.WaitAgentJobDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("Poll get_agent_job", McpPublicContract.WaitAgentJobDescription, StringComparison.Ordinal);
        AssertWorkerPromptContract(McpPublicContract.PromptDescription);
        AssertRequestIdContract(McpPublicContract.RequestIdDescription);
        AssertWorkingDirectoryContract(McpPublicContract.WorkingDirectoryDescription);
        AssertCompleteRpcContract(McpPublicContract.ServerInstructions);
        AssertCompleteRpcContract(McpPublicContract.StartAgentDescription);
        AssertSkillsContract(McpPublicContract.SkillsDescription);
        AssertDoesNotContainLegacyPassthroughPhrases(
            McpPublicContract.ServerInstructions
            + "\n" + McpPublicContract.StartAgentDescription
            + "\n" + McpPublicContract.PromptDescription
            + "\n" + McpPublicContract.RequestIdDescription
            + "\n" + McpPublicContract.WorkingDirectoryDescription
            + "\n" + McpPublicContract.SkillsDescription);
    }

    [Fact]
    public void ReadmeAllowsParentAgentWorkerDelegation()
    {
        var readme = File.ReadAllText(Path.Combine(LocateRepoRoot(), "README.md"));
        Assert.Contains("呼び出し側", readme, StringComparison.Ordinal);
        Assert.Contains("計画・分割", readme, StringComparison.Ordinal);
        Assert.Contains("worker prompt", readme, StringComparison.Ordinal);
        Assert.Contains("元の user prompt 全体を転送する必要はない", readme, StringComparison.Ordinal);
        Assert.Contains("distinct な agent job", readme, StringComparison.Ordinal);
        Assert.Contains("同じ `start_agent` の結果を取り損ねた再試行だけ", readme, StringComparison.Ordinal);
        Assert.Contains("同じ Codex thread や同じ外部 agent `session_id` を継続することは、`request_id` の再利用理由にならない", readme, StringComparison.Ordinal);
        Assert.Contains("呼び出しごとに完全な引数セットを渡す RPC", readme, StringComparison.Ordinal);
        Assert.Contains("前回の `working_directory` 等は MCP / Facade 側で暗黙継承されない", readme, StringComparison.Ordinal);
        Assert.Contains("`completed` は CLI 実行が完了したことだけを示す", readme, StringComparison.Ordinal);
        Assert.Contains("`failure.kind`", readme, StringComparison.Ordinal);
        Assert.Contains("outputKind", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("pollAfterMs", readme, StringComparison.Ordinal);
        Assert.Contains("Facade 自身は planner や orchestrator にならない", readme, StringComparison.Ordinal);
        Assert.Contains("task payload を再解釈しない", readme, StringComparison.Ordinal);
        Assert.Contains("Cursor は現在このフィールドを変換しない", readme, StringComparison.Ordinal);
        Assert.Contains("[--model <model>]", readme, StringComparison.Ordinal);
        Assert.Contains("prompt 本文に現れたモデル名から起動設定を推測しない", readme, StringComparison.Ordinal);
        Assert.Contains("GitHub Copilot と Grok Build は agent 固有の prompt 指示へ変換する", readme, StringComparison.Ordinal);
        Assert.Contains("`wait_agent_job`", readme, StringComparison.Ordinal);
        Assert.Contains("通常の完了待ちは `wait_agent_job(job_id)`", readme, StringComparison.Ordinal);
        Assert.Contains("理由がある場合だけ指定する", readme, StringComparison.Ordinal);
        Assert.Contains("tool_timeout_sec = 1800", readme, StringComparison.Ordinal);
        Assert.Contains("rawOutput", readme, StringComparison.Ordinal);
        Assert.Contains("既定では返さない", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Codex / Facade は planner や orchestrator にならない", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("作業ごとに呼び出し側が `request_id` を一度生成", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("ユーザーの prompt を構造化 MCP 入力として受け", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Driver ごとに native 形式へ変換する", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("再解釈・書き換えせず selected agent へ転送する", readme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishedMcpSchemaMatchesWorkerDelegationContract()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        await using var client = await session.CreateClientAsync();

        Assert.Equal(McpPublicContract.ServerInstructions, client.ServerInstructions);

        var tools = await client.ListToolsAsync();
        var start = Assert.Single(tools, tool => tool.Name == "start_agent");
        var get = Assert.Single(tools, tool => tool.Name == "get_agent_job");
        var wait = Assert.Single(tools, tool => tool.Name == "wait_agent_job");
        Assert.Equal(McpPublicContract.StartAgentDescription, start.Description);
        Assert.Equal(McpPublicContract.GetAgentJobDescription, get.Description);
        Assert.Equal(McpPublicContract.WaitAgentJobDescription, wait.Description);
        Assert.Equal(McpPublicContract.WaitTimeoutSecondsDescription, ReadInputPropertyDescription(wait, "timeout_seconds"));
        Assert.True(wait.JsonSchema.TryGetProperty("properties", out var waitPropertiesForTimeout));
        Assert.True(waitPropertiesForTimeout.TryGetProperty("timeout_seconds", out var timeoutSchema));
        var timeoutType = timeoutSchema.GetProperty("type");
        if (timeoutType.ValueKind == JsonValueKind.String)
        {
            Assert.Equal("integer", timeoutType.GetString());
        }
        else
        {
            Assert.Contains("integer", timeoutType.EnumerateArray().Select(item => item.GetString()), StringComparer.Ordinal);
        }
        if (wait.JsonSchema.TryGetProperty("required", out var required))
        {
            Assert.DoesNotContain("timeout_seconds", required.EnumerateArray().Select(item => item.GetString()), StringComparer.Ordinal);
        }
        Assert.False(
            wait.JsonSchema.TryGetProperty("properties", out var waitProperties)
            && waitProperties.TryGetProperty("cancellationToken", out _),
            "wait_agent_job must not expose CancellationToken in the MCP schema.");
        Assert.Equal(McpPublicContract.RequestIdDescription, ReadInputPropertyDescription(start, "request_id"));
        Assert.Equal(McpPublicContract.PromptDescription, ReadInputPropertyDescription(start, "prompt"));
        Assert.Equal(McpPublicContract.WorkingDirectoryDescription, ReadInputPropertyDescription(start, "working_directory"));
        Assert.Equal(McpPublicContract.SkillsDescription, ReadInputPropertyDescription(start, "skills"));
        Assert.Equal(McpPublicContract.ModelDescription, ReadInputPropertyDescription(start, "model"));
        if (start.JsonSchema.TryGetProperty("required", out var startRequired))
        {
            Assert.DoesNotContain(
                "model",
                startRequired.EnumerateArray().Select(item => item.GetString()),
                StringComparer.Ordinal);
        }

        AssertWorkerDelegationContract(client.ServerInstructions + "\n" + start.Description);
        AssertWaitFirstContract(client.ServerInstructions + "\n" + start.Description + "\n" + get.Description + "\n" + wait.Description);
        AssertNormalWaitContract(client.ServerInstructions + "\n" + start.Description + "\n" + wait.Description);
        AssertNormalWaitContract(ReadInputPropertyDescription(wait, "timeout_seconds"));
        AssertWorkerPromptContract(ReadInputPropertyDescription(start, "prompt"));
        AssertRequestIdContract(ReadInputPropertyDescription(start, "request_id"));
        AssertWorkingDirectoryContract(ReadInputPropertyDescription(start, "working_directory"));
        AssertCompleteRpcContract(McpPublicContract.ServerInstructions);
        AssertCompleteRpcContract(McpPublicContract.StartAgentDescription);
        AssertSkillsContract(ReadInputPropertyDescription(start, "skills"));
        AssertDoesNotContainLegacyPassthroughPhrases(
            client.ServerInstructions
            + "\n" + start.Description
            + "\n" + ReadInputPropertyDescription(start, "request_id")
            + "\n" + ReadInputPropertyDescription(start, "prompt")
            + "\n" + ReadInputPropertyDescription(start, "working_directory")
            + "\n" + ReadInputPropertyDescription(start, "skills"));
    }

    [Fact]
    public async Task StartAgentForwardsCallerWorkerPromptUnchangedAndAllowsDistinctRequestIds()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = new ProcessRunResult(
            0,
            "{\"type\":\"text\",\"data\":\"ok\"}\n{\"type\":\"end\",\"sessionId\":\"11111111-1111-1111-1111-111111111111\"}",
            "");

        await using var client = await session.CreateClientAsync();
        const string firstPrompt = "Implement only the parser tests. Do not change production code.";
        const string secondPrompt = "Fix the failing parser assertion. Leave other files unchanged.";

        var first = await CallStartAgentAsync(client, "req-worker-a", AgentFacade.GrokBuildAgent, firstPrompt);
        await WaitForMcpJobAsync(client, first.JobId);
        Assert.Equal(firstPrompt, ReadGrokPrompt(session.Runner.LastRequest!));

        var second = await CallStartAgentAsync(client, "req-worker-b", AgentFacade.GrokBuildAgent, secondPrompt);
        await WaitForMcpJobAsync(client, second.JobId);
        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(secondPrompt, ReadGrokPrompt(session.Runner.LastRequest!));
        Assert.Equal(2, session.Runner.CallCount);

        var retry = await CallStartAgentAsync(client, "req-worker-a", AgentFacade.GrokBuildAgent, firstPrompt);
        Assert.Equal(first.JobId, retry.JobId);
        Assert.Equal(2, session.Runner.CallCount);
    }

    [Fact]
    public async Task StartAgentTranslatesSkillsOnlyForDriversThatSupportIt()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        await using var client = await session.CreateClientAsync();
        const string task = "Implement only the parser tests. Do not change production code.";
        string[] skills = ["review"];

        session.Runner.Result = CopilotStdout();
        await StartAndWaitAsync(client, "req-skill-copilot", AgentFacade.GitHubCopilotAgent, task, skills);
        Assert.Equal("Use the /review skill.\n" + task, session.Runner.LastRequest!.StandardInputText);
        Assert.Contains(task, session.Runner.LastRequest.StandardInputText, StringComparison.Ordinal);

        session.Runner.Result = GrokStdout();
        await StartAndWaitAsync(client, "req-skill-grok", AgentFacade.GrokBuildAgent, task, skills);
        var grokPrompt = ReadGrokPrompt(session.Runner.LastRequest!);
        Assert.Equal("/review\n" + task, grokPrompt);
        Assert.EndsWith(task, grokPrompt, StringComparison.Ordinal);

        session.Runner.Result = CursorStdout();
        await StartAndWaitAsync(client, "req-skill-cursor", AgentFacade.CursorAgent, task, skills);
        Assert.Equal(task, session.Runner.LastRequest!.Arguments[^1]);
        Assert.DoesNotContain("/review", session.Runner.LastRequest.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("Use the /review skill.", session.Runner.LastRequest.Arguments, StringComparer.Ordinal);
        Assert.Equal(3, session.Runner.CallCount);
    }

    [Fact]
    public async Task StartAgentForwardsModelToEachDriverAndLeavesPromptUnchanged()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        await using var client = await session.CreateClientAsync();
        const string prompt = "Mention model gpt-5 in the notes. Do not treat that sentence as a launch setting.";

        session.Runner.Result = CursorStdout();
        await StartAndWaitAsync(
            client,
            "req-model-cursor",
            AgentFacade.CursorAgent,
            prompt,
            model: "composer-2",
            sessionId: "33333333-3333-3333-3333-333333333333");
        Assert.Equal(prompt, session.Runner.LastRequest!.Arguments[^1]);
        Assert.Contains("--model", session.Runner.LastRequest.Arguments);
        Assert.Contains("composer-2", session.Runner.LastRequest.Arguments);
        Assert.Contains("--resume", session.Runner.LastRequest.Arguments);
        Assert.Contains("33333333-3333-3333-3333-333333333333", session.Runner.LastRequest.Arguments);
        Assert.DoesNotContain("gpt-5", session.Runner.LastRequest.Arguments);

        session.Runner.Result = CopilotStdout();
        await StartAndWaitAsync(client, "req-model-copilot", AgentFacade.GitHubCopilotAgent, prompt, model: "gpt-5.4");
        Assert.Equal(prompt, session.Runner.LastRequest!.StandardInputText);
        Assert.Contains("--model", session.Runner.LastRequest.Arguments);
        Assert.Contains("gpt-5.4", session.Runner.LastRequest.Arguments);
        Assert.DoesNotContain(prompt, session.Runner.LastRequest.Arguments);

        session.Runner.Result = GrokStdout();
        await StartAndWaitAsync(client, "req-model-grok", AgentFacade.GrokBuildAgent, prompt, model: "grok-4");
        Assert.Equal(prompt, ReadGrokPrompt(session.Runner.LastRequest!));
        Assert.Contains("--model", session.Runner.LastRequest.Arguments);
        Assert.Contains("grok-4", session.Runner.LastRequest.Arguments);

        session.Runner.Result = CursorStdout();
        await StartAndWaitAsync(client, "req-model-omit", AgentFacade.CursorAgent, prompt);
        Assert.DoesNotContain("--model", session.Runner.LastRequest!.Arguments);
        Assert.Equal(prompt, session.Runner.LastRequest.Arguments[^1]);

        session.Runner.Result = CursorStdout();
        await StartAndWaitAsync(client, "req-model-empty", AgentFacade.CursorAgent, prompt, model: "");
        Assert.DoesNotContain("--model", session.Runner.LastRequest!.Arguments);
    }

    private static void AssertWorkerDelegationContract(string text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("caller", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("worker prompt", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not plan", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Do not replan or split the task", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Pass the user prompt through", text, StringComparison.Ordinal);
    }

    private static void AssertWaitFirstContract(string text)
    {
        Assert.Contains("wait_agent_job", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Poll get_agent_job", text, StringComparison.Ordinal);
    }

    private static void AssertNormalWaitContract(string text)
    {
        Assert.Contains("omit", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("300", text, StringComparison.Ordinal);
    }

    private static void AssertWorkerPromptContract(string text)
    {
        Assert.Contains("worker prompt constructed by the caller", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not reinterpret this task payload", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("need not be the original user prompt", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skills", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("forwarded unchanged", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User prompt forwarded to the selected agent", text, StringComparison.Ordinal);
    }

    private static void AssertRequestIdContract(string text)
    {
        Assert.Contains("distinct", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lost", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("session", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("For each user task generate one request_id", text, StringComparison.Ordinal);
    }

    private static void AssertWorkingDirectoryContract(string text)
    {
        Assert.Contains("every start_agent call", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not retained", text, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertCompleteRpcContract(string text)
    {
        Assert.Contains("request_id, agent, prompt, and working_directory", text, StringComparison.Ordinal);
    }

    private static void AssertSkillsContract(string text)
    {
        Assert.Contains("GitHub Copilot and Grok Build translate", text, StringComparison.Ordinal);
        Assert.Contains("Cursor currently does not translate this field", text, StringComparison.Ordinal);
        Assert.Contains("worker prompt", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Each driver converts them to that agent's native invocation", text, StringComparison.Ordinal);
    }

    private static void AssertDoesNotContainLegacyPassthroughPhrases(string text)
    {
        string[] forbidden =
        [
            "Do not replan or split the task",
            "Pass the user prompt through",
            "For each user task generate one request_id",
            "User prompt forwarded to the selected agent",
            "This facade does not plan or split the task",
            "forwards that supplied worker prompt unchanged",
            "forwarded unchanged to the selected external agent",
            "Each driver converts them to that agent's native invocation",
        ];
        foreach (var phrase in forbidden)
        {
            Assert.DoesNotContain(phrase, text, StringComparison.Ordinal);
        }
    }

    private static string ReadInputPropertyDescription(McpClientTool tool, string propertyName)
    {
        if (!tool.JsonSchema.TryGetProperty("properties", out var properties)
            || !properties.TryGetProperty(propertyName, out var property)
            || !property.TryGetProperty("description", out var description))
        {
            throw new InvalidOperationException(
                "start_agent input schema is missing description for " + propertyName + ".");
        }

        var value = description.GetString();
        Assert.False(string.IsNullOrWhiteSpace(value), propertyName + " description is empty.");
        return value;
    }

    private static string LocateRepoRoot([CallerFilePath] string? callerFile = null)
    {
        var seeds = new List<string> { Directory.GetCurrentDirectory() };
        if (!string.IsNullOrEmpty(callerFile))
        {
            var testsDir = Path.GetDirectoryName(callerFile);
            if (!string.IsNullOrEmpty(testsDir))
            {
                seeds.Add(Path.GetFullPath(Path.Combine(testsDir, "..")));
            }
        }

        foreach (var seed in seeds)
        {
            for (var dir = new DirectoryInfo(seed); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "README.md"))
                    && Directory.Exists(Path.Combine(dir.FullName, "src")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string ReadGrokPrompt(ProcessRunRequest request)
    {
        for (var i = 0; i < request.Arguments.Count - 1; i++)
        {
            if (request.Arguments[i] == "-p")
            {
                return request.Arguments[i + 1];
            }
        }

        Assert.Fail("Grok launch is missing -p prompt.");
        return "";
    }

    private static ProcessRunResult GrokStdout()
    {
        return new ProcessRunResult(
            0,
            "{\"type\":\"text\",\"data\":\"ok\"}\n{\"type\":\"end\",\"sessionId\":\"11111111-1111-1111-1111-111111111111\"}",
            "");
    }

    private static ProcessRunResult CopilotStdout()
    {
        return new ProcessRunResult(
            0,
            """
            {"type":"assistant.message","data":{"content":"ok"}}
            {"type":"result","sessionId":"22222222-2222-2222-2222-222222222222","exitCode":0}
            """,
            "");
    }

    private static ProcessRunResult CursorStdout()
    {
        return new ProcessRunResult(
            0,
            """{"type":"result","subtype":"success","result":"ok","session_id":"33333333-3333-3333-3333-333333333333"}""",
            "");
    }

    private static async Task<AgentJobPublicSnapshot> StartAndWaitAsync(
        McpClient client,
        string requestId,
        string agent,
        string prompt,
        IReadOnlyList<string>? skills = null,
        string? model = null,
        string? sessionId = null)
    {
        var started = await CallStartAgentAsync(client, requestId, agent, prompt, skills, model, sessionId);
        return await WaitForMcpJobAsync(client, started.JobId);
    }

    private static async Task<AgentJobPublicSnapshot> CallStartAgentAsync(
        McpClient client,
        string requestId,
        string agent,
        string prompt,
        IReadOnlyList<string>? skills = null,
        string? model = null,
        string? sessionId = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["agent"] = agent,
            ["prompt"] = prompt,
            ["working_directory"] = Path.GetTempPath(),
        };
        if (skills is not null)
        {
            arguments["skills"] = skills.ToArray();
        }

        if (model is not null)
        {
            arguments["model"] = model;
        }

        if (sessionId is not null)
        {
            arguments["session_id"] = sessionId;
        }

        var call = await client.CallToolAsync("start_agent", arguments);
        var text = string.Concat(call.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(call.IsError != true, text);
        var result = JsonSerializer.Deserialize<AgentJobPublicSnapshot>(text, AgentJson.Options);
        Assert.NotNull(result);
        McpPublicJsonAssert.Compact(text);
        return result;
    }

    private static async Task<AgentJobPublicSnapshot> WaitForMcpJobAsync(McpClient client, string jobId)
    {
        for (var i = 0; i < 100; i++)
        {
            var call = await client.CallToolAsync(
                "get_agent_job",
                new Dictionary<string, object?> { ["job_id"] = jobId });
            var text = string.Concat(call.Content.OfType<TextContentBlock>().Select(block => block.Text));
            Assert.True(call.IsError != true, text);
            var snapshot = JsonSerializer.Deserialize<AgentJobPublicSnapshot>(text, AgentJson.Options);
            Assert.NotNull(snapshot);
            McpPublicJsonAssert.Compact(text);
            if (snapshot.Status != AgentJobStatus.Running)
            {
                return snapshot;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("job did not finish: " + jobId);
    }
}

[Collection("http-host")]
public class McpHttpHostTests
{
    private static readonly object EnvironmentLock = new();

    [Fact]
    public void FromEnvironmentThrowsWhenTokenIsMissing()
    {
        lock (EnvironmentLock)
        {
            using (OverrideEnv(McpHttpHost.TokenEnvironmentVariable, null))
            using (OverrideEnv(McpHttpHost.PortEnvironmentVariable, null))
            {
                var ex = Assert.Throws<InvalidOperationException>(McpHttpHost.FromEnvironment);
                Assert.Contains(McpHttpHost.TokenEnvironmentVariable, ex.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void FromEnvironmentThrowsWhenPortIsInvalid()
    {
        lock (EnvironmentLock)
        {
            using (OverrideEnv(McpHttpHost.TokenEnvironmentVariable, "secret"))
            using (OverrideEnv(McpHttpHost.PortEnvironmentVariable, "0"))
            {
                var ex = Assert.Throws<ArgumentException>(McpHttpHost.FromEnvironment);
                Assert.Contains(McpHttpHost.PortEnvironmentVariable, ex.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void FromEnvironmentUsesDefaultPort()
    {
        lock (EnvironmentLock)
        {
            using (OverrideEnv(McpHttpHost.TokenEnvironmentVariable, "secret"))
            using (OverrideEnv(McpHttpHost.PortEnvironmentVariable, null))
            {
                var options = McpHttpHost.FromEnvironment();
                Assert.Equal("secret", options.Token);
                Assert.Equal(McpHttpHost.DefaultPort, options.Port);
            }
        }
    }

    [Fact]
    public void CreateThrowsWhenTokenIsBlank()
    {
        var ex = Assert.Throws<ArgumentException>(() => McpHttpHost.Create(
            [],
            new McpHttpHostOptions { Token = "  ", Port = 0 }));
        Assert.Contains("Token is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindsLoopbackOnlyAndServesStartAndGet()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = GrokResult("ok", "22222222-2222-2222-2222-222222222222");

        var addresses = session.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Assert.NotEmpty(addresses.Addresses);
        Assert.All(addresses.Addresses, address =>
            Assert.StartsWith("http://127.0.0.1:", address, StringComparison.Ordinal));
        Assert.DoesNotContain(addresses.Addresses, address =>
            address.Contains("0.0.0.0", StringComparison.Ordinal)
            || address.Contains("[::]", StringComparison.OrdinalIgnoreCase));

        await using var client = await session.CreateClientAsync();
        var started = await CallStartAgentAsync(client, "req-http-1", AgentFacade.GrokBuildAgent, "hello");
        var payload = await WaitForMcpJobAsync(client, started.JobId);
        Assert.Equal(AgentJobStatus.Completed, payload.Status);
        Assert.Equal(AgentFacade.GrokBuildAgent, payload.Result!.Agent);
        Assert.Equal("ok", payload.Result.OutputText);
        Assert.Equal("22222222-2222-2222-2222-222222222222", payload.Result.SessionId);
        Assert.Equal(1, session.Runner.CallCount);
        Assert.Equal("grok", session.Runner.LastRequest!.FileName);
    }

    [Fact]
    public async Task MissingOrInvalidBearerReturns401()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        using var http = new HttpClient();

        var missing = new HttpRequestMessage(HttpMethod.Post, session.Endpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        missing.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        missing.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using (var missingResponse = await http.SendAsync(missing))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, missingResponse.StatusCode);
        }

        var wrong = new HttpRequestMessage(HttpMethod.Post, session.Endpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        wrong.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using (var wrongResponse = await http.SendAsync(wrong))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrongResponse.StatusCode);
        }

        Assert.Equal(0, session.Runner.CallCount);
    }

    [Fact]
    public async Task DisallowedHostIsRejected()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, session.Endpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Host = "evil.example";
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, session.Runner.CallCount);
    }

    [Fact]
    public async Task TwoClientsShareOneHostWithoutSessionHeader()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = GrokResult("hi", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        await using var first = await session.CreateClientAsync();
        var firstStarted = await CallStartAgentAsync(first, "req-share-1", AgentFacade.GrokBuildAgent, "one");
        var firstResult = await WaitForMcpJobAsync(first, firstStarted.JobId);
        Assert.Equal("hi", firstResult.Result!.OutputText);

        await using var second = await session.CreateClientAsync();
        var t1 = StartAndWaitAsync(second, "req-share-2", AgentFacade.GrokBuildAgent, "two");
        await using var third = await session.CreateClientAsync();
        var t2 = StartAndWaitAsync(third, "req-share-3", AgentFacade.GrokBuildAgent, "three");
        await Task.WhenAll(t1, t2);

        Assert.Equal(3, session.Runner.CallCount);
        Assert.Equal("hi", (await t1).Result!.OutputText);
        Assert.Equal("hi", (await t2).Result!.OutputText);
    }

    [Fact]
    public async Task ReconnectCanPollRunningJob()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = GrokResult("later", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        session.Runner.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var starter = await session.CreateClientAsync();
        var started = await CallStartAgentAsync(starter, "req-reconnect", AgentFacade.GrokBuildAgent, "hello");
        Assert.Equal(AgentJobStatus.Running, started.Status);

        await using var recovered = await session.CreateClientAsync();
        var polled = await CallGetAgentJobAsync(recovered, started.JobId);
        Assert.Equal(AgentJobStatus.Running, polled.Status);
        Assert.Equal(started.JobId, polled.JobId);
        Assert.Equal(1, session.Runner.CallCount);

        session.Runner.Gate.SetResult(true);
        var completed = await WaitForMcpJobAsync(recovered, started.JobId);
        Assert.Equal("later", completed.Result!.OutputText);
    }

    [Fact]
    public async Task LostStartResultIsRecoveredByRequestId()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = GrokResult("once", "cccccccc-cccc-cccc-cccc-cccccccccccc");
        session.Runner.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var first = await session.CreateClientAsync();
        var started = await CallStartAgentAsync(first, "req-lost-start", AgentFacade.GrokBuildAgent, "hello");

        await using var retry = await session.CreateClientAsync();
        var recovered = await CallStartAgentAsync(retry, "req-lost-start", AgentFacade.GrokBuildAgent, "hello");
        Assert.Equal(started.JobId, recovered.JobId);
        Assert.Equal(1, session.Runner.CallCount);

        session.Runner.Gate.SetResult(true);
        var completed = await WaitForMcpJobAsync(retry, recovered.JobId);
        Assert.Equal("once", completed.Result!.OutputText);
        Assert.Equal(1, session.Runner.CallCount);
    }

    [Fact]
    public async Task UnknownJobIdIsToolError()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        await using var client = await session.CreateClientAsync();
        var call = await client.CallToolAsync(
            "get_agent_job",
            new Dictionary<string, object?> { ["job_id"] = "missing-job" });
        Assert.True(call.IsError);
        var text = string.Concat(call.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("Unknown job", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelAgentJobStopsRunner()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = GrokResult("nope", "dddddddd-dddd-dddd-dddd-dddddddddddd");
        session.Runner.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = await session.CreateClientAsync();
        var started = await CallStartAgentAsync(client, "req-http-cancel", AgentFacade.GrokBuildAgent, "hello");
        var cancelled = await CallCancelAgentJobAsync(client, started.JobId);
        Assert.Equal(AgentJobStatus.Cancelled, cancelled.Status);

        for (var i = 0; i < 100 && !session.Runner.LastCancellationToken.IsCancellationRequested; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(session.Runner.LastCancellationToken.IsCancellationRequested);
        var snapshot = await CallGetAgentJobAsync(client, started.JobId);
        Assert.Equal(AgentJobStatus.Cancelled, snapshot.Status);
    }

    [Fact]
    public async Task ServerLogCorrelatesMcpPollingWithCompletedJob()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = GrokResult("safe-result", "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        session.Runner.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = await session.CreateClientAsync();
        var started = await CallStartAgentAsync(client, "req-observe", AgentFacade.GrokBuildAgent, "observe");
        Assert.Equal(AgentJobStatus.Running, started.Status);

        var running = await CallGetAgentJobAsync(client, started.JobId);
        Assert.Equal(AgentJobStatus.Running, running.Status);

        session.Runner.Gate.SetResult(true);
        var completed = await WaitForMcpJobAsync(client, started.JobId);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);

        var logFactory = session.App.Services.GetRequiredService<NLog.LogFactory>();
        logFactory.Flush();
        var contents = TestRunLogs.ReadShared(FacadeLogging.GetLogFilePath(session.ServerLogDirectory));
        Assert.Contains("MCP tool=start_agent phase=started", contents, StringComparison.Ordinal);
        Assert.Contains("MCP tool=start_agent phase=completed", contents, StringComparison.Ordinal);
        Assert.Contains("agent=grok-build", contents, StringComparison.Ordinal);
        Assert.Contains("MCP tool=get_agent_job phase=completed", contents, StringComparison.Ordinal);
        Assert.Contains("status=running", contents, StringComparison.Ordinal);
        Assert.Contains("status=completed", contents, StringComparison.Ordinal);
        Assert.Contains("terminal=false", contents, StringComparison.Ordinal);
        Assert.Contains("terminal=true", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("pollAfterMs", contents, StringComparison.Ordinal);
        Assert.Contains("durationMs=", contents, StringComparison.Ordinal);
        Assert.Contains("JOB phase=completed jobId=" + started.JobId + " status=completed exitCode=0", contents, StringComparison.Ordinal);
        Assert.Contains("jobId=" + started.JobId, contents, StringComparison.Ordinal);
        Assert.DoesNotContain("safe-result", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerLogRecordsToolErrorsWithoutPromptOrResultContent()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        const string secret = "xai-observability-secret-123";
        session.Runner.Result = GrokResult("safe-result", "ffffffff-ffff-ffff-ffff-ffffffffffff");

        await using var client = await session.CreateClientAsync();
        var unknown = await client.CallToolAsync(
            "get_agent_job",
            new Dictionary<string, object?> { ["job_id"] = "missing-observability-job" });
        Assert.True(unknown.IsError);

        var invalid = await client.CallToolAsync(
            "start_agent",
            new Dictionary<string, object?>
            {
                ["request_id"] = "req-observe-error",
                ["agent"] = AgentFacade.GrokBuildAgent,
                ["prompt"] = secret,
                ["working_directory"] = "",
            });
        Assert.True(invalid.IsError);

        var logFactory = session.App.Services.GetRequiredService<NLog.LogFactory>();
        logFactory.Flush();
        var contents = TestRunLogs.ReadShared(FacadeLogging.GetLogFilePath(session.ServerLogDirectory));
        Assert.Contains("MCP tool=get_agent_job phase=failed", contents, StringComparison.Ordinal);
        Assert.Contains("errorType=KeyNotFoundException", contents, StringComparison.Ordinal);
        Assert.Contains("MCP tool=start_agent phase=failed", contents, StringComparison.Ordinal);
        Assert.Contains("errorType=ArgumentException", contents, StringComparison.Ordinal);
        Assert.Contains("durationMs=", contents, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, contents, StringComparison.Ordinal);
        Assert.DoesNotContain("safe-result", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitAgentJobCompletesWithoutPollingGet()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = GrokResult("waited", "12121212-1212-1212-1212-121212121212");
        session.Runner.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = await session.CreateClientAsync();
        var started = await CallStartAgentAsync(client, "req-wait-complete", AgentFacade.GrokBuildAgent, "hello");
        Assert.Equal(AgentJobStatus.Running, started.Status);

        var wait = CallWaitAgentJobAsync(client, started.JobId, 30);
        session.Runner.Gate.SetResult(true);
        var completed = await wait;
        Assert.Equal(AgentJobStatus.Completed, completed.Status);
        Assert.Equal("waited", completed.Result!.OutputText);
        Assert.False(string.IsNullOrWhiteSpace(completed.Result.TextLogPath));
        Assert.False(string.IsNullOrWhiteSpace(completed.Result.EventsLogPath));
        Assert.Equal(1, session.Runner.CallCount);
    }

    [Fact]
    public async Task WaitAgentJobAllowsOmittedTimeoutAndUsesDefault()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = GrokResult("default-rpc", "16161616-1616-1616-1616-161616161616");
        await using var client = await session.CreateClientAsync();

        var started = await CallStartAgentAsync(client, "req-default-rpc-timeout", AgentFacade.GrokBuildAgent, "hello");
        var call = await client.CallToolAsync(
            "wait_agent_job",
            new Dictionary<string, object?> { ["job_id"] = started.JobId });
        var waited = ReadSnapshot(call);
        Assert.Equal(AgentJobStatus.Completed, waited.Status);
        Assert.Equal("default-rpc", waited.Result!.OutputText);
    }

    [Fact]
    public async Task FailedMcpProjectionIsConsistentAcrossStartGetWaitAndRetry()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = new ProcessRunResult(9, "stdout-secret", "stderr\r\nxai-public-secret-value");
        await using var client = await session.CreateClientAsync();

        var started = await CallStartAgentAsync(client, "req-failed-projection", AgentFacade.GrokBuildAgent, "hello");
        var waited = await WaitForMcpJobAsync(client, started.JobId);
        AssertFailureProjection(waited);

        var gotten = await CallGetAgentJobAsync(client, started.JobId);
        var retried = await CallStartAgentAsync(client, "req-failed-projection", AgentFacade.GrokBuildAgent, "hello");
        var cancelled = await CallCancelAgentJobAsync(client, started.JobId);
        AssertFailureProjection(gotten);
        AssertFailureProjection(retried);
        AssertFailureProjection(cancelled);
        Assert.Equal(waited.Failure, gotten.Failure);
        Assert.Equal(waited.Failure, retried.Failure);
        Assert.Equal(waited.Failure, cancelled.Failure);
        Assert.Equal(1, session.Runner.CallCount);
    }

    private static void AssertFailureProjection(AgentJobPublicSnapshot snapshot)
    {
        Assert.Equal(AgentJobStatus.Failed, snapshot.Status);
        Assert.Equal("agent job failed. See the server log for details.", snapshot.Error);
        Assert.NotNull(snapshot.Failure);
        Assert.Equal("non_zero_exit", snapshot.Failure!.Kind);
        Assert.Equal(9, snapshot.Failure.ExitCode);
        Assert.True(snapshot.Failure.Summary.Length <= 512);
        Assert.DoesNotContain("xai-public-secret-value", snapshot.Failure.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', snapshot.Failure.Summary);
        Assert.DoesNotContain('\n', snapshot.Failure.Summary);
        Assert.NotNull(snapshot.Failure.RunId);
        Assert.NotNull(snapshot.Failure.EventsLogPath);
        Assert.NotNull(snapshot.Failure.TextLogPath);
    }

    [Fact]
    public async Task WaitAgentJobTimeoutLeavesWorkerRunning()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = GrokResult("after-timeout", "13131313-1313-1313-1313-131313131313");
        session.Runner.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = await session.CreateClientAsync();
        var started = await CallStartAgentAsync(client, "req-wait-timeout", AgentFacade.GrokBuildAgent, "hello");
        var timedOut = await CallWaitAgentJobAsync(client, started.JobId, 1);
        Assert.Equal(AgentJobStatus.Running, timedOut.Status);
        Assert.False(session.Runner.LastCancellationToken.IsCancellationRequested);

        session.Runner.Gate.SetResult(true);
        var completed = await CallWaitAgentJobAsync(client, started.JobId, 30);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);
        Assert.Equal("after-timeout", completed.Result!.OutputText);
    }

    [Fact]
    public async Task WaitAgentJobCancelDoesNotCancelWorker()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = GrokResult("still-running", "14141414-1414-1414-1414-141414141414");
        session.Runner.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = await session.CreateClientAsync();
        var started = await CallStartAgentAsync(client, "req-wait-rpc-cancel", AgentFacade.GrokBuildAgent, "hello");
        using var cts = new CancellationTokenSource();
        var wait = client.CallToolAsync(
            "wait_agent_job",
            new Dictionary<string, object?>
            {
                ["job_id"] = started.JobId,
                ["timeout_seconds"] = 30,
            },
            cancellationToken: cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.AsTask());
        Assert.False(session.Runner.LastCancellationToken.IsCancellationRequested);

        session.Runner.Gate.SetResult(true);
        var completed = await CallWaitAgentJobAsync(client, started.JobId, 30);
        Assert.Equal(AgentJobStatus.Completed, completed.Status);
        Assert.Equal("still-running", completed.Result!.OutputText);
    }

    [Fact]
    public async Task TerminalMcpSnapshotsOmitRawOutputAcrossTools()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        session.Runner.Result = GrokResult("compact-text", "15151515-1515-1515-1515-151515151515");
        session.Runner.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = await session.CreateClientAsync();
        var started = await CallStartAgentAsync(client, "req-compact", AgentFacade.GrokBuildAgent, "hello");
        session.Runner.Gate.SetResult(true);
        var waited = await CallWaitAgentJobAsync(client, started.JobId, 30);
        Assert.Equal(AgentJobStatus.Completed, waited.Status);

        var retried = await CallStartAgentAsync(client, "req-compact", AgentFacade.GrokBuildAgent, "hello");
        var gotten = await CallGetAgentJobAsync(client, started.JobId);
        Assert.Equal("compact-text", retried.Result!.OutputText);
        Assert.Equal("compact-text", gotten.Result!.OutputText);
        Assert.False(string.IsNullOrWhiteSpace(retried.Result.TextLogPath));
        Assert.Equal(1, session.Runner.CallCount);

        session.Runner.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellable = await CallStartAgentAsync(client, "req-compact-cancel", AgentFacade.GrokBuildAgent, "cancel-me");
        var cancelled = await CallCancelAgentJobAsync(client, cancellable.JobId);
        Assert.Equal(AgentJobStatus.Cancelled, cancelled.Status);
    }

    private static ProcessRunResult GrokResult(string text, string sessionId)
    {
        var stdout = "{\"type\":\"text\",\"data\":" + JsonSerializer.Serialize(text)
            + "}\n{\"type\":\"end\",\"sessionId\":" + JsonSerializer.Serialize(sessionId) + "}";
        return new ProcessRunResult(0, stdout, "");
    }

    private static async Task<AgentJobPublicSnapshot> StartAndWaitAsync(
        McpClient client,
        string requestId,
        string agent,
        string prompt)
    {
        var started = await CallStartAgentAsync(client, requestId, agent, prompt);
        return await WaitForMcpJobAsync(client, started.JobId);
    }

    private static async Task<AgentJobPublicSnapshot> CallStartAgentAsync(
        McpClient client,
        string requestId,
        string agent,
        string prompt)
    {
        var call = await client.CallToolAsync(
            "start_agent",
            new Dictionary<string, object?>
            {
                ["request_id"] = requestId,
                ["agent"] = agent,
                ["prompt"] = prompt,
                ["working_directory"] = Path.GetTempPath(),
            });
        return ReadSnapshot(call);
    }

    private static async Task<AgentJobPublicSnapshot> CallGetAgentJobAsync(McpClient client, string jobId)
    {
        var call = await client.CallToolAsync(
            "get_agent_job",
            new Dictionary<string, object?> { ["job_id"] = jobId });
        return ReadSnapshot(call);
    }

    private static async Task<AgentJobPublicSnapshot> CallWaitAgentJobAsync(McpClient client, string jobId, int timeoutSeconds)
    {
        var call = await client.CallToolAsync(
            "wait_agent_job",
            new Dictionary<string, object?>
            {
                ["job_id"] = jobId,
                ["timeout_seconds"] = timeoutSeconds,
            });
        return ReadSnapshot(call);
    }

    private static async Task<AgentJobPublicSnapshot> CallCancelAgentJobAsync(McpClient client, string jobId)
    {
        var call = await client.CallToolAsync(
            "cancel_agent_job",
            new Dictionary<string, object?> { ["job_id"] = jobId });
        return ReadSnapshot(call);
    }

    private static async Task<AgentJobPublicSnapshot> WaitForMcpJobAsync(McpClient client, string jobId)
    {
        for (var i = 0; i < 100; i++)
        {
            var snapshot = await CallGetAgentJobAsync(client, jobId);
            if (snapshot.Status != AgentJobStatus.Running)
            {
                return snapshot;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("job did not finish: " + jobId);
    }

    private static AgentJobPublicSnapshot ReadSnapshot(CallToolResult call)
    {
        var text = string.Concat(call.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(call.IsError != true, text);
        var result = JsonSerializer.Deserialize<AgentJobPublicSnapshot>(text, AgentJson.Options);
        Assert.NotNull(result);
        McpPublicJsonAssert.Compact(text);
        if (result.Status == AgentJobStatus.Completed)
        {
            Assert.Contains("\"outputText\"", text, StringComparison.Ordinal);
            Assert.Contains("\"eventsLogPath\"", text, StringComparison.Ordinal);
            Assert.Contains("\"textLogPath\"", text, StringComparison.Ordinal);
        }

        return result;
    }

    private static IDisposable OverrideEnv(string name, string? value)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new EnvironmentVariableRestore(name, previous);
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

internal static class McpPublicJsonAssert
{
    public static void Compact(string json)
    {
        Assert.DoesNotContain("\"rawOutput\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pollAfterMs\"", json, StringComparison.Ordinal);
    }
}

internal sealed class McpHttpTestHost
{
    public static async Task<McpHttpTestSession> StartAsync()
    {
        var runner = new RecordingProcessRunner();
        var factory = TestRunLogs.CreateFactory();
        var token = "test-token-" + Guid.NewGuid().ToString("N");
        var serverLogDirectory = Directory.CreateTempSubdirectory("caf-serverlog-").FullName;
        var app = McpHttpHost.Create(
            [],
            new McpHttpHostOptions
            {
                Token = token,
                Port = 0,
                ProcessRunner = runner,
                RunLogFactory = factory,
                JobStoreDirectory = Directory.CreateTempSubdirectory("caf-jobs-").FullName,
                ServerLogDirectory = serverLogDirectory,
            });
        await app.StartAsync();
        return new McpHttpTestSession(app, runner, token, McpHttpHost.GetMcpEndpoint(app), serverLogDirectory);
    }
}

internal sealed class McpHttpTestSession : IAsyncDisposable
{
    public McpHttpTestSession(
        WebApplication app,
        RecordingProcessRunner runner,
        string token,
        Uri endpoint,
        string serverLogDirectory)
    {
        App = app;
        Runner = runner;
        Token = token;
        Endpoint = endpoint;
        ServerLogDirectory = serverLogDirectory;
    }

    public WebApplication App { get; }
    public RecordingProcessRunner Runner { get; }
    public string Token { get; }
    public Uri Endpoint { get; }
    public string ServerLogDirectory { get; }

    public Task<McpClient> CreateClientAsync()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + Token,
            },
            EnableStandaloneGetStream = false,
        });
        return McpClient.CreateAsync(transport);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

internal sealed class CapturingLogger : ILogger
{
    public StringBuilder Buffer { get; } = new();

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Buffer.AppendLine(formatter(state, exception));
        if (exception is not null)
        {
            Buffer.AppendLine(exception.ToString());
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public CapturingLogger Logger { get; } = new();

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName)
    {
        return Logger;
    }

    public void Dispose()
    {
    }
}

[Collection("http-host")]
public class ServerLogTests
{
    [Fact]
    public void DefaultPathIsBesideRunsAndJobs()
    {
        var directory = FacadeLogging.GetDefaultDirectory();
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "." + AgentRunLogFactory.DefaultProductDirectoryName),
            directory);
        Assert.Equal("server.log", FacadeLogging.FileName);
        Assert.DoesNotContain(
            Path.DirectorySeparatorChar + "runs",
            FacadeLogging.GetLogFilePath(directory),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.DirectorySeparatorChar + "jobs",
            FacadeLogging.GetLogFilePath(directory),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RollingLimitsAreConfigured()
    {
        var directory = Directory.CreateTempSubdirectory("caf-nlog-cfg-").FullName;
        var target = FacadeLogging.CreateFileTarget(directory);
        Assert.Equal(FacadeLogging.ArchiveAboveSizeBytes, target.ArchiveAboveSize);
        Assert.Equal(1 * 1024 * 1024, target.ArchiveAboveSize);
        Assert.Equal(FacadeLogging.MaxArchiveFiles, target.MaxArchiveFiles);
        Assert.InRange(target.MaxArchiveFiles, 3, 5);
        Assert.Equal(FacadeLogging.ArchiveSuffixFormat, target.ArchiveSuffixFormat);
        var rendered = target.FileName.Render(new NLog.LogEventInfo());
        Assert.EndsWith("server.log", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(target.ArchiveFileName);
        var archiveRendered = target.ArchiveFileName.Render(new NLog.LogEventInfo());
        Assert.EndsWith("server.log", archiveRendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RollingSettingsCreateBoundedArchives()
    {
        var directory = Directory.CreateTempSubdirectory("caf-nlog-roll-").FullName;
        using var nlog = FacadeLogging.CreateNLogFactory(directory, archiveAboveSizeBytes: 200, maxArchiveFiles: 2);
        using var loggerFactory = FacadeLogging.CreateLoggerFactory(nlog);
        var logger = loggerFactory.CreateLogger(FacadeLogging.LoggerCategory);
        for (var i = 0; i < 80; i++)
        {
            logger.LogInformation("rolling-probe-{Index} {Padding}", i, new string('x', 80));
        }

        nlog.Flush();
        var files = Directory.GetFiles(directory, "server*.log");
        Assert.Contains(files, path => string.Equals(Path.GetFileName(path), "server.log", StringComparison.OrdinalIgnoreCase));
        Assert.True(files.Length > 1);
        Assert.True(files.Length <= 3);
        Assert.All(files, path => Assert.True(new FileInfo(path).Length <= 200 * 4));
    }

    [Fact]
    public async Task HostWritesInformationAndErrorToServerLogWithoutConsoleOrTrace()
    {
        var traceBefore = Trace.Listeners.Cast<TraceListener>().ToArray();
        await using var session = await McpHttpTestHost.StartAsync();
        var nlog = session.App.Services.GetRequiredService<NLog.LogFactory>();
        var logger = session.App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(FacadeLogging.LoggerCategory);
        logger.LogInformation("host-information-probe");
        logger.LogError("host-error-probe");
        nlog.Flush();

        var logPath = FacadeLogging.GetLogFilePath(session.ServerLogDirectory);
        Assert.True(File.Exists(logPath));
        var contents = TestRunLogs.ReadShared(logPath);
        Assert.Contains("host-information-probe", contents, StringComparison.Ordinal);
        Assert.Contains("host-error-probe", contents, StringComparison.Ordinal);

        Assert.Equal(traceBefore.Length, Trace.Listeners.Count);
        Assert.DoesNotContain(Trace.Listeners.Cast<TraceListener>(), listener => listener is ConsoleTraceListener);
    }

    [Fact]
    public async Task ServerLogDoesNotContainBearerToken()
    {
        const string knownSecret = "xai-serverlogsecretvalue99";
        await using var session = await McpHttpTestHost.StartAsync();
        using var http = new HttpClient();
        var missing = new HttpRequestMessage(HttpMethod.Post, session.Endpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        missing.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using (await http.SendAsync(missing))
        {
        }

        var wrong = new HttpRequestMessage(HttpMethod.Post, session.Endpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        wrong.Headers.Host = "evil.example";
        using (await http.SendAsync(wrong))
        {
        }

        CliJson.TraceException(new InvalidOperationException("Authorization: Bearer " + knownSecret));
        session.App.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(FacadeLogging.LoggerCategory)
            .LogError("token={Secret}", knownSecret);
        session.App.Services.GetRequiredService<NLog.LogFactory>().Flush();

        var contents = TestRunLogs.ReadShared(FacadeLogging.GetLogFilePath(session.ServerLogDirectory));
        Assert.DoesNotContain(session.Token, contents, StringComparison.Ordinal);
        Assert.DoesNotContain(knownSecret, contents, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Replacement, contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStartsWithoutConsoleLoggingProvider()
    {
        await using var session = await McpHttpTestHost.StartAsync();
        var addresses = session.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        Assert.NotNull(addresses);
        Assert.NotEmpty(addresses.Addresses);
        var factory = session.App.Services.GetRequiredService<ILoggerFactory>();
        Assert.NotSame(NullLoggerFactory.Instance, factory);
        factory.CreateLogger(FacadeLogging.LoggerCategory).LogInformation("startup-without-console");
        session.App.Services.GetRequiredService<NLog.LogFactory>().Flush();
        Assert.True(File.Exists(FacadeLogging.GetLogFilePath(session.ServerLogDirectory)));
    }
}
