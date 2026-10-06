using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// Codex から受け取る構造化入力。caller が渡した worker task payload は再解釈しない。
/// skills 等の structured option は、対応 Driver が agent 固有形式へ変換する場合がある。
/// Model は agent 固有のモデル識別子である。null、空、空白のみは未指定で、Driver はモデル引数を付けない。
/// Facade はモデル名の対応表、自動選択、別モデルへの置換を持たない。
/// </summary>
public sealed record AgentRunRequest(
    string Agent,
    string Prompt,
    string WorkingDirectory,
    string? SessionId,
    IReadOnlyList<string>? Skills,
    bool AutoApprove = true,
    string? Model = null);

/// <summary>
/// CLI から得た結果。独自セマンティクスは持たせず、Driver が読めた範囲だけを返す。
/// </summary>
public sealed record AgentRunResult(
    string Agent,
    string SessionId,
    int ExitCode,
    string OutputText,
    string RawOutput,
    string RunId,
    string EventsLogPath,
    string TextLogPath,
    string OutputKind = "assistant_transcript");

internal sealed record ParsedCliOutput(
    string SessionId,
    string OutputText,
    string OutputKind = "assistant_transcript");

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
    public const string CursorAgent = "cursor";

    private readonly GitHubCopilotDriver _gitHubCopilot;
    private readonly GrokBuildDriver _grokBuild;
    private readonly CursorCliDriver _cursorCli;
    private readonly IAgentRunLogFactory _runLogFactory;

    public AgentFacade(
        GitHubCopilotDriver gitHubCopilot,
        GrokBuildDriver grokBuild,
        CursorCliDriver cursorCli,
        IAgentRunLogFactory runLogFactory)
    {
        _gitHubCopilot = gitHubCopilot;
        _grokBuild = grokBuild;
        _cursorCli = cursorCli;
        _runLogFactory = runLogFactory;
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken,
        string? runId = null)
    {
        try
        {
            Validate(request);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw;
        }

        await using var log = string.IsNullOrWhiteSpace(runId)
            ? _runLogFactory.Start(request)
            : _runLogFactory.Start(request, runId);
        try
        {
            var result = request.Agent.Trim() switch
            {
                GitHubCopilotAgent => await _gitHubCopilot.RunAsync(request, log, onStdoutLine, cancellationToken)
                    .ConfigureAwait(false),
                GrokBuildAgent => await _grokBuild.RunAsync(request, log, onStdoutLine, cancellationToken)
                    .ConfigureAwait(false),
                CursorAgent => await _cursorCli.RunAsync(request, log, onStdoutLine, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown agent '{request.Agent}'. Supported agents: {GitHubCopilotAgent}, {GrokBuildAgent}, {CursorAgent}."),
            };

            var withLogs = result with
            {
                RunId = log.RunId,
                EventsLogPath = log.EventsPath,
                TextLogPath = log.TextLogPath,
            };
            log.WriteCompleted(withLogs);
            return withLogs;
        }
        catch (OperationCanceledException ex)
        {
            CliJson.TraceException(ex);
            log.WriteCancelled();
            throw;
        }
        catch (Exception ex)
        {
            CliJson.AttachFailureLog(ex, log);
            CliJson.TraceException(ex);
            log.WriteFailed(ex);
            throw;
        }
    }

    internal static void Validate(AgentRunRequest request)
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

        // fingerprint は行単位である。改行を含む model は別フィールドと衝突し、CLI の1引数としても渡せない。
        if (request.Model is not null
            && (request.Model.Contains('\r') || request.Model.Contains('\n')))
        {
            throw new ArgumentException("model must not contain line breaks.");
        }
    }
}

internal static class CliJson
{
    internal const string FailureKindKey = "CodexAgentFacade.FailureKind";
    internal const string FailureExitCodeKey = "CodexAgentFacade.FailureExitCode";
    internal const string FailureSummaryKey = "CodexAgentFacade.FailureSummary";
    internal const string FailureRunIdKey = "CodexAgentFacade.FailureRunId";
    internal const string FailureEventsLogPathKey = "CodexAgentFacade.FailureEventsLogPath";
    internal const string FailureTextLogPathKey = "CodexAgentFacade.FailureTextLogPath";

    public static void MarkFailure(
        Exception exception,
        string kind,
        string? summary = null,
        int? exitCode = null)
    {
        exception.Data[FailureKindKey] = kind;
        if (summary is not null)
        {
            exception.Data[FailureSummaryKey] = summary;
        }

        if (exitCode is not null)
        {
            exception.Data[FailureExitCodeKey] = exitCode.Value;
        }
    }

    public static void AttachFailureLog(Exception exception, IAgentRunLog log)
    {
        exception.Data[FailureRunIdKey] = log.RunId;
        exception.Data[FailureEventsLogPathKey] = log.EventsPath;
        exception.Data[FailureTextLogPathKey] = log.TextLogPath;
    }

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
        FacadeLog.Exception(exception);
    }
}
