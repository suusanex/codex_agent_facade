using System.Text;
using System.Text.Json;

/// <summary>
/// Codex CLI の non-interactive exec 経路へ変換する Driver。
/// </summary>
public sealed class CodexCliDriver
{
    private readonly IProcessRunner _processRunner;

    public CodexCliDriver(IProcessRunner processRunner)
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
            runLog.BindSession(request.SessionId, SessionBinding.Resume);
        }

        runLog.WriteStarted(new AgentRunStartedInfo(
            Agent: AgentFacade.CodexAgent,
            WorkingDirectory: request.WorkingDirectory,
            SessionId: request.SessionId,
            AutoApprove: request.AutoApprove,
            Skills: request.Skills,
            Prompt: request.Prompt,
            FileName: "codex",
            Arguments: arguments,
            Model: request.Model,
            ReasoningEffort: request.ReasoningEffort,
            Fast: request.Fast));

        var accumulator = new CodexJsonlAccumulator(runLog, request.Fast == true);
        ProcessRunResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    FileName: "codex",
                    Arguments: arguments,
                    WorkingDirectory: request.WorkingDirectory,
                    StandardInputText: request.Prompt,
                    StdoutLineCallback: line =>
                    {
                        accumulator.OnStdoutLine(line);
                        onStdoutLine?.Invoke(line);
                    },
                    StderrLineCallback: line => runLog.WriteProcessLine("stderr", line),
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

        if (processResult.ExitCode != 0)
        {
            var failure = new InvalidOperationException(
                SecretRedactor.RedactText($"Codex CLI exited with code {processResult.ExitCode}. stdout: {processResult.StandardOutput} stderr: {processResult.StandardError}"));
            CliJson.MarkFailure(
                failure,
                "non_zero_exit",
                $"Codex CLI exited with code {processResult.ExitCode}. stderr: {processResult.StandardError}",
                processResult.ExitCode);
            CliJson.TraceException(failure);
            throw failure;
        }

        ParsedCliOutput parsed;
        try
        {
            parsed = accumulator.Complete();
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            CliJson.MarkFailureIfMissing(ex, accumulator.FailureKind ?? "output_parse_failed", ex.Message);
            throw;
        }

        var sessionId = SessionBinding.Resolve(runLog, parsed.SessionId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var missing = new InvalidOperationException("Codex CLI did not report a session id.");
            CliJson.MarkFailure(missing, "output_parse_failed");
            CliJson.TraceException(missing);
            throw missing;
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId)
            && !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal))
        {
            var failure = new InvalidOperationException("Codex CLI resumed with a different session id than requested.");
            CliJson.MarkFailure(failure, "session_mismatch");
            CliJson.TraceException(failure);
            throw failure;
        }

        return new AgentRunResult(
            Agent: AgentFacade.CodexAgent,
            SessionId: sessionId,
            ExitCode: processResult.ExitCode,
            OutputText: parsed.OutputText,
            OutputKind: parsed.OutputKind,
            RawOutput: processResult.StandardOutput,
            RunId: runLog.RunId,
            EventsLogPath: runLog.EventsPath,
            TextLogPath: runLog.TextLogPath);
    }

    internal static List<string> BuildArguments(AgentRunRequest request)
    {
        var arguments = new List<string> { "exec" };
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            arguments.Add("resume");
        }

        arguments.Add("--json");
        arguments.Add("--skip-git-repo-check");
        arguments.Add("-c");
        arguments.Add("approval_policy=\"never\"");
        arguments.Add("-c");
        arguments.Add("sandbox_mode=\"" + (request.AutoApprove ? "workspace-write" : "read-only") + "\"");
        if (request.Model is not null && !string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }

        if (request.ReasoningEffort is not null)
        {
            arguments.Add("-c");
            arguments.Add("model_reasoning_effort=\"" + request.ReasoningEffort + "\"");
        }

        if (request.Fast is not null)
        {
            arguments.Add("-c");
            arguments.Add("service_tier=\"" + (request.Fast.Value ? "priority" : "default") + "\"");
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            arguments.Add(request.SessionId);
        }

        arguments.Add("-");
        return arguments;
    }
}

