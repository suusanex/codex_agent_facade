using System.Text;
using System.Text.Json;

/// <summary>
/// 人間向け run log を追従表示し、facade の terminal イベントを見たら終了する。
/// job の lifetime は持たない。
/// </summary>
public sealed class RunLogViewerSession
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _pollInterval;
    private readonly Func<CancellationToken, Task>? _onTerminalDetected;

    public RunLogViewerSession(
        TimeSpan? pollInterval = null,
        Func<CancellationToken, Task>? onTerminalDetected = null)
    {
        var poll = pollInterval ?? DefaultPollInterval;
        if (poll < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Poll interval must be zero or positive.");
        }

        _pollInterval = poll;
        _onTerminalDetected = onTerminalDetected;
    }

    public async Task<int> RunAsync(
        string textLogPath,
        string eventsLogPath,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textLogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventsLogPath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var logTail = new SharedTextTail(textLogPath);
        var eventsTail = new SharedTextTail(eventsLogPath);
        var remainder = new StringBuilder();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteNew(logTail, output);
                if (HasTerminalEvent(eventsTail, remainder, error))
                {
                    if (_onTerminalDetected is not null)
                    {
                        await _onTerminalDetected(cancellationToken).ConfigureAwait(false);
                    }

                    // 人間向け最終行は events の terminal 行より先に flush される。
                    // 検出後は、いま読めるバイトが無くなるまで出してから終了する。
                    // 固定時間で打ち切ると、その時間を超える書き込みを取りこぼす。
                    while (WriteNew(logTail, output))
                    {
                    }

                    return 0;
                }

                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex)
        {
            // ウィンドウを閉じた場合の終了。agent job へは伝播しない。
            error.WriteLine(ex.ToString());
            try
            {
                WriteNew(logTail, output);
            }
            catch (Exception flushEx)
            {
                error.WriteLine(flushEx.ToString());
            }

            return 0;
        }
    }

    internal static bool IsFacadeTerminalEvent(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("source", out var source)
            || source.ValueKind != JsonValueKind.String
            || !string.Equals(source.GetString(), "facade", StringComparison.Ordinal))
        {
            return false;
        }

        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var name = type.GetString();
        return name is "completed" or "failed" or "cancelled";
    }

    private static bool HasTerminalEvent(SharedTextTail tail, StringBuilder remainder, TextWriter error)
    {
        remainder.Append(tail.ReadNewText());
        var found = false;
        while (true)
        {
            var text = remainder.ToString();
            var newline = text.IndexOf('\n');
            if (newline < 0)
            {
                break;
            }

            var line = text[..newline].TrimEnd('\r');
            remainder.Remove(0, newline + 1);
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                if (IsFacadeTerminalEvent(line))
                {
                    found = true;
                }
            }
            catch (JsonException ex)
            {
                error.WriteLine(ex.ToString());
            }
        }

        return found;
    }

    private static bool WriteNew(SharedTextTail tail, TextWriter output)
    {
        var text = tail.ReadNewText();
        if (text.Length == 0)
        {
            return false;
        }

        output.Write(text);
        output.Flush();
        return true;
    }
}

/// <summary>
/// console viewer の入口。Windows Terminal から別プロセスとして起動する。
/// </summary>
public static class RunLogViewerProgram
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: RunLogViewer <text-log-path> <events-log-path>");
            return 1;
        }

        try
        {
            var session = new RunLogViewerSession();
            return await session.RunAsync(args[0], args[1], output, error, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.ToString());
            return 1;
        }
    }
}

/// <summary>
/// 追記中の UTF-8 ファイルを、不完全な末尾シーケンスを残して読む。
/// StreamReader は内部バッファでファイル位置を隠すため、byte offset で追う。
/// </summary>
internal sealed class SharedTextTail
{
    private readonly string _path;
    private long _offset;
    private byte[] _pending = [];

    public SharedTextTail(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string ReadNewText()
    {
        if (!File.Exists(_path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        if (stream.Length < _offset)
        {
            _offset = 0;
            _pending = [];
        }

        stream.Position = _offset;
        var available = stream.Length - _offset;
        if (available > int.MaxValue)
        {
            throw new InvalidOperationException("Run log growth exceeds the supported read size.");
        }

        var count = (int)available;
        if (count == 0 && _pending.Length == 0)
        {
            return string.Empty;
        }

        var combined = new byte[_pending.Length + count];
        _pending.CopyTo(combined, 0);
        var read = count == 0 ? 0 : stream.Read(combined, _pending.Length, count);
        if (read != count)
        {
            Array.Resize(ref combined, _pending.Length + read);
        }

        _offset += read;
        var complete = Utf8TailDecoder.CompleteLength(combined);
        var text = complete == 0 ? string.Empty : Encoding.UTF8.GetString(combined, 0, complete);
        _pending = complete == combined.Length ? [] : combined[complete..];
        return text;
    }
}

internal static class Utf8TailDecoder
{
    public static int CompleteLength(ReadOnlySpan<byte> bytes)
    {
        var index = 0;
        var complete = 0;
        while (index < bytes.Length)
        {
            var needed = SequenceLength(bytes[index]);
            if (needed == 0)
            {
                index++;
                complete = index;
                continue;
            }

            if (index + needed > bytes.Length)
            {
                break;
            }

            if (!HasValidContinuations(bytes.Slice(index, needed)))
            {
                index++;
                complete = index;
                continue;
            }

            index += needed;
            complete = index;
        }

        return complete;
    }

    private static int SequenceLength(byte lead)
    {
        if (lead <= 0x7F)
        {
            return 1;
        }

        if (lead is >= 0xC2 and <= 0xDF)
        {
            return 2;
        }

        if (lead is >= 0xE0 and <= 0xEF)
        {
            return 3;
        }

        if (lead is >= 0xF0 and <= 0xF4)
        {
            return 4;
        }

        return 0;
    }

    private static bool HasValidContinuations(ReadOnlySpan<byte> sequence)
    {
        if (sequence.Length <= 1)
        {
            return sequence.Length == 1;
        }

        for (var i = 1; i < sequence.Length; i++)
        {
            if ((sequence[i] & 0xC0) != 0x80)
            {
                return false;
            }
        }

        return true;
    }
}
