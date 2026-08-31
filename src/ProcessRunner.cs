using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

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
    IReadOnlyList<string> LogicalArguments,
    bool UsedWindowsCmdWrapper,
    string? RawArguments = null,
    string Wrapper = "none",
    bool HasStandardInput = false,
    long? StandardInputByteCount = null);

public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    Action<string>? StdoutLineCallback = null,
    Action<string>? StderrLineCallback = null,
    Action<IProcessLifetime>? OnProcessStarted = null,
    Action<ProcessLaunchInfo>? OnLaunchResolved = null,
    string? StandardInputText = null);

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// 起動済み子プロセスを OS の終了保証へ関連付ける。Windows では Job Object。テストでは失敗を注入する。
/// </summary>
public interface IProcessJobGuard
{
    IDisposable? Assign(Process process);
}

/// <summary>
/// UseShellExecute を使わず stdout/stderr を収集する。Windows script は適切な host 経由で起動する。
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly IProcessJobGuard _jobGuard;
    private readonly IProcessEncodingProvider _encodingProvider;

    public ProcessRunner()
        : this(
            OperatingSystem.IsWindows() ? new WindowsKillOnCloseJobGuard() : NullProcessJobGuard.Instance,
            SystemProcessEncodingProvider.Instance)
    {
    }

    public ProcessRunner(IProcessJobGuard jobGuard)
        : this(jobGuard, SystemProcessEncodingProvider.Instance)
    {
    }

    internal ProcessRunner(IProcessJobGuard jobGuard, IProcessEncodingProvider encodingProvider)
    {
        ArgumentNullException.ThrowIfNull(jobGuard);
        ArgumentNullException.ThrowIfNull(encodingProvider);
        _jobGuard = jobGuard;
        _encodingProvider = encodingProvider;
    }

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        string resolved;
        ProcessStartInfo startInfo;
        ProcessWrapperKind wrapperKind;
        try
        {
            resolved = ExecutableResolver.Resolve(request.FileName);
            startInfo = CreateStartInfo(resolved, request, out wrapperKind);
            request.OnLaunchResolved?.Invoke(new ProcessLaunchInfo(
                request.FileName,
                resolved,
                startInfo.FileName,
                wrapperKind == ProcessWrapperKind.WindowsCmd
                    ? ["/d", "/v:off", "/s", "/c"]
                    : [.. startInfo.ArgumentList],
                request.Arguments,
                wrapperKind == ProcessWrapperKind.WindowsCmd,
                wrapperKind == ProcessWrapperKind.WindowsCmd ? startInfo.Arguments : null,
                GetWrapperName(wrapperKind),
                request.StandardInputText is not null,
                request.StandardInputText is null
                    ? null
                    : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                        .GetByteCount(request.StandardInputText)));
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            throw;
        }

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

        IDisposable? killOnClose = null;
        try
        {
            killOnClose = _jobGuard.Assign(process);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            TryKill(process);
            throw;
        }

        using (killOnClose)
        {
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

        var stdoutTask = ReadTextLinesAsync(process.StandardOutput, stdout, request.StdoutLineCallback, cancellationToken);
        var stderrTask = ReadLinesAsync(
            process.StandardError.BaseStream,
            stderr,
            request.StderrLineCallback,
            wrapperKind == ProcessWrapperKind.WindowsCmd,
            _encodingProvider,
            cancellationToken);
        var stdinTask = WriteStandardInputAsync(process, request.StandardInputText, cancellationToken);
        var ioTasks = new[] { stdoutTask, stderrTask, stdinTask };
        var waitTask = process.WaitForExitAsync(cancellationToken);

        try
        {
            // 任意のI/O taskが失敗した時点でkillし、残りのpipeが閉じるのを待つ。
            var pendingIoTasks = new HashSet<Task>(ioTasks);
            while (pendingIoTasks.Count > 0)
            {
                var finished = await Task.WhenAny([waitTask, .. pendingIoTasks]).ConfigureAwait(false);
                if (finished == waitTask)
                {
                    break;
                }

                pendingIoTasks.Remove(finished);
                if (finished.IsFaulted || finished.IsCanceled)
                {
                    TryKill(process);
                    break;
                }
            }

            await waitTask.ConfigureAwait(false);
            await Task.WhenAll(ioTasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CliJson.TraceException(ex);
            TryKill(process);
            throw UnwrapProcessException(ex);
        }

        return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string resolvedExecutable,
        ProcessRunRequest request,
        out ProcessWrapperKind wrapperKind)
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
        };

        var isWindowsScript = OperatingSystem.IsWindows()
            && (resolvedExecutable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || resolvedExecutable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        if (isWindowsScript)
        {
            // cmd.exe の /c は1本の raw command string として渡し、ArgumentList の
            // 再エスケープだけを避ける。値の引用は対象バッチの %1 / %* に依存する。
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var launch = WindowsCmd.BuildCommandWithEnvironment(resolvedExecutable, request.Arguments);
            startInfo.Arguments = $"/d /v:off /s /c \"{launch.Command}\"";
            foreach (var pair in launch.EnvironmentVariables)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
            wrapperKind = ProcessWrapperKind.WindowsCmd;
        }
        else if (resolvedExecutable.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = ExecutableResolver.Resolve(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(resolvedExecutable);
            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            wrapperKind = ProcessWrapperKind.PowerShell;
        }
        else
        {
            startInfo.FileName = resolvedExecutable;
            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            wrapperKind = ProcessWrapperKind.None;
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

    private static string GetWrapperName(ProcessWrapperKind wrapperKind)
    {
        return wrapperKind switch
        {
            ProcessWrapperKind.WindowsCmd => "windows-cmd",
            ProcessWrapperKind.PowerShell => "powershell",
            _ => "none",
        };
    }

    private static async Task WriteStandardInputAsync(
        Process process,
        string? text,
        CancellationToken cancellationToken)
    {
        if (text is null)
        {
            process.StandardInput.Close();
            return;
        }

        try
        {
            // TextWriterのWriteAsyncは本文を変換・改行追加せず、指定されたUTF-8文字列だけを送る。
            await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    private static async Task ReadLinesAsync(
        Stream stream,
        StringBuilder sink,
        Action<string>? onLine,
        bool windowsCmdWrapper,
        IProcessEncodingProvider encodingProvider,
        CancellationToken cancellationToken)
    {
        var readBuffer = new byte[4096];
        var lineBuffer = new List<byte>(256);
        var pendingCarriageReturn = false;
        while (true)
        {
            var read = await stream.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var value = readBuffer[index];
                if (pendingCarriageReturn)
                {
                    AppendDecodedLine(lineBuffer, sink, onLine, windowsCmdWrapper, encodingProvider);
                    lineBuffer.Clear();
                    pendingCarriageReturn = false;
                    if (value == (byte)'\n')
                    {
                        continue;
                    }
                }

                if (value == (byte)'\r')
                {
                    pendingCarriageReturn = true;
                }
                else if (value == (byte)'\n')
                {
                    AppendDecodedLine(lineBuffer, sink, onLine, windowsCmdWrapper, encodingProvider);
                    lineBuffer.Clear();
                }
                else
                {
                    lineBuffer.Add(value);
                }
            }

        }

        if (pendingCarriageReturn || lineBuffer.Count > 0)
        {
            AppendDecodedLine(lineBuffer, sink, onLine, windowsCmdWrapper, encodingProvider);
        }
    }

    private static async Task ReadTextLinesAsync(
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

    private static void AppendDecodedLine(
        List<byte> lineBuffer,
        StringBuilder sink,
        Action<string>? onLine,
        bool windowsCmdWrapper,
        IProcessEncodingProvider encodingProvider)
    {
        var line = StderrDecoder.Decode(lineBuffer.ToArray(), windowsCmdWrapper, encodingProvider);
        if (sink.Length > 0)
        {
            sink.Append('\n');
        }

        sink.Append(line);
        onLine?.Invoke(line);
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

internal enum ProcessWrapperKind
{
    None,
    WindowsCmd,
    PowerShell,
}

/// <summary>
/// cmd.exe 用の raw command string を組み立て、literal percent は環境変数の置換結果で表現する。
/// </summary>
internal static class WindowsCmd
{
    private const string EnvironmentVariablePrefix = "__CODEX_AGENT_FACADE_ARG_";

    public static WindowsCmdLaunch BuildCommandWithEnvironment(
        string executable,
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var environmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prefix = EnvironmentVariablePrefix + Guid.NewGuid().ToString("N") + "_";
        var percentVariable = prefix + "PERCENT";
        environmentVariables[percentVariable] = "%";

        var command = new StringBuilder(QuoteArgument(executable, percentVariable));
        foreach (var argument in arguments)
        {
            command.Append(' ').Append(QuoteArgument(argument, percentVariable));
        }

        return new WindowsCmdLaunch(command.ToString(), environmentVariables);
    }

    private static string QuoteArgument(string value, string percentVariable)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(percentVariable);
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            // cmd.exe はバッチを呼び出す境界で CR/LF をコマンド区切りとして扱うため、
            // 対象スクリプトを変更せずに %1 / %* の1引数へ復元する表現が存在しない。
            throw new ArgumentException(
                "Windows cmd arguments cannot contain NUL, CR, or LF.",
                nameof(value));
        }

        // cmd.exe の percent 展開は変数の置換結果を再展開しないため、環境変数に
        // literal '%' を置いてから raw command string へ展開する。
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var trailingBackslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                trailingBackslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', trailingBackslashes * 2);
                builder.Append("\"\"");
            }
            else if (character == '%')
            {
                builder.Append('\\', trailingBackslashes);
                builder.Append('%').Append(percentVariable).Append('%');
            }
            else
            {
                builder.Append('\\', trailingBackslashes);
                builder.Append(character);
            }

            trailingBackslashes = 0;
        }

        builder.Append('\\', trailingBackslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}

internal sealed record WindowsCmdLaunch(
    string Command,
    IReadOnlyDictionary<string, string> EnvironmentVariables);

internal interface IProcessEncodingProvider
{
    Encoding GetWindowsOemEncoding();
}

internal sealed class SystemProcessEncodingProvider : IProcessEncodingProvider
{
    public static SystemProcessEncodingProvider Instance { get; } = new();

    public Encoding GetWindowsOemEncoding()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows OEM encoding is only available on Windows.");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var codePage = GetOEMCP();
        if (codePage == 0)
        {
            throw new InvalidOperationException("Windows OEM code page could not be determined.");
        }

        return Encoding.GetEncoding(
            checked((int)codePage),
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    // .NET の Encoding.Default は UTF-8 であり、WinExe や Task Scheduler では
    // Console.OutputEncoding に依存できないため、Windows の OEM API で判定する。
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}

internal static class StderrDecoder
{
    private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);

    public static string Decode(
        byte[] bytes,
        bool windowsCmdWrapper,
        IProcessEncodingProvider encodingProvider)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(encodingProvider);

        if (Utf8.IsValid(bytes))
        {
            return Utf8Strict.GetString(bytes);
        }

        if (!windowsCmdWrapper)
        {
            throw new DecoderFallbackException("stderr contains invalid UTF-8.");
        }

        return encodingProvider.GetWindowsOemEncoding().GetString(bytes);
    }
}

public sealed class NullProcessJobGuard : IProcessJobGuard
{
    public static NullProcessJobGuard Instance { get; } = new();

    public IDisposable? Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return null;
    }
}

public sealed class WindowsKillOnCloseJobGuard : IProcessJobGuard
{
    public IDisposable? Assign(Process process)
    {
        return KillOnCloseJob.AssignOrThrow(process);
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
