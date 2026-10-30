using System.Text;
using System.Text.Json;

/// <summary>
/// GitHub Copilot CLI の non-interactive 経路へ変換する Driver。
/// </summary>
public sealed class GitHubCopilotDriver
{
    internal static readonly string[] SupportedReasoningEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];
    private readonly IProcessRunner _processRunner;
    private readonly Func<string> _allocateSessionId;

    public GitHubCopilotDriver(IProcessRunner processRunner, Func<string>? allocateSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
        _allocateSessionId = allocateSessionId ?? (() => Guid.NewGuid().ToString("D"));
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IAgentRunLog runLog,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
        var resume = !string.IsNullOrWhiteSpace(request.SessionId);
        var sessionId = resume ? request.SessionId! : AllocateSessionId();
        var arguments = BuildArguments(request, resume ? null : sessionId);
        runLog.BindSession(sessionId, resume ? SessionBinding.Resume : SessionBinding.Preallocated);
        var prompt = ApplyCopilotSkills(request.Prompt, request.Skills);
        runLog.WriteStarted(new AgentRunStartedInfo(
            Agent: AgentFacade.GitHubCopilotAgent,
            WorkingDirectory: request.WorkingDirectory,
            SessionId: request.SessionId,
            AutoApprove: request.AutoApprove,
            Skills: request.Skills,
            Prompt: prompt,
            FileName: "copilot",
            Arguments: arguments,
            Model: request.Model,
            ReasoningEffort: request.ReasoningEffort,
            Fast: request.Fast));

        var accumulator = new GitHubCopilotStreamAccumulator(runLog);
        ProcessRunResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    FileName: "copilot",
                    Arguments: arguments,
                    WorkingDirectory: request.WorkingDirectory,
                    StandardInputText: prompt,
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
            var message = SecretRedactor.RedactText(
                $"GitHub Copilot CLI exited with code {processResult.ExitCode}. stdout: {processResult.StandardOutput} stderr: {processResult.StandardError}");
            var failure = new InvalidOperationException(message);
            CliJson.MarkFailure(
                failure,
                "non_zero_exit",
                $"GitHub Copilot CLI exited with code {processResult.ExitCode}. stderr: {processResult.StandardError}",
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
            CliJson.MarkFailureIfMissing(ex, "output_parse_failed");
            throw;
        }
        return new AgentRunResult(
            Agent: AgentFacade.GitHubCopilotAgent,
            SessionId: SessionBinding.Resolve(runLog, parsed.SessionId),
            ExitCode: processResult.ExitCode,
            OutputText: parsed.OutputText,
            OutputKind: parsed.OutputKind,
            RawOutput: processResult.StandardOutput,
            RunId: runLog.RunId,
            EventsLogPath: runLog.EventsPath,
            TextLogPath: runLog.TextLogPath);
    }

    internal static List<string> BuildArguments(AgentRunRequest request, string? allocatedSessionId = null)
    {
        var arguments = new List<string>
        {
            "--output-format",
            "json",
        };

        if (request.AutoApprove)
        {
            arguments.Add("--allow-all");
        }

        AppendModelArgument(arguments, request.Model);
        if (request.ReasoningEffort is not null)
        {
            arguments.Add("--reasoning-effort");
            arguments.Add(request.ReasoningEffort);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            arguments.Add("--resume");
            arguments.Add(request.SessionId);
        }
        else if (!string.IsNullOrWhiteSpace(allocatedSessionId))
        {
            // v1.0.51 以降の新規 session 指定。--resume とは同時に渡さない。
            arguments.Add("--session-id");
            arguments.Add(allocatedSessionId);
        }

        return arguments;
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
            var failure = new InvalidOperationException("Allocated GitHub Copilot session id was empty.");
            CliJson.TraceException(failure);
            throw failure;
        }

        return sessionId;
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
    /// Copilot CLI は先頭行の `/name` を CLI slash command として解釈する。
    /// 公式の skill 指定は prompt 本文中の "Use the /name skill." 形式。
    /// </summary>
    internal static string ApplyCopilotSkills(string prompt, IReadOnlyList<string>? skills)
    {
        if (skills is null || skills.Count == 0)
        {
            return prompt;
        }

        var builder = new StringBuilder();
        foreach (var skill in skills)
        {
            builder.Append("Use the ");
            builder.Append(ToSlashName(skill));
            builder.Append(" skill.");
            builder.Append('\n');
        }

        builder.Append(prompt);
        return builder.ToString();
    }

    internal static string ToSlashName(string skill)
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
/// Copilot JSONL を 1 パスで蓄積する。Grok の wire schema には合わせない。
/// </summary>
internal sealed class GitHubCopilotStreamAccumulator
{
    private readonly IAgentRunLog _runLog;
    private readonly List<string> _texts = [];
    private readonly List<string> _completedTurns = [];
    private readonly List<string> _currentTurn = [];
    private string? _sessionId;
    private int _jsonLineCount;
    private bool _sawNonWhitespace;
    private JsonException? _lastJsonError;
    private bool _insideAssistantTurn;
    private bool _sawCompleteAssistantTurn;
    private bool _sawTerminalResult;
    private bool _sawUnscopedAssistantText;
    private bool _sawIncompleteAssistantTurn;

    public GitHubCopilotStreamAccumulator(IAgentRunLog runLog)
    {
        _runLog = runLog;
    }

