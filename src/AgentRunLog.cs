using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 1 invocation の run log。同一 run ID の events.jsonl と .log へ逐次 append する。
/// </summary>
public interface IAgentRunLog : IAsyncDisposable, IDisposable
{
    string RunId { get; }
    string EventsPath { get; }
    string TextLogPath { get; }
    void WriteStarted(AgentRunStartedInfo info);
    void WriteLaunch(ProcessLaunchInfo info);
    void WriteAgentEvent(string type, JsonElement payload, string? humanSummary);
    void WriteProcessLine(string stream, string line);
    void NoteExternalOutput();
    void AttachProcess(IProcessLifetime process);
    void WriteCompleted(AgentRunResult result);
    void WriteFailed(Exception exception);
    void WriteCancelled();
}

public interface IAgentRunLogFactory
{
    IAgentRunLog Start(AgentRunRequest request);
}

public sealed record AgentRunStartedInfo(
    string Agent,
    string WorkingDirectory,
    string? SessionId,
    bool AutoApprove,
    IReadOnlyList<string>? Skills,
    string Prompt,
    string FileName,
    IReadOnlyList<string> Arguments);

/// <summary>
/// LocalApplicationData 配下へ run log を作る。テストではディレクトリと TimeProvider を注入する。
/// </summary>
public sealed class AgentRunLogFactory : IAgentRunLogFactory
{
    public const string DefaultProductDirectoryName = "codex-agent-facade";

    private readonly string _logDirectory;
    private readonly TimeProvider _timeProvider;

    public AgentRunLogFactory()
        : this(GetDefaultLogDirectory(), TimeProvider.System)
    {
    }

    public AgentRunLogFactory(string logDirectory, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _logDirectory = logDirectory;
        _timeProvider = timeProvider;
        LogDirectory = logDirectory;
    }

    public string LogDirectory { get; }

    public static string GetDefaultLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DefaultProductDirectoryName,
            "runs");
    }

    public IAgentRunLog Start(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var runId = CreateRunId(_timeProvider);
            var eventsPath = Path.Combine(_logDirectory, runId + ".events.jsonl");
            var textPath = Path.Combine(_logDirectory, runId + ".log");
            return new AgentRunLog(runId, eventsPath, textPath, _timeProvider);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw;
        }
    }

    internal static string CreateRunId(TimeProvider timeProvider)
    {
        var stamp = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var unique = Guid.NewGuid().ToString("N")[..8];
        return stamp + "-" + unique;
    }
}

internal sealed class AgentRunLog : IAgentRunLog
{
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    internal const int HumanSummaryMaxLength = 300;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAt;
    private readonly object _gate = new();
    private readonly StreamWriter _eventsWriter;
    private readonly StreamWriter _textWriter;
    private readonly CancellationTokenSource _heartbeatCts = new();
    private readonly Task _heartbeatTask;

    private DateTimeOffset _lastOutputAt;
    private IProcessLifetime? _process;
    private Exception? _backgroundError;
    private bool _disposed;

    public AgentRunLog(string runId, string eventsPath, string textLogPath, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(textLogPath);
        ArgumentNullException.ThrowIfNull(timeProvider);

        RunId = runId;
        EventsPath = eventsPath;
        TextLogPath = textLogPath;
        _timeProvider = timeProvider;
        _startedAt = timeProvider.GetUtcNow();
        _lastOutputAt = _startedAt;
        _eventsWriter = OpenWriter(eventsPath);
        _textWriter = OpenWriter(textLogPath);
        _heartbeatTask = RunHeartbeatAsync(_heartbeatCts.Token);
    }

    public string RunId { get; }
    public string EventsPath { get; }
    public string TextLogPath { get; }

