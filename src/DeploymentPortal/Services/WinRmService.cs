using DeploymentPortal.Models;

namespace DeploymentPortal.Services;
public record ConnectionResult(bool Success, string Message, string ServerName, DateTimeOffset Timestamp, string OperatingSystem = "", long? TotalMemoryBytes = null);
public interface IWinRmService
{
    Task<ConnectionResult> TestConnectionAsync(Server server);
    Task<ConnectionResult> GetBasicServerInfoAsync(Server server);
}
public class WinRmService(IPowerShellService powerShell, ICredentialProvider credentials) : IWinRmService
{
    public Task<ConnectionResult> TestConnectionAsync(Server server) => Execute(server, RemoteOperation.TestConnection);
    public Task<ConnectionResult> GetBasicServerInfoAsync(Server server) => Execute(server, RemoteOperation.GetBasicServerInfo);
    private async Task<ConnectionResult> Execute(Server server, RemoteOperation operation)
    {
        try
        {
            var result = await powerShell.ExecuteAsync(operation, server, await credentials.GetAsync(server));
            return new(result.Success, result.Message, result.ServerName, DateTimeOffset.UtcNow, result.OperatingSystem, result.TotalMemoryBytes);
        }
        catch (InvalidOperationException ex) { return new(false, ex.Message, server.HostNameOrIp, DateTimeOffset.UtcNow); }
    }
}
