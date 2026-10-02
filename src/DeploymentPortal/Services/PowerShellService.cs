using System.Diagnostics;
using System.Text.Json;
using DeploymentPortal.Models;
using Microsoft.Extensions.Options;

namespace DeploymentPortal.Services;
public enum RemoteOperation { TestConnection, GetBasicServerInfo, PreviewDeployment, Deploy, PreviewDeleteSite, DeleteSite, ListSites }
public record RemoteResult(bool Success, string Message, string ServerName = "", string OperatingSystem = "", string? BackupPath = null, long? TotalMemoryBytes = null, string? StateFingerprint = null, string? DeploymentMode = null, IReadOnlyList<IisSite>? Sites = null);
public record IisSite(string Name, string State, string Bindings);
public interface IPowerShellService
{
    Task<RemoteResult> ExecuteAsync(RemoteOperation operation, Server server, RemoteCredential credential, Deployment? deployment = null);
}
public class PowerShellService(IHostEnvironment environment, IOptions<AutomationOptions> options) : IPowerShellService
{
    public async Task<RemoteResult> ExecuteAsync(RemoteOperation operation, Server server, RemoteCredential credential, Deployment? deployment = null)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Automation requires a Windows dashboard host.");
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(environment.ContentRootPath, "PowerShell", "Invoke-RemoteOperation.ps1") }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync(); // Drain, but never log raw PowerShell output.
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                Operation = operation.ToString(), server.HostNameOrIp, server.WinRmPort, server.UseSsl,
                credential.Username, credential.Password, Options = options.Value,
                Deployment = deployment is null ? null : new { deployment.Id, deployment.SiteName, deployment.ApplicationName, deployment.AppPoolName, deployment.SourcePath, deployment.TargetPath, deployment.BackupPath, deployment.SitePort, deployment.SiteHostName, deployment.ExpectedState }
            }));
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(operation == RemoteOperation.Deploy ? options.Value.DeploymentTimeoutSeconds + 60 : options.Value.ConnectionTimeoutSeconds + 15));
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr;
            var result = JsonSerializer.Deserialize<RemoteResult>(output.Trim(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (result is null || (process.ExitCode != 0 && result.Success)) return new(false, "Automation returned an invalid result. Verify the target state before retrying.");
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return new(false, "Automation timed out. Remote state and rollback are unconfirmed; inspect the server and retained backup before retrying.");
        }
        catch
        {
            return new(false, "Automation could not complete. Verify Windows PowerShell, remoting configuration and the target state before retrying.");
        }
    }
}
