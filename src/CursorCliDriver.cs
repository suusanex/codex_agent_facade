using System.Text.Json;

/// <summary>
/// Cursor Agent CLI の non-interactive 経路へ変換する Driver。
/// PATH 上の実行ファイル名は <c>cursor-agent</c> を使う。<c>agent</c> は Grok Build と衝突するため採用しない。
/// Windows では <c>cursor-agent.cmd</c> を避け、公式の <c>cursor-agent.ps1</c> を起動する。
/// cmd 経路は CR/LF を引数へ渡せない。
/// </summary>
public sealed class CursorCliDriver
{
    internal static readonly string FileName = OperatingSystem.IsWindows()
        ? "cursor-agent.ps1"
        : "cursor-agent";

    private readonly IProcessRunner _processRunner;

    public CursorCliDriver(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IAgentRunLog runLog,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
        var selectedModel = await ResolveSelectedModelAsync(request, cancellationToken).ConfigureAwait(false);
        var arguments = BuildArguments(request, selectedModel);
        runLog.WriteStarted(new AgentRunStartedInfo(
            Agent: AgentFacade.CursorAgent,
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

        var accumulator = new CursorStreamAccumulator(runLog);
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
            CliJson.MarkFailure(ex, "internal_error");
            throw;
        }

        if (processResult.ExitCode != 0)
        {
            var message = SecretRedactor.RedactText(
                $"Cursor CLI exited with code {processResult.ExitCode}. stdout: {processResult.StandardOutput} stderr: {processResult.StandardError}");
            var failure = new InvalidOperationException(message);
            CliJson.MarkFailure(
                failure,
                "non_zero_exit",
                $"Cursor CLI exited with code {processResult.ExitCode}. stderr: {processResult.StandardError}",
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
            CliJson.MarkFailure(ex, "output_parse_failed");
            throw;
        }
        return new AgentRunResult(
            Agent: AgentFacade.CursorAgent,
            SessionId: parsed.SessionId,
            ExitCode: processResult.ExitCode,
            OutputText: parsed.OutputText,
            OutputKind: parsed.OutputKind,
            RawOutput: processResult.StandardOutput,
            RunId: runLog.RunId,
            EventsLogPath: runLog.EventsPath,
            TextLogPath: runLog.TextLogPath);
    }

    /// <summary>
    /// Cursor CLI の headless 引数。
    /// <c>--print</c> が非対話。<c>--output-format stream-json</c> が NDJSON。
    /// <c>--trust</c> は workspace 信頼ダイアログ回避のため常に付ける。
    /// <c>--force</c> だけが <c>auto_approve</c> に対応する。Skill は prompt 変換しない。
    /// <c>reasoning_effort</c> と <c>fast</c> を両方省略した model はそのまま渡す。
    /// どちらかを指定したときは、呼び出し側でカタログ解決済みの識別子を <paramref name="selectedModel"/> に渡す。
    /// </summary>
    internal static List<string> BuildArguments(AgentRunRequest request)
    {
        if (request.ReasoningEffort is not null || request.Fast is not null)
        {
            throw new ArgumentException(
                "Cursor reasoning_effort and fast must be resolved against the model catalog before building arguments.");
        }

        return BuildArguments(request, string.IsNullOrWhiteSpace(request.Model) ? null : request.Model);
    }

    internal static List<string> BuildArguments(AgentRunRequest request, string? selectedModel)
    {
        var arguments = new List<string>
        {
            "--print",
            "--output-format",
            "stream-json",
            "--trust",
            "--workspace",
            request.WorkingDirectory,
        };

        if (request.AutoApprove)
        {
            arguments.Add("--force");
        }

        if (!string.IsNullOrWhiteSpace(selectedModel))
        {
            arguments.Add("--model");
            arguments.Add(selectedModel);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            arguments.Add("--resume");
            arguments.Add(request.SessionId);
        }

        arguments.Add(request.Prompt);
        return arguments;
    }

    private async Task<string?> ResolveSelectedModelAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        if (request.ReasoningEffort is null && request.Fast is null)
        {
            return string.IsNullOrWhiteSpace(request.Model) ? null : request.Model;
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ArgumentException("Cursor requires an explicit model when reasoning_effort or fast is specified.");
        }

        var catalog = await ReadModelCatalogAsync(request.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        return CursorModelCatalog.Resolve(catalog, request.Model, request.ReasoningEffort, request.Fast);
    }

    private async Task<IReadOnlyList<string>> ReadModelCatalogAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessRunResult listed;
        try
        {
            listed = await _processRunner.RunAsync(
                new ProcessRunRequest(
                    FileName: FileName,
                    Arguments: ["models"],
                    WorkingDirectory: workingDirectory),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw new ArgumentException("Cursor model catalog could not be read.", ex);
        }

        if (listed.ExitCode != 0)
        {
            var failure = new ArgumentException(
                "Cursor model catalog could not be read. " + CliJson.FailureSummary(listed.StandardError));
            CliJson.TraceException(failure);
            throw failure;
        }

        var ids = CursorModelCatalog.ParseIds(listed.StandardOutput);
        if (ids.Count == 0)
        {
            var failure = new ArgumentException("Cursor model catalog did not list any model ids.");
            CliJson.TraceException(failure);
            throw failure;
        }

        return ids;
    }
}

/// <summary>
/// 実行直前の <c>cursor-agent models</c> から、基底・effort・fast に一致する識別子を1つ選ぶ。
/// 一覧に無い組み合わせは起動前に失敗し、別系統のモデルへは替えない。
/// </summary>
internal static class CursorModelCatalog
{
    private static readonly string[] SeedEfforts =
    [
        "extra-high",
        "minimal",
        "medium",
        "xhigh",
        "ultra",
        "none",
        "high",
        "max",
        "low",
    ];

    internal static void RejectMalformedModel(string model, string? reasoningEffort, bool? fast)
    {
        var requested = ParseRequestedModel(model, SeedEfforts.OrderByDescending(effort => effort.Length).ToArray());
        if (reasoningEffort is not null && requested.Effort is not null)
        {
            throw new ArgumentException("reasoning_effort is specified both in the Cursor model and as an independent field.");
        }

        if (fast is not null && requested.Fast is not null)
        {
            throw new ArgumentException("fast is specified both in the Cursor model and as an independent field.");
        }
    }

    internal static IReadOnlyList<string> ParseIds(string modelsOutput)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in modelsOutput.Split(['\r', '\n']))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var id = line[..separator].Trim();
            if (!IsModelId(id) || !seen.Add(id))
            {
                continue;
            }

            ids.Add(id);
        }

