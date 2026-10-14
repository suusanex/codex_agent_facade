#:property TargetFramework=net10.0
#:property OutputType=Exe
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property PackAsTool=false
#:property NoWarn=CA2266
#:package NLog.Extensions.Logging@6.2.0
#:include AgentFacade.cs
#:include ProcessRunner.cs
#:include AgentRunLog.cs
#:include SecretRedactor.cs
#:include FacadeLogging.cs
#:include GitHubCopilotDriver.cs
#:include GrokBuildDriver.cs
#:include CursorCliDriver.cs
#:include CodexCliDriver.cs
#:include AgentJob.cs
#:include RunLogViewerLauncher.cs
#:include AgentCli.cs

using System.Text;
using Microsoft.Extensions.Logging;

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.InputEncoding = utf8;
Console.OutputEncoding = utf8;

var productDirectory = ResolveProductDirectory();
var diagnosticLogPath = FacadeLogging.GetCliLogFilePath(productDirectory, Environment.ProcessId);
using var nlogFactory = FacadeLogging.CreateCliNLogFactory(productDirectory, Environment.ProcessId);
using var loggerFactory = FacadeLogging.CreateLoggerFactory(nlogFactory);
using var loggerScope = FacadeLog.UseLoggerFactory(loggerFactory);

try
{
    if (args.Length != 0)
    {
        return AgentCli.WriteInvalidArguments(Console.Out);
    }

    using var cancellation = new CancellationTokenSource();
    try
    {
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
    }
    catch (Exception ex)
    {
        // リダイレクト起動ではコンソール制御ハンドラを持てない場合がある。
        // その場合もプロセス終了時の子プロセス停止は ProcessRunner の Job Object が担う。
        CliJson.TraceException(ex);
    }

    string input;
    try
    {
        input = await Console.In.ReadToEndAsync(cancellation.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException ex)
    {
        CliJson.TraceException(ex);
        return AgentCli.WriteCancelled(Console.Out);
    }

    var processRunner = new ProcessRunner();
    var facade = new AgentFacade(
        new GitHubCopilotDriver(processRunner),
        new GrokBuildDriver(processRunner),
        new CursorCliDriver(processRunner),
        new CodexCliDriver(processRunner),
        new AgentRunLogFactory(ResolveRunLogDirectory(productDirectory), TimeProvider.System),
        new WindowsRunLogViewerLauncher(
            new FileRunLogViewerSettingsSource(Path.Combine(productDirectory, FacadeUserConfig.FileName))));
    return await AgentCli.ExecuteAsync(facade, input, Console.Out, cancellation.Token, diagnosticLogPath).ConfigureAwait(false);
}
catch (Exception ex)
{
    CliJson.TraceException(ex);
    Console.Error.WriteLine(AgentCli.StartupFailureError(diagnosticLogPath));
    return AgentCli.ExitFailed;
}

static string ResolveProductDirectory()
{
    var home = Environment.GetEnvironmentVariable("USERPROFILE");
    if (string.IsNullOrWhiteSpace(home))
    {
        home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    return Path.Combine(Path.GetFullPath(home), "." + AgentRunLogFactory.DefaultProductDirectoryName);
}

static string ResolveRunLogDirectory(string productDirectory)
{
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AgentRunLogFactory.LogDirectoryEnvironmentVariable)))
    {
        return Path.Combine(productDirectory, "runs");
    }

    return AgentRunLogFactory.GetDefaultLogDirectory();
}
