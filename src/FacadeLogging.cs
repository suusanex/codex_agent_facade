using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using MsLogger = Microsoft.Extensions.Logging.ILogger;
using MsLoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory;
using MsLoggerProvider = Microsoft.Extensions.Logging.ILoggerProvider;
using MsLoggingBuilder = Microsoft.Extensions.Logging.ILoggingBuilder;
using NLogLevel = NLog.LogLevel;

/// <summary>
/// server / host 診断ログ。run log や job store とは別ファイルへ、NLog rolling file として書く。
/// </summary>
internal static class FacadeLogging
{
    public const string FileName = "server.log";
    public const string LoggerCategory = "CodexAgentFacade";
    public const long ArchiveAboveSizeBytes = 1024 * 1024;
    public const int MaxArchiveFiles = 4;
    public const string ArchiveSuffixFormat = ".{0}";

    public static string GetDefaultDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "." + AgentRunLogFactory.DefaultProductDirectoryName);
    }

    public static string GetLogFilePath(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Path.Combine(directory, FileName);
    }

    public static FileTarget CreateFileTarget(string directory)
    {
        return CreateFileTarget(directory, ArchiveAboveSizeBytes, MaxArchiveFiles);
    }

    public static FileTarget CreateFileTarget(string directory, long archiveAboveSizeBytes, int maxArchiveFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(archiveAboveSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxArchiveFiles);
        Directory.CreateDirectory(directory);
        var fileName = GetLogFilePath(directory);
        return new FileTarget("server")
        {
            FileName = fileName,
            ArchiveFileName = fileName,
            ArchiveAboveSize = archiveAboveSizeBytes,
            MaxArchiveFiles = maxArchiveFiles,
            ArchiveSuffixFormat = ArchiveSuffixFormat,
            KeepFileOpen = true,
            Encoding = Encoding.UTF8,
            Layout = "${longdate}|${level:uppercase=true}|${logger}|${message}",
            CreateDirs = true,
        };
    }

    public static LoggingConfiguration CreateConfiguration(string directory)
    {
        return CreateConfiguration(directory, ArchiveAboveSizeBytes, MaxArchiveFiles);
    }

    public static LoggingConfiguration CreateConfiguration(
        string directory,
        long archiveAboveSizeBytes,
        int maxArchiveFiles)
    {
        var target = CreateFileTarget(directory, archiveAboveSizeBytes, maxArchiveFiles);
        var config = new LoggingConfiguration();
        config.AddRule(NLogLevel.Info, NLogLevel.Fatal, target);
        return config;
    }

    public static LogFactory CreateNLogFactory(string directory)
    {
        var factory = new LogFactory();
        factory.ThrowConfigExceptions = true;
        factory.Configuration = CreateConfiguration(directory);
        return factory;
    }

    public static LogFactory CreateNLogFactory(string directory, long archiveAboveSizeBytes, int maxArchiveFiles)
    {
        var factory = new LogFactory();
        factory.ThrowConfigExceptions = true;
        factory.Configuration = CreateConfiguration(directory, archiveAboveSizeBytes, maxArchiveFiles);
        return factory;
    }

    public static MsLoggerFactory CreateLoggerFactory(LogFactory logFactory)
    {
        ArgumentNullException.ThrowIfNull(logFactory);
        return Microsoft.Extensions.Logging.LoggerFactory.Create(builder => Configure(builder, logFactory));
    }

    public static void Configure(MsLoggingBuilder logging, LogFactory logFactory)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(logFactory);
        logging.ClearProviders();
        logging.SetMinimumLevel(MsLogLevel.Information);
        logging.AddFilter("Microsoft", MsLogLevel.Warning);
        logging.AddFilter("Microsoft.Hosting.Lifetime", MsLogLevel.Information);
        logging.AddFilter("ModelContextProtocol", MsLogLevel.Warning);
        logging.AddProvider(new RedactingLoggerProvider(
            new NLogLoggerProvider(new NLogProviderOptions { ShutdownOnDispose = false }, logFactory)));
    }
}

/// <summary>
/// host 構築前や DI 外からの例外記録。ILogger へ集約する。
/// </summary>
internal static class FacadeLog
{
    private static readonly object Gate = new();
    private static MsLoggerFactory _factory = NullLoggerFactory.Instance;

    public static MsLoggerFactory Factory
    {
        get
        {
            lock (Gate)
            {
                return _factory;
            }
        }
    }

    public static void SetLoggerFactory(MsLoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Gate)
        {
            _factory = factory;
        }
    }

    public static IDisposable UseLoggerFactory(MsLoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Gate)
        {
            var previous = _factory;
            _factory = factory;
            return new Restore(previous);
        }
    }

    public static MsLogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return Factory.CreateLogger(categoryName);
    }

    public static void Exception(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var logger = CreateLogger(FacadeLogging.LoggerCategory);
        logger.LogError("{Exception}", SecretRedactor.RedactText(exception.ToString()));
    }

    private sealed class Restore : IDisposable
    {
        private readonly MsLoggerFactory _previous;
        private int _disposed;

        public Restore(MsLoggerFactory previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (Gate)
            {
                _factory = _previous;
            }
        }
    }
}

/// <summary>
/// 既知の secret を ILogger 出力から落とす。NLog へ渡す前に適用する。
/// </summary>
internal sealed class RedactingLoggerProvider : MsLoggerProvider
{
    private readonly MsLoggerProvider _inner;

    public RedactingLoggerProvider(MsLoggerProvider inner)
    {
        _inner = inner;
    }

    public MsLogger CreateLogger(string categoryName)
    {
        return new RedactingLogger(_inner.CreateLogger(categoryName));
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}

internal sealed class RedactingLogger : MsLogger
{
    private readonly MsLogger _inner;

    public RedactingLogger(MsLogger inner)
    {
        _inner = inner;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return _inner.BeginScope(state);
    }

    public bool IsEnabled(MsLogLevel logLevel)
    {
        return _inner.IsEnabled(logLevel);
    }

    public void Log<TState>(
        MsLogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);
        var message = formatter(state, exception);
        if (exception is not null)
        {
            message = string.IsNullOrEmpty(message)
                ? exception.ToString()
                : message + Environment.NewLine + exception.ToString();
        }

        var redacted = SecretRedactor.RedactText(message);
        _inner.Log(logLevel, eventId, redacted, exception: null, static (s, _) => s);
    }
}