        return ids;
    }

    internal static string Resolve(
        IReadOnlyList<string> catalogIds,
        string model,
        string? reasoningEffort,
        bool? fast)
    {
        if (reasoningEffort is not null && !IsSafeToken(reasoningEffort))
        {
            throw new ArgumentException("Cursor reasoning_effort must be a non-empty ASCII token.");
        }

        var efforts = DiscoverEfforts(catalogIds);
        var requested = ParseRequestedModel(model, efforts);
        if (reasoningEffort is not null && requested.Effort is not null)
        {
            throw new ArgumentException("reasoning_effort is specified both in the Cursor model and as an independent field.");
        }

        if (fast is not null && requested.Fast is not null)
        {
            throw new ArgumentException("fast is specified both in the Cursor model and as an independent field.");
        }

        var targetEffort = reasoningEffort ?? requested.Effort;
        var targetFast = fast ?? requested.Fast ?? false;
        var matches = new List<CatalogModel>();
        foreach (var candidate in SplitCatalog(catalogIds, efforts))
        {
            if (!string.Equals(candidate.Base, requested.Base, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!EffortEquals(candidate.Effort, targetEffort) || candidate.Fast != targetFast)
            {
                continue;
            }

            matches.Add(candidate);
        }

        if (matches.Count == 1)
        {
            return matches[0].Id;
        }

        var siblings = SplitCatalog(catalogIds, efforts)
            .Where(candidate => string.Equals(candidate.Base, requested.Base, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Id)
            .ToArray();
        var available = siblings.Length == 0 ? "none" : string.Join(", ", siblings);
        var outcome = matches.Count == 0 ? "no id" : "multiple ids";
        throw new ArgumentException(
            "Cursor model catalog has "
            + outcome
            + " for model '"
            + requested.Base
            + "' with reasoning_effort '"
            + (targetEffort ?? "")
            + "' and fast '"
            + (targetFast ? "true" : "false")
            + "'. Available ids: "
            + available
            + ".");
    }

    private static RequestedModel ParseRequestedModel(string model, IReadOnlyList<string> efforts)
    {
        var modelId = model.Trim();
        string? bracketEffort = null;
        bool? bracketFast = null;
        var openBracket = modelId.IndexOf('[');
        if (openBracket >= 0 || modelId.Contains(']', StringComparison.Ordinal))
        {
            var closeBracket = modelId.EndsWith(']') ? modelId.Length - 1 : -1;
            if (openBracket <= 0
                || closeBracket < openBracket
                || modelId.IndexOf('[', StringComparison.Ordinal) != openBracket
                || modelId.IndexOf(']', StringComparison.Ordinal) != closeBracket)
            {
                throw new ArgumentException("Cursor model parameter syntax is invalid.");
            }

            var parameterText = modelId[(openBracket + 1)..closeBracket];
            modelId = modelId[..openBracket].Trim();
            if (modelId.Length == 0)
            {
                throw new ArgumentException("Cursor model id must not be empty.");
            }

            if (parameterText.Length > 0)
            {
                foreach (var parameter in parameterText.Split(','))
                {
                    var separator = parameter.IndexOf('=');
                    if (separator <= 0)
                    {
                        throw new ArgumentException("Cursor model parameters must use key=value syntax.");
                    }

                    var key = parameter[..separator];
                    var value = parameter[(separator + 1)..];
                    if (!IsSafeToken(key) || !IsSafeToken(value))
                    {
                        throw new ArgumentException("Cursor model parameter keys and values must be non-empty ASCII tokens.");
                    }

                    if (key.Equals("effort", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bracketEffort is not null)
                        {
                            throw new ArgumentException("Cursor model parameter 'effort' is specified more than once.");
                        }

                        bracketEffort = value;
                    }
                    else if (key.Equals("fast", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bracketFast is not null)
                        {
                            throw new ArgumentException("Cursor model parameter 'fast' is specified more than once.");
                        }

                        if (!bool.TryParse(value, out var parsedFast))
                        {
                            throw new ArgumentException("Cursor model parameter 'fast' must be true or false.");
                        }

                        bracketFast = parsedFast;
                    }
                    else
                    {
                        throw new ArgumentException(
                            "Cursor model parameters other than effort and fast cannot be combined with reasoning_effort or fast.");
                    }
                }
            }
        }

        var split = SplitId(modelId, efforts);
        return new RequestedModel(
            split.Base,
            bracketEffort ?? split.Effort,
            bracketFast ?? (split.Fast ? true : null));
    }

    private static IReadOnlyList<string> DiscoverEfforts(IReadOnlyList<string> catalogIds)
    {
        var efforts = new HashSet<string>(SeedEfforts, StringComparer.OrdinalIgnoreCase);
        var parentsByToken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var idSet = new HashSet<string>(catalogIds, StringComparer.OrdinalIgnoreCase);
        foreach (var id in catalogIds)
        {
            var core = id.EndsWith("-fast", StringComparison.OrdinalIgnoreCase) ? id[..^5] : id;
            var dash = core.LastIndexOf('-');
            if (dash <= 0 || dash == core.Length - 1)
            {
                continue;
            }

            var token = core[(dash + 1)..];
            var parent = core[..dash];
            if (!IsSafeToken(token))
            {
                continue;
            }

            if (!idSet.Contains(parent + "-" + token) || !idSet.Contains(parent + "-" + token + "-fast"))
            {
                continue;
            }

            if (!parentsByToken.TryGetValue(token, out var parents))
            {
                parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                parentsByToken.Add(token, parents);
            }

            parents.Add(parent);
        }

        foreach (var pair in parentsByToken)
        {
            if (pair.Value.Count >= 2)
            {
                efforts.Add(pair.Key);
            }
        }

        return efforts.OrderByDescending(effort => effort.Length).ToArray();
    }

    private static IEnumerable<CatalogModel> SplitCatalog(IReadOnlyList<string> catalogIds, IReadOnlyList<string> efforts)
    {
        foreach (var id in catalogIds)
        {
            var split = SplitId(id, efforts);
            yield return new CatalogModel(id, split.Base, split.Effort, split.Fast);
        }
    }

    private static (string Base, string? Effort, bool Fast) SplitId(string id, IReadOnlyList<string> efforts)
    {
        var fast = false;
        var core = id;
        if (core.EndsWith("-fast", StringComparison.OrdinalIgnoreCase))
        {
            fast = true;
            core = core[..^5];
        }

        foreach (var effort in efforts)
        {
            var suffix = "-" + effort;
            if (core.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && core.Length > suffix.Length)
            {
                return (core[..^suffix.Length], effort, fast);
            }
        }

        return (core, null, fast);
    }

    private static bool EffortEquals(string? catalogEffort, string? requestedEffort)
    {
        if (requestedEffort is null)
        {
            return catalogEffort is null;
        }

        return catalogEffort is not null
            && catalogEffort.Equals(requestedEffort, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsModelId(string value)
    {
        return value.Length > 0
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static bool IsSafeToken(string value)
    {
        return value.Length > 0
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private sealed record RequestedModel(string Base, string? Effort, bool? Fast);

    private sealed record CatalogModel(string Id, string Base, string? Effort, bool Fast);
}

/// <summary>
/// Cursor <c>stream-json</c> の NDJSON を 1 パスで蓄積する。終了後に stdout 全体は再 parse しない。
/// session ID は明示フィールド <c>session_id</c> / <c>sessionId</c> だけを採用する。
/// <c>request_id</c> や任意 UUID は使わない。
/// </summary>
internal sealed class CursorStreamAccumulator
{
    private readonly IAgentRunLog _runLog;
    private readonly List<string> _assistantTexts = [];
    private readonly List<string> _assistantGroups = [];
    private string? _sessionId;
    private string? _resultText;
    private int _jsonEventCount;
    private bool _sawNonWhitespace;
    private bool _sawProtocolViolation;
    private bool _toolSinceAssistant;
    private JsonException? _lastJsonError;

    public CursorStreamAccumulator(IAgentRunLog runLog)
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
            _sawProtocolViolation = true;
            CliJson.TraceException(ex);
            _runLog.WriteProcessLine("stdout", line);
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            _sawProtocolViolation = true;
            _runLog.WriteProcessLine("stdout", line);
            return;
        }

        _jsonEventCount++;
        _sessionId ??= CliJson.FindExplicitSessionId(root);
        var type = CliJson.FindFirstString(root, "type") ?? "unknown";

        if (string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            HandleAssistant(root, type);
            return;
        }

        if (string.Equals(type, "result", StringComparison.OrdinalIgnoreCase))
        {
            HandleResult(root, type);
            return;
        }

        if (string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "thought", StringComparison.OrdinalIgnoreCase))
        {
            var thought = CursorCliOutputParser.ReadThinkingText(root);
            _runLog.WriteAgentEvent(type, root, humanSummary: null);
            if (!string.IsNullOrEmpty(thought))
            {
                _runLog.AppendHumanFragment("thought", thought);
            }

            return;
        }

        if (string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase)
            || AgentLogSummary.LooksLikeTool(type, root))
        {
            _toolSinceAssistant = _assistantGroups.Count > 0;
        }

        _runLog.WriteAgentEvent(type, root, CursorCliHumanSummary.Format(type, root, assistantText: null));
    }

    public ParsedCliOutput Complete()
    {
        if (!_sawNonWhitespace)
        {
            throw new InvalidOperationException("Cursor CLI returned empty stdout.");
        }

        if (_sawProtocolViolation)
        {
            throw new InvalidOperationException(
                "Cursor CLI stdout contained a line that was not a JSON object event.",
                _lastJsonError);
        }

        if (_jsonEventCount == 0)
        {
            throw new InvalidOperationException(
                "Cursor CLI stdout did not contain JSON objects.",
                _lastJsonError);
        }

        var (outputText, outputKind) = SelectPublicOutput();
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new InvalidOperationException("Cursor CLI JSON did not contain a recognized response field.");
        }

        return new ParsedCliOutput(
            _sessionId ?? string.Empty,
            outputText,
            outputKind);
    }

    private (string OutputText, string OutputKind) SelectPublicOutput()
    {
        if (string.IsNullOrWhiteSpace(_resultText))
        {
            return (string.Join("\n", _assistantTexts), "assistant_transcript");
        }

        if (_assistantTexts.Count == 0)
        {
            return (_resultText, "assistant_transcript");
        }

        var finalAssistantText = _assistantGroups[^1];
        var assistantTranscript = string.Concat(_assistantTexts);
        if (string.Equals(_resultText, finalAssistantText, StringComparison.Ordinal)
            || string.Equals(_resultText, assistantTranscript, StringComparison.Ordinal))
        {
            return (finalAssistantText, "final_response");
        }

        // resultの格納場所だけでは本文が最終報告のみとは断定できないため、情報を保持して限界を明示する。
        return (_resultText, "assistant_transcript");
    }

    private void HandleAssistant(JsonElement root, string type)
    {
        if (CursorCliOutputParser.IsDuplicateAssistantFlush(root))
        {
            _runLog.WriteAgentEvent(type, root, "assistant flush (duplicate)");
            return;
        }

        var assistantText = CursorCliOutputParser.ReadAssistantText(root);
        _runLog.WriteAgentEvent(type, root, humanSummary: null);
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return;
        }

        _runLog.AppendHumanFragment("assistant", assistantText);
        if (_assistantGroups.Count == 0 || _toolSinceAssistant)
        {
            _assistantGroups.Add(assistantText);
        }
        else
        {
            _assistantGroups[^1] += assistantText;
        }

        _toolSinceAssistant = false;
        if (CursorCliOutputParser.IsStreamingDelta(root)
            && _assistantTexts.Count > 0)
        {
            _assistantTexts[^1] += assistantText;
            return;
        }

        _assistantTexts.Add(assistantText);
    }

    private void HandleResult(JsonElement root, string type)
    {
        var resultText = CursorCliOutputParser.ReadResultText(root);
        if (!string.IsNullOrWhiteSpace(resultText))
        {
            _resultText = resultText;
        }

        _runLog.WriteAgentEvent(type, root, CursorCliHumanSummary.Format(type, root, assistantText: null));
    }
}

