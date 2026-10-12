#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

var tempRoot = Path.GetFullPath(Path.GetTempPath())
    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var scratchRoot = Path.Combine(tempRoot, "codex-apm-" + Guid.NewGuid().ToString("N"));
var safeToDelete = false;

try
{
    var packageRoot = GetPackageRoot();
    Directory.CreateDirectory(scratchRoot);
    var resolvedScratchRoot = Path.GetFullPath(scratchRoot);
    EnsureTempChild(tempRoot, resolvedScratchRoot);
    safeToDelete = true;

    using var process = new Process
    {
        StartInfo = new ProcessStartInfo("apm")
        {
            WorkingDirectory = resolvedScratchRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        },
    };
    process.StartInfo.ArgumentList.Add("install");
    process.StartInfo.ArgumentList.Add(packageRoot);
    process.StartInfo.ArgumentList.Add("--target");
    process.StartInfo.ArgumentList.Add("codex,agent-skills");

    if (!process.Start())
    {
        throw new InvalidOperationException("Could not start apm.");
    }

    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"apm install exited with code {process.ExitCode}. stdout: {stdout} stderr: {stderr}");
    }

    var sourceSkill = Path.Combine(packageRoot, ".apm", "skills", "codex", "SKILL.md");
    var installedSkill = Path.Combine(resolvedScratchRoot, ".agents", "skills", "codex", "SKILL.md");
    if (!File.Exists(installedSkill))
    {
        throw new InvalidOperationException("APM installation did not deploy codex/SKILL.md.");
    }

    var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceSkill));
    var installedHash = SHA256.HashData(await File.ReadAllBytesAsync(installedSkill));
    if (!CryptographicOperations.FixedTimeEquals(sourceHash, installedHash))
    {
        throw new InvalidOperationException("Installed Skill does not match the package source.");
    }

    var hasFacadeBinary = Directory.EnumerateFiles(resolvedScratchRoot, "*", SearchOption.AllDirectories)
        .Any(path => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).StartsWith("CodexAgentFacade", StringComparison.OrdinalIgnoreCase));
    if (hasFacadeBinary)
    {
        throw new InvalidOperationException("APM package unexpectedly contains Facade binaries.");
    }

    Console.WriteLine("Codex CLI package-root APM install smoke test: PASS");
}
catch (Exception ex)
{
    Trace.WriteLine(ex.ToString());
    Console.Error.WriteLine(ex.ToString());
    Environment.ExitCode = 1;
}
finally
{
    if (safeToDelete && Directory.Exists(scratchRoot))
    {
        try
        {
            var resolvedScratchRoot = Path.GetFullPath(scratchRoot);
            EnsureTempChild(tempRoot, resolvedScratchRoot);
            Directory.Delete(resolvedScratchRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex.ToString());
            Console.Error.WriteLine(ex.ToString());
            Environment.ExitCode = 1;
        }
    }
}

static string GetPackageRoot([CallerFilePath] string sourceFile = "")
{
    var scriptsDirectory = Path.GetDirectoryName(sourceFile)
        ?? throw new InvalidOperationException("Could not locate the package smoke source directory.");
    return Path.GetFullPath(Path.Combine(scriptsDirectory, ".."));
}

static void EnsureTempChild(string tempRoot, string candidate)
{
    var requiredPrefix = tempRoot + Path.DirectorySeparatorChar;
    if (!candidate.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Refusing to use or delete a path outside the temp directory: {candidate}");
    }
}
