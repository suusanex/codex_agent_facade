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
    private readonly Func<string> _allocateSessionId;

    public GrokBuildDriver(IProcessRunner processRunner)
        : this(processRunner, TimeProvider.System, FileGrokUnifiedLogReader.CreateDefault(), null)
    {
    }

    internal GrokBuildDriver(
        IProcessRunner processRunner,
        TimeProvider timeProvider,
        IGrokUnifiedLogReader logReader,
        IGrokAcpProcessFactory? acpProcesses = null,
        Func<string>? allocateSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logReader);
        _processRunner = processRunner;
        _timeProvider = timeProvider;
        _logReader = logReader;
        _acpProcesses = acpProcesses ?? new GrokAcpProcessFactory();
        _allocateSessionId = allocateSessionId ?? (() => Guid.NewGuid().ToString("D"));
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

        var resume = !string.IsNullOrWhiteSpace(request.SessionId);
        var sessionId = resume ? request.SessionId! : AllocateSessionId();
        var arguments = BuildArguments(request, resume ? null : sessionId);
        runLog.BindSession(sessionId, resume ? SessionBinding.Resume : SessionBinding.Preallocated);
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
            sessionId,
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
            CliJson.MarkFailureIfMissing(ex, "internal_error");
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

        if (!watch.Confirmed)
        {
            watch.EnsureBackgroundWorkCompleted(processResult.ExitCode);
        }

        ParsedCliOutput parsed;
        try
        {
            parsed = accumulator.Complete(watch.Confirmed ? watch.Evidence?.SessionId : null);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            CliJson.MarkFailureIfMissing(ex, "output_parse_failed");
            throw;
        }

        if (!watch.Confirmed)
        {
            watch.EnsureNaturalExitCompleted(processResult.ExitCode);
        }

        return new AgentRunResult(
            Agent: AgentFacade.GrokBuildAgent,
            SessionId: SessionBinding.Resolve(runLog, parsed.SessionId),
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

    internal static List<string> BuildArguments(AgentRunRequest request, string? allocatedSessionId = null)
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
        else if (!string.IsNullOrWhiteSpace(allocatedSessionId))
        {
            arguments.Add("--session-id");
            arguments.Add(allocatedSessionId);
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
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            runLog.BindSession(request.SessionId, SessionBinding.Resume);
        }

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

        cancellationToken.ThrowIfCancellationRequested();
        IGrokAcpProcess process;
        try
        {
            process = _acpProcesses.Start(new GrokAcpStartInfo(
                "grok",
                arguments,
                request.WorkingDirectory,
                line => runLog.WriteProcessLine("stderr", line)));
        }
        catch (ProcessStartException ex)
        {
            CliJson.TraceException(ex);
            CliJson.MarkFailure(ex, "process_start_failed");
            throw;
        }

        AgentRunResult result;
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
            var sessionId = SessionBinding.Resolve(runLog, outcome.SessionId);
            GrokPlanArtifact.Write(request.WorkingDirectory, sessionId, outcome.Content);
            result = new AgentRunResult(
                Agent: AgentFacade.GrokBuildAgent,
                SessionId: sessionId,
                ExitCode: null,
                OutputText: outcome.Content,
                OutputKind: "plan",
                RawOutput: outcome.Content,
                RunId: runLog.RunId,
                EventsLogPath: runLog.EventsPath,
                TextLogPath: runLog.TextLogPath,
                PlanPath: GrokPlanArtifact.RelativePath(sessionId));
        }

        if (!process.ExitObserved)
        {
            throw PlanFailure("internal_error", "Grok Build plan mode did not exit after termination.");
        }

        return result;
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

    private string AllocateSessionId()
    {
        string sessionId;
        try
        {
            sessionId = _allocateSessionId();
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var failure = new InvalidOperationException("Allocated Grok session id was empty.");
            CliJson.TraceException(failure);
            throw failure;
        }

        return sessionId;
    }
}