internal static class CursorCliHumanSummary
{
    public static string Format(string type, JsonElement root, string? assistantText)
    {
        if (!string.IsNullOrWhiteSpace(assistantText)
            && (string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "result", StringComparison.OrdinalIgnoreCase)))
        {
            return "assistant: " + assistantText;
        }

        if (string.Equals(type, "result", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "system", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "user", StringComparison.OrdinalIgnoreCase))
        {
            var subtype = CliJson.FindFirstString(root, "subtype") ?? string.Empty;
            return type
                + (string.IsNullOrEmpty(subtype) ? string.Empty : " " + subtype)
                + " sessionId="
                + (CliJson.FindExplicitSessionId(root) ?? string.Empty);
        }

        if (string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase)
            || AgentLogSummary.LooksLikeTool(type, root))
        {
            var subtype = CliJson.FindFirstString(root, "subtype") ?? string.Empty;
            var started = string.Equals(subtype, "started", StringComparison.OrdinalIgnoreCase)
                || (!subtype.Contains("update", StringComparison.OrdinalIgnoreCase)
                    && !subtype.Contains("completed", StringComparison.OrdinalIgnoreCase)
                    && !subtype.Contains("result", StringComparison.OrdinalIgnoreCase));
            var described = AgentLogSummary.DescribeTool(root, started);
            var cursorTool = CursorCliOutputParser.ReadToolName(root);
            if (string.IsNullOrWhiteSpace(cursorTool))
            {
                return described;
            }

            return described + " " + cursorTool;
        }

        if (AgentLogSummary.LooksLikeMode(type))
        {
            return AgentLogSummary.DescribeModeOrLifecycle(type, root);
        }

        return AgentLogSummary.DescribeGeneric(type, root);
    }
}

