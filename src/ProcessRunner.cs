using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// 外部 CLI プロセス起動のテスト境界。実OS変更を伴う処理はここだけに閉じる。
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 起動中プロセスの生存確認だけを公開する。実 Process は ProcessRunner 内に閉じる。
/// </summary>
public interface IProcessLifetime
{
    int Id { get; }
    bool HasExited { get; }
}

public sealed record ProcessLaunchInfo(
    string RequestedFileName,
    string ResolvedExecutable,
    string ProcessFileName,
    IReadOnlyList<string> ProcessArguments,
    bool UsedWindowsCmdWrapper);

public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    Action<string>? StdoutLineCallback = null,
    Action<string>? StderrLineCallback = null,
    Action<IProcessLifetime>? OnProcessStarted = null,
    Action<ProcessLaunchInfo>? OnLaunchResolved = null);

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
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = ExecutableResolver.Resolve(request.FileName);
        var startInfo = CreateStartInfo(resolved, request, out var usedWindowsCmdWrapper);
        request.OnLaunchResolved?.Invoke(new ProcessLaunchInfo(
            request.FileName,
            resolved,
            startInfo.FileName,
            [.. startInfo.ArgumentList],
            usedWindowsCmdWrapper));

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
        using var killOnClose = KillOnCloseJob.AssignOrThrow(process);
        request.OnProcessStarted?.Invoke(new ProcessLifetime(process));

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
        var stderrTask = ReadLinesAsync(process.StandardError, stderr, request.StderrLineCallback, cancellationToken);
        var readersTask = Task.WhenAll(stdoutTask, stderrTask);
        var waitTask = process.WaitForExitAsync(cancellationToken);

        try
        {
            // callback 失敗で pipe を読まなくなると WaitForExit が無限待ちになる。先に reader 故障を見て kill する。
            var finished = await Task.WhenAny(waitTask, readersTask).ConfigureAwait(false);
            if (finished == readersTask && readersTask.IsFaulted)
            {
                TryKill(process);
            }

            await waitTask.ConfigureAwait(false);
            await readersTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            TryKill(process);
            throw UnwrapProcessException(ex);
        }

        return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static ProcessStartInfo CreateStartInfo(
        string resolvedExecutable,
        ProcessRunRequest request,
        out bool usedWindowsCmdWrapper)
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
        usedWindowsCmdWrapper = isWindowsScript;

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

    private sealed class ProcessLifetime : IProcessLifetime
    {
        private readonly Process _process;

        public ProcessLifetime(Process process)
        {
            _process = process;
        }

        public int Id
        {
            get
            {
                try
                {
                    return _process.Id;
                }
                catch (Exception ex)
                {
                    CliJson.TraceException(ex);
                    return 0;
                }
            }
        }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (Exception ex)
                {
                    CliJson.TraceException(ex);
                    return true;
                }
            }
        }
    }

    private static Exception UnwrapProcessException(Exception exception)
    {
        if (exception is not AggregateException aggregate)
        {
            return exception;
        }

        var flattened = aggregate.Flatten().InnerExceptions;
        foreach (var inner in flattened)
        {
            if (inner is not IOException)
            {
                return inner;
            }
        }

        return flattened[0];
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

/// <summary>
/// Windows Job Object で KILL_ON_JOB_CLOSE を付け、Facade プロセス終了時に子 CLI も終了させる。
/// 非 Windows では何もしない。
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly nint _handle;
    private int _disposed;

    private KillOnCloseJob(nint handle)
    {
        _handle = handle;
    }

    public static KillOnCloseJob? AssignOrThrow(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObjectW(0, null);
        if (handle == 0)
        {
            throw new InvalidOperationException("Failed to create a Windows job object.");
        }

        var job = new KillOnCloseJob(handle);
        try
        {
            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
                {
                    throw new InvalidOperationException("Failed to set KILL_ON_JOB_CLOSE on the Windows job object.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                throw new InvalidOperationException("Failed to assign the child process to the Windows job object.");
            }
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            job.Dispose();
            throw;
        }

        return job;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_handle != 0)
        {
            CloseHandle(_handle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        nint job,
        int infoClass,
        nint jobObjectInfo,
        uint jobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
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
