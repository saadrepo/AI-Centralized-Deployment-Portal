using System.Security.Cryptography;
using System.Text.Json;
using DeploymentPortal.Models;
using Microsoft.AspNetCore.DataProtection;

namespace DeploymentPortal.Services;

public class SiteDeletionService(IPowerShellService shell, ICredentialProvider credentials, IDataProtectionProvider protection)
{
    public async Task<IReadOnlyList<IisSite>> ListAsync(Server server)
    {
        var result = await shell.ExecuteAsync(RemoteOperation.ListSites, server, await credentials.GetAsync(server));
        if (!result.Success) throw new InvalidOperationException(result.Message);
        return result.Sites ?? [];
    }
    private readonly IDataProtector protector = protection.CreateProtector("SiteDeletion.v1");
    private static string Destination(Deployment d) => JsonSerializer.Serialize(new { d.ServerId, d.Server.HostNameOrIp, d.Server.WinRmPort, d.Server.UseSsl, d.Server.Username, d.SiteName });
    private record Approval(string Destination, string State, DateTimeOffset Expires);

    public async Task<(string Token, string Message)> PreviewAsync(Deployment d)
    {
        var result = await shell.ExecuteAsync(RemoteOperation.PreviewDeleteSite, d.Server, await credentials.GetAsync(d.Server), d);
        if (!result.Success || string.IsNullOrEmpty(result.StateFingerprint)) throw new InvalidOperationException(result.Message);
        return (protector.Protect(JsonSerializer.Serialize(new Approval(Destination(d), result.StateFingerprint, DateTimeOffset.UtcNow.AddMinutes(10)))), result.Message);
    }

    public async Task<RemoteResult> DeleteAsync(Deployment d, string? token, string? confirmedName)
    {
        if (!string.Equals(confirmedName, d.SiteName, StringComparison.Ordinal)) return new(false, "Type the exact site name to confirm deletion.");
        Approval? approval;
        try { approval = JsonSerializer.Deserialize<Approval>(protector.Unprotect(token ?? "")); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException) { return new(false, "Invalid confirmation. Review the site again."); }
        if (approval is null || approval.Expires <= DateTimeOffset.UtcNow || approval.Destination != Destination(d)) return new(false, "Confirmation expired or destination changed. Review the site again.");
        var request = new Deployment { SiteName = d.SiteName, ExpectedState = approval.State };
        return await shell.ExecuteAsync(RemoteOperation.DeleteSite, d.Server, await credentials.GetAsync(d.Server), request);
    }
}
