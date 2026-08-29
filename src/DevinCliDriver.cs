using System.Text;
using System.Text.Json;

/// <summary>
/// Devin CLI の non-interactive 経路へ変換する Driver。ACP は使わない。
/// </summary>
public sealed class DevinCliDriver
{
    private readonly IProcessRunner _processRunner;

    public DevinCliDriver(IProcessRunner processRunner)
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
        var prompt = ApplyDevinSkills(request.Prompt, request.Skills);
        runLog.WriteStarted(new AgentRunStartedInfo(
            Agent: AgentFacade.DevinCliAgent,
            WorkingDirectory: request.WorkingDirectory,
            SessionId: request.SessionId,
            AutoApprove: request.AutoApprove,
            Skills: request.Skills,
            Prompt: prompt,
            FileName: "devin",
            Arguments: arguments));

        var accumulator = new DevinStreamAccumulator(runLog);
        ProcessRunResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    FileName: "devin",
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
                    $"Devin CLI exited with code {processResult.ExitCode}. stdout: {processResult.StandardOutput} stderr: {processResult.StandardError}"));
            CliJson.TraceException(failure);
            throw failure;
        }

        var parsed = accumulator.Complete();
        return new AgentRunResult(
            Agent: AgentFacade.DevinCliAgent,
            SessionId: parsed.SessionId,
            ExitCode: processResult.ExitCode,
            OutputText: parsed.OutputText,
            RawOutput: processResult.StandardOutput,
            RunId: runLog.RunId,
            EventsLogPath: runLog.EventsPath,
            TextLogPath: runLog.TextLogPath);
    }

    /// <summary>
    /// flags を先に置き、prompt は <c>--print --</c> の後へ渡す。
    /// <c>--continue</c> は並列 job で最新 session を取り違えるため使わない。
    /// </summary>
    internal static List<string> BuildArguments(AgentRunRequest request)
    {
        var prompt = ApplyDevinSkills(request.Prompt, request.Skills);
        var arguments = new List<string>
        {
            "--respect-workspace-trust",
            "false",
        };

        if (request.AutoApprove)
        {
            arguments.Add("--permission-mode");
            arguments.Add("dangerous");
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            arguments.Add("--resume");
            arguments.Add(request.SessionId);
        }

        arguments.Add("--print");
        arguments.Add("--");
        arguments.Add(prompt);
        return arguments;
    }

    /// <summary>
    /// Devin CLI は user-invocable skill を slash command として扱う。
    /// Copilot / Grok と共通 runtime にはしない。
    /// </summary>
    internal static string ApplyDevinSkills(string prompt, IReadOnlyList<string>? skills)
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
/// Devin <c>--print</c> の stdout を 1 パスで蓄積する。JSON 必須にはしない。
/// assistant JSON があるときはそれを最終応答とし、無いときだけ plain text を使う。
/// JSON object に見えない行は Deserialize せず、例外も出さない。
/// session ID は明示フィールドだけを採用し、任意 UUID や時刻推測は使わない。
/// </summary>
internal sealed class DevinStreamAccumulator
{
    private readonly IAgentRunLog _runLog;
    private readonly List<string> _plainTexts = [];
    private readonly List<string> _assistantTexts = [];
    private string? _sessionId;
    private bool _sawNonWhitespace;

    public DevinStreamAccumulator(IAgentRunLog runLog)
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
        if (!LooksLikeJsonObjectLine(line))
        {
            RecordPlainText(line);
            return;
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(line);
        }
        catch (JsonException ex)
        {
            // `{` 始まりなのに壊れている行だけを例外として残す。plain text の --print は想定内なので投げない。
            CliJson.TraceException(ex);
            RecordPlainText(line);
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            RecordPlainText(line);
            return;
        }

        _sessionId ??= CliJson.FindExplicitSessionId(root);
        var type = CliJson.FindFirstString(root, "type") ?? "unknown";
        var assistantText = DevinCliOutputParser.ReadAssistantText(root);
        _runLog.WriteAgentEvent(type, root, DevinCliHumanSummary.Format(type, root, assistantText));
        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            _assistantTexts.Add(assistantText);
        }
    }

    private void RecordPlainText(string line)
    {
        _runLog.WriteProcessLine("stdout", line);
        _plainTexts.Add(line);
    }

    /// <summary>
    /// Devin <c>--print</c> は JSON とは限らない。先頭が <c>{</c> の行だけ JSON として読む。
    /// それ以外を Deserialize すると JsonException が毎行 error ログになる。
    /// </summary>
    internal static bool LooksLikeJsonObjectLine(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '{';
    }

    public ParsedCliOutput Complete()
    {
        if (!_sawNonWhitespace)
        {
            throw new InvalidOperationException("Devin CLI returned empty stdout.");
        }

        var texts = _assistantTexts.Count > 0 ? _assistantTexts : _plainTexts;
        if (texts.Count == 0)
        {
            throw new InvalidOperationException("Devin CLI did not produce a final response.");
        }

        return new ParsedCliOutput(_sessionId ?? string.Empty, string.Join("\n", texts));
    }
}

internal static class DevinCliHumanSummary
{
    public static string Format(string type, JsonElement root, string? assistantText)
    {
        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            return "assistant: " + assistantText;
        }

        if (string.Equals(type, "result", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "session", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "end", StringComparison.OrdinalIgnoreCase))
        {
            return type + " sessionId=" + (CliJson.FindExplicitSessionId(root) ?? string.Empty);
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

internal static class DevinCliOutputParser
{
    internal static string? ReadAssistantText(JsonElement root)
    {
        if (CliJson.TryGetPropertyIgnoreCase(root, "type", out var typeElement))
        {
            var type = typeElement.GetString();
            if (type is not null && !IsAssistantType(type))
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

    private static bool IsAssistantType(string type)
    {
        return string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "assistant.message", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "assistant_message", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "message", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "final", StringComparison.OrdinalIgnoreCase);
    }
}
