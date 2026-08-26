#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property PackAsTool=false
#:property NoWarn=CA2266
#:package ModelContextProtocol.AspNetCore@2.2.0
#:include AgentFacade.cs
#:include ProcessRunner.cs
#:include AgentRunLog.cs
#:include SecretRedactor.cs
#:include GitHubCopilotDriver.cs
#:include GrokBuildDriver.cs
#:include AgentTools.cs
#:include McpHttpHost.cs

using System.Diagnostics;

Trace.Listeners.Add(new ConsoleTraceListener(useErrorStream: true));

try
{
    var options = McpHttpHost.FromEnvironment();
    var app = McpHttpHost.Create(args, options);
    Trace.TraceInformation(
        "Listening on http://127.0.0.1:{0}{1}",
        options.Port,
        McpHttpHost.McpPath);
    await app.RunAsync();
}
catch (Exception ex)
{
    CliJson.TraceException(ex);
    throw;
}
