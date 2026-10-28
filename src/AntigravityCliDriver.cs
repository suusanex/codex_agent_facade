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
            CliJson.MarkFailure(ex, "internal_error");
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
            SessionId: parsed.ConversationId,
            ExitCode: processResult.ExitCode,
            OutputText: parsed.Response,
            RawOutput: processResult.StandardOutput,
            RunId: runLog.RunId,
            EventsLogPath: runLog.EventsPath,
            TextLogPath: runLog.TextLogPath,
            OutputKind: "final_response",
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

        _runLog.WriteAgentEvent(eventName, root, _response);
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
            throw Fail("output_parse_failed", "Antigravity CLI did not report a conversation id.");
        }

        if (!string.IsNullOrWhiteSpace(requestedSessionId)
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
                ConversationId = _conversationId,
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
