using System.Diagnostics;
using Xunit;

namespace DeploymentPortal.Tests;

public class PowerShellCredentialProvisioningTests
{
    [Theory]
    [InlineData("WinRmCredentialProvisioning.Tests.ps1")]
    [InlineData("TfsCredentialProvisioning.Tests.ps1")]
    public async Task ProvisioningUsesPrivateAclAndRotatesSyntheticCiphertext(string script)
    {
        if (!OperatingSystem.IsWindows()) return;
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "tools", "Provision-TfsGitCredential.ps1"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(repo!.FullName, "tests", "PowerShell", script) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, (await output) + (await errors));
        Assert.Contains("PASS:", await output);
    }
}
