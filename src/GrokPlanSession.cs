using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

/// <summary>
/// Grok Build の Plan-only を one-shot ACP で実行する境界。
/// 通常実行の <c>grok -p</c> には入らない。headless は exit_plan_mode を自動承認するため使わない。
/// インストール済み 1.0.70 は Content-Length ではなく NDJSON JSON-RPC である。
/// </summary>
public sealed record GrokAcpStartInfo(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    Action<string>? OnStandardError = null);

public interface IGrokAcpProcess : IAsyncDisposable
{
    ProcessLaunchInfo Launch { get; }

    /// <summary>
    /// Dispose が終了確認できたとき true。未確認の停止を成功として返さない。
    /// </summary>
    bool ExitObserved { get; }

    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    string CopyStandardError();

    void Kill();
}

public interface IGrokAcpProcessFactory
{
    IGrokAcpProcess Start(GrokAcpStartInfo info);
}

/// <summary>
/// caller workspace へプラン本文を書く。Grok の session 内 plan.md は探さない。
/// </summary>
internal static class GrokPlanArtifact
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static bool IsSafeSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId is "." or "..")
        {
            return false;
        }

        if (sessionId != sessionId.Trim())
        {
            return false;
        }

        foreach (var character in sessionId)
        {
            if (character is '/' or '\\' or ':')
            {
                return false;
            }

            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    public static string RelativePath(string sessionId)
    {
        return ".codex-agent-facade/plans/" + sessionId + ".md";
    }

    public static void Write(string workingDirectory, string sessionId, string content)
    {
        if (!IsSafeSessionId(sessionId))
        {
            throw new InvalidOperationException("Grok session id is not a safe path segment.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Grok Build plan mode did not return plan content.");
        }

        var directory = Path.Combine(workingDirectory, ".codex-agent-facade", "plans");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, sessionId + ".md");
        var temporary = Path.Combine(directory, sessionId + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, content, Utf8);
            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }
    }
}

internal readonly record struct GrokPlanOutcome(string SessionId, string Content);

/// <summary>
/// initialize、session/new または session/load、config、plan mode、prompt、exit_plan_mode のあと session/cancel までを行う。
/// 1.0.70 の session/set_config_option は value を JSON 文字列で受け、object 形式は Invalid params で拒否する。
/// session/load の result は sessionId を返さないことがある。返った値が要求と違えば失敗する。
/// </summary>
internal static class GrokPlanSession
{
    public const string Mode = "plan";
    private const string ExitPlanMethod = "_x.ai/exit_plan_mode";
    private const string ExitPlanMethodAlias = "x.ai/exit_plan_mode";

    public static async Task<GrokPlanOutcome> RunAsync(
        IGrokAcpProcess process,
        AgentRunRequest request,
        string prompt,
        IAgentRunLog runLog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(runLog);

        using var killOnCancel = cancellationToken.Register(process.Kill);
        var session = new Session(process, runLog);
        try
        {
            using (var initialized = await session.SendAsync(
                "initialize",
                "{\"protocolVersion\":1,\"clientCapabilities\":{},\"clientInfo\":{\"name\":\"codex-agent-facade\",\"version\":\"1\"}}",
                "internal_error",
                cancellationToken).ConfigureAwait(false))
            {
                RequireProtocolVersion(initialized.RootElement);
            }

            var requestedSessionId = string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId;
            if (requestedSessionId is not null && !GrokPlanArtifact.IsSafeSessionId(requestedSessionId))
            {
                throw Fail("internal_error", "Grok session id is not a safe path segment.");
            }

            string sessionId;
            JsonElement configSource;
            JsonDocument sessionDocument;
            if (requestedSessionId is null)
            {
                sessionDocument = await session.SendAsync(
                    "session/new",
                    "{\"cwd\":" + Quote(request.WorkingDirectory) + ",\"mcpServers\":[]}",
                    "internal_error",
                    cancellationToken).ConfigureAwait(false);
                sessionId = ReadNewSessionId(sessionDocument.RootElement);
            }
            else
            {
                sessionDocument = await session.SendAsync(
                    "session/load",
                    "{\"sessionId\":" + Quote(requestedSessionId) + ",\"cwd\":" + Quote(request.WorkingDirectory) + ",\"mcpServers\":[]}",
                    "internal_error",
                    cancellationToken).ConfigureAwait(false);
                sessionId = ResolveLoadedSessionId(requestedSessionId, sessionDocument.RootElement);
            }

            using (sessionDocument)
            {
                configSource = sessionDocument.RootElement.Clone();
            }

            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                configSource = await session.ApplyConfigAsync(sessionId, configSource, "model", request.Model, cancellationToken).ConfigureAwait(false);
            }

            if (request.ReasoningEffort is not null)
            {
                configSource = await session.ApplyConfigAsync(sessionId, configSource, "reasoning_effort", request.ReasoningEffort, cancellationToken).ConfigureAwait(false);
            }

            using (await session.SendAsync(
                "session/set_mode",
                "{\"sessionId\":" + Quote(sessionId) + ",\"modeId\":\"plan\"}",
                "internal_error",
                cancellationToken).ConfigureAwait(false))
            {
            }

            return await session.PromptAsync(sessionId, prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            CliJson.TraceException(ex);
            throw Fail("internal_error", "Grok Build plan mode returned a line that is not JSON.");
        }
    }

