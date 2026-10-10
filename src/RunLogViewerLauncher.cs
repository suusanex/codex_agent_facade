using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// ユーザー領域の Facade 設定。publish 成果物とは別パスに置く。
/// </summary>
public static class FacadeUserConfig
{
    public const string FileName = "config.json";

    public static string GetDefaultPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "." + AgentRunLogFactory.DefaultProductDirectoryName,
            FileName);
    }
}

/// <summary>
/// run log viewer の有効フラグ。未設定時は無効。既存の job 開始で Terminal を開かない。
/// </summary>
public sealed record RunLogViewerSettings(bool Enabled)
{
    public static RunLogViewerSettings Disabled { get; } = new(false);
}

public interface IRunLogViewerSettingsSource
{
    RunLogViewerSettings Load();
}

public sealed class FileRunLogViewerSettingsSource : IRunLogViewerSettingsSource
{
    private readonly string _path;
    private readonly ILogger? _logger;

    public FileRunLogViewerSettingsSource(string path, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = logger;
    }

    public string Path => _path;

    public RunLogViewerSettings Load()
    {
        try
        {
            // File.Exists はアクセス拒否などを false に畳む。無い設定だけを無言で無効にし、
            // それ以外の読み取り失敗は下の診断ログへ落とす。
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                LogInvalid("Run log viewer config root must be an object.");
                return RunLogViewerSettings.Disabled;
            }

            if (!root.TryGetProperty("runLogViewer", out var section))
            {
                return RunLogViewerSettings.Disabled;
            }

            if (section.ValueKind != JsonValueKind.Object)
            {
                LogInvalid("runLogViewer must be an object.");
                return RunLogViewerSettings.Disabled;
            }

            if (!section.TryGetProperty("enabled", out var enabled))
            {
                return RunLogViewerSettings.Disabled;
            }

            if (enabled.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                LogInvalid("runLogViewer.enabled must be a boolean.");
                return RunLogViewerSettings.Disabled;
            }

            return new RunLogViewerSettings(enabled.GetBoolean());
        }
        catch (FileNotFoundException)
        {
            return RunLogViewerSettings.Disabled;
        }
        catch (DirectoryNotFoundException)
        {
            return RunLogViewerSettings.Disabled;
        }
        catch (Exception ex)
        {
            // 設定不備で agent job を失敗させない。観測は無効のまま続行する。
            Logger.LogError("{Exception}", SecretRedactor.RedactText(ex.ToString()));
            Logger.LogWarning(
                "Failed to read run log viewer settings. Run log viewer stays disabled. path={Path}",
                _path);
            return RunLogViewerSettings.Disabled;
        }
    }

    private void LogInvalid(string message)
    {
        Logger.LogWarning("{Message} Run log viewer stays disabled. path={Path}", message, _path);
    }

    private ILogger Logger => _logger ?? FacadeLog.CreateLogger(FacadeLogging.LoggerCategory);
}

public interface IOperatingSystemInfo
{
    bool IsWindows { get; }
}

public sealed class RuntimeOperatingSystemInfo : IOperatingSystemInfo
{
    public bool IsWindows => OperatingSystem.IsWindows();
}

public sealed record RunLogViewerCommand(string FileName, IReadOnlyList<string> Arguments);

public interface IRunLogViewerCommandResolver
{
    RunLogViewerCommand? TryResolve(string textLogPath, string eventsLogPath);
}

/// <summary>
/// publish 済みの隣の exe を優先し、無いときはリポジトリの file-based app を探す。
/// viewer は console プロセスである必要があり、Facade の WinExe には同梱できない。
/// 隣の file-based app を解決する BCL は無い。
/// </summary>
public sealed class RunLogViewerCommandResolver : IRunLogViewerCommandResolver
{
    public const string PublishedExecutableName = "RunLogViewer.exe";
    public const string SourceFileName = "RunLogViewer.cs";

    private readonly string? _processDirectory;
    private readonly IReadOnlyList<string> _sourceSearchStarts;

    public RunLogViewerCommandResolver()
        : this(
            Path.GetDirectoryName(Environment.ProcessPath),
            [Environment.CurrentDirectory, AppContext.BaseDirectory])
    {
    }

    public RunLogViewerCommandResolver(string? processDirectory, IReadOnlyList<string> sourceSearchStarts)
    {
        ArgumentNullException.ThrowIfNull(sourceSearchStarts);
        _processDirectory = processDirectory;
        _sourceSearchStarts = sourceSearchStarts;
    }

    public RunLogViewerCommand? TryResolve(string textLogPath, string eventsLogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textLogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventsLogPath);

        var published = FindPublishedExecutable();
        if (published is not null)
        {
            return new RunLogViewerCommand(published, [textLogPath, eventsLogPath]);
        }

        var source = FindViewerSource();
        if (source is null)
        {
            return null;
        }