internal sealed class CodexJsonlAccumulator
{
    private readonly IAgentRunLog _runLog;
    private readonly List<string> _assistantMessages = [];
    private readonly bool _fastRequested;
    private string? _sessionId;
    private bool _sawTurnCompleted;
    private string? _terminalFailure;
    private string? _unsupportedPriorityWarning;
    public string? FailureKind { get; private set; }
    private Exception? _lastJsonError;

    public CodexJsonlAccumulator(IAgentRunLog runLog, bool fastRequested)
    {
        _runLog = runLog;
        _fastRequested = fastRequested;
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
            _runLog.WriteProcessLine("stdout", line);
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            _runLog.WriteProcessLine("stdout", line);
            _terminalFailure = "Codex CLI output contained a non-object JSON event.";
            return;
        }

        var type = GetString(root, "type") ?? "unknown";
        NoteSession(GetString(root, "thread_id") ?? GetString(root, "threadId"));
        if (string.Equals(type, "item.completed", StringComparison.OrdinalIgnoreCase))
        {
            var item = GetProperty(root, "item");
            if (item is { ValueKind: JsonValueKind.Object })
            {
                var itemType = GetString(item.Value, "type");
                if (string.Equals(itemType, "agent_message", StringComparison.OrdinalIgnoreCase))
                {
                    var text = ReadMessageText(item.Value);
                    if (!string.IsNullOrEmpty(text))
                    {
                        _assistantMessages.Add(text);
                    }
                }
                else if (_fastRequested
                    && string.Equals(itemType, "error", StringComparison.OrdinalIgnoreCase)
                    && IsUnsupportedPriorityWarning(GetString(item.Value, "message")))
                {
                    _unsupportedPriorityWarning = GetString(item.Value, "message");
                }
            }
        }
        else if (string.Equals(type, "turn.completed", StringComparison.OrdinalIgnoreCase))
        {
            _sawTurnCompleted = true;
        }
        else if (string.Equals(type, "turn.failed", StringComparison.OrdinalIgnoreCase))
        {
            var error = GetProperty(root, "error");
            _terminalFailure = error is { ValueKind: JsonValueKind.Object }
                ? GetString(error.Value, "message") ?? GetString(root, "message") ?? type
                : GetString(root, "message") ?? GetString(root, "error") ?? type;
        }
        else if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
        {
            _terminalFailure = GetString(root, "message") ?? GetString(root, "error") ?? type;
        }

        _runLog.WriteAgentEvent(type, root, string.Equals(type, "item.completed", StringComparison.OrdinalIgnoreCase)
            ? _assistantMessages.LastOrDefault()
            : null);
    }

    public ParsedCliOutput Complete()
    {
        if (_lastJsonError is not null)
        {
            throw new InvalidOperationException("Codex CLI output contained invalid JSONL.", _lastJsonError);
        }

        if (_terminalFailure is not null)
        {
            throw new InvalidOperationException("Codex CLI reported a failed turn: " + _terminalFailure);
        }

        if (_unsupportedPriorityWarning is not null)
        {
            FailureKind = "option_not_applied";
            throw new InvalidOperationException(
                "Codex CLI did not apply the requested Fast setting: " + _unsupportedPriorityWarning);
        }

        if (!_sawTurnCompleted)
        {
            throw new InvalidOperationException("Codex CLI did not report a completed turn.");
        }

        // 空のエコーはここで失敗させない。継続時は driver が bind 済み ID を結果にする。
        return new ParsedCliOutput(_sessionId ?? string.Empty, string.Join("\n", _assistantMessages));
    }

    private void NoteSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _runLog.BindSession(sessionId, SessionBinding.EarlyStream);
        _sessionId = sessionId;
    }

    private static bool IsUnsupportedPriorityWarning(string? message)
    {
        return message is not null
            && message.StartsWith(
                "Configured service tier `priority` is not advertised as supported for model `",
                StringComparison.Ordinal)
            && message.EndsWith("` and will be omitted from requests.", StringComparison.Ordinal);
    }

    private static string? ReadMessageText(JsonElement item)
    {
        var direct = GetString(item, "text");
        if (direct is not null)
        {
            return direct;
        }

        var content = GetProperty(item, "content");
        if (content is not { ValueKind: JsonValueKind.Array })
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var part in content.Value.EnumerateArray())
        {
            if (string.Equals(GetString(part, "type"), "output_text", StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetString(part, "type"), "text", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(GetString(part, "text"));
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return GetProperty(element, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;
    }
}
