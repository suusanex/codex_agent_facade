using System.Diagnostics;
using System.Text;

/// <summary>
/// 外部 CLI プロセス起動のテスト境界。実OS変更を伴う処理はここだけに閉じる。
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken);
}

public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    Action<string>? StdoutLineCallback = null);

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// UseShellExecute を使わず stdout/stderr を収集する。Windows の .cmd shim は cmd.exe /c で起動する。
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);

        var resolved = ExecutableResolver.Resolve(request.FileName);
        var startInfo = CreateStartInfo(resolved, request);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{resolved}'.");
            }
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw;
        }

        process.StandardInput.Close();

        await using var killOnCancel = cancellationToken.Register(() =>
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
        });

        var stdoutTask = ReadLinesAsync(process.StandardOutput, stdout, request.StdoutLineCallback, cancellationToken);
        var stderrTask = ReadLinesAsync(process.StandardError, stderr, onLine: null, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            TryKill(process);
            throw;
        }

        return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static ProcessStartInfo CreateStartInfo(string resolvedExecutable, ProcessRunRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        var isWindowsScript = OperatingSystem.IsWindows()
            && (resolvedExecutable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || resolvedExecutable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        if (isWindowsScript)
        {
            // ArgumentList を cmd にバラして渡すと & | 等がメタ文字になる。/s /c で1本の quoted command にする。
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(WindowsCmd.BuildCommand(resolvedExecutable, request.Arguments));
        }
        else
        {
            startInfo.FileName = resolvedExecutable;
            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        if (request.EnvironmentVariables is not null)
        {
            foreach (var pair in request.EnvironmentVariables)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        StringBuilder sink,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (sink.Length > 0)
            {
                sink.Append('\n');
            }

            sink.Append(line);
            onLine?.Invoke(line);
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

/// <summary>
/// cmd.exe に渡す1コマンドを引用符で囲み、メタ文字として解釈されないようにする。
/// </summary>
internal static class WindowsCmd
{
    public static string QuoteArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var escaped = value
            .Replace("\"", "\"\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }

    public static string BuildCommand(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(QuoteArgument(executable));
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(QuoteArgument(argument));
        }

        return builder.ToString();
    }
}

internal static class ExecutableResolver
{
    public static string Resolve(string fileName)
    {
        if (Path.IsPathRooted(fileName))
        {
            if (File.Exists(fileName))
            {
                return fileName;
            }

            throw new FileNotFoundException($"Executable not found: {fileName}", fileName);
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var dir in pathDirs)
        {
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (Path.HasExtension(fileName))
            {
                var named = Path.Combine(trimmed, fileName);
                if (File.Exists(named))
                {
                    return Path.GetFullPath(named);
                }

                continue;
            }

            foreach (var extension in extensions)
            {
                if (extension.Length == 0)
                {
                    continue;
                }

                var candidate = Path.Combine(trimmed, fileName + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                var exact = Path.Combine(trimmed, fileName);
                if (File.Exists(exact))
                {
                    return Path.GetFullPath(exact);
                }
            }
        }

        throw new FileNotFoundException($"Executable '{fileName}' was not found on PATH.", fileName);
    }
}
