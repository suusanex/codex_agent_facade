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
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
        var arguments = BuildArguments(request);
        ProcessRunResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    FileName: "grok",
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
                $"Grok Build CLI exited with code {processResult.ExitCode}. stdout: {processResult.StandardOutput} stderr: {processResult.StandardError}");
            CliJson.TraceException(failure);
            throw failure;
        }

        var parsed = GrokBuildOutputParser.Parse(processResult.StandardOutput);
        return new AgentRunResult(
            Agent: AgentFacade.GrokBuildAgent,
            SessionId: parsed.SessionId,
            ExitCode: processResult.ExitCode,
            OutputText: parsed.OutputText,
            RawOutput: processResult.StandardOutput);
    }

    internal static List<string> BuildArguments(AgentRunRequest request)
    {
        var prompt = AgentPrompt.ApplySkills(request.Prompt, request.Skills);
        var arguments = new List<string>
        {
            "--no-auto-update",
            "-p",
            prompt,
            "--cwd",
            request.WorkingDirectory,
            "--output-format",
            "json",
            "--always-approve",
        };

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            arguments.Add("--session-id");
            arguments.Add(request.SessionId);
        }

        return arguments;
    }
}

internal static class GrokBuildOutputParser
{
    public static ParsedCliOutput Parse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException("Grok Build CLI returned empty stdout.");
        }

        var json = ExtractTrailingJsonObject(stdout);
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException ex)
        {
            CliJson.TraceException(ex);
            throw new InvalidOperationException("Grok Build CLI stdout did not end with a JSON object.", ex);
        }

        var sessionId = CliJson.FindSessionId(root, stdout) ?? string.Empty;
        var outputText = CliJson.FindFirstString(root, "text", "result", "message", "output", "content", "response")
            ?? stdout.Trim();
        return new ParsedCliOutput(sessionId, outputText);
    }

    /// <summary>
    /// Grok の json 形式は「末尾の 1 JSON object」。末尾まで閉じる object の開始位置を探してから parse する。
    /// </summary>
    internal static string ExtractTrailingJsonObject(string stdout)
    {
        var trimmed = stdout.Trim();
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] != '{')
            {
                continue;
            }

            if (!IsObjectBalancedToEnd(trimmed, i))
            {
                continue;
            }

            var candidate = trimmed[i..];
            try
            {
                var element = JsonSerializer.Deserialize<JsonElement>(candidate);
                if (element.ValueKind == JsonValueKind.Object)
                {
                    return candidate;
                }
            }
            catch (JsonException ex)
            {
                CliJson.TraceException(ex);
            }
        }

        throw new InvalidOperationException("Grok Build CLI stdout did not contain a JSON object.");
    }

    private static bool IsObjectBalancedToEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        for (var j = i + 1; j < text.Length; j++)
                        {
                            if (!char.IsWhiteSpace(text[j]))
                            {
                                return false;
                            }
                        }

                        return true;
                    }

                    if (depth < 0)
                    {
                        return false;
                    }

                    break;
            }
        }

        return false;
    }
}
