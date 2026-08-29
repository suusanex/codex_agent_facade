#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property OutputType=WinExe
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property PackAsTool=false
#:property NoWarn=CA2266
#:package ModelContextProtocol.AspNetCore@2.2.0
#:package NLog.Extensions.Logging@6.2.0
#:include AgentFacade.cs
#:include ProcessRunner.cs
#:include AgentRunLog.cs
#:include SecretRedactor.cs
#:include FacadeLogging.cs
#:include GitHubCopilotDriver.cs
#:include GrokBuildDriver.cs
#:include AgentTools.cs
#:include AgentJob.cs
#:include AgentJobService.cs
#:include McpHttpHost.cs

using Microsoft.Extensions.Logging;

var logDirectory = FacadeLogging.GetDefaultDirectory();
using var nlogFactory = FacadeLogging.CreateNLogFactory(logDirectory);
using var loggerFactory = FacadeLogging.CreateLoggerFactory(nlogFactory);
using var loggerScope = FacadeLog.UseLoggerFactory(loggerFactory);
var logger = loggerFactory.CreateLogger(FacadeLogging.LoggerCategory);

try
{
    logger.LogInformation("Starting Codex Agent Facade.");
    var envOptions = McpHttpHost.FromEnvironment();
    var options = new McpHttpHostOptions
    {
        Token = envOptions.Token,
        Port = envOptions.Port,
        LogFactory = nlogFactory,
        ServerLogDirectory = logDirectory,
    };
    await using var app = McpHttpHost.Create(args, options);
    await app.StartAsync();
    logger.LogInformation("Listening on {Endpoint}", McpHttpHost.GetMcpEndpoint(app));
    await app.WaitForShutdownAsync();
    logger.LogInformation("Stopped.");
}
catch (Exception ex)
{
    CliJson.TraceException(ex);
    throw;
}
