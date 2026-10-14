using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 通常アプリ向けの1回実行CLI。MCPのjob保存やExact retryは持たず、
/// 同じ <see cref="AgentFacade"/> へ1件のrequestを渡して公開結果をstdoutへ書く。
/// コマンド分解用のCLIフレームワークは使わない。公開I/FはJSON 1件であり、
/// サブコマンドや診断オプションを増やすとMCP引数との対応が別実装になるため、
/// System.Text.Json と既存の実行層だけで入出力を閉じる。
/// </summary>
public static class AgentCli
{
    public const int ExitCompleted = 0;
    public const int ExitFailed = 1;
    public const int ExitInvalidRequest = 2;
    public const int ExitCancelled = 3;
    public const int ExitTimedOut = 4;

    public const string StatusTimedOut = "timed_out";

    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 86400;

    private static readonly JsonSerializerOptions InputOptions = CreateInputOptions();

    public static int WriteInvalidArguments(TextWriter standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        WriteResponse(standardOutput, Response(
            AgentJobStatus.Failed,
            result: null,
            error: "The CLI reads one JSON request from stdin and does not accept arguments.",
            failure: new AgentJobFailure(
                "invalid_request",
                "The CLI reads one JSON request from stdin and does not accept arguments.")));
        return ExitInvalidRequest;
    }

    public static int WriteCancelled(TextWriter standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        WriteResponse(standardOutput, CancelledResponse("cancelled", "The CLI run was cancelled."));
        return ExitCancelled;
    }

