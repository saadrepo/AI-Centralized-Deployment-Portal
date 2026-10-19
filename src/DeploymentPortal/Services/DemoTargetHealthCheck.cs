using DeploymentPortal.Data;
using Microsoft.EntityFrameworkCore;

namespace DeploymentPortal.Services;

public interface IDemoTargetHealthCheck
{
    Task<PipelineHealthResult> Check(CancellationToken ct);
}

public sealed class DemoTargetHealthCheck(ApplicationDbContext db, ISiteRegistry registry,
    ICredentialProvider credentials, IPowerShellService powerShell) : IDemoTargetHealthCheck
{
    public const string Url = "https://deployment-test.vitonta.com/";
    public async Task<PipelineHealthResult> Check(CancellationToken ct)
    {
        try
        {
            var app = registry.Find("Demo");
            // Resolve the registered WinRM destination independently of the HTTPS
            // certificate hostname. Keep the original Server for credential binding.
            var registered = await db.Servers.AsNoTracking().ToListAsync(ct);
            var matches = app?.Destinations.SelectMany(destination => registered.Where(destination.Matches)).ToList();
            if (matches is null || matches.Count != 1)
                return new(Url, "pending", "Unverified: configure one registered Demo TARGET destination");
            var server = matches[0];
            ct.ThrowIfCancellationRequested();
            var credential = await credentials.GetAsync(server);
            var result = await powerShell.ExecuteAsync(RemoteOperation.VerifyDemoHealth, server, credential);
            ct.ThrowIfCancellationRequested();
            if (result.Success && result.Verified && result.HealthHttpStatus == 200)
                return new(Url, "completed", "TARGET HTTPS GET returned HTTP 200 with valid TLS");
            if (result.HealthHttpStatus is >= 100 and <= 599)
                return new(Url, "failed", $"TARGET health check returned HTTP {result.HealthHttpStatus}");
            return result.Verification switch
            {
                "tls" => new(Url, "failed", "TARGET HTTPS TLS validation or handshake failed"),
                "timeout" => new(Url, "pending", "Unverified: TARGET HTTPS request timed out"),
                "network" => new(Url, "pending", "Unverified: TARGET HTTPS endpoint could not be reached"),
                _ => new(Url, "pending", "Unverified: TARGET WinRM health check could not be verified")
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or IOException or OperationCanceledException)
        {
            return new(Url, "pending", "Unverified: protected TARGET connection unavailable or check interrupted");
        }
    }
}