internal readonly record struct GrokStreamSnapshot(
    bool HasPendingBackgroundWork,
    bool HasInFlightForegroundTool,
    bool HasSeenEndEvent,
    string? EndStopReason,
    string? PendingDescription,
    IReadOnlySet<string> ResolvedMonitorToolCalls);

/// <summary>
/// streaming-json の NDJSON を 1 パスで蓄積する。終了後に stdout 全体は再 parse しない。
/// </summary>
internal sealed class GrokStreamAccumulator
{
    private static readonly HashSet<string> TerminalToolStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed",
        "failed",
        "error",
        "cancelled",
        "canceled",
        "killed",
        "timed_out",
        "stopped",
    };

    private static readonly HashSet<string> ActiveToolStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "pending",
        "running",
        "in_progress",
        "queued",
    };

    private sealed record ToolInvocationState(string? ToolName, string? Kind, bool IsBackgroundCapable);

    private readonly IAgentRunLog _runLog;
    private readonly StringBuilder _text = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, ToolInvocationState> _inFlightToolCalls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeBackgroundTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _monitorToolCallToTaskId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _backgroundTaskToMonitorToolCall = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolvedMonitorToolCalls = new(StringComparer.Ordinal);
    private string _sessionId = string.Empty;
    private int _jsonEventCount;
    private bool _sawNonWhitespace;
    private bool _frozen;
    private string? _frozenText;
    private bool _hasAmbiguousBackgroundWait;
    private string? _ambiguousReason;
    private bool _hasSeenEndEvent;
    private string? _endStopReason;

    public GrokStreamAccumulator(IAgentRunLog runLog)
    {
        _runLog = runLog;
    }

    public GrokStreamSnapshot GetWatchSnapshot(IReadOnlySet<string>? externalCompletedTaskIds = null)
    {
        lock (_gate)
        {
            var remainingActiveTasks = 0;
            foreach (var taskId in _activeBackgroundTasks)
            {
                if (externalCompletedTaskIds is null || !externalCompletedTaskIds.Contains(taskId))
                {
                    remainingActiveTasks++;
                }
            }

            var hasInFlightBackgroundTool = false;
            var hasInFlightForegroundTool = false;
            foreach (var invocation in _inFlightToolCalls.Values)
            {
                if (invocation.IsBackgroundCapable)
                {
                    hasInFlightBackgroundTool = true;
                }
                else
                {
                    hasInFlightForegroundTool = true;
                }
            }

            var hasPendingBackground = remainingActiveTasks > 0
                || hasInFlightBackgroundTool
                || _hasAmbiguousBackgroundWait;
            return new GrokStreamSnapshot(
                HasPendingBackgroundWork: hasPendingBackground,
                HasInFlightForegroundTool: hasInFlightForegroundTool,
                HasSeenEndEvent: _hasSeenEndEvent,
                EndStopReason: _endStopReason,
                PendingDescription: BuildPendingDescription(remainingActiveTasks),
                ResolvedMonitorToolCalls: _resolvedMonitorToolCalls.Count == 0
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(_resolvedMonitorToolCalls, StringComparer.Ordinal));
        }
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
            var observedSessionId = CliJson.FindExplicitSessionId(root);
            if (!string.IsNullOrWhiteSpace(observedSessionId))
            {
                _runLog.BindSession(observedSessionId, SessionBinding.EarlyStream);
                _sessionId = observedSessionId;
            }

            TrackStreamLifecycle(type, root);

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

            return IsProgress(type, root);
        }
    }

    private void TrackStreamLifecycle(string type, JsonElement root)
    {
        if (string.Equals(type, "end", StringComparison.OrdinalIgnoreCase))
        {
            _hasSeenEndEvent = true;
            _endStopReason = ReadStringProperty(root, "stopReason", "stop_reason");
            // end を受信しても未解決のバックグラウンド待機や曖昧状態は消去しない。
            return;
        }

        if (string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase))
        {
            ObserveToolCall(root);
            return;
        }

        if (string.Equals(type, "tool_call_update", StringComparison.OrdinalIgnoreCase))
        {
            ObserveToolCallUpdate(root);
        }
    }

    private void ObserveToolCall(JsonElement root)
    {
        var toolCallId = ReadStringProperty(root, "toolCallId", "tool_call_id");
        var toolName = ReadStringProperty(root, "toolName", "tool_name", "title");
        var kind = ReadStringProperty(root, "kind");
        var status = ReadStringProperty(root, "status");
        var isBgCapable = IsBackgroundCapableTool(toolName, kind, root);

        if (IsTerminalStatus(status))
        {
            if (isBgCapable && string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetObjectProperty(root, "rawOutput", out _))
                {
                    ObserveToolCallUpdate(root, new ToolInvocationState(toolName, kind, true));
                }
                else
                {
                    MarkAmbiguous("Background-capable tool_call closed cleanly without registration metadata.");
                }
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            if (isBgCapable)
            {
                MarkAmbiguous("Background-capable tool_call omitted toolCallId.");
            }

            return;
        }

        _inFlightToolCalls[toolCallId] = new ToolInvocationState(toolName, kind, isBgCapable);
    }

    private void ObserveToolCallUpdate(JsonElement root, ToolInvocationState? inlineInvocation = null)
    {
        var toolCallId = ReadStringProperty(root, "toolCallId", "tool_call_id");
        var status = ReadStringProperty(root, "status");
        var hasRawOutput = TryGetObjectProperty(root, "rawOutput", out var rawOutput);
        var rawType = hasRawOutput ? ReadStringProperty(rawOutput, "type") : null;

        ToolInvocationState? inFlight = inlineInvocation;
        if (!string.IsNullOrWhiteSpace(toolCallId) && _inFlightToolCalls.TryGetValue(toolCallId, out var existing))
        {
            inFlight ??= existing;
        }

        if (string.Equals(rawType, "Monitor", StringComparison.OrdinalIgnoreCase))
        {
            RemoveInFlight(toolCallId);
            var taskId = ReadStringProperty(rawOutput, "taskId", "task_id") ?? toolCallId;
            if (string.IsNullOrWhiteSpace(taskId))
            {
                MarkAmbiguous("Monitor registration update omitted taskId and toolCallId.");
                return;
            }

            if (HasMonitorCompletionEvidence(status, hasRawOutput, rawOutput))
            {
                ResolveTaskId(taskId);
                if (!string.IsNullOrWhiteSpace(toolCallId))
                {
                    _resolvedMonitorToolCalls.Add(toolCallId);
                }

                return;
            }

            _activeBackgroundTasks.Add(taskId);
            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                _monitorToolCallToTaskId[toolCallId] = taskId;
                _backgroundTaskToMonitorToolCall[taskId] = toolCallId;
            }

            return;
        }

        if (string.Equals(rawType, "BackgroundTaskStarted", StringComparison.OrdinalIgnoreCase))
        {
            RemoveInFlight(toolCallId);
            var bgStatus = ReadStringProperty(rawOutput, "status");
            if (!IsTerminalStatus(bgStatus))
            {
                var bgTaskId = ReadStringProperty(rawOutput, "task_id", "taskId");
                if (string.IsNullOrWhiteSpace(bgTaskId))
                {
                    MarkAmbiguous("BackgroundTaskStarted omitted task_id.");
                }
                else
                {
                    _activeBackgroundTasks.Add(bgTaskId);
                }
            }

            return;
        }

        if (string.Equals(rawType, "TaskOutput", StringComparison.OrdinalIgnoreCase))
        {
            RemoveInFlight(toolCallId);
            HandleTaskOutputUpdate(rawOutput);
            return;
        }

        if (string.Equals(rawType, "KillTask", StringComparison.OrdinalIgnoreCase))
        {
            RemoveInFlight(toolCallId);
            HandleKillTaskUpdate(rawOutput);
            return;
        }

        if (!string.IsNullOrWhiteSpace(toolCallId)
            && _monitorToolCallToTaskId.TryGetValue(toolCallId, out var linkedTaskId))
        {
            if (HasMonitorCompletionEvidence(status, hasRawOutput, rawOutput))
            {
                RemoveInFlight(toolCallId);
                ResolveTaskId(linkedTaskId);
                _resolvedMonitorToolCalls.Add(toolCallId);
            }
            else if (IsTerminalStatus(status))
            {
                RemoveInFlight(toolCallId);
                MarkAmbiguous("Registered monitor emitted a terminal status without completion evidence.");
            }

            return;
        }

        if (inFlight is not null
            && IsSpawnSubagentBackground(inFlight, hasRawOutput, rawOutput)
            && string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            RemoveInFlight(toolCallId);
            var subagentId = hasRawOutput ? TryExtractSubagentId(rawOutput) : null;
            if (string.IsNullOrWhiteSpace(subagentId))
            {
                MarkAmbiguous("Background spawn_subagent completed without parseable subagent_id.");
            }
            else
            {
                _activeBackgroundTasks.Add(subagentId);
            }

            return;
        }

        if (IsTerminalStatus(status))
        {
            RemoveInFlight(toolCallId);
            if (inFlight is { IsBackgroundCapable: true }
                && string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                MarkAmbiguous($"Background-capable tool '{inFlight.ToolName ?? inFlight.Kind}' completed without task metadata.");
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(toolCallId) && !_inFlightToolCalls.ContainsKey(toolCallId))
        {
            _inFlightToolCalls[toolCallId] = inFlight ?? new ToolInvocationState(null, null, false);
        }
    }

    private void HandleTaskOutputUpdate(JsonElement rawOutput)
    {
        if (!TryGetObjectProperty(rawOutput, "Result", out var result))
        {
            MarkAmbiguous("TaskOutput update did not include a Result object.");
            return;
        }

        var taskId = ReadStringProperty(result, "task_id", "taskId");
        var taskStatus = ReadStringProperty(result, "status");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            if (!IsTerminalStatus(taskStatus))
            {
                MarkAmbiguous("TaskOutput update omitted task_id for a non-terminal result.");
            }

            return;
        }

        if (IsTerminalStatus(taskStatus))
        {
            ResolveTaskId(taskId);
            return;
        }

        _activeBackgroundTasks.Add(taskId);
    }

    private void HandleKillTaskUpdate(JsonElement rawOutput)
    {
        if (!TryGetObjectProperty(rawOutput, "Result", out var result))
        {
            return;
        }

        var taskId = ReadStringProperty(result, "task_id", "taskId");
        var outcome = ReadStringProperty(result, "outcome");
        var status = ReadStringProperty(result, "status");
        if (!string.IsNullOrWhiteSpace(taskId)
            && (string.Equals(outcome, "killed", StringComparison.OrdinalIgnoreCase)
                || IsTerminalStatus(status)))
        {
            ResolveTaskId(taskId);
        }
    }

    private static bool HasMonitorCompletionEvidence(string? status, bool hasRawOutput, JsonElement rawOutput)
    {
        if (!hasRawOutput)
        {
            return IsTerminalStatus(status);
        }

        // rawOutput がある場合、親 tool_call_update.status == "completed" は登録応答や途中通知でも出るため、rawOutput 本体で判定する。
        var rawStatus = ReadStringProperty(rawOutput, "status");
        if (IsActiveStatus(rawStatus))
        {
            return false;
        }

        if (IsTerminalStatus(rawStatus))
        {
            return true;
        }

        // exit_code が数値で返っている場合、または timed_out が true の場合は終了を確定できる。
        // timed_out: false 単独では終了証明にならない。
        if ((TryGetPropertyIgnoreCase(rawOutput, "exit_code", out var exitCodeElement)
                || TryGetPropertyIgnoreCase(rawOutput, "exitCode", out exitCodeElement))
            && exitCodeElement.ValueKind == JsonValueKind.Number)
        {
            return true;
        }

        if (TryGetPropertyIgnoreCase(rawOutput, "timed_out", out var timedOutElement)
            && timedOutElement.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        return TryGetObjectProperty(rawOutput, "Result", out var nestedResult)
            && IsTerminalStatus(ReadStringProperty(nestedResult, "status"));
    }

    private void ResolveTaskId(string taskId)
    {
        _activeBackgroundTasks.Remove(taskId);
        if (_backgroundTaskToMonitorToolCall.TryGetValue(taskId, out var monitorToolCallId))
        {
            _resolvedMonitorToolCalls.Add(monitorToolCallId);
        }
    }

    private void RemoveInFlight(string? toolCallId)
    {
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            _inFlightToolCalls.Remove(toolCallId);
        }
    }

    private void MarkAmbiguous(string reason)
    {
        _hasAmbiguousBackgroundWait = true;
        _ambiguousReason ??= reason;
    }

    private string? BuildPendingDescription(int remainingActiveTasks)
    {
        var parts = new List<string>();
        if (remainingActiveTasks > 0)
        {
            parts.Add($"activeTasks={remainingActiveTasks}");
        }

        if (_inFlightToolCalls.Count > 0)
        {
            parts.Add($"inFlightTools={_inFlightToolCalls.Count}");
        }

        if (_hasAmbiguousBackgroundWait)
        {
            parts.Add("ambiguous=" + (_ambiguousReason ?? "unspecified"));
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static bool IsTerminalStatus(string? status)
    {
        return !string.IsNullOrWhiteSpace(status) && TerminalToolStatuses.Contains(status);
    }

    private static bool IsActiveStatus(string? status)
    {
        return !string.IsNullOrWhiteSpace(status) && ActiveToolStatuses.Contains(status);
    }

    private static bool IsBackgroundCapableTool(string? toolName, string? kind, JsonElement root)
    {
        if (string.Equals(toolName, "monitor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "monitor", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryGetObjectProperty(root, "rawInput", out var rawInput))
        {
            if (TryGetPropertyIgnoreCase(rawInput, "run_in_background", out var bgRunElement)
                && bgRunElement.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (string.Equals(toolName, "spawn_subagent", StringComparison.OrdinalIgnoreCase)
                && TryGetPropertyIgnoreCase(rawInput, "background", out var bgElement)
                && bgElement.ValueKind == JsonValueKind.True)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSpawnSubagentBackground(
        ToolInvocationState invocation,
        bool hasRawOutput,
        JsonElement rawOutput)
    {
        if (string.Equals(invocation.ToolName, "spawn_subagent", StringComparison.OrdinalIgnoreCase)
            && invocation.IsBackgroundCapable)
        {
            return true;
        }

        if (!hasRawOutput)
        {
            return false;
        }

        var text = ReadStringProperty(rawOutput, "text");
        return !string.IsNullOrWhiteSpace(text)
            && text.Contains("subagent_id:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractSubagentId(JsonElement rawOutput)
    {
        var direct = ReadStringProperty(rawOutput, "subagent_id", "task_id", "taskId");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var text = ReadStringProperty(rawOutput, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        const string marker = "subagent_id:";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var span = text.AsSpan(idx + marker.Length).TrimStart();
        var end = 0;
        while (end < span.Length && !char.IsWhiteSpace(span[end]))
        {
            end++;
        }

        return end > 0 ? span[..end].ToString() : null;
    }

    private static string? ReadStringProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                var s = value.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
        }

        return null;
    }

    private static bool TryGetObjectProperty(JsonElement element, string name, out JsonElement value)
    {
        if (TryGetPropertyIgnoreCase(element, name, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        return CliJson.TryGetPropertyIgnoreCase(element, name, out value);
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
    private int _processId;
    private DateTimeOffset _launchedAt;
    private int _confirmed;
    private bool _stopped;
    private bool _logReadFailed;
    private bool _loggedBackgroundWaitSuppression;
    private bool _inspectedAtNaturalExit;

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
        _processId = process.Id;
        _launchedAt = launchedAt;
        _activity.Start(launchedAt);
        if (process.Id <= 0)
        {
            Stop("Grok process id was not available. Internal completion recovery is disabled.");
            return Task.CompletedTask;
        }

        if (cursor.Abandoned)
        {
            _logReadFailed = true;
            Stop("Grok internal log could not be read before launch: " + (cursor.Problem ?? "unreadable"));
            return Task.CompletedTask;
        }

        _cursor = cursor;
        return WatchAsync(process.Id, launchedAt, cancellationToken);
    }

    public void EnsureBackgroundWorkCompleted(int? exitCode)
    {
        var snapshot = CaptureSnapshotAtNaturalExit();
        if (snapshot.HasPendingBackgroundWork
            || _correlation.HasUnresolvedMonitorWait(snapshot.ResolvedMonitorToolCalls))
        {
            var detail = snapshot.PendingDescription
                ?? "unresolved monitor background wait remained active when Grok exited.";
            ThrowMarkedFailure(
                "grok_incomplete_background_wait",
                $"Grok exited before background work completed: {detail}",
                exitCode);
        }
    }

    public void EnsureNaturalExitCompleted(int? exitCode)
    {
        EnsureBackgroundWorkCompleted(exitCode);
        var snapshot = CaptureSnapshotAtNaturalExit();

        if (snapshot.HasSeenEndEvent)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.EndStopReason)
                && !string.Equals(snapshot.EndStopReason, "end_turn", StringComparison.OrdinalIgnoreCase))
            {
                ThrowMarkedFailure(
                    "grok_internal_state_indeterminate",
                    $"Grok stream ended with non-terminal stopReason '{snapshot.EndStopReason}'.",
                    exitCode);
            }

            if (_correlation.HasObservedEvents
                && (_correlation.IsWakePending
                    || (_correlation.SawMonitorOrWakeAdmission
                        && (_correlation.IsPromptInFlight
                            || _correlation.PromptFailed
                            || _correlation.IsAbandoned
                            || _correlation.HasIndeterminateState))))
            {
                var detail = _correlation.IndeterminateReason
                    ?? _correlation.AbandonReason
                    ?? (_correlation.IsWakePending
                        ? "queued or background wake prompt remained pending"
                        : _correlation.IsPromptInFlight
                            ? "a prompt turn was still in flight"
                            : "internal prompt failed");
                ThrowMarkedFailure(
                    "grok_internal_state_indeterminate",
                    $"Grok stream ended while internal monitor/wake state remained inconsistent: {detail}",
                    exitCode);
            }

            return;
        }

        // type:"end" を受信しない自然終了で、内部完了証拠が無い・失敗・継続中・不整合・読み取り不能なら completed に偽装しない。
        if (_logReadFailed
            || snapshot.HasInFlightForegroundTool
            || _correlation.HasIndeterminateState
            || _correlation.IsAbandoned
            || _correlation.PromptFailed
            || _correlation.IsPromptInFlight
            || _correlation.IsWakePending
            || _correlation.TryGetConfirmedCompletion(snapshot.ResolvedMonitorToolCalls) is null)
        {
            var reason = _correlation.IndeterminateReason
                ?? _correlation.AbandonReason
                ?? (_logReadFailed
                    ? "Grok internal log could not be read reliably."
                    : snapshot.HasInFlightForegroundTool
                        ? snapshot.PendingDescription ?? "Grok exited while a tool call was still in flight."
                        : _correlation.PromptFailed
                        ? "Grok internal prompt failed."
                        : _correlation.IsWakePending
                            ? "Grok background wake admission remained pending."
                            : _correlation.IsPromptInFlight
                                ? "Grok internal prompt was still in flight."
                                : "Grok exited without terminal end event and without confirmed completion.");
            ThrowMarkedFailure(
                "grok_internal_state_indeterminate",
                $"Grok exited without confirmed completion: {reason}",
                exitCode);
        }
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

        if (!ObservePendingLogLines(processId, launchedAt, stopOnFailure: true))
        {
            return true;
        }

        var snapshot = _accumulator.GetWatchSnapshot(_correlation.WokenTaskIds);
        if (_correlation.HasIndeterminateState)
        {
            Stop(
                "Grok internal completion recovery suppressed due to indeterminate internal state: "
                + (_correlation.IndeterminateReason ?? "indeterminate"));
            return true;
        }

        if (snapshot.HasPendingBackgroundWork
            || snapshot.HasInFlightForegroundTool
            || _correlation.HasUnresolvedMonitorWait(snapshot.ResolvedMonitorToolCalls)
            || _correlation.IsPromptInFlight
            || _correlation.IsWakePending)
        {
            if (!_loggedBackgroundWaitSuppression)
            {
                _loggedBackgroundWaitSuppression = true;
                var waitReason = snapshot.HasPendingBackgroundWork || snapshot.HasInFlightForegroundTool
                    ? snapshot.PendingDescription ?? "active tool or background task"
                    : _correlation.HasUnresolvedMonitorWait(snapshot.ResolvedMonitorToolCalls)
                        ? "unresolved monitor wait"
                        : _correlation.IsWakePending
                            ? "pending background wake admission"
                            : "active prompt in flight";
                FacadeLog.Information(
                    $"Grok internal completion recovery is suppressed while waiting for background work: {waitReason}.");
            }

            return false;
        }

        _loggedBackgroundWaitSuppression = false;
        var success = _correlation.TryGetConfirmedCompletion(snapshot.ResolvedMonitorToolCalls);
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

    private bool ObservePendingLogLines(int processId, DateTimeOffset launchedAt, bool stopOnFailure)
    {
        if (_cursor is null)
        {
            return false;
        }

        GrokLogRead read;
        try
        {
            read = _logReader.Read(_cursor);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            _logReadFailed = true;
            if (stopOnFailure)
            {
                Stop("Grok internal log read failed.");
            }

            return false;
        }

        _cursor = read.Cursor;
        if (read.Cursor.Abandoned)
        {
            _logReadFailed = true;
            if (stopOnFailure)
            {
                Stop("Grok internal log correlation stopped: " + (read.Cursor.Problem ?? "unreadable"));
            }

            return false;
        }

        foreach (var line in read.Lines)
        {
            _correlation.Observe(line, processId, launchedAt, _resumeSessionId);
            if (_correlation.IsAbandoned)
            {
                if (stopOnFailure)
                {
                    Stop("Grok internal log correlation stopped: " + (_correlation.AbandonReason ?? "inconsistent"));
                }

                return false;
            }
        }

        return true;
    }

    private GrokStreamSnapshot CaptureSnapshotAtNaturalExit()
    {
        if (!_inspectedAtNaturalExit)
        {
            _inspectedAtNaturalExit = true;
            if (!_stopped
                && _cursor is not null
                && _processId > 0
                && !_correlation.IsAbandoned)
            {
                ObservePendingLogLines(_processId, _launchedAt, stopOnFailure: false);
            }
        }

        return _accumulator.GetWatchSnapshot(_correlation.WokenTaskIds);
    }

    private static void ThrowMarkedFailure(string kind, string message, int? exitCode)
    {
        var failure = new InvalidOperationException(message);
        CliJson.MarkFailure(failure, kind, message, exitCode);
        CliJson.TraceException(failure);
        throw failure;
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