    private static void RequireProtocolVersion(JsonElement result)
    {
        if (result.TryGetProperty("protocolVersion", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var number)
            && number == 1)
        {
            return;
        }

        var observed = result.TryGetProperty("protocolVersion", out var actual)
            ? actual.GetRawText()
            : "missing";
        throw Fail("internal_error", "Grok Build plan mode returned protocolVersion " + observed + ". This Facade supports protocolVersion 1.");
    }

    private static string ReadNewSessionId(JsonElement result)
    {
        if (!result.TryGetProperty("sessionId", out var sessionId) || sessionId.ValueKind != JsonValueKind.String)
        {
            throw Fail("internal_error", "Grok Build plan mode did not return a session id.");
        }

        var value = sessionId.GetString() ?? string.Empty;
        if (!GrokPlanArtifact.IsSafeSessionId(value))
        {
            throw Fail("internal_error", "Grok session id is not a safe path segment.");
        }

        return value;
    }

    private static string ResolveLoadedSessionId(string requestedSessionId, JsonElement result)
    {
        if (result.TryGetProperty("sessionId", out var sessionId))
        {
            var returned = sessionId.ValueKind == JsonValueKind.String ? sessionId.GetString() : null;
            if (!string.Equals(returned, requestedSessionId, StringComparison.Ordinal))
            {
                throw Fail("session_mismatch", "Grok Build plan mode returned a different session id.");
            }
        }

        return requestedSessionId;
    }

    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static Exception Fail(string kind, string message)
    {
        var exception = new InvalidOperationException(message);
        CliJson.MarkFailure(exception, kind, message);
        CliJson.TraceException(exception);
        return exception;
    }

    private sealed class Session
    {
        private readonly IGrokAcpProcess _process;
        private readonly IAgentRunLog _runLog;
        private int _nextId = 1;

        public Session(IGrokAcpProcess process, IAgentRunLog runLog)
        {
            _process = process;
            _runLog = runLog;
        }

        public async Task<JsonElement> ApplyConfigAsync(
            string sessionId,
            JsonElement sessionResult,
            string configId,
            string requested,
            CancellationToken cancellationToken)
        {
            if (!TryCurrentValue(sessionResult, configId, out var current))
            {
                throw Fail("option_not_applied", "Grok Build plan mode did not expose config option '" + configId + "'.");
            }

            if (string.Equals(current, requested, StringComparison.Ordinal))
            {
                return sessionResult;
            }

            // 1.0.70 は value の JSON 文字列だけを受け付ける。{"value":"low"} は成功し、object 形式は拒否された。
            // 返った configOptions を次の option に使う。モデル変更前の一覧で effort を判断しない。
            using var updated = await SendAsync(
                "session/set_config_option",
                "{\"sessionId\":" + Quote(sessionId) + ",\"configId\":" + Quote(configId) + ",\"value\":" + Quote(requested) + "}",
                "option_not_applied",
                cancellationToken).ConfigureAwait(false);
            if (!TryCurrentValue(updated.RootElement, configId, out var applied)
                || !string.Equals(applied, requested, StringComparison.Ordinal))
            {
                throw Fail("option_not_applied", "Grok Build plan mode did not apply " + configId + ".");
            }

            return updated.RootElement.Clone();
        }

