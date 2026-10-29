using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// Codex から受け取る構造化入力。caller が渡した worker task payload は再解釈しない。
/// skills 等の structured option は、対応 Driver が agent 固有形式へ変換する場合がある。
/// Model は agent 固有のモデル識別子である。null、空、空白のみは未指定で、Driver はモデル引数を付けない。
/// Facade はモデル名の対応表、自動選択、別モデルへの置換を持たない。
/// ReasoningEffort と Fast は起動設定であり、prompt 本文からは推測しない。
/// RunMode が null のときは従来の通常実行である。指定できる値は plan だけである。
/// </summary>
public sealed record AgentRunRequest(
    string Agent,
    string Prompt,
    string WorkingDirectory,
    string? SessionId,
    IReadOnlyList<string>? Skills,
    bool AutoApprove = true,
    string? Model = null,
    string? ReasoningEffort = null,
    bool? Fast = null,
    string? RunMode = null);

/// <summary>
/// 強制終了の公開状態。例外本文やログ行は含めない。
/// </summary>
public static class ProcessTerminationStatus
{
    public const string Exited = "exited";
    public const string Failed = "failed";
    public const string Unconfirmed = "unconfirmed";
}

/// <summary>
/// 子プロセスを止めた結果。outputRecoveryCompleted は stdout/stderr の読取が終わったことだけを示す。
/// </summary>
public sealed record ProcessTerminationInfo(
    string Status,
    bool OutputRecoveryCompleted);

/// <summary>
/// Grok 内部ログで照合した完了根拠。識別子と時刻とバージョンだけを公開する。
/// </summary>
public sealed record GrokCompletionEvidence(
    int Pid,
    string SessionId,
    string PromptId,
    DateTimeOffset CompletedAt,
    string GrokVersion);

/// <summary>
/// CLI から得た結果。独自セマンティクスは持たせず、Driver が読めた範囲だけを返す。
/// ExitCode は取得できたプロセス終了コードだけを入れる。未取得の 0 は作らない。
/// </summary>
public sealed record AgentRunResult(
    string Agent,
    string SessionId,
    int? ExitCode,
    string OutputText,
    string RawOutput,
    string RunId,
    string EventsLogPath,
    string TextLogPath,
    string OutputKind = "assistant_transcript",
    string? CompletionReason = null,
    GrokCompletionEvidence? CompletionEvidence = null,
    ProcessTerminationInfo? ProcessTermination = null,
    string? PlanPath = null,
    string? SessionUrl = null,
    string? InputPrompt = null,
    string? RemotePhase = null,
    IReadOnlyList<AgentJobArtifact>? Artifacts = null);

internal sealed record ParsedCliOutput(
    string SessionId,
    string OutputText,
    string OutputKind = "assistant_transcript");

/// <summary>
/// agent job の人間向け run log を観測用プロセスで開く。失敗しても job は継続する。
/// </summary>
public interface IRunLogViewerLauncher
{
    void TryLaunch(string jobId, string textLogPath, string eventsLogPath);
}

public sealed class NullRunLogViewerLauncher : IRunLogViewerLauncher
{
    public static NullRunLogViewerLauncher Instance { get; } = new();

