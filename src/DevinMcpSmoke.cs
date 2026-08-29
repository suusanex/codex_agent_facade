#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property PackAsTool=false
#:property NoWarn=CA2266
#:package ModelContextProtocol@2.2.0

using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>
/// 実 HTTP MCP 経路で Devin CLI まで一周する人手実行用スモーク。
/// 通常の unit test からは呼ばない。実 <c>devin</c> を起動するため、CI や <c>tests/CodexAgentFacade.Tests.cs</c> には載せない。
/// </summary>
return await DevinMcpSmoke.RunAsync(args);

internal static class DevinMcpSmoke
{
    internal const string AgentName = "devin-cli";
    internal const string SmokeFileName = "DEVIN_SMOKE.txt";
    internal const string ExpectedFileContents = "devin facade smoke ok";
    // swe-1-6-fast は Free plan で "Upgrade to Pro to access this model" になる。
    // スモークでは DEVIN_MODEL を付けず、アカウント既定（Free で使えるモデル）を使う。
    internal const int DefaultSmokePort = 18767;
    internal const int ServerStartTimeoutSeconds = 90;
    internal const int JobTimeoutMinutes = 10;
    internal const int CodexProbeTimeoutSeconds = 90;

    internal const string Prompt =
        "Create DEVIN_SMOKE.txt with exactly one line: devin facade smoke ok\n"
        + "Do not create, edit, or delete any other files.";

    private static string? SmokeToken;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        _ = args;
        var repoRoot = FindRepoRoot();
        var facadeEntry = Path.Combine(repoRoot, "src", "CodexAgentFacade.cs");
        var smokeRoot = Directory.CreateTempSubdirectory("caf-devin-mcp-smoke-");
        var workspace = Directory.CreateDirectory(Path.Combine(smokeRoot.FullName, "workspace"));
        var runLogDir = Path.Combine(smokeRoot.FullName, "runs");
        var serverStdoutPath = Path.Combine(smokeRoot.FullName, "facade-stdout.log");
        var serverStderrPath = Path.Combine(smokeRoot.FullName, "facade-stderr.log");
        Directory.CreateDirectory(runLogDir);

        Console.WriteLine("smokeRoot=" + smokeRoot.FullName);
        Console.WriteLine("workspace=" + workspace.FullName);
        Console.WriteLine("devinModel=(account default; DEVIN_MODEL is not set because swe-1-6-fast is Pro-only)");
        Console.WriteLine("repoRoot=" + repoRoot);