        return new RunLogViewerCommand(
            "dotnet",
            ["run", "--file", source, "--verbosity", "minimal", "--", textLogPath, eventsLogPath]);
    }

    private string? FindPublishedExecutable()
    {
        if (string.IsNullOrWhiteSpace(_processDirectory))
        {
            return null;
        }

        var path = Path.Combine(_processDirectory, PublishedExecutableName);
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private string? FindViewerSource()
    {
        foreach (var start in _sourceSearchStarts)
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var dir = new DirectoryInfo(start);
            for (var depth = 0; depth < 8 && dir is not null; depth++)
            {
                var candidate = Path.Combine(dir.FullName, "src", SourceFileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}

public sealed record WindowsTerminalLaunchCommand(string FileName, IReadOnlyList<string> Arguments);

public interface IWindowsTerminalProcessStarter
{
    void Start(WindowsTerminalLaunchCommand command);
}

public static class WindowsTerminalCommandBuilder
{
    public static WindowsTerminalLaunchCommand Build(
        string windowsTerminalFileName,
        string jobId,
        RunLogViewerCommand viewer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsTerminalFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(viewer);
        // -w new は既存ウィンドウへのタブ追加ではなく、常に専用ウィンドウを作る。
        // --suppressApplicationTitle は job id のタイトルを exe 名で上書きされないようにする。
        var arguments = new List<string>
        {
            "-w",
            "new",
            "new-tab",
            "--suppressApplicationTitle",
            "--title",
            "codex-agent-facade " + jobId,
            viewer.FileName,
        };
        arguments.AddRange(viewer.Arguments);
        return new WindowsTerminalLaunchCommand(windowsTerminalFileName, arguments);
    }
}

internal static class WindowsCommandLine
{
    public static string Join(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', arguments.Select(QuoteForWindowsTerminal));
    }

    // wt.exe は Windows の argv 化のあとも ';' をコマンド区切りにする。引用符では区切られない。
    // 区切りにしない ';' は '\;' として渡し、Windows の引用はその後にかける。
    private static string QuoteForWindowsTerminal(string argument)
    {
        return Quote(argument.Replace(";", "\\;", StringComparison.Ordinal));
    }

    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (argument.IndexOfAny([' ', '\t', '"', ';']) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes);
                backslashes = 0;
            }

            builder.Append(ch);
        }

        if (backslashes > 0)
        {
            builder.Append('\\', backslashes * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }
}

/// <summary>
/// wt.exe は WindowsApps の実行エイリアスであることが多い。
/// UseShellExecute=false では起動できないことがあるため、シェル経由で起動する。
/// viewer の終了は待たず、ハンドルを閉じてもプロセスは殺さない。
/// </summary>
internal sealed class ShellWindowsTerminalProcessStarter : IWindowsTerminalProcessStarter
{
    public void Start(WindowsTerminalLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = WindowsCommandLine.Join(command.Arguments),
            UseShellExecute = true,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Windows Terminal.");
    }
}

public sealed class WindowsRunLogViewerLauncher : IRunLogViewerLauncher
{
    private readonly IRunLogViewerSettingsSource _settings;
    private readonly IOperatingSystemInfo _operatingSystem;
    private readonly IRunLogViewerCommandResolver _resolver;
    private readonly IWindowsTerminalProcessStarter _starter;
    private readonly string _windowsTerminalFileName;
    private readonly ILogger? _logger;

    public WindowsRunLogViewerLauncher(IRunLogViewerSettingsSource settings)
        : this(
            settings,
            new RuntimeOperatingSystemInfo(),
            new RunLogViewerCommandResolver(),
            new ShellWindowsTerminalProcessStarter(),
            "wt.exe")
    {
    }

    public WindowsRunLogViewerLauncher(
        IRunLogViewerSettingsSource settings,
        IOperatingSystemInfo operatingSystem,
        IRunLogViewerCommandResolver resolver,
        IWindowsTerminalProcessStarter starter,
        string windowsTerminalFileName,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(operatingSystem);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsTerminalFileName);
        _settings = settings;
        _operatingSystem = operatingSystem;
        _resolver = resolver;
        _starter = starter;
        _windowsTerminalFileName = windowsTerminalFileName;
        _logger = logger;
    }

    public void TryLaunch(string jobId, string textLogPath, string eventsLogPath)
    {
        try
        {
            if (!_settings.Load().Enabled)
            {
                return;
            }

            if (!_operatingSystem.IsWindows)
            {
                Logger.LogWarning(
                    "Run log viewer is enabled, but the Windows Terminal viewer is not used on this OS. jobId={JobId}",
                    jobId);
                return;
            }

            var viewer = _resolver.TryResolve(textLogPath, eventsLogPath);
            if (viewer is null)
            {
                Logger.LogWarning(
                    "Run log viewer is enabled, but the viewer executable was not found. jobId={JobId}",
                    jobId);
                return;
            }

            var command = WindowsTerminalCommandBuilder.Build(_windowsTerminalFileName, jobId, viewer);
            _starter.Start(command);
            Logger.LogInformation(
                "Launched run log viewer. jobId={JobId} textLog={TextLogPath}",
                jobId,
                textLogPath);
        }
        catch (Exception ex)
        {
            // 観測の起動失敗は agent job の失敗にしない。
            Logger.LogError("{Exception}", SecretRedactor.RedactText(ex.ToString()));
            Logger.LogWarning(
                "Failed to launch run log viewer. The agent job will continue. jobId={JobId}",
                jobId);
        }
    }

    private ILogger Logger => _logger ?? FacadeLog.CreateLogger(FacadeLogging.LoggerCategory);
}
