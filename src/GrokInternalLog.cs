using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Grok Build の unified.jsonl を差分で読む。公開された相関 API もファイル同一性の API も無いため、
/// 読取位置、消費済み先頭の SHA-256、未完行の保持だけをこの境界に置く。
/// 照合そのものは <see cref="GrokPromptCorrelation"/> が行う。
/// </summary>
internal interface IGrokUnifiedLogReader
{
    GrokLogCursor CaptureCursor();

    GrokLogRead Read(GrokLogCursor cursor);
}

internal sealed record GrokLogCursor(long Offset, byte[] Pending, bool Abandoned, string? Problem, string? Continuity = null)
{
    public static GrokLogCursor At(long offset)
    {
        return new GrokLogCursor(offset, Array.Empty<byte>(), false, null);
    }
}

internal sealed record GrokLogRead(IReadOnlyList<string> Lines, GrokLogCursor Cursor);

/// <summary>
/// %USERPROFILE%\.grok\logs\unified.jsonl を FileShare.ReadWrite で読む。
/// ハンドルは呼び出しのたびに閉じる。Grok が書き込み中でも共有読みする。
/// </summary>
internal sealed class FileGrokUnifiedLogReader : IGrokUnifiedLogReader
{
    internal const int MaxPendingBytes = 4 * 1024 * 1024;

    private static readonly Encoding Utf8Strict = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _path;

    public FileGrokUnifiedLogReader(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public static FileGrokUnifiedLogReader CreateDefault()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new FileGrokUnifiedLogReader(Path.Combine(home, ".grok", "logs", "unified.jsonl"));
    }

