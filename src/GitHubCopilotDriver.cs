using System.Text.Json;

/// <summary>
/// GitHub Copilot CLI の non-interactive 経路へ変換する Driver。
/// </summary>
public sealed class GitHubCopilotDriver
{
    private readonly IProcessRunner _processRunner;

    public GitHubCopilotDriver(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
        var arguments = BuildArguments(request);
        ProcessRunResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    FileName: "copilot",
                    Arguments: arguments,
                    WorkingDirectory: request.WorkingDirectory,
                    StdoutLineCallback: onStdoutLine),
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
                $"GitHub Copilot CLI exited with code {processResult.ExitCode}. stdout: {processResult.StandardOutput} stderr: {processResult.StandardError}");
            CliJson.TraceException(failure);
            throw failure;
        }

        var parsed = GitHubCopilotOutputParser.Parse(processResult.StandardOutput);
        return new AgentRunResult(
            Agent: AgentFacade.GitHubCopilotAgent,
            SessionId: parsed.SessionId,
            ExitCode: processResult.ExitCode,
            OutputText: parsed.OutputText,
            RawOutput: processResult.StandardOutput);
    }

    internal static List<string> BuildArguments(AgentRunRequest request)
    {
        var prompt = ApplyCopilotSkills(request.Prompt, request.Skills);
        var arguments = new List<string>
        {
            "--prompt",
            prompt,
            "--output-format",
            "json",
        };

        if (request.AutoApprove)
        {
            arguments.Add("--allow-all");
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            arguments.Add("--resume");
            arguments.Add(request.SessionId);
        }

        return arguments;
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

        var builder = new System.Text.StringBuilder();
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

internal static class GitHubCopilotOutputParser
{
    public static ParsedCliOutput Parse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException("GitHub Copilot CLI returned empty stdout.");
        }

        var lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? sessionId = null;
        var texts = new List<string>();
        var jsonLineCount = 0;
        JsonException? lastJsonError = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonElement root;
            try
            {
                root = JsonSerializer.Deserialize<JsonElement>(line);
            }
            catch (JsonException ex)
            {
                lastJsonError = ex;
                CliJson.TraceException(ex);
                continue;
            }

            jsonLineCount++;
            sessionId ??= CliJson.FindExplicitSessionId(root);
            sessionId ??= CliJson.FindCopilotResumeHint(line);
            var text = ReadAssistantText(root);
            if (!string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text);
            }
        }

        if (jsonLineCount == 0)
        {
            throw new InvalidOperationException(
                "GitHub Copilot CLI stdout did not contain JSONL objects.",
                lastJsonError);
        }

        sessionId ??= CliJson.FindCopilotResumeHint(stdout);
        var outputText = texts.Count > 0 ? string.Join("\n", texts) : stdout.Trim();
        return new ParsedCliOutput(sessionId ?? string.Empty, outputText);
    }

    private static string? ReadAssistantText(JsonElement root)
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

internal sealed record ParsedCliOutput(string SessionId, string OutputText);
