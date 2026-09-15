#:property TargetFramework=net10.0

using System.Diagnostics;
using System.Text.Json;

var repoRoot = Environment.CurrentDirectory;
var configPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(repoRoot, "tools", "publish-facade.local.json");
if (!File.Exists(configPath))
    throw new FileNotFoundException("Publish configuration was not found. Copy tools/publish-facade.local.example.json and edit it.", configPath);

using var configDocument = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
var configRoot = configDocument.RootElement;
var config = new PublishConfig(
    configRoot.TryGetProperty("deploymentDirectory", out var deploymentDirectory)
        ? deploymentDirectory.GetString() ?? string.Empty
        : string.Empty,
    configRoot.TryGetProperty("taskName", out var taskName)
        ? taskName.GetString() ?? string.Empty
        : string.Empty);
if (string.IsNullOrWhiteSpace(config.DeploymentDirectory)) throw new InvalidOperationException("deploymentDirectory is required.");
if (string.IsNullOrWhiteSpace(config.TaskName)) throw new InvalidOperationException("taskName is required.");
var deployment = Path.GetFullPath(config.DeploymentDirectory);
var candidate = deployment + ".publish-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
var backup = deployment + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
var exe = Path.Combine(deployment, "CodexAgentFacade.exe");
var candidateExe = Path.Combine(candidate, "CodexAgentFacade.exe");

try
{
    await Run("dotnet", $"publish src/CodexAgentFacade.cs -c Release -r win-x64 --self-contained false -o {Quote(candidate)} --nologo", repoRoot);
    if (!File.Exists(candidateExe)) throw new InvalidOperationException("Publish did not produce CodexAgentFacade.exe.");

    var taskBefore = await Run("schtasks", $"/Query /TN {Quote(config.TaskName)} /FO LIST /V", repoRoot);
    var processIds = Process.GetProcessesByName("CodexAgentFacade").Where(p => p.MainModule?.FileName is not null && string.Equals(Path.GetFullPath(p.MainModule.FileName), exe, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (processIds.Length > 1) throw new InvalidOperationException("Multiple Facade processes are running.");
    await Run("schtasks", $"/End /TN {Quote(config.TaskName)}", repoRoot);
    await WaitForExit(exe, TimeSpan.FromSeconds(30));
    Directory.Move(deployment, backup);
    Directory.Move(candidate, deployment);
    await Run("schtasks", $"/Run /TN {Quote(config.TaskName)}", repoRoot);
    await WaitForRunning(exe, TimeSpan.FromSeconds(30));
    var taskAfter = await Run("schtasks", $"/Query /TN {Quote(config.TaskName)} /FO LIST /V", repoRoot);
    if (!taskAfter.Contains("Running", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Facade task did not return to Running state.");
    Console.WriteLine($"Deployment completed. Backup: {backup}");
    Console.WriteLine("Task registration preserved:\n" + taskBefore.Contains("Task To Run", StringComparison.OrdinalIgnoreCase));
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.ToString());
    if (Directory.Exists(deployment + ".failed")) Directory.Delete(deployment + ".failed", true);
    if (Directory.Exists(deployment) && Directory.Exists(backup)) Directory.Move(deployment, deployment + ".failed");
    if (!Directory.Exists(deployment) && Directory.Exists(backup)) { Directory.Move(backup, deployment); _ = Run("schtasks", $"/Run /TN {Quote(config.TaskName)}", repoRoot); }
    throw;
}

static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
static async Task<string> Run(string file, string arguments, string workingDirectory)
{
    using var p = Process.Start(new ProcessStartInfo(file, arguments) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }) ?? throw new InvalidOperationException($"Failed to start {file}.");
    var output = await p.StandardOutput.ReadToEndAsync(); var error = await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
    if (p.ExitCode != 0) throw new InvalidOperationException($"{file} failed ({p.ExitCode}): {error}\n{output}");
    return output;
}
static async Task WaitForExit(string executable, TimeSpan timeout) { var end = DateTime.UtcNow + timeout; while (DateTime.UtcNow < end) { if (!Process.GetProcessesByName("CodexAgentFacade").Any(p => { try { return string.Equals(Path.GetFullPath(p.MainModule?.FileName ?? ""), executable, StringComparison.OrdinalIgnoreCase); } catch { return false; } })) return; await Task.Delay(500); } throw new TimeoutException("Facade did not stop."); }
static async Task WaitForRunning(string executable, TimeSpan timeout) { var end = DateTime.UtcNow + timeout; while (DateTime.UtcNow < end) { if (Process.GetProcessesByName("CodexAgentFacade").Any(p => { try { return string.Equals(Path.GetFullPath(p.MainModule?.FileName ?? ""), executable, StringComparison.OrdinalIgnoreCase); } catch { return false; } })) return; await Task.Delay(500); } throw new TimeoutException("Facade did not start."); }
sealed record PublishConfig(string DeploymentDirectory, string TaskName);