    public GrokLogCursor CaptureCursor()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return GrokLogCursor.At(0);
            }

            using var stream = Open();
            if (stream.Length == 0)
            {
                return GrokLogCursor.At(0);
            }

            // 長さだけでは同サイズの置換を見分けられない。消費済み先頭の SHA-256 を連続性にする。
            return new GrokLogCursor(stream.Length, Array.Empty<byte>(), false, null, HashConsumed(stream, stream.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 相関できない場合は救済しない。通常の終了待ちは継続する。
            CliJson.TraceException(ex);
            return new GrokLogCursor(0, Array.Empty<byte>(), true, "unreadable");
        }
    }

    public GrokLogRead Read(GrokLogCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (cursor.Abandoned)
        {
            return new GrokLogRead(Array.Empty<string>(), cursor);
        }

        try
        {
            if (!File.Exists(_path))
            {
                if (cursor.Offset == 0 && cursor.Pending.Length == 0)
                {
                    return new GrokLogRead(Array.Empty<string>(), cursor);
                }

                return Stop(cursor, "missing");
            }

            using var stream = Open();
            if (stream.Length < cursor.Offset)
            {
                return Stop(cursor, "replaced");
            }

            // 短縮だけでなく、同サイズ以上への置換や truncate 後の再成長もここで捨てる。
            if (cursor.Offset > 0
                && !string.Equals(HashConsumed(stream, cursor.Offset), cursor.Continuity, StringComparison.Ordinal))
            {
                return Stop(cursor, "replaced");
            }

            stream.Position = cursor.Offset;
            var remaining = stream.Length - cursor.Offset;
            if (remaining > int.MaxValue)
            {
                return Stop(cursor, "unreadable");
            }

            var fresh = new byte[(int)remaining];
            var read = 0;
            while (read < fresh.Length)
            {
                var count = stream.Read(fresh, read, fresh.Length - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            if (read != fresh.Length)
            {
                return Stop(cursor, "unreadable");
            }

            var combinedLength = cursor.Pending.Length + fresh.Length;
            var combined = new byte[combinedLength];
            Buffer.BlockCopy(cursor.Pending, 0, combined, 0, cursor.Pending.Length);
            if (fresh.Length > 0)
            {
                Buffer.BlockCopy(fresh, 0, combined, cursor.Pending.Length, fresh.Length);
            }

            var lines = new List<string>();
            var start = 0;
            for (var index = 0; index < combined.Length; index++)
            {
                if (combined[index] != (byte)'\n')
                {
                    continue;
                }

                var length = index - start;
                if (length > 0 && combined[start + length - 1] == (byte)'\r')
                {
                    length--;
                }

                if (length > 0)
                {
                    lines.Add(Utf8Strict.GetString(combined, start, length));
                }

                start = index + 1;
            }

            var pendingLength = combined.Length - start;
            if (pendingLength > MaxPendingBytes)
            {
                return Stop(cursor, "line-too-long");
            }

            var pending = pendingLength == 0 ? Array.Empty<byte>() : combined[start..];
            var nextOffset = cursor.Offset + fresh.Length;
            if (cursor.Offset > 0
                && !string.Equals(HashConsumed(stream, cursor.Offset), cursor.Continuity, StringComparison.Ordinal))
            {
                return Stop(cursor, "replaced");
            }

            var continuity = nextOffset == 0 ? null : HashConsumed(stream, cursor.Offset, fresh);
            var next = new GrokLogCursor(nextOffset, pending, false, null, continuity);
            return new GrokLogRead(lines, next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            CliJson.TraceException(ex);
            return Stop(cursor, "unreadable");
        }
    }

    private static string HashConsumed(Stream stream, long prefixLength, byte[]? suffix = null)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        var remaining = prefixLength;
        while (remaining > 0)
        {
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0)
            {
                throw new EndOfStreamException("The Grok log ended before the consumed prefix.");
            }

            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }

        if (suffix is { Length: > 0 })
        {
            hash.AppendData(suffix);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private FileStream Open()
    {
        return new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    private static GrokLogRead Stop(GrokLogCursor cursor, string problem)
    {
        return new GrokLogRead(
            Array.Empty<string>(),
            new GrokLogCursor(cursor.Offset, cursor.Pending, true, problem));
    }
}

/// <summary>
/// 今回の起動以降の shell ログから、最初の prompt の内部成功だけを確定する。
/// Grok は起動 PID・session・prompt を結び付ける公開 API を出していない。
/// 実測形式（src、pid、ts、sid、msg、ctx.prompt_id、ctx.ok）の一致だけを見る。
/// 作業ディレクトリはジャンクションで表記が分かれるため照合しない。
/// </summary>
internal sealed class GrokPromptCorrelation
{
    private bool _abandoned;
    private string? _abandonReason;
    private bool _sessionBound;
    private string? _sessionId;
    private string? _version;
    private string? _promptId;
    private DateTimeOffset _promptStartedAt;
    private bool _promptFailed;
    private GrokCompletionEvidence? _success;

    public bool IsAbandoned => _abandoned;

    public string? AbandonReason => _abandonReason;

    public GrokCompletionEvidence? Success => _abandoned ? null : _success;

    public void Observe(string line, int processId, DateTimeOffset launchedAt, string? resumeSessionId)
    {
        if (_abandoned)
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            CliJson.TraceException(ex);
            Abandon("Grok internal log line was not valid JSON.");
            return;
        }

        using (document)
        {
            Observe(document.RootElement, processId, launchedAt, resumeSessionId);
        }
    }

    private void Observe(JsonElement root, int processId, DateTimeOffset launchedAt, string? resumeSessionId)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            Abandon("Grok internal log line was not a JSON object.");
            return;
        }

        if (!TryReadEnvelope(root, out var timestamp, out var source, out var pid, out var message))
        {
            Abandon("Grok internal log line did not have the expected envelope.");
            return;
        }

        // launchedAt はプロセス起動直前の時刻。それより前は過去の記録として無視する。
        if (!string.Equals(source, "shell", StringComparison.Ordinal) || pid != processId || timestamp < launchedAt)
        {
            return;
        }

        if (string.Equals(message, "session created", StringComparison.Ordinal)
            || string.Equals(message, "session loaded", StringComparison.Ordinal))
        {
            ObserveSession(root, timestamp, message, resumeSessionId);
            return;
        }

        if (string.Equals(message, "shell.handle_prompt.start", StringComparison.Ordinal))
        {
            ObserveStart(root, timestamp);
            return;
        }

        if (string.Equals(message, "shell.handle_prompt.done", StringComparison.Ordinal))
        {
            ObserveDone(root, timestamp, processId);
        }
    }

    private void ObserveSession(JsonElement root, DateTimeOffset timestamp, string message, string? resumeSessionId)
    {
        if (!TryReadSession(root, out var sessionId, out var version))
        {
            Abandon("Grok internal log " + message + " did not include a session id and version.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(resumeSessionId)
            && !string.Equals(sessionId, resumeSessionId, StringComparison.Ordinal))
        {
            Abandon("Grok internal log session did not match the requested session.");
            return;
        }

        if (_sessionBound && !string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
        {
            Abandon("Grok internal log had more than one session for the process.");
            return;
        }

        if (_version is not null && !string.Equals(_version, version, StringComparison.Ordinal))
        {
            Abandon("Grok internal log version changed while correlating the prompt.");
            return;
        }

        _sessionBound = true;
        _sessionId = sessionId;
        _version ??= version;
        _ = timestamp;
    }

    private void ObserveStart(JsonElement root, DateTimeOffset timestamp)
    {
        if (!_sessionBound || _sessionId is null)
        {
            Abandon("Grok internal log recorded a prompt start before the session.");
            return;
        }

        if (!TryReadPrompt(root, out var sessionId, out var promptId, out var version))
        {
            Abandon("Grok internal log prompt start did not include the expected identifiers.");
            return;
        }

        if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
        {
            Abandon("Grok internal log prompt start session did not match.");
            return;
        }

        if (_version is not null && !string.Equals(_version, version, StringComparison.Ordinal))
        {
            Abandon("Grok internal log version changed while correlating the prompt.");
            return;
        }

        if (_promptId is null)
        {
            _promptId = promptId;
            _promptStartedAt = timestamp;
            _version ??= version;
            return;
        }

        if (_success is null)
        {
            Abandon("Grok internal log started another prompt before the target prompt completed.");
        }
    }

    private void ObserveDone(JsonElement root, DateTimeOffset timestamp, int processId)
    {
        if (_promptId is null || _sessionId is null)
        {
            Abandon("Grok internal log recorded a prompt completion before the prompt start.");
            return;
        }

        if (!TryReadDone(root, out var sessionId, out var promptId, out var version, out var ok))
        {
            Abandon("Grok internal log prompt completion did not include a boolean ok.");
            return;
        }

        if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal)
            || !string.Equals(promptId, _promptId, StringComparison.Ordinal))
        {
            if (_success is null)
            {
                Abandon("Grok internal log completion identifiers did not match the target prompt.");
            }

            return;
        }

        if (timestamp < _promptStartedAt)
        {
            Abandon("Grok internal log prompt completion preceded its start.");
            return;
        }

        if (_version is not null && !string.Equals(_version, version, StringComparison.Ordinal))
        {
            Abandon("Grok internal log version changed while correlating the prompt.");
            return;
        }

        if (!ok)
        {
            _promptFailed = true;
            return;
        }

        if (_promptFailed || _success is not null)
        {
            return;
        }

        _version ??= version;
        _success = new GrokCompletionEvidence(processId, sessionId, promptId, timestamp, version);
    }

    private static bool TryReadEnvelope(
        JsonElement root,
        out DateTimeOffset timestamp,
        out string source,
        out long pid,
        out string message)
    {
        timestamp = default;
        source = string.Empty;
        pid = 0;
        message = string.Empty;
        if (!TryGetProperty(root, "ts", out var tsElement)
            || tsElement.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                tsElement.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out timestamp))
        {
            return false;
        }

        if (!TryGetProperty(root, "src", out var sourceElement) || sourceElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        source = sourceElement.GetString() ?? string.Empty;
        if (!TryGetProperty(root, "pid", out var pidElement)
            || pidElement.ValueKind != JsonValueKind.Number
            || !pidElement.TryGetInt64(out pid))
        {
            return false;
        }

        if (!TryGetProperty(root, "msg", out var messageElement) || messageElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        message = messageElement.GetString() ?? string.Empty;
        return source.Length > 0 && message.Length > 0;
    }

    private static bool TryReadSession(JsonElement root, out string sessionId, out string version)
    {
        return TryReadIdentity(root, requirePrompt: false, out sessionId, out _, out version);
    }

    private static bool TryReadPrompt(JsonElement root, out string sessionId, out string promptId, out string version)
    {
        return TryReadIdentity(root, requirePrompt: true, out sessionId, out promptId, out version);
    }

    private static bool TryReadDone(
        JsonElement root,
        out string sessionId,
        out string promptId,
        out string version,
        out bool ok)
    {
        ok = false;
        if (!TryReadIdentity(root, requirePrompt: true, out sessionId, out promptId, out version))
        {
            return false;
        }

        if (!TryGetProperty(root, "ctx", out var context) || context.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetProperty(context, "ok", out var okElement) || okElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        ok = okElement.GetBoolean();
        return true;
    }

    private static bool TryReadIdentity(
        JsonElement root,
        bool requirePrompt,
        out string sessionId,
        out string promptId,
        out string version)
    {
        sessionId = string.Empty;
        promptId = string.Empty;
        version = string.Empty;
        if (!TryGetProperty(root, "sid", out var sessionElement)
            || sessionElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        sessionId = sessionElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Any(char.IsControl))
        {
            return false;
        }

        if (!TryGetProperty(root, "ver", out var versionElement) || versionElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        version = versionElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(version) || version.Any(char.IsControl))
        {
            return false;
        }

        if (!requirePrompt)
        {
            return true;
        }

        if (!TryGetProperty(root, "ctx", out var context) || context.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetProperty(context, "prompt_id", out var promptElement) || promptElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        promptId = promptElement.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(promptId) && !promptId.Any(char.IsControl);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        return CliJson.TryGetPropertyIgnoreCase(element, name, out value);
    }

    private void Abandon(string reason)
    {
        if (_abandoned)
        {
            return;
        }

        _abandoned = true;
        _abandonReason = reason;
        _success = null;
    }
}
