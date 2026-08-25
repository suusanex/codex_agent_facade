using System.Text;
using System.Text.Json;

/// <summary>
/// Grok Build CLI の non-interactive 経路へ変換する Driver。
/// </summary>
public sealed class GrokBuildDriver
{
    private readonly IProcessRunner _processRunner;

    public GrokBuildDriver(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IAgentRunLog runLog,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
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
            Arguments: arguments));

        var accumulator = new GrokStreamAccumulator(runLog);
        ProcessRunResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    FileName: "grok",
                    Arguments: arguments,
                    WorkingDirectory: request.WorkingDirectory,
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
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw;
        }

        if (processResult.ExitCode != 0)
        {
            var failure = new InvalidOperationException(
                SecretRedactor.RedactText(
                    $"Grok Build CLI exited with code {processResult.ExitCode}. stdout: {processResult.StandardOutput} stderr: {processResult.StandardError}"));
            CliJson.TraceException(failure);
            throw failure;
        }

        var parsed = accumulator.Complete();
        return new AgentRunResult(
            Agent: AgentFacade.GrokBuildAgent,
            SessionId: parsed.SessionId,
            ExitCode: processResult.ExitCode,
            OutputText: parsed.OutputText,
            RawOutput: processResult.StandardOutput,
            RunId: runLog.RunId,
            EventsLogPath: runLog.EventsPath,
            TextLogPath: runLog.TextLogPath);
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

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            // --session-id は新規作成用。実機では既存 ID に対して "already in use" になるため継続は --resume。
            arguments.Add("--resume");
            arguments.Add(request.SessionId);
        }

        return arguments;
    }

    /// <summary>
    /// Grok Build は user-invocable skill を slash command として扱う。実機未確認の共通 runtime にはしない。
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
    private string _sessionId = string.Empty;
    private int _jsonEventCount;
    private bool _sawNonWhitespace;

    public GrokStreamAccumulator(IAgentRunLog runLog)
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
            CliJson.TraceException(ex);
            _runLog.WriteProcessLine("stdout", line);
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            _runLog.WriteProcessLine("stdout", line);
            return;
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
    }

    public ParsedCliOutput Complete()
    {
        if (!_sawNonWhitespace)
        {
            throw new InvalidOperationException("Grok Build CLI returned empty stdout.");
        }

        if (_jsonEventCount == 0)
        {
            throw new InvalidOperationException("Grok Build CLI stdout did not contain JSON objects.");
        }

        var outputText = _text.ToString();
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new InvalidOperationException("Grok Build CLI JSON did not contain a recognized response field.");
        }

        return new ParsedCliOutput(_sessionId, outputText);
    }

    private static string? ReadDataString(JsonElement root)
    {
        return CliJson.FindFirstString(root, "data", "text", "content");
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