    public void OnStdoutLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _sawNonWhitespace = true;
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(line);
        }
        catch (JsonException ex)
        {
            _lastJsonError = ex;
            CliJson.TraceException(ex);
            NoteSession(CliJson.FindCopilotResumeHint(line));
            _runLog.WriteProcessLine("stdout", line);
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            NoteSession(CliJson.FindCopilotResumeHint(line));
            _runLog.WriteProcessLine("stdout", line);
            return;
        }

        _jsonLineCount++;
        NoteSession(CliJson.FindExplicitSessionId(root) ?? CliJson.FindCopilotResumeHint(line));
        var type = CliJson.FindFirstString(root, "type") ?? "unknown";
        if (GitHubCopilotOutputParser.IsAssistantTurnStart(type))
        {
            if (_insideAssistantTurn || _currentTurn.Count > 0)
            {
                _sawIncompleteAssistantTurn = true;
                _currentTurn.Clear();
            }

            _insideAssistantTurn = true;
        }
        var assistantText = GitHubCopilotOutputParser.ReadAssistantText(root);
        _runLog.WriteAgentEvent(type, root, GitHubCopilotHumanSummary.Format(type, root, assistantText));
        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            _texts.Add(assistantText);
            if (_insideAssistantTurn)
            {
                _currentTurn.Add(assistantText);
            }
            else
            {
                _sawUnscopedAssistantText = true;
            }
        }

        if (GitHubCopilotOutputParser.IsAssistantTurnEnd(type))
        {
            if (!_insideAssistantTurn)
            {
                _sawIncompleteAssistantTurn = true;
            }
            else
            {
                CommitTurn();
            }

            _insideAssistantTurn = false;
        }

        if (GitHubCopilotOutputParser.IsTerminalResult(type))
        {
            _sawTerminalResult = true;
        }
    }

    public ParsedCliOutput Complete()
    {
        if (!_sawNonWhitespace)
        {
            throw new InvalidOperationException("GitHub Copilot CLI returned empty stdout.");
        }

        if (_jsonLineCount == 0)
        {
            throw new InvalidOperationException(
                "GitHub Copilot CLI stdout did not contain JSONL objects.",
                _lastJsonError);
        }

        if (_texts.Count == 0)
        {
            throw new InvalidOperationException("GitHub Copilot CLI JSONL did not contain assistant text.");
        }

        var hasVerifiedFinalResponse = _sawTerminalResult
            && _sawCompleteAssistantTurn
            && !_insideAssistantTurn
            && !_sawIncompleteAssistantTurn
            && !_sawUnscopedAssistantText
            && _completedTurns.Count > 0;
        var output = hasVerifiedFinalResponse
            ? _completedTurns[^1]
            : string.Join("\n", _texts);
        var outputKind = hasVerifiedFinalResponse
            ? "final_response"
            : "assistant_transcript";
        return new ParsedCliOutput(_sessionId ?? string.Empty, output, outputKind);
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

    private void CommitTurn()
    {
        if (_currentTurn.Count == 0)
        {
            return;
        }

        _completedTurns.Add(string.Join("", _currentTurn));
        _currentTurn.Clear();
        _sawCompleteAssistantTurn = true;
    }
}
internal static class GitHubCopilotHumanSummary
{
    public static string Format(string type, JsonElement root, string? assistantText)
    {
        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            return "assistant: " + assistantText;
        }

        if (string.Equals(type, "result", StringComparison.OrdinalIgnoreCase))
        {
            return "result sessionId=" + (CliJson.FindExplicitSessionId(root) ?? string.Empty);
        }

        if (string.Equals(type, "session", StringComparison.OrdinalIgnoreCase))
        {
            return "session sessionId=" + (CliJson.FindExplicitSessionId(root) ?? string.Empty);
        }

        if (AgentLogSummary.LooksLikeTool(type, root))
        {
            var started = !type.Contains("update", StringComparison.OrdinalIgnoreCase)
                && !type.Contains("result", StringComparison.OrdinalIgnoreCase);
            return AgentLogSummary.DescribeTool(root, started);
        }

        if (AgentLogSummary.LooksLikeMode(type))
        {
            return AgentLogSummary.DescribeModeOrLifecycle(type, root);
        }

        return AgentLogSummary.DescribeGeneric(type, root);
    }
}

internal static class GitHubCopilotOutputParser
{
    internal static bool IsAssistantTurnStart(string type)
    {
        return string.Equals(type, "assistant.turn_start", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAssistantTurnEnd(string type)
    {
        return string.Equals(type, "assistant.turn_end", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsTerminalResult(string type)
    {
        return string.Equals(type, "result", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ReadAssistantText(JsonElement root)
    {
        if (CliJson.TryGetPropertyIgnoreCase(root, "type", out var typeElement))
        {
            var type = typeElement.GetString();
            if (type is not null
                && !string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "assistant.message", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "assistant_message", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "message", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "final", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        if (CliJson.TryGetPropertyIgnoreCase(root, "data", out var data))
        {
            var fromData = CliJson.FindFirstString(data, "content", "text", "message", "output", "result");
            if (!string.IsNullOrWhiteSpace(fromData))
            {
                return fromData;
            }
        }

        return CliJson.FindFirstString(root, "text", "content", "message", "output", "result");
    }
}
