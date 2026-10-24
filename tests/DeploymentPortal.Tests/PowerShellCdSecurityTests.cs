using System.Diagnostics;
using Xunit;

namespace DeploymentPortal.Tests;

public class PowerShellCdSecurityTests
{
    [Theory]
    [InlineData("CdPackageSecurity.Tests.ps1")]
    [InlineData("CdIisWorkflow.Tests.ps1")]
    [InlineData("../PowerShell.Workflow.Tests.ps1")]
    public async Task FixedScriptsParseAndLocalPreservationRejectsUnsafeUpdates(string scriptName)
    {
        if (!OperatingSystem.IsWindows()) return; // Production automation is Windows-only.
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "src", "DeploymentPortal", "PowerShell"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "cd-security-fixtures"));
        var root = Path.Combine(fixtures, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            WindowsPowerShellTestEnvironment.ConfigureModulePath(start);
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(repo!.FullName, "tests", "PowerShell", scriptName), "-Root", root, "-Scripts", Path.Combine(repo.FullName, "src", "DeploymentPortal", "PowerShell") }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
            var stdout = await output;
            Assert.True(process.ExitCode == 0, stdout + (await errors));
            if (scriptName == "CdIisWorkflow.Tests.ps1")
                Assert.Equal(9, System.Text.RegularExpressions.Regex.Matches(stdout, "(?m)^PASS:").Count);
            if (scriptName == "../PowerShell.Workflow.Tests.ps1")
                Assert.Equal(11, System.Text.RegularExpressions.Regex.Matches(stdout, "(?m)^PASS:").Count);
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(fixtures + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its root.");
            // Unlink junctions before deleting their targets. Never recurse into a reparse point.
            RemoveFixtureLinks(new DirectoryInfo(root));
            Directory.Delete(root, true);
        }
    }

    private static void RemoveFixtureLinks(DirectoryInfo directory)
    {
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((item.Attributes & FileAttributes.Directory) != 0) Directory.Delete(item.FullName);
                else File.Delete(item.FullName);
            }
            else if (item is DirectoryInfo child) RemoveFixtureLinks(child);
        }
    }
}
