using System.Text;
using System.Text.Json;

/// <summary>
/// Grok Build CLI の non-interactive 経路へ変換する Driver。
/// </summary>
public sealed class GrokBuildDriver
{
    internal const string InternalPromptDoneReason = "grok_internal_prompt_done";
    internal static readonly TimeSpan IdleBeforeLogCheck = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan LogRecheckInterval = TimeSpan.FromSeconds(15);

    private readonly IProcessRunner _processRunner;
    private readonly TimeProvider _timeProvider;
    private readonly IGrokUnifiedLogReader _logReader;
    private readonly IGrokAcpProcessFactory _acpProcesses;

    public GrokBuildDriver(IProcessRunner processRunner)
        : this(processRunner, TimeProvider.System, FileGrokUnifiedLogReader.CreateDefault(), null)
    {
    }

    internal GrokBuildDriver(
        IProcessRunner processRunner,
        TimeProvider timeProvider,
        IGrokUnifiedLogReader logReader,
        IGrokAcpProcessFactory? acpProcesses = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logReader);
        _processRunner = processRunner;
        _timeProvider = timeProvider;
        _logReader = logReader;
        _acpProcesses = acpProcesses ?? new GrokAcpProcessFactory();
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IAgentRunLog runLog,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
        if (request.RunMode is not null)
        {
            if (!string.Equals(request.RunMode, GrokPlanSession.Mode, StringComparison.Ordinal))
            {
                throw new ArgumentException("run_mode must be omitted or \"plan\".");
            }

            return await RunPlanAsync(request, runLog, cancellationToken).ConfigureAwait(false);
        }

        var arguments = BuildArguments(request);
        var prompt = ApplyGrokSkills(request.Prompt, request.Skills);
        runLog.WriteStarted(new AgentRunStartedInfo(
            Agent: AgentFacade.GrokBuildAgent,
            WorkingDirectory: request.WorkingDirectory,
            SessionId: request.SessionId,
            AutoApprove: request.AutoApprove,
            Skills: request.Skills,
            Prompt: prompt,
            FileName: "grok",
            Arguments: arguments,
            Model: request.Model,
            ReasoningEffort: request.ReasoningEffort,
            Fast: request.Fast,
            RunMode: request.RunMode));

