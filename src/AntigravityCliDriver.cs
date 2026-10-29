using System.Text;
using System.Text.Json;

/// <summary>
/// Antigravity CLI の headless / stream-json 経路へ変換する Driver。
/// 長寿命の stdin session は使わず、1 process と conversation id の継続にする。
/// </summary>
public sealed class AntigravityCliDriver
{
    public const string FileName = "agy";
    public const string PermissionSoftDeniedReason = "permission_soft_denied";

    private readonly IProcessRunner _processRunner;

    public AntigravityCliDriver(IProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IAgentRunLog runLog,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
        var arguments = BuildArguments(request);
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            runLog.BindSession(request.SessionId.Trim(), SessionBinding.Resume);
        }

        runLog.WriteStarted(new AgentRunStartedInfo(
            Agent: AgentFacade.AntigravityAgent,
            WorkingDirectory: request.WorkingDirectory,
            SessionId: request.SessionId,
            AutoApprove: request.AutoApprove,
            Skills: request.Skills,
            Prompt: request.Prompt,
            FileName: FileName,
            Arguments: arguments,
            Model: request.Model,
            ReasoningEffort: request.ReasoningEffort,
            Fast: request.Fast));

        var accumulator = new AntigravityStreamAccumulator(runLog);
        ProcessRunResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    FileName: FileName,
                    Arguments: arguments,
                    WorkingDirectory: request.WorkingDirectory,
                    StdoutLineCallback: line =>
                    {
                        accumulator.OnStdoutLine(line);
                        onStdoutLine?.Invoke(line);
                    },
                    StderrLineCallback: line =>
                    {
                        accumulator.OnStderrLine(line);
                        runLog.WriteProcessLine("stderr", line);
                    },
                    OnProcessStarted: runLog.AttachProcess,
                    OnLaunchResolved: runLog.WriteLaunch),
                cancellationToken).ConfigureAwait(false);
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

        AntigravityParsedOutput parsed;
        try
        {
            parsed = accumulator.Complete(request.SessionId, cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            if (ex.Data[CliJson.FailureKindKey] is not string)
            {
                CliJson.MarkFailure(ex, accumulator.FailureKind ?? "output_parse_failed", ex.Message);
            }

            throw;
        }

        if (processResult.ExitCode is null)
        {
            var failure = Fail("output_parse_failed", "Antigravity CLI did not report a process exit code.");
            throw failure;
        }

        if (!parsed.Succeeded)
        {
            var failure = Fail(
                parsed.FailureKind ?? "agent_error",
                parsed.FailureMessage ?? "Antigravity CLI did not succeed.",
                processResult.ExitCode);
            throw failure;
        }

        if (processResult.ExitCode != 0)
        {
            var failure = Fail(
                "non_zero_exit",
                "Antigravity CLI exited with code " + processResult.ExitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
                processResult.ExitCode);
            throw failure;
        }

        return new AgentRunResult(
            Agent: AgentFacade.AntigravityAgent,
            SessionId: SessionBinding.Resolve(runLog, parsed.ConversationId),
            ExitCode: processResult.ExitCode,
            OutputText: parsed.Response,
            RawOutput: processResult.StandardOutput,
            RunId: runLog.RunId,
            EventsLogPath: runLog.EventsPath,
            TextLogPath: runLog.TextLogPath,
            OutputKind: string.Equals(request.RunMode, "plan", StringComparison.Ordinal) ? "plan" : "final_response",
            CompletionReason: parsed.PermissionSoftDenied ? PermissionSoftDeniedReason : null);
    }

    internal static List<string> BuildArguments(AgentRunRequest request)
    {
        var arguments = new List<string>
        {
            "-p",
            request.Prompt,
            "--output-format",
            "stream-json",
            "--print-timeout",
            "0s",
        };
        if (request.RunMode is not null)
        {
            if (!string.Equals(request.RunMode, "plan", StringComparison.Ordinal))
            {
                throw new ArgumentException("run_mode must be omitted or \"plan\".");
            }

            arguments.Add("--mode");
            arguments.Add("plan");
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            arguments.Add("--conversation");
            arguments.Add(request.SessionId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }

        if (request.ReasoningEffort is not null)
        {
            arguments.Add("--effort");
            arguments.Add(request.ReasoningEffort);
        }

        if (request.AutoApprove)
        {
            arguments.Add("--dangerously-skip-permissions");
        }

        return arguments;
    }

    private static InvalidOperationException Fail(string kind, string message, int? exitCode = null)
    {
        var exception = new InvalidOperationException(message);
        CliJson.MarkFailure(exception, kind, message, exitCode);
        CliJson.TraceException(exception);
        return exception;
    }
}