        public async Task<JsonDocument> SendAsync(
            string method,
            string paramsJson,
            string errorKind,
            CancellationToken cancellationToken)
        {
            var id = _nextId++;
            await WriteAsync(Request(id, method, paramsJson), cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var incoming = await ReadRequiredAsync(cancellationToken).ConfigureAwait(false);
                using var parsed = Parse(incoming);
                var root = parsed.RootElement;
                if (root.TryGetProperty("method", out _))
                {
                    await RejectReverseOrIgnoreAsync(root, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (IsUnmatchedResponse(root, id))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    throw Fail(errorKind, "Grok Build plan mode rejected " + method + ". " + ReadError(error));
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    throw Fail(errorKind, "Grok Build plan mode omitted the result of " + method + ".");
                }

                return JsonDocument.Parse(result.GetRawText());
            }
        }

        public async Task<GrokPlanOutcome> PromptAsync(string sessionId, string prompt, CancellationToken cancellationToken)
        {
            var id = _nextId++;
            await WriteAsync(
                Request(
                    id,
                    "session/prompt",
                    "{\"sessionId\":" + Quote(sessionId) + ",\"prompt\":[{\"type\":\"text\",\"text\":" + Quote(prompt) + "}]}"),
                cancellationToken).ConfigureAwait(false);

            string? plan = null;
            var cancelSent = false;
            while (true)
            {
                var incoming = await ReadRequiredAsync(cancellationToken).ConfigureAwait(false);
                using var parsed = Parse(incoming);
                var root = parsed.RootElement;
                if (root.TryGetProperty("method", out _))
                {
                    if (root.TryGetProperty("id", out var reverseId) && reverseId.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                    {
                        plan = await HandleReverseAsync(root, sessionId, cancelSent, cancellationToken).ConfigureAwait(false);
                        cancelSent = true;
                    }
                    else
                    {
                        RejectToolCallAfterCancel(root, cancelSent);
                    }

                    continue;
                }

                if (IsUnmatchedResponse(root, id))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    throw Fail("internal_error", "Grok Build plan mode rejected session/prompt. " + ReadError(error));
                }

                if (!root.TryGetProperty("result", out var result)
                    || !result.TryGetProperty("stopReason", out var stopReason)
                    || stopReason.ValueKind != JsonValueKind.String
                    || stopReason.GetString() != "cancelled")
                {
                    var observed = result.ValueKind == JsonValueKind.Object
                        && result.TryGetProperty("stopReason", out var actual)
                        ? actual.GetRawText()
                        : "missing";
                    throw Fail("internal_error", "Grok Build plan mode stopped with " + observed + ".");
                }

                if (string.IsNullOrWhiteSpace(plan))
                {
                    throw Fail("internal_error", "Grok Build plan mode did not return plan content.");
                }

                return new GrokPlanOutcome(sessionId, plan);
            }
        }

        private async Task<string?> HandleReverseAsync(
            JsonElement root,
            string sessionId,
            bool cancelSent,
            CancellationToken cancellationToken)
        {
            var method = root.GetProperty("method").GetString();
            if (method is ExitPlanMethod or ExitPlanMethodAlias)
            {
                if (cancelSent)
                {
                    // abandoned を返すと 1.0.70 は同じ prompt を default mode で続ける。二度目も答えずに失敗させる。
                    throw Fail("internal_error", "Grok Build plan mode requested exit_plan_mode more than once.");
                }

                var content = ReadPlanContent(root);
                // 1.0.70 は id 付き session/cancel を -32601 で拒否する。notification だけが
                // 未応答の exit_plan_mode を残したまま stopReason cancelled で prompt を終える。
                await WriteAsync(Cancel(sessionId), cancellationToken).ConfigureAwait(false);
                return content;
            }

            await WriteAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + root.GetProperty("id").GetRawText()
                + ",\"error\":{\"code\":-32601,\"message\":\"Method not supported by codex-agent-facade plan mode.\"}}",
                cancellationToken).ConfigureAwait(false);
            throw Fail("internal_error", "Grok Build plan mode made an unsupported request '" + (method ?? "") + "'.");
        }

        private static void RejectToolCallAfterCancel(JsonElement root, bool cancelSent)
        {
            if (!cancelSent || !IsNewToolCall(root))
            {
                return;
            }

            throw Fail("internal_error", "Grok Build plan mode started a tool call after the plan was cancelled.");
        }