        var accumulator = new GrokStreamAccumulator(runLog);
        var signal = new ProcessCompletionSignal();
        var watch = new GrokCompletionWatch(
            _timeProvider,
            _logReader,
            accumulator,
            signal,
            request.SessionId,
            request.ReasoningEffort);
        GrokLogCursor cursor;
        try
        {
            cursor = _logReader.CaptureCursor();
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            cursor = new GrokLogCursor(0, Array.Empty<byte>(), true, "unreadable");
        }
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? monitor = null;
        ProcessRunResult? processResult = null;
        try
        {
            processResult = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    FileName: "grok",
                    Arguments: arguments,
                    WorkingDirectory: request.WorkingDirectory,
                    StdoutLineCallback: line =>
                    {
                        if (accumulator.OnStdoutLine(line))
                        {
                            watch.MarkProgress(_timeProvider.GetUtcNow());
                        }

                        onStdoutLine?.Invoke(line);
                    },
                    StderrLineCallback: line =>
                    {
                        watch.AppendStderr(line);
                        runLog.WriteProcessLine("stderr", line);
                    },
                    OnProcessStarted: lifetime =>
                    {
                        runLog.AttachProcess(lifetime);
                        monitor = watch.Start(lifetime, cursor, monitorCancellation.Token);
                    },
                    OnLaunchResolved: runLog.WriteLaunch,
                    Completion: signal),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (watch.Confirmed)
        {
            // 救済確定後の内部停止は、利用者キャンセルとして返さない。
            CliJson.TraceException(ex);
            processResult = new ProcessRunResult(
                null,
                string.Empty,
                watch.CopyStderr(),
                new ProcessTerminationInfo(ProcessTerminationStatus.Unconfirmed, false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProcessStartException ex)
        {
            CliJson.TraceException(ex);
            CliJson.MarkFailure(ex, "process_start_failed");
            throw;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            CliJson.MarkFailure(ex, "internal_error");
            throw;
        }
        finally
        {
            try
            {
                monitorCancellation.Cancel();
            }
            catch (Exception ex)
            {
                CliJson.TraceException(ex);
            }

            if (monitor is not null)
            {
                try
                {
                    await monitor.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    CliJson.TraceException(ex);
                }
            }
        }

        if (processResult is null)
        {
            var failure = new InvalidOperationException("Grok Build CLI did not return a process result.");
            CliJson.MarkFailure(failure, "internal_error");
            CliJson.TraceException(failure);
            throw failure;
        }

        // runner が通知を受理しなかった自然終了は、救済確定で上書きしない。
        watch.RevokeIfRunnerDidNotAccept(processResult.Termination);

        if (!watch.Confirmed && processResult.ExitCode != 0)
        {
            var message = SecretRedactor.RedactText(
                $"Grok Build CLI exited with code {processResult.ExitCode}. stdout: {processResult.StandardOutput} stderr: {processResult.StandardError}");
            var failure = new InvalidOperationException(message);
            CliJson.MarkFailure(
                failure,
                "non_zero_exit",
                $"Grok Build CLI exited with code {processResult.ExitCode}. stderr: {processResult.StandardError}",
                processResult.ExitCode);
            CliJson.TraceException(failure);
            throw failure;
        }

        if (request.ReasoningEffort is not null
            && ReportsIgnoredReasoningEffort(processResult.StandardError))
        {
            var failure = new InvalidOperationException("Grok Build CLI did not apply the requested reasoning_effort.");
            CliJson.MarkFailure(failure, "option_not_applied", "Grok Build CLI reported that the requested reasoning effort was not applied.", processResult.ExitCode);
            CliJson.TraceException(failure);
            throw failure;
        }

        ParsedCliOutput parsed;
        try
        {
            parsed = accumulator.Complete(watch.Confirmed ? watch.Evidence?.SessionId : null);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            CliJson.MarkFailure(ex, "output_parse_failed");
            throw;
        }

        return new AgentRunResult(
            Agent: AgentFacade.GrokBuildAgent,
            SessionId: parsed.SessionId,
            ExitCode: processResult.ExitCode,
            OutputText: parsed.OutputText,
            OutputKind: parsed.OutputKind,
            RawOutput: processResult.StandardOutput,
            RunId: runLog.RunId,
            EventsLogPath: runLog.EventsPath,
            TextLogPath: runLog.TextLogPath,
            CompletionReason: watch.Confirmed ? InternalPromptDoneReason : null,
            CompletionEvidence: watch.Confirmed ? watch.Evidence : null,
            ProcessTermination: watch.Confirmed ? processResult.Termination : null);
    }

    internal static List<string> BuildArguments(AgentRunRequest request)
    {
        var prompt = ApplyGrokSkills(request.Prompt, request.Skills);
        var arguments = new List<string>
        {
            "--no-auto-update",
            "-p",
            prompt,
            "--cwd",
            request.WorkingDirectory,
            "--output-format",
            "streaming-json",
        };

        if (request.AutoApprove)
        {
            arguments.Add("--always-approve");
        }

        AppendModelArgument(arguments, request.Model);
        if (request.ReasoningEffort is not null)
        {
            arguments.Add("--reasoning-effort");
            arguments.Add(request.ReasoningEffort);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            // --session-id は新規作成用。実機では既存 ID に対して "already in use" になるため継続は --resume。
            arguments.Add("--resume");
            arguments.Add(request.SessionId);
        }

        return arguments;
    }

    /// <summary>
    /// plan は <c>grok agent stdio</c> だけを使う。<c>-p</c> と <c>--reasoning-effort</c> は付けない。
    /// 1.0.70 の <c>grok agent --reasoning-effort</c> は session の currentValue を変えない。
    /// model と reasoning_effort は ACP の session/set_config_option で適用する。
    /// </summary>
    internal static List<string> BuildPlanArguments(AgentRunRequest request)
    {
        var arguments = new List<string>
        {
            "agent",
            "--no-leader",
        };
        if (request.AutoApprove)
        {
            arguments.Add("--always-approve");
        }

        AppendModelArgument(arguments, request.Model);
        arguments.Add("stdio");
        return arguments;
    }

    private async Task<AgentRunResult> RunPlanAsync(
        AgentRunRequest request,
        IAgentRunLog runLog,
        CancellationToken cancellationToken)
    {
        var arguments = BuildPlanArguments(request);
        var prompt = ApplyGrokSkills(request.Prompt, request.Skills);
        runLog.WriteStarted(new AgentRunStartedInfo(
            Agent: AgentFacade.GrokBuildAgent,
            WorkingDirectory: request.WorkingDirectory,
            SessionId: request.SessionId,
            AutoApprove: request.AutoApprove,
            Skills: request.Skills,
            Prompt: prompt,
            FileName: "grok",
            Arguments: arguments,
            Model: request.Model,
            ReasoningEffort: request.ReasoningEffort,
            Fast: request.Fast,
            RunMode: request.RunMode));

        if (!string.IsNullOrWhiteSpace(request.SessionId) && !GrokPlanArtifact.IsSafeSessionId(request.SessionId))
        {
            throw PlanFailure("internal_error", "Grok session id is not a safe path segment.");
        }

        IGrokAcpProcess process;
        try
        {
            process = _acpProcesses.Start(new GrokAcpStartInfo("grok", arguments, request.WorkingDirectory));
        }
        catch (ProcessStartException ex)
        {
            CliJson.TraceException(ex);
            CliJson.MarkFailure(ex, "process_start_failed");
            throw;
        }

        await using (process)
        {
            runLog.WriteLaunch(process.Launch);
            GrokPlanOutcome outcome;
            try
            {
                outcome = await GrokPlanSession.RunAsync(process, request, prompt, runLog, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            var standardError = process.CopyStandardError();
            if (request.ReasoningEffort is not null && ReportsIgnoredReasoningEffort(standardError))
            {
                throw PlanFailure(
                    "option_not_applied",
                    "Grok Build CLI reported that the requested reasoning effort was not applied.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            GrokPlanArtifact.Write(request.WorkingDirectory, outcome.SessionId, outcome.Content);
            return new AgentRunResult(
                Agent: AgentFacade.GrokBuildAgent,
                SessionId: outcome.SessionId,
                ExitCode: null,
                OutputText: outcome.Content,
                OutputKind: "plan",
                RawOutput: outcome.Content,
                RunId: runLog.RunId,
                EventsLogPath: runLog.EventsPath,
                TextLogPath: runLog.TextLogPath,
                PlanPath: GrokPlanArtifact.RelativePath(outcome.SessionId));
        }
    }

    private static InvalidOperationException PlanFailure(string kind, string message)
    {
        var failure = new InvalidOperationException(message);
        CliJson.MarkFailure(failure, kind, message);
        CliJson.TraceException(failure);
        return failure;
    }

    private static void AppendModelArgument(List<string> arguments, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        arguments.Add("--model");
        arguments.Add(model);
    }

    /// <summary>
    /// Grok Build 1.0.52 は、要求した reasoning effort を適用しないとき stderr に無視を書く。
    /// exit 0 のまま成功へ落とさない。非ゼロ終了は先に <c>non_zero_exit</c> として失敗する。
    /// </summary>
    internal static bool ReportsIgnoredReasoningEffort(string standardError)
    {
        if (string.IsNullOrEmpty(standardError))
        {
            return false;
        }

        // 1.0.5 付近の文言と、1.0.52 で実測した "ignoring --effort/--reasoning-effort"。
        // 同じバイナリにある "--effort/--reasoning-effort ignored" も無視通知として扱う。
        return standardError.Contains("model does not support reasoning effort; ignoring", StringComparison.OrdinalIgnoreCase)
            || standardError.Contains("ignoring --effort/--reasoning-effort", StringComparison.OrdinalIgnoreCase)
            || standardError.Contains("--effort/--reasoning-effort ignored", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Grok Build は user-invocable skill を slash command として扱う。
    /// 1.0.52 では先頭の /name が skill 本文の注入になることを実機で確認した。共通 runtime にはしない。
    /// </summary>
    internal static string ApplyGrokSkills(string prompt, IReadOnlyList<string>? skills)
    {
        if (skills is null || skills.Count == 0)
        {
            return prompt;
        }

        var builder = new StringBuilder();
        foreach (var skill in skills)
        {
            builder.Append(ToSlashInvocation(skill));
            builder.Append('\n');
        }

        builder.Append(prompt);
        return builder.ToString();
    }

    internal static string ToSlashInvocation(string skill)
    {
        var name = skill.Trim();
        if (name.StartsWith('$') || name.StartsWith('/'))
        {
            name = name[1..].Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Skill name is empty.");
        }

        return "/" + name;
    }
}

/// <summary>
/// streaming-json の NDJSON を 1 パスで蓄積する。終了後に stdout 全体は再 parse しない。
/// </summary>
internal sealed class GrokStreamAccumulator
{
    private readonly IAgentRunLog _runLog;
    private readonly StringBuilder _text = new();
    private readonly object _gate = new();
    private string _sessionId = string.Empty;
    private int _jsonEventCount;
    private bool _sawNonWhitespace;
    private bool _frozen;
    private string? _frozenText;

    public GrokStreamAccumulator(IAgentRunLog runLog)
    {
        _runLog = runLog;
    }

    public bool OnStdoutLine(string line)
    {
        lock (_gate)
        {
            if (_frozen || string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            _sawNonWhitespace = true;
            JsonElement root;
            try
            {
                root = JsonSerializer.Deserialize<JsonElement>(line);
            }
            catch (JsonException ex)
            {
                CliJson.TraceException(ex);
                _runLog.WriteProcessLine("stdout", line);
                return false;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                _runLog.WriteProcessLine("stdout", line);
                return false;
            }

            _jsonEventCount++;
            var type = CliJson.FindFirstString(root, "type") ?? "unknown";
            if (string.Equals(type, "thought", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                _runLog.WriteAgentEvent(type, root, humanSummary: null);
                var chunk = ReadDataString(root);
                if (!string.IsNullOrEmpty(chunk))
                {
                    var kind = string.Equals(type, "thought", StringComparison.OrdinalIgnoreCase)
                        ? "thought"
                        : "assistant";
                    _runLog.AppendHumanFragment(kind, chunk);
                    if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        _text.Append(chunk);
                    }
                }
            }
            else
            {
                _runLog.WriteAgentEvent(type, root, GrokBuildHumanSummary.Format(type, root));
            }

            if (string.Equals(type, "end", StringComparison.OrdinalIgnoreCase))
            {
                _sessionId = CliJson.FindExplicitSessionId(root) ?? _sessionId;
            }

            return IsProgress(type, root);
        }
    }

    public string? FreezeIfNonEmpty()
    {
        lock (_gate)
        {
            if (_frozen)
            {
                return string.IsNullOrWhiteSpace(_frozenText) ? null : _frozenText;
            }

            var text = _text.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            _frozen = true;
            _frozenText = text;
            return text;
        }
    }

    public ParsedCliOutput Complete(string? sessionId = null)
    {
        lock (_gate)
        {
            if (!_sawNonWhitespace)
            {
                throw new InvalidOperationException("Grok Build CLI returned empty stdout.");
            }

            if (_jsonEventCount == 0)
            {
                throw new InvalidOperationException("Grok Build CLI stdout did not contain JSON objects.");
            }

            var outputText = _frozenText ?? _text.ToString();
            if (string.IsNullOrWhiteSpace(outputText))
            {
                throw new InvalidOperationException("Grok Build CLI JSON did not contain a recognized response field.");
            }

            return new ParsedCliOutput(sessionId ?? _sessionId, outputText);
        }
    }

    private static bool IsProgress(string type, JsonElement root)
    {
        if (string.Equals(type, "available_commands", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "usage", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "thought", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(ReadDataString(root));
        }

        return string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "tool_call_update", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "tool", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "plan", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadDataString(JsonElement root)
    {
        return CliJson.FindFirstString(root, "data", "text", "content");
    }
}

/// <summary>
/// 処理進捗が止まってから Grok 内部ログを照合する。判断は Driver 側に置き、停止そのものは process runner へ通知する。
/// </summary>
internal sealed class GrokCompletionWatch
{
    private readonly object _stderrGate = new();
    private readonly StringBuilder _stderr = new();
    private readonly TimeProvider _timeProvider;
    private readonly IGrokUnifiedLogReader _logReader;
    private readonly GrokStreamAccumulator _accumulator;
    private readonly ProcessCompletionSignal _signal;
    private readonly GrokActivityClock _activity = new();
    private readonly GrokPromptCorrelation _correlation = new();
    private readonly string? _resumeSessionId;
    private readonly string? _reasoningEffort;
    private GrokLogCursor? _cursor;
    private IProcessLifetime? _process;
    private int _confirmed;
    private bool _stopped;

    public GrokCompletionWatch(
        TimeProvider timeProvider,
        IGrokUnifiedLogReader logReader,
        GrokStreamAccumulator accumulator,
        ProcessCompletionSignal signal,
        string? resumeSessionId,
        string? reasoningEffort)
    {
        _timeProvider = timeProvider;
        _logReader = logReader;
        _accumulator = accumulator;
        _signal = signal;
        _resumeSessionId = resumeSessionId;
        _reasoningEffort = reasoningEffort;
    }

    public bool Confirmed => Volatile.Read(ref _confirmed) == 1;

    public GrokCompletionEvidence? Evidence { get; private set; }

    public void RevokeIfRunnerDidNotAccept(ProcessTerminationInfo? termination)
    {
        if (termination is not null || Volatile.Read(ref _confirmed) == 0)
        {
            return;
        }

        Volatile.Write(ref _confirmed, 0);
        Evidence = null;
    }

    public void MarkProgress(DateTimeOffset now)
    {
        _activity.Mark(now);
    }

    public void AppendStderr(string line)
    {
        lock (_stderrGate)
        {
            _stderr.AppendLine(line);
        }
    }

    public string CopyStderr()
    {
        lock (_stderrGate)
        {
            return _stderr.ToString();
        }
    }

    public Task Start(IProcessLifetime process, GrokLogCursor cursor, CancellationToken cancellationToken)
    {
        // 監視を始めた時刻だと、その前に出た今回の session 記録を過去分として落とす。
        var launchedAt = process.StartedAt;
        _process = process;
        _activity.Start(launchedAt);
        if (process.Id <= 0)
        {
            Stop("Grok process id was not available. Internal completion recovery is disabled.");
            return Task.CompletedTask;
        }

        if (cursor.Abandoned)
        {
            Stop("Grok internal log could not be read before launch: " + (cursor.Problem ?? "unreadable"));
            return Task.CompletedTask;
        }

        _cursor = cursor;
        return WatchAsync(process.Id, launchedAt, cancellationToken);
    }

    private async Task WatchAsync(int processId, DateTimeOffset launchedAt, CancellationToken cancellationToken)
    {
        try
        {
            var nextCheck = launchedAt + GrokBuildDriver.IdleBeforeLogCheck;
            while (!cancellationToken.IsCancellationRequested && !_signal.IsSignaled && !_stopped)
            {
                if (HasIgnoredEffort())
                {
                    Stop("Grok internal completion recovery was not used because reasoning effort was reported as ignored.");
                    return;
                }

                var now = _timeProvider.GetUtcNow();
                var wait = nextCheck - now;
                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.Zero;
                }

                if (wait > TimeSpan.Zero)
                {
                    using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    // このターゲットの TimeProvider は Delay を持たない。FakeTimeProvider は CreateTimer を進める。
                    var delay = DelayUntil(_timeProvider, wait, waitCancellation.Token);
                    var finished = await Task.WhenAny(delay, _activity.Pulse).ConfigureAwait(false);
                    if (finished != delay)
                    {
                        waitCancellation.Cancel();
                        try
                        {
                            await delay.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // 処理進捗で待ちをやり直す。失敗ではない。
                        }

                        nextCheck = _activity.LastProgress + GrokBuildDriver.IdleBeforeLogCheck;
                        continue;
                    }

                    await delay.ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested || _signal.IsSignaled || _stopped)
                {
                    return;
                }

                now = _timeProvider.GetUtcNow();
                if (now - _activity.LastProgress < GrokBuildDriver.IdleBeforeLogCheck)
                {
                    nextCheck = _activity.LastProgress + GrokBuildDriver.IdleBeforeLogCheck;
                    continue;
                }

                if (TryRescue(processId, launchedAt, cancellationToken))
                {
                    return;
                }

                nextCheck = _timeProvider.GetUtcNow() + GrokBuildDriver.LogRecheckInterval;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 実行終了に伴う監視停止。失敗ではない。
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            FacadeLog.Information("Grok internal completion watch stopped because reading the log failed.");
        }
    }

    private bool TryRescue(int processId, DateTimeOffset launchedAt, CancellationToken cancellationToken)
    {
        if (_stopped || _cursor is null)
        {
            return true;
        }

        if (HasIgnoredEffort())
        {
            Stop("Grok internal completion recovery was not used because reasoning effort was reported as ignored.");
            return true;
        }

        GrokLogRead read;
        try
        {
            read = _logReader.Read(_cursor);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            Stop("Grok internal log read failed.");
            return true;
        }

        _cursor = read.Cursor;
        if (read.Cursor.Abandoned)
        {
            Stop("Grok internal log correlation stopped: " + (read.Cursor.Problem ?? "unreadable"));
            return true;
        }

        foreach (var line in read.Lines)
        {
            _correlation.Observe(line, processId, launchedAt, _resumeSessionId);
            if (_correlation.IsAbandoned)
            {
                Stop("Grok internal log correlation stopped: " + (_correlation.AbandonReason ?? "inconsistent"));
                return true;
            }
        }

        var success = _correlation.Success;
        if (success is null || cancellationToken.IsCancellationRequested)
        {
            return success is not null;
        }

        if (ProcessAlreadyExited())
        {
            Stop("Grok internal completion recovery was not used because the process had already exited.");
            return true;
        }

        if (_accumulator.FreezeIfNonEmpty() is null)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _confirmed, 1) == 1)
        {
            return true;
        }

        Evidence = success;
        _signal.Notify();
        return true;
    }

    private static Task DelayUntil(TimeProvider timeProvider, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (delay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = timeProvider.CreateTimer(
            _ => completed.TrySetResult(),
            state: null,
            delay,
            Timeout.InfiniteTimeSpan);
        var registration = cancellationToken.Register(() => completed.TrySetCanceled(cancellationToken));
        return completed.Task.ContinueWith(
            task =>
            {
                registration.Dispose();
                timer.Dispose();
                if (task.IsCanceled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (task.IsFaulted)
                {
                    throw task.Exception?.GetBaseException() ?? new InvalidOperationException("Grok idle wait failed.");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool ProcessAlreadyExited()
    {
        if (_process is null)
        {
            return true;
        }

        try
        {
            return _process.HasExited;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            return true;
        }
    }

    private bool HasIgnoredEffort()
    {
        return _reasoningEffort is not null
            && GrokBuildDriver.ReportsIgnoredReasoningEffort(CopyStderr());
    }

    private void Stop(string reason)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        FacadeLog.Information(reason);
    }
}

internal sealed class GrokActivityClock
{
    private readonly object _gate = new();
    private DateTimeOffset _lastProgress;
    private bool _started;
    private TaskCompletionSource _pulse = CreatePulse();

    public void Start(DateTimeOffset now)
    {
        lock (_gate)
        {
            _lastProgress = now;
            _started = true;
        }
    }

    public void Mark(DateTimeOffset now)
    {
        TaskCompletionSource pulse;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _lastProgress = now;
            pulse = _pulse;
            _pulse = CreatePulse();
        }

        pulse.TrySetResult();
    }

    public DateTimeOffset LastProgress
    {
        get
        {
            lock (_gate)
            {
                return _lastProgress;
            }
        }
    }

    public Task Pulse
    {
        get
        {
            lock (_gate)
            {
                return _pulse.Task;
            }
        }
    }

    private static TaskCompletionSource CreatePulse()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal static class GrokBuildHumanSummary
{
    public static string? Format(string type, JsonElement root)
    {
        if (string.Equals(type, "usage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "available_commands", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(type, "thought", StringComparison.OrdinalIgnoreCase))
        {
            return "thought: " + ReadData(root);
        }

        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
        {
            return "assistant: " + ReadData(root);
        }

        if (string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase)
            || (AgentLogSummary.LooksLikeTool(type, root)
                && !string.Equals(type, "tool_call_update", StringComparison.OrdinalIgnoreCase)))
        {
            return AgentLogSummary.DescribeTool(root, started: true);
        }

        if (string.Equals(type, "tool_call_update", StringComparison.OrdinalIgnoreCase))
        {
            return AgentLogSummary.DescribeTool(root, started: false);
        }

        if (string.Equals(type, "plan", StringComparison.OrdinalIgnoreCase))
        {
            var entries = CliJson.TryGetPropertyIgnoreCase(root, "entries", out var element)
                ? Compact(element)
                : Compact(root);
            return "plan: " + entries;
        }

        if (AgentLogSummary.LooksLikeMode(type))
        {
            return AgentLogSummary.DescribeModeOrLifecycle(type, root);
        }

        if (string.Equals(type, "end", StringComparison.OrdinalIgnoreCase))
        {
            var stop = CliJson.FindFirstString(root, "stopReason") ?? string.Empty;
            var sid = CliJson.FindExplicitSessionId(root) ?? string.Empty;
            return "end stopReason=" + stop + " sessionId=" + sid;
        }

        if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
        {
            return "error: " + (CliJson.FindFirstString(root, "message") ?? Compact(root));
        }

        return AgentLogSummary.DescribeGeneric(type, root);
    }

    private static string ReadData(JsonElement root)
    {
        return CliJson.FindFirstString(root, "data", "text", "content") ?? string.Empty;
    }

    private static string Compact(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => element.GetRawText(),
        };
    }
}