internal sealed class AntigravityParsedOutput
{
    public bool Succeeded { get; init; }

    public string ConversationId { get; init; } = string.Empty;

    public string Response { get; init; } = string.Empty;

    public bool PermissionSoftDenied { get; init; }

    public string? FailureKind { get; init; }

    public string? FailureMessage { get; init; }
}

/// <summary>
/// stream-json の init / step_update / result を読む。必須イベントの欠落や session の不一致は失敗にする。
/// permission soft-deny は成功のまま completion reason で区別する。
/// </summary>
internal sealed class AntigravityStreamAccumulator
{
    private readonly IAgentRunLog _runLog;
    private readonly StringBuilder _stderr = new();
    private string? _conversationId;
    private string? _response;
    private string? _status;
    private bool _sawInit;
    private bool _sawResult;
    private bool _permissionSoftDenied;
    private bool _sawAgentResponseTextDelta;
    private string? _terminalFailure;
    private Exception? _lastJsonError;

    public AntigravityStreamAccumulator(IAgentRunLog runLog)
    {
        _runLog = runLog;
    }

    public string? FailureKind { get; private set; }

    public void OnStderrLine(string line)
    {
        if (_stderr.Length > 0)
        {
            _stderr.Append('\n');
        }

        _stderr.Append(line);
        if (IsAutoDeniedNotice(line))
        {
            _permissionSoftDenied = true;
        }
    }

