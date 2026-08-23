using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// Codex から受け取る構造化入力。prompt 本文は再構成しない。
/// </summary>
public sealed record AgentRunRequest(
    string Agent,
    string Prompt,
    string WorkingDirectory,
    string? SessionId,
    IReadOnlyList<string>? Skills,
    bool AutoApprove = true);

/// <summary>
/// CLI から得た結果。独自セマンティクスは持たせず、Driver が読めた範囲だけを返す。
/// </summary>
public sealed record AgentRunResult(
    string Agent,
    string SessionId,
    int ExitCode,
    string OutputText,
    string RawOutput);

internal static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

/// <summary>
/// MCP tool 入口の共通ルーティング。agent 固有の CLI 変換は各 Driver に委譲する。
/// </summary>
public sealed class AgentFacade
{
    public const string GitHubCopilotAgent = "github-copilot";
    public const string GrokBuildAgent = "grok-build";

    private readonly GitHubCopilotDriver _gitHubCopilot;
    private readonly GrokBuildDriver _grokBuild;

    public AgentFacade(GitHubCopilotDriver gitHubCopilot, GrokBuildDriver grokBuild)
    {
        _gitHubCopilot = gitHubCopilot;
        _grokBuild = grokBuild;
    }

    public Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
        Validate(request);

        return request.Agent.Trim() switch
        {
            GitHubCopilotAgent => _gitHubCopilot.RunAsync(request, onStdoutLine, cancellationToken),
            GrokBuildAgent => _grokBuild.RunAsync(request, onStdoutLine, cancellationToken),
            _ => throw new ArgumentException($"Unknown agent '{request.Agent}'. Supported agents: {GitHubCopilotAgent}, {GrokBuildAgent}."),
        };
    }

    private static void Validate(AgentRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Agent))
        {
            throw new ArgumentException("agent is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("prompt is required.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            throw new ArgumentException("working_directory is required.");
        }
    }
}

internal static class CliJson
{
    public static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static string? FindFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value))
            {
                var text = ReadStringValue(value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    public static string? ReadStringValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Object when TryGetPropertyIgnoreCase(value, "text", out var nested) => ReadStringValue(nested),
            JsonValueKind.Array => JoinArrayStrings(value),
            _ => null,
        };
    }

    public static string? FindExplicitSessionId(JsonElement element)
    {
        return FindFirstString(element, "sessionId", "session_id", "sessionID");
    }

    /// <summary>
    /// Copilot CLI が出力する `copilot --resume=&lt;uuid&gt;` hint だけを読む。任意 UUID は採用しない。
    /// </summary>
    public static string? FindCopilotResumeHint(string text)
    {
        var resumeIndex = text.IndexOf("--resume", StringComparison.OrdinalIgnoreCase);
        if (resumeIndex < 0)
        {
            return null;
        }

        var rest = text[resumeIndex..];
        var match = Regex.Match(
            rest,
            @"^--resume(?:=|\s+)([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? JoinArrayStrings(JsonElement array)
    {
        var parts = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var text = ReadStringValue(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    public static void TraceException(Exception exception)
    {
        Trace.TraceError(exception.ToString());
    }
}