    public void WriteStarted(AgentRunStartedInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var data = JsonSerializer.SerializeToElement(
            new StartedPayload(
                info.Agent,
                info.WorkingDirectory,
                info.SessionId,
                info.AutoApprove,
                info.Skills,
                info.Prompt,
                info.FileName,
                info.Arguments),
            AgentJson.Options);
        var skills = info.Skills is null || info.Skills.Count == 0
            ? ""
            : string.Join(",", info.Skills);
        var human = "started agent=" + info.Agent
            + " cwd=" + info.WorkingDirectory
            + " sessionId=" + (info.SessionId ?? "")
            + " autoApprove=" + info.AutoApprove.ToString(CultureInfo.InvariantCulture)
            + " skills=" + skills
            + " fileName=" + info.FileName
            + " prompt=" + info.Prompt;
        WriteEnvelope("facade", "started", data, human);
    }

    public void WriteLaunch(ProcessLaunchInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var data = JsonSerializer.SerializeToElement(
            new LaunchPayload(
                info.RequestedFileName,
                info.ResolvedExecutable,
                info.ProcessFileName,
                info.ProcessArguments,
                info.UsedWindowsCmdWrapper),
            AgentJson.Options);
        var wrapper = info.UsedWindowsCmdWrapper ? "cmd.exe" : "none";
        var human = "launch resolved=" + info.ResolvedExecutable
            + " processFileName=" + info.ProcessFileName
            + " wrapper=" + wrapper;
        WriteEnvelope("facade", "launch", data, human);
    }

    public void WriteAgentEvent(string type, JsonElement payload, string? humanSummary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        NoteExternalOutput();
        WriteEnvelope("agent", type, payload, humanSummary);
    }

    public void WriteProcessLine(string stream, string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentNullException.ThrowIfNull(line);
        NoteExternalOutput();
        var data = JsonSerializer.SerializeToElement(new ProcessLinePayload(stream, line), AgentJson.Options);
        WriteEnvelope("process", stream, data, stream + ": " + line);
    }