        private async Task RejectReverseOrIgnoreAsync(JsonElement root, CancellationToken cancellationToken)
        {
            if (!root.TryGetProperty("id", out var id) || id.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return;
            }

            await WriteAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText()
                + ",\"error\":{\"code\":-32601,\"message\":\"Method not supported by codex-agent-facade plan mode.\"}}",
                cancellationToken).ConfigureAwait(false);
            var method = root.GetProperty("method").GetString();
            throw Fail("internal_error", "Grok Build plan mode made an unsupported request '" + (method ?? "") + "'.");
        }

        private static bool IsNewToolCall(JsonElement root)
        {
            if (!root.TryGetProperty("method", out var method) || method.GetString() != "session/update")
            {
                return false;
            }

            if (!root.TryGetProperty("params", out var parameters)
                || !parameters.TryGetProperty("update", out var update)
                || update.ValueKind != JsonValueKind.Object
                || !update.TryGetProperty("sessionUpdate", out var sessionUpdate))
            {
                return false;
            }

            return sessionUpdate.GetString() == "tool_call";
        }

        private static string? ReadPlanContent(JsonElement root)
        {
            if (!root.TryGetProperty("params", out var parameters)
                || !parameters.TryGetProperty("planContent", out var planContent)
                || planContent.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return planContent.GetString();
        }

        private static bool TryCurrentValue(JsonElement result, string configId, out string? currentValue)
        {
            currentValue = null;
            if (!result.TryGetProperty("configOptions", out var options) || options.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var option in options.EnumerateArray())
            {
                if (!option.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != configId)
                {
                    continue;
                }

                if (option.TryGetProperty("currentValue", out var current) && current.ValueKind == JsonValueKind.String)
                {
                    currentValue = current.GetString();
                }

                return true;
            }

            return false;
        }

        private async Task WriteAsync(string line, CancellationToken cancellationToken)
        {
            _runLog.WriteProcessLine("acp", line);
            await _process.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> ReadRequiredAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var line = await _process.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw Fail("internal_error", "Grok Build plan mode closed its output before the prompt completed.");
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                _runLog.WriteProcessLine("acp", line);
                return line;
            }
        }

        private static JsonDocument Parse(string line)
        {
            try
            {
                return JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                CliJson.TraceException(ex);
                throw Fail("internal_error", "Grok Build plan mode returned a line that is not JSON.");
            }
        }

        private static string Request(int id, string method, string paramsJson)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(CultureInfo.InvariantCulture)
                + ",\"method\":" + Quote(method)
                + ",\"params\":" + paramsJson + "}";
        }

        private static string Cancel(string sessionId)
        {
            return "{\"jsonrpc\":\"2.0\",\"method\":\"session/cancel\",\"params\":{\"sessionId\":" + Quote(sessionId) + "}}";
        }

        /// <summary>
        /// method を持たない JSON-RPC response のうち、待っている request ID と一致しないものを unmatched とする。
        /// Grok Build は skills / workflow watcher の内部 response を同じ stream に出す。固定 ID は列挙しない。
        /// 受信行は ReadRequiredAsync が ACP log へ残している。一致する response が来なければ、この判定だけでは成功にしない。
        /// </summary>
        private static bool IsUnmatchedResponse(JsonElement root, int expectedId)
        {
            // object 以外は response として読まない。壊れた JSON は Parse が既に失敗させている。
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("method", out _))
            {
                return false;
            }

            return !IdEquals(root, expectedId);
        }

        private static bool IdEquals(JsonElement root, int expected)
        {
            if (!root.TryGetProperty("id", out var id))
            {
                return false;
            }

            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number))
            {
                return number == expected;
            }

            return id.ValueKind == JsonValueKind.String
                && int.TryParse(id.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                && parsed == expected;
        }

        private static string ReadError(JsonElement error)
        {
            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return SecretRedactor.RedactText(message.GetString() ?? string.Empty);
            }

            return "JSON-RPC error";
        }
    }
}

public sealed class GrokAcpProcessFactory : IGrokAcpProcessFactory
{
    private readonly IProcessJobGuard _jobGuard;

    public GrokAcpProcessFactory()
        : this(OperatingSystem.IsWindows() ? new WindowsKillOnCloseJobGuard() : NullProcessJobGuard.Instance)
    {
    }

    internal GrokAcpProcessFactory(IProcessJobGuard jobGuard)
    {
        ArgumentNullException.ThrowIfNull(jobGuard);
        _jobGuard = jobGuard;
    }

    public IGrokAcpProcess Start(GrokAcpStartInfo info)
    {
        try
        {
            return GrokAcpProcess.Start(info, _jobGuard);
        }
        catch (ProcessStartException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw new ProcessStartException("Failed to start Grok Build plan mode.", ex);
        }
    }
}