    public void TryLaunch(string jobId, string textLogPath, string eventsLogPath)
    {
    }
}

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
    public const string CodexAgent = "codex";
    public const string AntigravityAgent = "antigravity";
    public const string JulesAgent = "jules";

    private readonly GitHubCopilotDriver _gitHubCopilot;
    private readonly GrokBuildDriver _grokBuild;
    private readonly CursorCliDriver _cursorCli;
    private readonly CodexCliDriver _codexCli;
    private readonly AntigravityCliDriver? _antigravityCli;
    private readonly JulesDriver? _jules;
    private readonly IAgentRunLogFactory _runLogFactory;
    private readonly IRunLogViewerLauncher _runLogViewerLauncher;

    public AgentFacade(
        GitHubCopilotDriver gitHubCopilot,
        GrokBuildDriver grokBuild,
        CursorCliDriver cursorCli,
        CodexCliDriver codexCli,
        IAgentRunLogFactory runLogFactory,
        IRunLogViewerLauncher? runLogViewerLauncher = null,
        AntigravityCliDriver? antigravityCli = null,
        JulesDriver? jules = null)
    {
        _gitHubCopilot = gitHubCopilot;
        _grokBuild = grokBuild;
        _cursorCli = cursorCli;
        _codexCli = codexCli;
        _antigravityCli = antigravityCli;
        _jules = jules;
        _runLogFactory = runLogFactory;
        _runLogViewerLauncher = runLogViewerLauncher ?? NullRunLogViewerLauncher.Instance;
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
        TryLaunchRunLogViewer(log);
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
                CodexAgent => await _codexCli.RunAsync(request, log, onStdoutLine, cancellationToken)
                    .ConfigureAwait(false),
                AntigravityAgent => await RequireAntigravity().RunAsync(request, log, onStdoutLine, cancellationToken)
                    .ConfigureAwait(false),
                JulesAgent => throw new ArgumentException("The jules agent is available only through the MCP job host."),
                _ => throw new ArgumentException($"Unknown agent '{request.Agent}'. Supported agents: {GitHubCopilotAgent}, {GrokBuildAgent}, {CursorAgent}, {CodexAgent}, {AntigravityAgent}, {JulesAgent}."),
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

    internal async Task<JulesRunOutcome> RunJulesAsync(
        AgentRunRequest request,
        JulesRunContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        Validate(request);
        if (!string.Equals(request.Agent.Trim(), JulesAgent, StringComparison.Ordinal))
        {
            throw new ArgumentException("Jules execution was requested for a different agent.");
        }

        var jules = RequireJules();
        await using var log = context.Resume is null
            ? _runLogFactory.Start(request, context.JobId)
            : NullAgentRunLog.Instance;
        if (context.Resume is null)
        {
            TryLaunchRunLogViewer(log);
        }

        try
        {
            var outcome = await jules.RunAsync(request, log, context, cancellationToken).ConfigureAwait(false);
            if (outcome is JulesCompletedOutcome completed)
            {
                log.WriteCompleted(completed.Result);
            }
            else if (outcome is JulesWaitingOutcome waiting)
            {
                log.WriteCompleted(waiting.Result);
            }
            else if (outcome is JulesFailedOutcome failed)
            {
                log.WriteFailed(failed.Exception);
            }

            return outcome;
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

    private AntigravityCliDriver RequireAntigravity()
    {
        return _antigravityCli ?? throw new InvalidOperationException("Antigravity driver is not configured.");
    }

    private JulesDriver RequireJules()
    {
        return _jules ?? throw new InvalidOperationException("Jules driver is not configured.");
    }

    private void TryLaunchRunLogViewer(IAgentRunLog log)
    {
        try
        {
            _runLogViewerLauncher.TryLaunch(log.RunId, log.TextLogPath, log.EventsPath);
        }
        catch (Exception ex)
        {
            // viewer の失敗は execution の失敗へ昇格させない。
            CliJson.TraceException(ex);
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

        if (request.Skills is not null)
        {
            foreach (var skill in request.Skills)
            {
                if (string.IsNullOrWhiteSpace(skill))
                {
                    throw new ArgumentException("skill name is required.");
                }
            }
        }

        // fingerprint は行単位である。改行を含む model は別フィールドと衝突し、CLI の1引数としても渡せない。
        if (request.Model is not null
            && (request.Model.Contains('\r')
                || request.Model.Contains('\n')
                || (!string.IsNullOrWhiteSpace(request.Model) && request.Model.Any(char.IsControl))))
        {
            throw new ArgumentException("model must not contain control characters.");
        }

        if (request.ReasoningEffort is not null
            && (string.IsNullOrWhiteSpace(request.ReasoningEffort)
                || request.ReasoningEffort.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
        {
            throw new ArgumentException("reasoning_effort must be a non-empty token containing only ASCII letters, digits, hyphens, or underscores.");
        }

        var agent = request.Agent.Trim();
        if ((agent is GitHubCopilotAgent or GrokBuildAgent or AntigravityAgent or JulesAgent)
            && request.Fast is not null)
        {
            throw new ArgumentException($"The {agent} agent does not support an independent fast option.");
        }

        if ((agent is AntigravityAgent or JulesAgent)
            && request.Skills is not null
            && request.Skills.Count > 0)
        {
            throw new ArgumentException($"The {agent} agent does not support skills.");
        }

        if (agent == JulesAgent)
        {
            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                throw new ArgumentException("The jules agent does not support an explicit model.");
            }

            if (request.ReasoningEffort is not null)
            {
                throw new ArgumentException("The jules agent does not support reasoning_effort.");
            }

            if (!request.AutoApprove)
            {
                throw new ArgumentException("The jules agent does not support auto_approve=false.");
            }
        }

        if (request.Agent.Trim() == CursorAgent
            && (request.ReasoningEffort is not null || request.Fast is not null))
        {
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                throw new ArgumentException("Cursor requires an explicit model when reasoning_effort or fast is specified.");
            }

            CursorModelCatalog.RejectMalformedModel(request.Model, request.ReasoningEffort, request.Fast);
        }

        if (request.Agent.Trim() == GitHubCopilotAgent
            && request.ReasoningEffort is not null
            && !GitHubCopilotDriver.SupportedReasoningEfforts.Contains(request.ReasoningEffort, StringComparer.Ordinal))
        {
            throw new ArgumentException("GitHub Copilot reasoning_effort must be one of: none, minimal, low, medium, high, xhigh, max.");
        }

        if (request.RunMode is not null)
        {
            if (!string.Equals(request.RunMode, "plan", StringComparison.Ordinal))
            {
                throw new ArgumentException("run_mode must be omitted or \"plan\".");
            }

            if (agent is not GrokBuildAgent and not AntigravityAgent)
            {
                throw new ArgumentException("The " + agent + " agent does not support run_mode plan.");
            }
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

    internal static AgentJobFailure CreateFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Data[FailureKindKey] is string kind)
        {
            var exitCode = exception.Data[FailureExitCodeKey] is int code ? code : (int?)null;
            var summary = exception.Data[FailureSummaryKey] as string ?? exception.Message;
            return new AgentJobFailure(
                kind,
                FailureSummary(summary),
                exitCode,
                exception.Data[FailureRunIdKey] as string,
                exception.Data[FailureEventsLogPathKey] as string,
                exception.Data[FailureTextLogPathKey] as string);
        }

        return new AgentJobFailure(
            "internal_error",
            FailureSummary(exception.Message),
            null,
            exception.Data[FailureRunIdKey] as string,
            exception.Data[FailureEventsLogPathKey] as string,
            exception.Data[FailureTextLogPathKey] as string);
    }

    internal static string FailureSummary(string summary)
    {
        var oneLine = SecretRedactor.RedactText(summary)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return oneLine.Length <= 512 ? oneLine : oneLine[..512];
    }
}