    public void NoteExternalOutput()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            _lastOutputAt = _timeProvider.GetUtcNow();
        }
    }

    public void AttachProcess(IProcessLifetime process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            ThrowIfUnavailable();
            _process = process;
        }
    }

    public void WriteCompleted(AgentRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var data = JsonSerializer.SerializeToElement(
            new CompletedPayload(
                result.ExitCode,
                result.SessionId,
                result.OutputText,
                result.RunId,
                result.EventsLogPath,
                result.TextLogPath),
            AgentJson.Options);
        WriteEnvelope(
            "facade",
            "completed",
            data,
            "completed exitCode=" + result.ExitCode.ToString(CultureInfo.InvariantCulture) + " sessionId=" + result.SessionId);
    }

    public void WriteFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var data = JsonSerializer.SerializeToElement(
            new FailedPayload(exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.ToString()),
            AgentJson.Options);
        WriteEnvelope("facade", "failed", data, "failed " + exception.GetType().Name + ": " + exception.Message);
    }

    public void WriteCancelled()
    {
        var data = JsonSerializer.SerializeToElement(new { reason = "cancelled" }, AgentJson.Options);
        WriteEnvelope("facade", "cancelled", data, "cancelled");
    }

    internal void WriteHeartbeat()
    {
        DateTimeOffset now;
        TimeSpan elapsed;
        TimeSpan lastOutputAgo;
        bool processAlive;
        int? processId;
        lock (_gate)
        {
            ThrowIfUnavailable();
            now = _timeProvider.GetUtcNow();
            elapsed = now - _startedAt;
            lastOutputAgo = now - _lastOutputAt;
            processAlive = IsProcessAlive();
            processId = TryGetProcessId();
        }

        var data = JsonSerializer.SerializeToElement(
            new HeartbeatPayload(
                elapsed.TotalSeconds,
                processAlive,
                lastOutputAgo.TotalSeconds,
                processId),
            AgentJson.Options);
        var human = "heartbeat elapsed=" + elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
            + " processAlive=" + processAlive.ToString(CultureInfo.InvariantCulture)
            + " lastOutputAgo=" + lastOutputAgo.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        WriteEnvelope("facade", "heartbeat", data, human);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _heartbeatCts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            _backgroundError ??= ex;
        }

        try
        {
            await _heartbeatTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            _backgroundError ??= ex;
        }

        lock (_gate)
        {
            _eventsWriter.Dispose();
            _textWriter.Dispose();
            _disposed = true;
        }

        _heartbeatCts.Dispose();
        ThrowIfBroken();
    }

    internal static string TruncateHuman(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
        if (oneLine.Length <= HumanSummaryMaxLength)
        {
            return oneLine;
        }

        return oneLine[..HumanSummaryMaxLength];
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                WriteHeartbeat();
            }
        }
        catch (OperationCanceledException ex)
        {
            CliJson.TraceException(ex);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            _backgroundError ??= ex;
        }
    }

    private void WriteEnvelope(string source, string type, JsonElement data, string? humanSummary)
    {
        var ts = FormatTimestamp(_timeProvider.GetUtcNow());
        JsonElement redactedData;
        string json;
        try
        {
            redactedData = SecretRedactor.Redact(data);
            var envelope = new LogEnvelope(ts, RunId, source, type, redactedData);
            json = SecretRedactor.RedactText(JsonSerializer.Serialize(envelope, AgentJson.Options));
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw;
        }

        lock (_gate)
        {
            ThrowIfUnavailable();
            try
            {
                _eventsWriter.WriteLine(json);
                _eventsWriter.Flush();
                if (!string.IsNullOrWhiteSpace(humanSummary))
                {
                    var human = SecretRedactor.RedactText(TruncateHuman(humanSummary));
                    _textWriter.WriteLine(ts + " " + human);
                    _textWriter.Flush();
                }
            }
            catch (Exception ex)
            {
                CliJson.TraceException(ex);
                _backgroundError = ex;
                throw;
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfBroken();
    }

    private void ThrowIfBroken()
    {
        if (_backgroundError is not null)
        {
            throw new InvalidOperationException("Run log write failed.", _backgroundError);
        }
    }

    private bool IsProcessAlive()
    {
        if (_process is null)
        {
            return false;
        }

        try
        {
            return !_process.HasExited;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            return false;
        }
    }

    private int? TryGetProcessId()
    {
        if (_process is null)
        {
            return null;
        }

        try
        {
            var id = _process.Id;
            return id == 0 ? null : id;
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            return null;
        }
    }

    private static StreamWriter OpenWriter(string path)
    {
        try
        {
            // ReadWrite share: Get-Content -Wait が書き込み中に読めるようにする。
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096);
            return new StreamWriter(stream, Utf8NoBom)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw;
        }
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private sealed record LogEnvelope(
        [property: JsonPropertyName("ts")] string Ts,
        [property: JsonPropertyName("runId")] string RunId,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("data")] JsonElement Data);

    private sealed record LaunchPayload(
        string RequestedFileName,
        string ResolvedExecutable,
        string ProcessFileName,
        IReadOnlyList<string> ProcessArguments,
        bool UsedWindowsCmdWrapper);

    private sealed record StartedPayload(
        string Agent,
        string WorkingDirectory,
        string? SessionId,
        bool AutoApprove,
        IReadOnlyList<string>? Skills,
        string Prompt,
        string FileName,
        IReadOnlyList<string> Arguments);

    private sealed record ProcessLinePayload(string Stream, string Line);

    private sealed record CompletedPayload(
        int ExitCode,
        string SessionId,
        string OutputText,
        string RunId,
        string EventsLogPath,
        string TextLogPath);

    private sealed record FailedPayload(string ExceptionType, string Message, string Detail);

    private sealed record HeartbeatPayload(
        double ElapsedSeconds,
        bool ProcessAlive,
        double LastOutputAgoSeconds,
        int? ProcessId);
}