internal static class CursorCliOutputParser
{
    internal static bool IsDuplicateAssistantFlush(JsonElement root)
    {
        // stream-partial-output の buffered flush。model_call_id がある行は本文の重複なので捨てる。
        return HasProperty(root, "model_call_id");
    }

    internal static bool IsStreamingDelta(JsonElement root)
    {
        return HasProperty(root, "timestamp_ms") && !HasProperty(root, "model_call_id");
    }

    internal static string? ReadAssistantText(JsonElement root)
    {
        if (CliJson.TryGetPropertyIgnoreCase(root, "message", out var message)
            && message.ValueKind == JsonValueKind.Object)
        {
            var fromMessage = CliJson.FindFirstString(message, "content", "text");
            if (!string.IsNullOrWhiteSpace(fromMessage))
            {
                return fromMessage;
            }
        }

        if (CliJson.TryGetPropertyIgnoreCase(root, "data", out var data))
        {
            var fromData = CliJson.FindFirstString(data, "content", "text", "message");
            if (!string.IsNullOrWhiteSpace(fromData))
            {
                return fromData;
            }
        }

        return CliJson.FindFirstString(root, "text", "content");
    }

    internal static string? ReadResultText(JsonElement root)
    {
        return CliJson.FindFirstString(root, "result");
    }

