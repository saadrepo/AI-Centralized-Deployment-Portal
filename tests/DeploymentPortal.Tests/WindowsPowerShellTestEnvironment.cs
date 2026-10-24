using System.Diagnostics;

namespace DeploymentPortal.Tests;

internal static class WindowsPowerShellTestEnvironment
{
    internal static void ConfigureModulePath(ProcessStartInfo start)
    {
        // pwsh -> dotnet -> powershell.exe otherwise inherits PowerShell 7 modules.
        // These fixtures need only Windows PowerShell's built-in modules; IIS is mocked.
        start.Environment["PSModulePath"] = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\WindowsPowerShell\v1.0\Modules");
    }
}
