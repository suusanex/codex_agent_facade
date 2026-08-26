using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>
/// Streamable HTTP MCP host の起動オプション。token と listen port は環境変数から供給する。
/// </summary>
public sealed class McpHttpHostOptions
{
    public required string Token { get; init; }

    public int Port { get; init; } = McpHttpHost.DefaultPort;

    public IProcessRunner? ProcessRunner { get; init; }

    public IAgentRunLogFactory? RunLogFactory { get; init; }
}

/// <summary>
/// loopback 専用の stateless Streamable HTTP MCP host を組み立てる。
/// </summary>
public static class McpHttpHost
{
    public const int DefaultPort = 18765;
    public const string TokenEnvironmentVariable = "CODEX_AGENT_FACADE_TOKEN";
    public const string PortEnvironmentVariable = "CODEX_AGENT_FACADE_PORT";
    public const string McpPath = "/mcp";
    public const string AuthenticationScheme = "SharedSecret";

    public static McpHttpHostOptions FromEnvironment()
    {
        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"Environment variable {TokenEnvironmentVariable} is required.");
        }

        var portText = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        var port = DefaultPort;
        if (!string.IsNullOrWhiteSpace(portText))
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port < 1
                || port > 65535)
            {
                throw new ArgumentException(
                    $"Environment variable {PortEnvironmentVariable} must be an integer between 1 and 65535.");
            }
        }

        return new McpHttpHostOptions
        {
            Token = token,
            Port = port,
        };
    }

    public static WebApplication Create(string[] args, McpHttpHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new ArgumentException("Token is required.", nameof(options));
        }

        if (options.Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Port must be between 0 and 65535.");
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost";
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, options.Port);
            kestrel.Limits.MinRequestBodyDataRate = null;
            kestrel.Limits.MinResponseDataRate = null;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromHours(2);
        });
        builder.Logging.AddConsole(logging =>
        {
            logging.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IProcessRunner>(options.ProcessRunner ?? new ProcessRunner());
        builder.Services.AddSingleton<IAgentRunLogFactory>(options.RunLogFactory ?? new AgentRunLogFactory());
        builder.Services.AddSingleton<GitHubCopilotDriver>();
        builder.Services.AddSingleton<GrokBuildDriver>();
        builder.Services.AddSingleton<AgentFacade>();
        builder.Services
            .AddAuthentication(AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, SharedSecretAuthenticationHandler>(
                AuthenticationScheme,
                _ => { });
        builder.Services.AddAuthorization();
        builder.Services
            .AddMcpServer(mcp =>
            {
                mcp.ServerInfo = new Implementation
                {
                    Name = "codex-agent-facade",
                    Version = "0.1.0",
                };
                mcp.ServerInstructions =
                    "Thin messenger from Codex to GitHub Copilot or Grok Build. Call run_agent with agent, prompt, and working_directory. Do not replan or split the user's task. Pass the user prompt through. Reuse session_id to continue the same external agent session.";
            })
            .WithHttpTransport(http =>
            {
                http.SessionMode = HttpServerSessionMode.Stateless;
            })
            .WithTools<AgentTools>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp(McpPath).RequireAuthorization();
        return app;
    }

    public static Uri GetMcpEndpoint(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Server address feature is missing.");
        var loopback = addresses.Addresses.FirstOrDefault(address =>
            address.StartsWith("http://127.0.0.1", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(loopback))
        {
            throw new InvalidOperationException("Loopback address was not bound.");
        }

        return new Uri(loopback.TrimEnd('/') + McpPath);
    }
}

/// <summary>
/// 共有シークレットの Bearer token だけを検証する。OAuth は使わない。
/// </summary>
public sealed class SharedSecretAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly McpHttpHostOptions _hostOptions;

    public SharedSecretAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        McpHttpHostOptions hostOptions)
        : base(options, logger, encoder)
    {
        _hostOptions = hostOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var headerValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var header = headerValues.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid authorization scheme."));
        }

        var provided = Encoding.UTF8.GetBytes(header[prefix.Length..]);
        var expected = Encoding.UTF8.GetBytes(_hostOptions.Token);
        if (provided.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid token."));
        }

        var identity = new ClaimsIdentity(McpHttpHost.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, "mcp-client"));
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), McpHttpHost.AuthenticationScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
