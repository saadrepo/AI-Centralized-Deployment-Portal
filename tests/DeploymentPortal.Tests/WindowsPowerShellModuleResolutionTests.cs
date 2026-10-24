using System.Diagnostics;
using Xunit;

namespace DeploymentPortal.Tests;

public class WindowsPowerShellModuleResolutionTests
{
    [Fact]
    public async Task IncompatibleInheritedModulesCannotReplaceBuiltInHashAndAclCommands()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(AppContext.BaseDirectory, "module-resolution-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var module = Path.Combine(root, "Microsoft.PowerShell.Utility");
            Directory.CreateDirectory(module);
            await File.WriteAllTextAsync(Path.Combine(module, "Microsoft.PowerShell.Utility.psd1"),
                "@{ ModuleVersion='7.0.0'; PowerShellVersion='7.0'; FunctionsToExport=@('Get-FileHash') }");
            var file = Path.Combine(root, "literal[hash].bin");
            await File.WriteAllBytesAsync(file, "abc"u8.ToArray());
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment["PSModulePath"] = root;
            WindowsPowerShellTestEnvironment.ConfigureModulePath(start);
            start.Environment["HASH_TEST_FILE"] = file;
            var probe = Path.Combine(root, "Probe-Modules.ps1");
            await File.WriteAllTextAsync(probe, """
                $ErrorActionPreference = 'Stop'
                if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Expected Windows PowerShell.' }
                Get-Command Get-Acl, Set-Acl -ErrorAction Stop | Out-Null
                $hash = (Get-FileHash -LiteralPath $env:HASH_TEST_FILE -Algorithm SHA256).Hash
                if ($hash -cne 'BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD') { throw 'SHA256 vector mismatch.' }
                [IO.File]::WriteAllBytes($env:HASH_TEST_FILE, [byte[]](97, 98, 100))
                if ((Get-FileHash -LiteralPath $env:HASH_TEST_FILE -Algorithm SHA256).Hash -ceq $hash) { throw 'Tampering was not detected.' }
                Write-Output 'PASS: native SHA256 and ACL commands available; tampering detected.'
                """);
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", probe })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            Assert.True(process.ExitCode == 0, (await output) + (await errors));
            Assert.Contains("PASS:", await output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
