using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace DeploymentPortal.Services;

// Mutation credentials always belong to the visiting Windows user, never the stored TFS account.
public interface IPipelineUserTransport
{
    bool Available(ClaimsPrincipal user);
    Task<T> RunAsync<T>(ClaimsPrincipal user, Func<HttpClient, string, Task<T>> action);
    bool AvailableForBuild(ClaimsPrincipal user) => Available(user);
    Task<T> RunBuildAsync<T>(ClaimsPrincipal user, Func<HttpClient, string, Task<T>> action) => RunAsync(user, action);
}

public sealed class PipelineUserTransport(IOptions<TfsGitConnectionOptions> options, IHostEnvironment environment) : IPipelineUserTransport
{
    private static bool Delegated(ClaimsPrincipal user) => OperatingSystem.IsWindows()
        && user.Identity is WindowsIdentity { IsAuthenticated: true, ImpersonationLevel: TokenImpersonationLevel.Delegation };

    // A local interactive process can authenticate to TFS directly as its own user: no double hop.
    // Never accept a different browser SID, a claims-only principal or a production process fallback.
    private bool SameLocalUser(ClaimsPrincipal user)
    {
        if (!OperatingSystem.IsWindows() || !environment.IsDevelopment() || user.Identity is not WindowsIdentity { IsAuthenticated: true } identity) return false;
        using var current = WindowsIdentity.GetCurrent();
        return identity.User is not null && current.User is not null && identity.User.Equals(current.User);
    }

    public bool Available(ClaimsPrincipal user) => Delegated(user) || SameLocalUser(user);
    public bool AvailableForBuild(ClaimsPrincipal user) => Available(user);

    public Task<T> RunBuildAsync<T>(ClaimsPrincipal user, Func<HttpClient, string, Task<T>> action)
    {
        if (Delegated(user)) return RunDelegatedBuild(user, action);
        if (!AvailableForBuild(user) || user.Identity is not WindowsIdentity identity)
            throw new UnauthorizedAccessException("CI requires a delegated login or the same interactive Windows user running local Development.");
        // Development only: process credentials are allowed only for the identical Windows SID.
        // The TFS-authenticated SID is still verified before queueing. Never use stored service credentials.
        return RunBuildVerified(identity, action);
    }

    private async Task<T> RunDelegatedBuild<T>(ClaimsPrincipal user, Func<HttpClient, string, Task<T>> action)
    {
        if (!OperatingSystem.IsWindows() || user.Identity is not WindowsIdentity identity)
            throw PipelineQueueException.From(new UnauthorizedAccessException(), "Windows identity");
        return await WindowsIdentity.RunImpersonatedAsync(identity.AccessToken, () => RunBuildVerified(identity, action));
    }

    private async Task<T> RunBuildVerified<T>(WindowsIdentity identity, Func<HttpClient, string, Task<T>> action)
    {
        var verified = false;
        try
        {
            return await RunVerified(identity, (http, id) => { verified = true; return action(http, id); }, legacyIdentityFallback: true);
        }
        catch (PipelineQueueException) { throw; }
        catch (Exception ex) when (!verified && ex is HttpRequestException or OperationCanceledException or System.ComponentModel.Win32Exception or System.Security.Authentication.AuthenticationException or System.Text.Json.JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { throw PipelineQueueException.From(ex, "TFS Windows identity verification"); }
    }

    public async Task<T> RunAsync<T>(ClaimsPrincipal user, Func<HttpClient, string, Task<T>> action)
    {
        if (!OperatingSystem.IsWindows() || !Available(user) || user.Identity is not WindowsIdentity identity)
            throw new UnauthorizedAccessException("CD requires delegation, or the identical interactive Windows user running local Development; stored service credentials are prohibited.");
        if (Delegated(user))
            return await WindowsIdentity.RunImpersonatedAsync(identity.AccessToken, () => RunVerified(identity, action));
        // Available guarantees Development + identical process/browser SID. RunVerified also
        // checks the remote TFS authenticated SID before invoking any read or mutation callback.
        return await RunVerified(identity, action, legacyIdentityFallback: true);
    }

    private async Task<T> RunVerified<T>(WindowsIdentity identity, Func<HttpClient, string, Task<T>> action, bool legacyIdentityFallback = false)
        {
            if (!OperatingSystem.IsWindows()) throw new UnauthorizedAccessException("Windows delegation is required.");
            using var handler = new HttpClientHandler { UseDefaultCredentials = true, AllowAutoRedirect = false, UseProxy = false };
            using var guard = new PipelineUserRequestGuard(TfsGitClientConfiguration.GetBaseAddress(options.Value.BaseUrl)) { InnerHandler = handler };
            using var client = new HttpClient(guard) { BaseAddress = TfsGitClientConfiguration.GetBaseAddress(options.Value.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            var connection = await ReadIdentity(client, legacyIdentityFallback);
            var authenticated = connection["authenticatedUser"];
            // Check TFS really authenticated this Windows SID; reject any process-identity fallback.
            if (identity.User is null || PipelineState.Text(authenticated, "descriptor")?.EndsWith(";" + identity.User.Value, StringComparison.OrdinalIgnoreCase) != true)
            {
                if (legacyIdentityFallback) throw new PipelineQueueException("CI_TFS_IDENTITY_MISMATCH", "TFS Windows identity verification",
                    "TFS did not return the expected signed-in Windows SID. A different identity or unverifiable TFS identity cannot queue builds; service-account fallback is prohibited.");
                throw new UnauthorizedAccessException("TFS did not confirm the delegated Windows identity.");
            }
            var id = PipelineState.Text(authenticated, "id");
            if (!Guid.TryParse(id, out _))
            {
                if (legacyIdentityFallback) throw new PipelineQueueException("CI_TFS_USER_ID_MISSING", "TFS Windows identity verification", "TFS did not return a valid authenticated user ID.");
                throw new UnauthorizedAccessException("TFS user identity is unavailable.");
            }
            return await action(client, id!);
        }

    public static async Task<JsonNode> ReadIdentity(HttpClient client, bool legacyIdentityFallback)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { return await PipelineService.ReadAsync(client, "_apis/connectionData?api-version=4.1", deadline.Token); }
        catch (HttpRequestException ex) when (legacyIdentityFallback && ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            // Read-only compatibility fallback for older TFS connection-data routes. No mutation retries.
            return await PipelineService.ReadAsync(client, "_apis/connectionData", deadline.Token);
        }
    }
}

public sealed class PipelineUserRequestGuard(Uri collection) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !TfsGitClientConfiguration.IsWithinCollection(request.RequestUri, collection)
            || request.Headers.Authorization is not null || request.Headers.Host is not null
            || request.Headers.Contains("Proxy-Authorization")) throw new InvalidOperationException("Untrusted TFS request.");
        return base.SendAsync(request, cancellationToken);
    }
}