internal sealed class GrokAcpProcess : IGrokAcpProcess
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly Process _process;
    private readonly IDisposable? _job;
    private readonly Action<string>? _onStandardError;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly StringBuilder _standardError = new();
    private readonly object _errorGate = new();
    private Task _stdoutTask = Task.CompletedTask;
    private Task _stderrTask = Task.CompletedTask;
    private int _killed;

    private GrokAcpProcess(Process process, IDisposable? job, ProcessLaunchInfo launch, Action<string>? onStandardError)
    {
        _process = process;
        _job = job;
        _onStandardError = onStandardError;
        Launch = launch;
    }

    public ProcessLaunchInfo Launch { get; }

    public bool ExitObserved { get; private set; }

    public static GrokAcpProcess Start(GrokAcpStartInfo info, IProcessJobGuard jobGuard)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(jobGuard);
        string resolved;
        try
        {
            resolved = ExecutableResolver.Resolve(info.FileName);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw new ProcessStartException("Failed to prepare Grok Build plan mode.", ex);
        }

        var extension = Path.GetExtension(resolved);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var rejected = new InvalidOperationException(
                "Grok Build plan mode requires a native executable. Resolved '" + resolved + "'.");
            CliJson.TraceException(rejected);
            throw new ProcessStartException(rejected.Message, rejected);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = resolved,
            WorkingDirectory = info.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in info.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start process '" + resolved + "'.");
            }
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            process.Dispose();
            throw new ProcessStartException("Failed to start Grok Build plan mode.", ex);
        }

        IDisposable? job = null;
        try
        {
            job = jobGuard.Assign(process);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            TryKill(process);
            process.Dispose();
            throw new ProcessStartException("Failed to assign Grok Build plan mode to a kill-on-close job.", ex);
        }

        var launch = new ProcessLaunchInfo(
            info.FileName,
            resolved,
            startInfo.FileName,
            info.Arguments,
            info.Arguments,
            UsedWindowsCmdWrapper: false,
            RawArguments: null,
            Wrapper: "none",
            HasStandardInput: true,
            StandardInputByteCount: null);
        var started = new GrokAcpProcess(process, job, launch, info.OnStandardError);
        started._stdoutTask = started.ReadStdoutAsync();
        started._stderrTask = started.ReadStderrAsync();
        return started;
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            CliJson.TraceException(ex);
            return null;
        }
    }

    public string CopyStandardError()
    {
        lock (_errorGate)
        {
            return _standardError.ToString();
        }
    }

    public void Kill()
    {
        if (Interlocked.Exchange(ref _killed, 1) != 0)
        {
            return;
        }

        TryKill(_process);
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        _lines.Writer.TryComplete();
        // Kill が失敗しても無期限に待たない。予算は通常実行の ForcedProcessStop と同じ。
        // ここでは例外を出さない。session 側の失敗を Dispose の例外で潰さないため。
        var exitTask = WaitForExitOrCompleted();
        var outcome = await ForcedProcessStop.ExecuteAsync(
            () => TryKill(_process),
            exitTask,
            _stdoutTask,
            _stderrTask,
            TimeProvider.System).ConfigureAwait(false);
        if (!exitTask.IsCompleted)
        {
            _ = exitTask.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        CliJson.TraceException(task.Exception!);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        try
        {
            _job?.Dispose();
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }

        try
        {
            _process.Dispose();
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }

        ExitObserved = outcome.ExitObserved;
        if (!outcome.ExitObserved)
        {
            CliJson.TraceException(new InvalidOperationException("Grok Build plan mode did not exit after termination."));
        }
    }

    private Task WaitForExitOrCompleted()
    {
        try
        {
            if (_process.HasExited)
            {
                return Task.CompletedTask;
            }

            return _process.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            return Task.FromException(ex);
        }
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    _lines.Writer.TryComplete();
                    return;
                }

                await _lines.Writer.WriteAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            _lines.Writer.TryComplete(ex);
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            while (true)
            {
                var line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                lock (_errorGate)
                {
                    if (_standardError.Length > 0)
                    {
                        _standardError.Append('\n');
                    }

                    _standardError.Append(line);
                }

                try
                {
                    _onStandardError?.Invoke(line);
                }
                catch (Exception ex)
                {
                    CliJson.TraceException(ex);
                }
            }
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
        }
    }
}