        Process? server = null;
        var token = Guid.NewGuid().ToString("N");
        SmokeToken = token;
        var port = FindFreePort(DefaultSmokePort);
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var serverLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex-agent-facade",
            "server.log");
        var serverLogOffset = File.Exists(serverLogPath) ? new FileInfo(serverLogPath).Length : 0L;
        var failed = false;
        JobSnapshot? lastSnapshot = null;
        string? jobId = null;

        try
        {
            PrintVersions();
            InitializeGitWorkspace(workspace.FullName);
            var before = SnapshotWorkspace(workspace.FullName);

            server = StartFacadeProcess(
                facadeEntry,
                repoRoot,
                token,
                port,
                runLogDir,
                serverStdoutPath,
                serverStderrPath);
            await WaitForListenAsync(port, server, TimeSpan.FromSeconds(ServerStartTimeoutSeconds));
            Console.WriteLine("facadePid=" + server.Id);
            Console.WriteLine("mcpEndpoint=" + endpoint);

            await using var client = await CreateMcpClientAsync(endpoint, token);
            var tools = await client.ListToolsAsync();
            var toolNames = tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Console.WriteLine("mcpTools=" + string.Join(",", toolNames));
            Require(toolNames.Contains("start_agent", StringComparer.Ordinal), "MCP tool start_agent is missing.");
            Require(toolNames.Contains("get_agent_job", StringComparer.Ordinal), "MCP tool get_agent_job is missing.");

            var requestId = "devin-mcp-smoke-" + Guid.NewGuid().ToString("N");
            Console.WriteLine("requestId=" + requestId);

            var started = await CallStartAgentAsync(client, requestId, workspace.FullName);
            jobId = started.JobId;
            Console.WriteLine("start_agent.status=" + started.Status);
            Console.WriteLine("jobId=" + started.JobId);
            Require(!string.IsNullOrWhiteSpace(started.JobId), "start_agent did not return a jobId.");
            Require(
                started.Status is "running" or "completed" or "failed" or "cancelled",
                "start_agent returned unexpected status: " + started.Status);

            lastSnapshot = await WaitForTerminalAsync(
                client,
                started.JobId,
                TimeSpan.FromMinutes(JobTimeoutMinutes));
            Console.WriteLine("terminal.status=" + lastSnapshot.Status);
            PrintSnapshot(lastSnapshot);

            var serverAliveAfterJob = server is { HasExited: false };
            Console.WriteLine("facadeAliveAfterJob=" + serverAliveAfterJob);
            Require(serverAliveAfterJob, "Facade server process exited before smoke finished.");

            if (lastSnapshot.Status != "completed")
            {
                failed = true;
                Console.WriteLine("FAIL terminal status is not completed.");
            }
            else
            {
                failed = !VerifyCompletedResult(lastSnapshot, workspace.FullName, before);
            }

            ObserveSessionId(lastSnapshot);

            if (!failed)
            {
                await TryCodexExecProbeAsync(
                    workspace.FullName,
                    endpoint,
                    token,
                    serverLogPath,
                    serverLogOffset);
            }
        }
        catch (Exception ex)
        {
            failed = true;
            Console.WriteLine("exceptionType=" + ex.GetType().FullName);
            Console.WriteLine("exception=" + Truncate(Redact(ex.ToString())));
        }
        finally
        {
            DumpFailureContext(
                lastSnapshot,
                jobId,
                workspace.FullName,
                runLogDir,
                serverLogPath,
                serverLogOffset,
                serverStdoutPath,
                serverStderrPath,
                server);
            await StopFacadeAsync(server);
            Console.WriteLine("serverLogAfterStop=" + Truncate(ReadLogTail(serverLogPath, serverLogOffset), 8000));
        }

        Console.WriteLine(failed ? "DEVIN MCP SMOKE FAILED" : "DEVIN MCP SMOKE PASSED");
        return failed ? 1 : 0;
    }

    private static void PrintVersions()
    {
        Console.WriteLine("devinVersion=" + RunCapture("devin", ["--version"]));
        Console.WriteLine("devinAuth=" + SummarizeAuth(RunCapture("devin", ["auth", "status"])));
        Console.WriteLine("dotnetVersion=" + RunCapture("dotnet", ["--version"]));
        Console.WriteLine("codexVersion=" + RunCapture("codex", ["--version"]));
    }

    private static string SummarizeAuth(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line =>
                line.StartsWith("Logged in", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Tier:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Plan:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Enterprise:", StringComparison.OrdinalIgnoreCase));
        var summary = string.Join(" | ", lines);
        return string.IsNullOrWhiteSpace(summary) ? Truncate(Redact(text)) : summary;
    }

    private static void InitializeGitWorkspace(string workspace)
    {
        File.WriteAllText(Path.Combine(workspace, "README.md"), "devin mcp smoke workspace\n");
        RunChecked("git", ["init"], workspace);
        RunChecked("git", ["config", "user.email", "devin-mcp-smoke@example.invalid"], workspace);
        RunChecked("git", ["config", "user.name", "devin-mcp-smoke"], workspace);
        RunChecked("git", ["add", "README.md"], workspace);
        RunChecked("git", ["commit", "-m", "init smoke workspace"], workspace);
    }

    private static Process StartFacadeProcess(
        string facadeEntry,
        string repoRoot,
        string token,
        int port,
        string runLogDir,
        string stdoutPath,
        string stderrPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(facadeEntry);
        startInfo.Environment["CODEX_AGENT_FACADE_TOKEN"] = token;
        startInfo.Environment["CODEX_AGENT_FACADE_PORT"] = port.ToString();
        startInfo.Environment["CODEX_AGENT_FACADE_LOG_DIR"] = runLogDir;

        File.WriteAllText(stdoutPath, string.Empty);
        File.WriteAllText(stderrPath, string.Empty);
        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => AppendProcessLine(stdoutPath, e.Data);
        process.ErrorDataReceived += (_, e) => AppendProcessLine(stderrPath, e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start CodexAgentFacade process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForListenAsync(int port, Process server, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (server.HasExited)
            {
                throw new InvalidOperationException(
                    "Facade process exited before listening. exitCode=" + server.ExitCode);
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                Console.WriteLine(ex.ToString());
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("Facade did not listen on 127.0.0.1:" + port + " within " + timeout + ".");
    }

    private static Task<McpClient> CreateMcpClientAsync(Uri endpoint, string token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + token,
            },
            EnableStandaloneGetStream = false,
        });
        return McpClient.CreateAsync(transport);
    }

    private static async Task<JobSnapshot> CallStartAgentAsync(McpClient client, string requestId, string workspace)
    {
        var call = await client.CallToolAsync(
            "start_agent",
            new Dictionary<string, object?>
            {
                ["request_id"] = requestId,
                ["agent"] = AgentName,
                ["prompt"] = Prompt,
                ["working_directory"] = workspace,
                ["auto_approve"] = true,
            });
        return ReadSnapshot("start_agent", call);
    }

    private static async Task<JobSnapshot> CallGetAgentJobAsync(McpClient client, string jobId)
    {
        var call = await client.CallToolAsync(
            "get_agent_job",
            new Dictionary<string, object?>
            {
                ["job_id"] = jobId,
            });
        return ReadSnapshot("get_agent_job", call);
    }

    private static async Task<JobSnapshot> WaitForTerminalAsync(McpClient client, string jobId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        JobSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await CallGetAgentJobAsync(client, jobId);
            Console.WriteLine("poll.status=" + last.Status + " jobId=" + last.JobId);
            if (last.Status is "completed" or "failed" or "cancelled")
            {
                return last;
            }

            var delayMs = last.PollAfterMs > 0 ? last.PollAfterMs : 2000;
            delayMs = Math.Clamp(delayMs, 1000, 5000);
            await Task.Delay(delayMs);
        }

        throw new TimeoutException(
            "job did not reach a terminal state within " + timeout + ". lastStatus=" + (last?.Status ?? "(none)"));
    }

    private static bool VerifyCompletedResult(JobSnapshot snapshot, string workspace, WorkspaceSnapshot before)
    {
        var ok = true;
        var result = snapshot.Result;
        if (result is null)
        {
            Console.WriteLine("FAIL completed job has no result.");
            return false;
        }

        ok &= Check(string.Equals(result.Agent, AgentName, StringComparison.Ordinal), "result.agent=" + result.Agent);
        ok &= Check(result.ExitCode == 0, "exitCode=" + result.ExitCode);
        ok &= Check(!string.IsNullOrWhiteSpace(result.OutputText), "outputText present");
        Console.WriteLine("outputText=" + Truncate(result.OutputText));
        Console.WriteLine("rawOutput=" + Truncate(result.RawOutput));
        Console.WriteLine("sessionId=" + (string.IsNullOrWhiteSpace(result.SessionId) ? "(empty)" : result.SessionId));
        Console.WriteLine("runId=" + result.RunId);
        Console.WriteLine("eventsLogPath=" + result.EventsLogPath);
        Console.WriteLine("textLogPath=" + result.TextLogPath);
        ok &= Check(File.Exists(result.EventsLogPath), "eventsLogPath exists");
        ok &= Check(File.Exists(result.TextLogPath), "textLogPath exists");

        var smokePath = Path.Combine(workspace, SmokeFileName);
        ok &= Check(File.Exists(smokePath), SmokeFileName + " exists");
        if (File.Exists(smokePath))
        {
            var contents = File.ReadAllText(smokePath).TrimEnd('\r', '\n');
            Console.WriteLine("smokeFileContents=" + Truncate(contents));
            ok &= Check(
                string.Equals(contents, ExpectedFileContents, StringComparison.Ordinal),
                SmokeFileName + " contents match");
        }

        var after = SnapshotWorkspace(workspace);
        var unexpected = FindUnexpectedChanges(before, after);
        if (unexpected.Count == 0)
        {
            Console.WriteLine("workspaceChanges=only " + SmokeFileName + " (plus allowed agent metadata)");
        }
        else
        {
            ok = false;
            Console.WriteLine("FAIL unexpected workspace changes:");
            foreach (var change in unexpected)
            {
                Console.WriteLine("  " + change);
            }
        }

        return ok;
    }

    private static void ObserveSessionId(JobSnapshot? snapshot)
    {
        var sessionId = snapshot?.Result?.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Console.WriteLine(
                "sessionIdObservation=empty. DevinStreamAccumulator did not find an explicit sessionId/session_id field. Not a smoke failure. Resume was not attempted. Follow-up: consider --export / ATIF rather than guessing from timestamps or arbitrary UUIDs.");
            return;
        }

        Console.WriteLine(
            "sessionIdObservation=captured " + sessionId + ". Resume was skipped in this first MCP smoke to avoid a second Free-plan Devin call.");
    }

    private static async Task TryCodexExecProbeAsync(
        string workspace,
        Uri endpoint,
        string token,
        string serverLogPath,
        long serverLogOffset)
    {
        Console.WriteLine("----- codex-exec-mcp-probe (optional, does not fail Devin smoke) -----");
        var logBefore = ReadLogTail(serverLogPath, serverLogOffset);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveCommand("codex"),
                WorkingDirectory = workspace,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("--skip-git-repo-check");
            startInfo.ArgumentList.Add("--ignore-user-config");
            startInfo.ArgumentList.Add("--ephemeral");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("mcp_servers.codex_agent_facade.url=" + JsonSerializer.Serialize(endpoint.ToString()));
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("mcp_servers.codex_agent_facade.bearer_token_env_var=\"CODEX_AGENT_FACADE_TOKEN\"");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("mcp_servers.codex_agent_facade.enabled=true");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("mcp_servers.codex_agent_facade.startup_timeout_sec=30");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("mcp_servers.codex_agent_facade.tool_timeout_sec=60");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("features.code_mode.direct_only_tool_namespaces=[\"mcp__codex_agent_facade\"]");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("mcp_servers.codex_agent_facade.default_tools_approval_mode=\"auto\"");
            startInfo.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
            startInfo.ArgumentList.Add(
                "Connectivity check only. List MCP tools from the Facade. "
                + "If get_agent_job exists, call it with job_id=mcp-smoke-probe-missing and report the error. "
                + "Do not call start_agent. Do not modify files. Then stop.");
            startInfo.Environment["CODEX_AGENT_FACADE_TOKEN"] = token;

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                Console.WriteLine("codexProbe=failed to start");
                return;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CodexProbeTimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException ex)
            {
                Console.WriteLine(ex.ToString());
                TryKill(process);
                Console.WriteLine("codexProbe=timeout after " + CodexProbeTimeoutSeconds + "s");
            }

            var stdout = Redact(await stdoutTask);
            var stderr = Redact(await stderrTask);
            Console.WriteLine("codexExitCode=" + (process.HasExited ? process.ExitCode.ToString() : "(killed)"));
            Console.WriteLine("codexStdout=" + Truncate(stdout, 6000));
            Console.WriteLine("codexStderr=" + Truncate(stderr, 2000));
            Console.WriteLine("codexSawStartAgent=" + ContainsToolMention(stdout + "\n" + stderr, "start_agent"));
            Console.WriteLine("codexSawGetAgentJob=" + ContainsToolMention(stdout + "\n" + stderr, "get_agent_job"));
            Console.WriteLine("codexToolCallReached=" + ContainsToolCall(stdout));
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            Console.WriteLine("codexProbe=exception " + Truncate(Redact(ex.Message)));
        }

        var logAfter = ReadLogTail(serverLogPath, serverLogOffset);
        var newLog = logAfter.Length > logBefore.Length ? logAfter[logBefore.Length..] : logAfter;
        var requestReached = newLog.Contains("/mcp", StringComparison.OrdinalIgnoreCase)
            || newLog.Contains("get_agent_job", StringComparison.OrdinalIgnoreCase)
            || newLog.Contains("start_agent", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine("codexReachedFacadeServerLog=" + requestReached);
        Console.WriteLine("codexNote=Windows Streamable HTTP MCP may not advertise tools to the Codex model. A Codex exec failure does not fail this Devin MCP smoke.");
    }

    private static bool ContainsToolMention(string text, string toolName)
    {
        return text.Contains(toolName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsToolCall(string stdout)
    {
        return stdout.Contains("mcp_tool_call", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("\"tool\":\"get_agent_job\"", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("\"tool\":\"start_agent\"", StringComparison.OrdinalIgnoreCase);
    }

    private static JobSnapshot ReadSnapshot(string tool, CallToolResult call)
    {
        var text = string.Concat(call.Content.OfType<TextContentBlock>().Select(block => block.Text));
        if (call.IsError == true)
        {
            throw new InvalidOperationException(tool + " returned an MCP error: " + Truncate(text));
        }

        var snapshot = JsonSerializer.Deserialize<JobSnapshot>(text, JsonOptions);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.JobId))
        {
            throw new InvalidOperationException(tool + " returned invalid JSON: " + Truncate(text));
        }

        return snapshot;
    }

    private static void PrintSnapshot(JobSnapshot snapshot)
    {
        Console.WriteLine("job.status=" + snapshot.Status);
        Console.WriteLine("job.error=" + (snapshot.Error ?? "(none)"));
        if (snapshot.Result is null)
        {
            Console.WriteLine("job.result=(none)");
            return;
        }

        Console.WriteLine("result.agent=" + snapshot.Result.Agent);
        Console.WriteLine("result.exitCode=" + snapshot.Result.ExitCode);
        Console.WriteLine("result.sessionId=" + (string.IsNullOrWhiteSpace(snapshot.Result.SessionId) ? "(empty)" : snapshot.Result.SessionId));
        Console.WriteLine("result.outputText=" + Truncate(snapshot.Result.OutputText));
        Console.WriteLine("result.rawOutput=" + Truncate(snapshot.Result.RawOutput));
    }

    private static void DumpFailureContext(
        JobSnapshot? snapshot,
        string? jobId,
        string workspace,
        string runLogDir,
        string serverLogPath,
        long serverLogOffset,
        string serverStdoutPath,
        string serverStderrPath,
        Process? server)
    {
        Console.WriteLine("----- diagnostics -----");
        Console.WriteLine("jobId=" + (jobId ?? "(none)"));
        if (snapshot is not null)
        {
            PrintSnapshot(snapshot);
        }

        Console.WriteLine("facadeHasExited=" + (server?.HasExited.ToString() ?? "(not started)"));
        if (server is { HasExited: true })
        {
            Console.WriteLine("facadeExitCode=" + server.ExitCode);
        }

        DumpFile("facadeStdout", serverStdoutPath);
        DumpFile("facadeStderr", serverStderrPath);
        Console.WriteLine("serverLog=" + Truncate(ReadLogTail(serverLogPath, serverLogOffset), 8000));

        if (snapshot?.Result is not null)
        {
            DumpFile("eventsLog", snapshot.Result.EventsLogPath);
            DumpFile("textLog", snapshot.Result.TextLogPath);
        }
        else if (!string.IsNullOrWhiteSpace(jobId))
        {
            DumpFile("eventsLog", Path.Combine(runLogDir, jobId + ".events.jsonl"));
            DumpFile("textLog", Path.Combine(runLogDir, jobId + ".log"));
        }

        var smokePath = Path.Combine(workspace, SmokeFileName);
        Console.WriteLine("workspaceListing=");
        foreach (var path in Directory.EnumerateFileSystemEntries(workspace, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(workspace, path);
            if (IsAgentMetadataPath(relative))
            {
                continue;
            }

            Console.WriteLine("  " + relative);
        }

        if (File.Exists(smokePath))
        {
            Console.WriteLine("smokeFile=" + Truncate(File.ReadAllText(smokePath)));
        }
    }

    private static void DumpFile(string label, string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine(label + "=(missing) " + path);
            return;
        }

        Console.WriteLine(label + "Path=" + path);
        Console.WriteLine(label + "=" + Truncate(ReadShared(path), 8000));
    }

    private static async Task StopFacadeAsync(Process? server)
    {
        if (server is null)
        {
            return;
        }

        try
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await server.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
        finally
        {
            server.Dispose();
        }
    }

    private static void AppendProcessLine(string path, string? line)
    {
        if (line is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
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
            Console.WriteLine(ex.ToString());
        }
    }

    private static WorkspaceSnapshot SnapshotWorkspace(string workspace)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workspace, path);
            if (IsAgentMetadataPath(relative))
            {
                continue;
            }

            files[relative] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        }

        return new WorkspaceSnapshot(files);
    }

    private static List<string> FindUnexpectedChanges(WorkspaceSnapshot before, WorkspaceSnapshot after)
    {
        var changes = new List<string>();
        foreach (var (path, hash) in after.Files)
        {
            if (string.Equals(path, SmokeFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!before.Files.TryGetValue(path, out var previous))
            {
                changes.Add("added " + path);
            }
            else if (!string.Equals(previous, hash, StringComparison.Ordinal))
            {
                changes.Add("modified " + path);
            }
        }

        foreach (var path in before.Files.Keys)
        {
            if (!after.Files.ContainsKey(path))
            {
                changes.Add("deleted " + path);
            }
        }

        return changes;
    }

    private static bool IsAgentMetadataPath(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        return normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, ".git", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(".devin/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, ".devin", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "CodexAgentFacade.cs")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "CodexAgentFacade.cs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate src/CodexAgentFacade.cs from this working directory.");
    }

    private static int FindFreePort(int preferred)
    {
        if (IsPortFree(preferred))
        {
            return preferred;
        }

        for (var port = preferred + 1; port < preferred + 20; port++)
        {
            if (IsPortFree(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("No free loopback port near " + preferred + ".");
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            return false;
        }
    }

    private static string RunCapture(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            return RunProcess(fileName, arguments, workingDirectory: null, check: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            return "(unavailable) " + Truncate(ex.Message);
        }
    }

    private static void RunChecked(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        RunProcess(fileName, arguments, workingDirectory, check: true);
    }

    private static string RunProcess(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, bool check)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCommand(fileName),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start " + fileName);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var text = (stdout + "\n" + stderr).Trim();
        if (check && process.ExitCode != 0)
        {
            throw new InvalidOperationException(fileName + " exited " + process.ExitCode + ": " + Truncate(text));
        }

        return text;
    }

    private static string ReadLogTail(string path, long offset)
    {
        if (!File.Exists(path))
        {
            return "(missing) " + path;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > stream.Length)
        {
            offset = 0;
        }

        stream.Position = offset;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Redact(reader.ReadToEnd());
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Redact(reader.ReadToEnd());
    }

    private static bool Check(bool condition, string message)
    {
        Console.WriteLine((condition ? "ok: " : "FAIL: ") + message);
        return condition;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Windows の npm shim は拡張子なしだと PE ではない。PATHEXT の .cmd / .exe を優先する。
    /// ProcessRunner.ExecutableResolver と同等の最小解決。スモークが Facade 本体を include しないための局所実装。
    /// </summary>
    private static string ResolveCommand(string fileName)
    {
        if (Path.IsPathRooted(fileName))
        {
            return fileName;
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
        }

        return fileName;
    }

    private static string Truncate(string? text, int max = 4000)
    {
        text ??= string.Empty;
        return text.Length <= max ? text : text[..max] + "...";
    }

    /// <summary>
    /// スモークが発行した token と Bearer 値だけを伏せる。Driver 側の SecretRedactor には依存しない。
    /// </summary>
    private static string Redact(string text)
    {
        var current = text;
        if (!string.IsNullOrWhiteSpace(SmokeToken))
        {
            current = current.Replace(SmokeToken, "[REDACTED]", StringComparison.Ordinal);
        }

        current = Regex.Replace(current, @"Bearer\s+[A-Za-z0-9\-._~+/]+=*", "[REDACTED]", RegexOptions.IgnoreCase);
        current = Regex.Replace(
            current,
            @"(CODEX_AGENT_FACADE_TOKEN|DEVIN_API_KEY|WINDSURF_API_KEY|token)\s*[=:]\s*\S+",
            "$1=[REDACTED]",
            RegexOptions.IgnoreCase);
        return current;
    }
}

internal sealed record JobSnapshot(
    string JobId,
    string RequestId,
    string Status,
    int PollAfterMs,
    JobResult? Result,
    string? Error);

internal sealed record JobResult(
    string Agent,
    string SessionId,
    int ExitCode,
    string OutputText,
    string RawOutput,
    string RunId,
    string EventsLogPath,
    string TextLogPath);

internal sealed record WorkspaceSnapshot(IReadOnlyDictionary<string, string> Files);