    public static string RunFailureError(string diagnosticLogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticLogPath);
        return "agent run failed. See the CLI diagnostic log for details: " + diagnosticLogPath.Trim();
    }

    public static string StartupFailureError(string diagnosticLogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticLogPath);
        return "Codex Agent Facade CLI failed. See the CLI diagnostic log for details: " + diagnosticLogPath.Trim();
    }

    public const string DiagnosticLogUnavailableError =
        "Codex Agent Facade CLI failed. The CLI diagnostic log could not be created.";

    public static async Task<int> ExecuteAsync(
        AgentFacade facade,
        string input,
        TextWriter standardOutput,
        CancellationToken cancellationToken,
        string diagnosticLogPath)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticLogPath);

        if (!TryParse(input, out var parsed, out var parseError))
        {
            WriteResponse(standardOutput, Invalid(parseError));
            return ExitInvalidRequest;
        }

        if (parsed.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            WriteResponse(standardOutput, Invalid(
                "timeout_seconds must be an integer from "
                + MinTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " to "
                + MaxTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "."));
            return ExitInvalidRequest;
        }

        var request = new AgentRunRequest(
            parsed.Agent ?? string.Empty,
            parsed.Prompt ?? string.Empty,
            parsed.WorkingDirectory ?? string.Empty,
            parsed.SessionId,
            parsed.Skills,
            parsed.AutoApprove,
            parsed.Model,
            parsed.ReasoningEffort,
            parsed.Fast);

        try
        {
            AgentFacade.Validate(request);
        }
        catch (ArgumentException ex)
        {
            CliJson.TraceException(ex);
            WriteResponse(standardOutput, Invalid(ex.Message));
            return ExitInvalidRequest;
        }

        if (!IsKnownAgent(request.Agent))
        {
            WriteResponse(standardOutput, Invalid(
                "Unknown agent '" + request.Agent.Trim()
                + "'. Supported agents: "
                + AgentFacade.GitHubCopilotAgent + ", "
                + AgentFacade.GrokBuildAgent + ", "
                + AgentFacade.CursorAgent + ", "
                + AgentFacade.CodexAgent + "."));
            return ExitInvalidRequest;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (parsed.TimeoutSeconds is int timeoutSeconds)
        {
            linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }

        try
        {
            var result = await facade.RunAsync(request, onStdoutLine: null, linked.Token).ConfigureAwait(false);
            WriteResponse(standardOutput, Response(
                AgentJobStatus.Completed,
                AgentJobPublicProjection.From(result),
                error: null,
                failure: null));
            return ExitCompleted;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            CliJson.TraceException(ex);
            WriteResponse(standardOutput, CancelledResponse(
                "timeout",
                "The CLI run exceeded timeout_seconds and the agent process was stopped."));
            return ExitTimedOut;
        }
        catch (OperationCanceledException ex)
        {
            CliJson.TraceException(ex);
            WriteResponse(standardOutput, CancelledResponse("cancelled", "The CLI run was cancelled."));
            return ExitCancelled;
        }
        catch (ArgumentException ex)
        {
            CliJson.TraceException(ex);
            WriteResponse(standardOutput, InvalidFromException(ex));
            return ExitInvalidRequest;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            WriteResponse(standardOutput, Response(
                AgentJobStatus.Failed,
                result: null,
                error: RunFailureError(diagnosticLogPath),
                failure: CliJson.CreateFailure(ex)));
            return ExitFailed;
        }
    }

    private static bool IsKnownAgent(string agent)
    {
        return agent.Trim() is AgentFacade.GitHubCopilotAgent
            or AgentFacade.GrokBuildAgent
            or AgentFacade.CursorAgent
            or AgentFacade.CodexAgent;
    }

    private static bool TryParse(string input, out AgentCliRequest request, out string error)
    {
        request = null!;
        var text = input.TrimStart('\uFEFF').Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "stdin must contain one JSON object.";
            return false;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            var parsed = JsonSerializer.Deserialize<AgentCliRequest>(ref reader, InputOptions);
            if (parsed is null)
            {
                error = "stdin must contain one JSON object.";
                return false;
            }

            if (reader.BytesConsumed != bytes.Length)
            {
                var rest = Encoding.UTF8.GetString(bytes.AsSpan((int)reader.BytesConsumed));
                if (!string.IsNullOrWhiteSpace(rest))
                {
                    error = "stdin must contain one JSON object.";
                    return false;
                }
            }

            request = parsed;
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            CliJson.TraceException(ex);
            error = "stdin JSON is invalid. " + ex.Message;
            return false;
        }
    }

    private static AgentCliResponse Invalid(string summary)
    {
        return Response(
            AgentJobStatus.Failed,
            result: null,
            error: summary,
            failure: new AgentJobFailure("invalid_request", CliJson.FailureSummary(summary)));
    }

    private static AgentCliResponse InvalidFromException(Exception exception)
    {
        var failure = exception.Data[CliJson.FailureKindKey] is string
            ? CliJson.CreateFailure(exception)
            : new AgentJobFailure(
                "invalid_request",
                CliJson.FailureSummary(exception.Message),
                null,
                exception.Data[CliJson.FailureRunIdKey] as string,
                exception.Data[CliJson.FailureEventsLogPathKey] as string,
                exception.Data[CliJson.FailureTextLogPathKey] as string);
        return Response(AgentJobStatus.Failed, result: null, error: failure.Summary, failure: failure);
    }

    private static AgentCliResponse CancelledResponse(string kind, string summary)
    {
        var status = kind == "timeout" ? StatusTimedOut : AgentJobStatus.Cancelled;
        return Response(
            status,
            result: null,
            error: summary,
            failure: new AgentJobFailure(kind, summary));
    }

    private static AgentCliResponse Response(
        string status,
        AgentRunPublicResult? result,
        string? error,
        AgentJobFailure? failure)
    {
        return new AgentCliResponse(status, result, error, failure);
    }

    private static void WriteResponse(TextWriter standardOutput, AgentCliResponse response)
    {
        standardOutput.Write(JsonSerializer.Serialize(response, AgentJson.Options));
        standardOutput.Write('\n');
        standardOutput.Flush();
    }

    private static JsonSerializerOptions CreateInputOptions()
    {
        return new JsonSerializerOptions(AgentJson.Options)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
        };
    }

    private sealed class AgentCliRequest
    {
        public string? Agent { get; set; }

        public string? Prompt { get; set; }

        public string? WorkingDirectory { get; set; }

        public string? SessionId { get; set; }

        public string[]? Skills { get; set; }

        public bool AutoApprove { get; set; } = true;

        public string? Model { get; set; }

        public string? ReasoningEffort { get; set; }

        public bool? Fast { get; set; }

        public int? TimeoutSeconds { get; set; }
    }
}

public sealed record AgentCliResponse(
    string Status,
    AgentRunPublicResult? Result,
    string? Error,
    AgentJobFailure? Failure);