    public void OnStdoutLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(line);
        }
        catch (JsonException ex)
        {
            CliJson.TraceException(ex);
            _lastJsonError = ex;
            _terminalFailure = "Antigravity CLI output contained invalid JSON.";
            FailureKind = "output_parse_failed";
            _runLog.WriteProcessLine("stdout", line);
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            _terminalFailure = "Antigravity CLI output contained a non-object JSON event.";
            FailureKind = "output_parse_failed";
            _runLog.WriteProcessLine("stdout", line);
            return;
        }

        var eventName = ReadString(root, "event");
        if (string.IsNullOrWhiteSpace(eventName))
        {
            _terminalFailure = "Antigravity CLI output omitted the event name.";
            FailureKind = "output_parse_failed";
            _runLog.WriteProcessLine("stdout", line);
            return;
        }

        NoteConversation(ReadString(root, "conversation_id"));
        if (string.Equals(eventName, "init", StringComparison.Ordinal))
        {
            if (_sawInit)
            {
                _terminalFailure = "Antigravity CLI reported more than one init event.";
                FailureKind = "output_parse_failed";
            }

            _sawInit = true;
            if (TryGetObject(root, "init", out var init) && IsAutoDeniedPayload(init))
            {
                _permissionSoftDenied = true;
            }
        }
        else if (string.Equals(eventName, "step_update", StringComparison.Ordinal))
        {
            if (TryGetObject(root, "step_update", out var step))
            {
                NoteConversation(ReadString(step, "conversation_id"));
                if (IsAutoDeniedPayload(step) || IsAutoDeniedNotice(step.GetRawText()))
                {
                    _permissionSoftDenied = true;
                }
            }
        }
        else if (string.Equals(eventName, "result", StringComparison.Ordinal))
        {
            if (_sawResult)
            {
                _terminalFailure = "Antigravity CLI reported more than one result event.";
                FailureKind = "output_parse_failed";
            }

            _sawResult = true;
            if (!TryGetObject(root, "result", out var result))
            {
                _terminalFailure = "Antigravity CLI result event did not include a result object.";
                FailureKind = "output_parse_failed";
            }
            else
            {
                NoteConversation(ReadString(result, "conversation_id"));
                _status = ReadString(result, "status");
                _response = ReadString(result, "response");
                if (IsAutoDeniedPayload(result))
                {
                    _permissionSoftDenied = true;
                }
            }
        }

        // raw payload は従来どおり events JSONL へ残す。human .log だけを投影する。
        ProjectHuman(eventName, root);
    }

    /// <summary>
    /// 実行中の tool と agent_response を human .log へ出す。
    /// <see cref="AgentLogSummary"/> は toolName / data 前提の別形式なので使わない。
    /// Antigravity は step_update.tool_info.parameters と text_delta の細切れ配信である。
    /// </summary>
    private void ProjectHuman(string eventName, JsonElement root)
    {
        if (string.Equals(eventName, "init", StringComparison.Ordinal))
        {
            _runLog.WriteAgentEvent(eventName, root, AntigravityHumanSummary.FormatInit(root));
            return;
        }

        if (string.Equals(eventName, "result", StringComparison.Ordinal))
        {
            // text_delta が一度も無い版だけ、最終 response を human log へ出す。
            // MCP の OutputText はここを見ず、常に result.response を正本にする。
            if (!_sawAgentResponseTextDelta && !string.IsNullOrEmpty(_response))
            {
                _runLog.AppendHumanFragment("assistant", _response);
            }

            _runLog.WriteAgentEvent(eventName, root, AntigravityHumanSummary.FormatResult(_status));
            return;
        }

        if (string.Equals(eventName, "step_update", StringComparison.Ordinal)
            && TryGetObject(root, "step_update", out var step))
        {
            var stepType = ReadString(step, "step_type");
            if (string.Equals(stepType, "agent_response", StringComparison.Ordinal))
            {
                _runLog.WriteAgentEvent(eventName, root, humanSummary: null);
                var textDelta = ReadString(step, "text_delta");
                if (!string.IsNullOrEmpty(textDelta))
                {
                    _sawAgentResponseTextDelta = true;
                    _runLog.AppendHumanFragment("assistant", textDelta);
                }

                return;
            }

            if (string.Equals(stepType, "tool", StringComparison.Ordinal))
            {
                _runLog.WriteAgentEvent(eventName, root, AntigravityHumanSummary.FormatTool(step));
                return;
            }
        }

        _runLog.WriteAgentEvent(eventName, root, humanSummary: null);
    }

    public AntigravityParsedOutput Complete(string? requestedSessionId, bool cancellationRequested)
    {
        if (_lastJsonError is not null || _terminalFailure is not null)
        {
            throw Fail(FailureKind ?? "output_parse_failed", _terminalFailure ?? "Antigravity CLI output contained invalid JSON.");
        }

        if (!_sawInit)
        {
            throw Fail("output_parse_failed", "Antigravity CLI did not report an init event.");
        }

        if (!_sawResult || _status is null)
        {
            throw Fail("output_parse_failed", "Antigravity CLI did not report a result event.");
        }

        if (string.IsNullOrWhiteSpace(_conversationId))
        {
            // 継続で conversation id が無いのは不一致ではない。新規だけ ID 不足で失敗する。
            if (string.IsNullOrWhiteSpace(requestedSessionId))
            {
                throw Fail("output_parse_failed", "Antigravity CLI did not report a conversation id.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(requestedSessionId)
            && !string.Equals(requestedSessionId.Trim(), _conversationId, StringComparison.Ordinal))
        {
            throw Fail("session_mismatch", "Antigravity CLI resumed with a different conversation id than requested.");
        }

        if (_response is null)
        {
            throw Fail("output_parse_failed", "Antigravity CLI result did not include a response.");
        }

        var status = _status.Trim();
        if (string.Equals(status, "SUCCESS", StringComparison.Ordinal))
        {
            return new AntigravityParsedOutput
            {
                Succeeded = true,
                ConversationId = string.IsNullOrWhiteSpace(_conversationId)
                    ? requestedSessionId!.Trim()
                    : _conversationId,
                Response = _response,
                PermissionSoftDenied = _permissionSoftDenied || IsAutoDeniedNotice(_stderr.ToString()),
            };
        }

        if (status is "CANCELED" or "INTERRUPTED")
        {
            if (cancellationRequested)
            {
                throw new OperationCanceledException("Antigravity CLI reported " + status + " after the Facade cancellation.");
            }

            throw Fail("agent_error", "Antigravity CLI reported " + status + " without a Facade cancellation.");
        }

        if (status is "WAITING" or "RUNNING")
        {
            throw Fail("incomplete_run", "Antigravity CLI ended in " + status + ".");
        }

        var error = string.IsNullOrWhiteSpace(_response) ? status : status + ": " + _response.Trim();
        throw Fail("agent_error", "Antigravity CLI reported " + error + ".");
    }

    private void NoteConversation(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        _runLog.BindSession(conversationId, SessionBinding.EarlyStream);
        if (_conversationId is null)
        {
            _conversationId = conversationId;
            return;
        }

        if (!string.Equals(_conversationId, conversationId, StringComparison.Ordinal))
        {
            _terminalFailure = "Antigravity CLI reported conflicting conversation ids.";
            FailureKind = "session_mismatch";
        }
    }

    private static bool IsAutoDeniedPayload(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (IsAutoDeniedPayload(item))
                {
                    return true;
                }
            }

            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty("denied_actions", out var denied)
            && denied.ValueKind == JsonValueKind.Array
            && denied.GetArrayLength() > 0)
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if ((property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                && IsAutoDeniedPayload(property.Value))
            {
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.String
                && IsAutoDeniedNotice(property.Value.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 公開されている headless の拒否通知。実測文は jetski の "was auto-denied" と result の denied_actions。
    /// </summary>
    private static bool IsAutoDeniedNotice(string? text)
    {
        return text is not null
            && text.Contains("auto-denied", StringComparison.OrdinalIgnoreCase);
    }

    private static Exception Fail(string kind, string message)
    {
        var exception = new InvalidOperationException(message);
        CliJson.MarkFailure(exception, kind, message);
        return exception;
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

/// <summary>
/// Antigravity stream-json の人間向け1行。tool 出力全文と tools 一覧は展開しない。
/// </summary>
internal static class AntigravityHumanSummary
{
    public static string FormatInit(JsonElement root)
    {
        var builder = new StringBuilder("antigravity init");
        if (!root.TryGetProperty("init", out var init) || init.ValueKind != JsonValueKind.Object)
        {
            return builder.ToString();
        }

        AppendField(builder, "model", ReadString(init, "model"));
        AppendField(builder, "permissionMode", ReadString(init, "permission_mode"));
        AppendField(builder, "cwd", ReadString(init, "cwd"));
        return builder.ToString();
    }

    public static string FormatResult(string? status)
    {
        return string.IsNullOrWhiteSpace(status)
            ? "antigravity result"
            : "antigravity result status=" + status.Trim();
    }

    public static string FormatTool(JsonElement step)
    {
        var name = ReadString(step, "tool_name");
        if (string.IsNullOrWhiteSpace(name)
            && step.TryGetProperty("tool_info", out var info)
            && info.ValueKind == JsonValueKind.Object)
        {
            name = ReadString(info, "name");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "tool";
        }

        var builder = new StringBuilder("tool: ");
        builder.Append(name.Trim());
        var state = ReadString(step, "state");
        if (!string.IsNullOrWhiteSpace(state))
        {
            builder.Append(' ');
            builder.Append(state.Trim());
        }

        if (step.TryGetProperty("tool_info", out var toolInfo)
            && toolInfo.ValueKind == JsonValueKind.Object
            && toolInfo.TryGetProperty("parameters", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object)
        {
            // 実 stream で安定している主パラメータだけ。output と error 本文は JSONL に残す。
            AppendField(builder, "command", CliJson.FindFirstString(parameters, "CommandLine"));
            AppendField(builder, "path", CliJson.FindFirstString(parameters, "Path"));
        }

        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(' ');
        builder.Append(name);
        builder.Append('=');
        builder.Append(value.Trim());
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