    internal static string? ReadThinkingText(JsonElement root)
    {
        if (CliJson.TryGetPropertyIgnoreCase(root, "message", out var message)
            && message.ValueKind == JsonValueKind.Object)
        {
            var fromMessage = CliJson.FindFirstString(message, "content", "text");
            if (!string.IsNullOrWhiteSpace(fromMessage))
            {
                return fromMessage;
            }
        }

        return CliJson.FindFirstString(root, "text", "content", "data");
    }

    internal static string? ReadToolName(JsonElement root)
    {
        if (!CliJson.TryGetPropertyIgnoreCase(root, "tool_call", out var toolCall)
            || toolCall.ValueKind != JsonValueKind.Object)
        {
            return CliJson.FindFirstString(root, "call_id");
        }

        foreach (var property in toolCall.EnumerateObject())
        {
            if (string.Equals(property.Name, "function", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                return CliJson.FindFirstString(property.Value, "name") ?? property.Name;
            }

            const string suffix = "ToolCall";
            if (property.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && property.Name.Length > suffix.Length)
            {
                return property.Name[..^suffix.Length];
            }

            return property.Name;
        }

        return null;
    }

    private static bool HasProperty(JsonElement root, string name)
    {
        return CliJson.TryGetPropertyIgnoreCase(root, name, out _);
    }
}
