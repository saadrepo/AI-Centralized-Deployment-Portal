using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using static DeploymentPortal.Services.PipelineState;

namespace DeploymentPortal.Services;

public sealed class PipelineWorkgroupOptions
{
    public bool ServiceCiEnabled { get; set; }
    public string[] ServiceCiUsers { get; set; } = []; // Portal Windows SIDs; default deny.
    public string? ServiceIdentityId { get; set; } // Actual TFS identity GUID, never a browser identity.
    public bool ServiceCdEnabled { get; set; } // Explicit authorization, independent of CI; default off.
    public string[] ServiceCdUsers { get; set; } = []; // Real Portal Windows SIDs; default deny.
    public string? ServiceCdIdentitySid { get; set; } // Independently pinned TFS actor SID, not a Portal SID.
    public bool ServiceDeploymentCancellationEnabled { get; set; } // Separate default-off mutation gate.
    public bool PerUserAuthenticationEnabled { get; set; }
    public string? PersonalCollectionUrl { get; set; } // Optional HTTPS binding for the same configured host/collection.
    public Dictionary<string, string> PerUserTfsIdentities { get; set; } = []; // Portal SID -> personal TFS GUID.
}

public sealed record PipelinePersonalSession(string PortalSid, string TfsId,
    [property: System.Text.Json.Serialization.JsonIgnore] string Token, DateTimeOffset Expires, string Collection)
{
    public override string ToString() => $"Personal TFS session: {PortalSid}, {TfsId}, expires {Expires:O}; token redacted";
}

// Volatile, bounded, fixed-lifetime storage. No token is persisted or returned to the browser.
public sealed class PipelinePersonalSessions : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 100 });
    public static string? Sid(ClaimsPrincipal user) => OperatingSystem.IsWindows()
        && user.Identity is WindowsIdentity { IsAuthenticated: true } windows ? windows.User?.Value : null;
    public string Store(PipelinePersonalSession session)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        cache.Set(key, session, new MemoryCacheEntryOptions { AbsoluteExpiration = session.Expires, Size = 1 });
        if (!cache.TryGetValue(key, out _)) throw new InvalidOperationException("Personal session capacity reached; retry later.");
        return key;
    }
    public PipelinePersonalSession? Find(string? key, string? sid) => key is { Length: 64 } && sid is not null
        && cache.TryGetValue(key, out PipelinePersonalSession? session) && session?.PortalSid == sid
        && session.Expires > DateTimeOffset.UtcNow ? session : null;
    public void Remove(string? key, string? sid) { if (Find(key, sid) is not null) cache.Remove(key!); }
    public void Dispose() => cache.Dispose();
}

public interface IPipelinePersonalClientFactory { HttpClient Create(string collection, string token); }
public sealed class PipelinePersonalClientFactory : IPipelinePersonalClientFactory
{
    public HttpClient Create(string collection, string token) => PipelineWorkgroupTransport.CreatePersonalClient(collection, token);
}

public sealed class PipelineWorkgroupTransport(PipelineUserTransport windows, IHttpClientFactory clients,
    IOptions<PipelineWorkgroupOptions> settings, IOptions<TfsGitConnectionOptions> connection,
    PipelinePersonalSessions sessions, IHttpContextAccessor accessor, ILogger<PipelineWorkgroupTransport> logger, IPipelinePersonalClientFactory personalClients) : IPipelineUserTransport
{
    public const string Cookie = "__Secure-PipelineTfs";
    private PipelinePersonalSession? Session(ClaimsPrincipal user)
    {
        var sid = PipelinePersonalSessions.Sid(user);
        var session = sessions.Find(accessor.HttpContext?.Request.Cookies[Cookie], sid);
        string collection;
        try { collection = PersonalCollection(settings.Value, connection.Value); } catch (InvalidOperationException) { return null; }
        return accessor.HttpContext?.Request.IsHttps == true && settings.Value.PerUserAuthenticationEnabled && session is not null && sid is not null
            && session.Collection == collection && PersonalIdentityAllowed(settings.Value, sid, session.TfsId) ? session : null;
    }
    public static bool ServiceCiAllowed(PipelineWorkgroupOptions options, ClaimsPrincipal user) => options.ServiceCiEnabled
        && Guid.TryParse(options.ServiceIdentityId, out _) && PipelinePersonalSessions.Sid(user) is { } sid
        && options.ServiceCiUsers.Contains(sid, StringComparer.OrdinalIgnoreCase);

    public static bool PersonalIdentityAllowed(PipelineWorkgroupOptions options, string sid, string? actual) =>
        options.PerUserAuthenticationEnabled && Guid.TryParse(actual, out _)
        && options.PerUserTfsIdentities.TryGetValue(sid, out var expected)
        && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(actual, options.ServiceIdentityId, StringComparison.OrdinalIgnoreCase);

    public const string SingleAccountActor = @"fff\saad.bilal";
    public static bool ServiceCdAllowed(PipelineWorkgroupOptions options, ClaimsPrincipal user) => options.ServiceCdEnabled
        && Guid.TryParse(options.ServiceIdentityId, out _) && ValidWindowsSid(options.ServiceCdIdentitySid)
        && PipelinePersonalSessions.Sid(user) is { } sid
        && options.ServiceCdUsers.Contains(sid, StringComparer.OrdinalIgnoreCase);

    private static bool ValidWindowsSid(string? sid) => sid is not null
        && System.Text.RegularExpressions.Regex.IsMatch(sid, @"\AS-1-5-21-[0-9]{1,10}-[0-9]{1,10}-[0-9]{1,10}-[0-9]{1,10}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
        && sid.Split('-').Skip(4).All(part => uint.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _));

    public static bool SingleAccountIdentityMatches(System.Text.Json.Nodes.JsonNode? actor, PipelineWorkgroupOptions options) =>
        actor is System.Text.Json.Nodes.JsonObject
        && Guid.TryParse(options.ServiceIdentityId, out var expected) && expected != Guid.Empty
        && actor["id"] is System.Text.Json.Nodes.JsonValue id && id.TryGetValue<string>(out var idText)
        && Guid.TryParse(idText, out var actual) && actual == expected
        && ValidWindowsSid(options.ServiceCdIdentitySid)
        && actor["descriptor"] is System.Text.Json.Nodes.JsonValue descriptor && descriptor.TryGetValue<string>(out var descriptorText)
        && string.Equals(descriptorText, "System.Security.Principal.WindowsIdentity;" + options.ServiceCdIdentitySid, StringComparison.Ordinal);

    // Explicit single-account mode denies unauthorized callers without personal/delegation fallback.
    public bool Available(ClaimsPrincipal user) => settings.Value.ServiceCdEnabled
        ? ServiceCdAllowed(settings.Value, user) : Session(user) is not null || windows.Available(user);
    public bool AvailableForBuild(ClaimsPrincipal user) => settings.Value.ServiceCiEnabled
        ? ServiceCiAllowed(settings.Value, user) : windows.AvailableForBuild(user);
    public bool AvailableForDeploymentCancellation(ClaimsPrincipal user) =>
        settings.Value.ServiceDeploymentCancellationEnabled && ServiceCdAllowed(settings.Value, user);

    public Task<T> RunDeploymentCancellationAsync<T>(ClaimsPrincipal user, Func<HttpClient, string, Task<T>> action)
    {
        if (!AvailableForDeploymentCancellation(user))
            throw new UnauthorizedAccessException("Deployment cancellation requires its explicit gate and an authorized Portal Windows SID.");
        return RunAsync(user, action);
    }

    public async Task<T> RunBuildAsync<T>(ClaimsPrincipal user, Func<HttpClient, string, Task<T>> action)
    {
        if (!settings.Value.ServiceCiEnabled) return await windows.RunBuildAsync(user, action);
        if (!ServiceCiAllowed(settings.Value, user)) throw new UnauthorizedAccessException("CI service actions require an explicitly authorized Portal Windows SID.");
        using var http = clients.CreateClient("PipelineServiceCi");
        var data = await PipelineUserTransport.ReadIdentity(http, true);
        var actual = Text(data["authenticatedUser"], "id");
        if (!Guid.TryParse(actual, out _) || !string.Equals(actual, settings.Value.ServiceIdentityId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Provisioned TFS service identity does not match the approved CI identity.");
        logger.LogInformation("Pipeline CI service action: PortalSid={PortalSid}, TfsActor={TfsActor}, Reference={Reference}.",
            PipelinePersonalSessions.Sid(user), actual, accessor.HttpContext?.TraceIdentifier);
        return await action(http, actual!);
    }

    public async Task<T> RunAsync<T>(ClaimsPrincipal user, Func<HttpClient, string, Task<T>> action)
    {
        if (settings.Value.ServiceCdEnabled)
        {
            if (!ServiceCdAllowed(settings.Value, user))
                throw new UnauthorizedAccessException("Single-account CD requires an explicitly authorized Portal Windows SID.");
            using var service = clients.CreateClient("PipelineServiceCd");
            var cdData = await PipelineUserTransport.ReadIdentity(service, true);
            var actor = cdData["authenticatedUser"];
            var id = Text(actor, "id");
            // connectionData authenticatedUser is the remote authenticated actor. Display names,
            // Account properties, authorizedUser and browser claims cannot establish its identity.
            if (!SingleAccountIdentityMatches(actor, settings.Value))
                throw new UnauthorizedAccessException("Protected credential did not authenticate as the authorized single-account TFS actor.");
            logger.LogInformation("Pipeline single-account CD action: PortalSid={PortalSid}, TfsActor={TfsActor}, TfsAccount={TfsAccount}, Reference={Reference}. This is not delegated personal identity.",
                PipelinePersonalSessions.Sid(user), id, SingleAccountActor, accessor.HttpContext?.TraceIdentifier);
            return await action(service, id!);
        }
        var session = Session(user);
        if (session is null) return await windows.RunAsync(user, action); // Never fall back to CI service credentials.
        using var http = personalClients.Create(session.Collection, session.Token);
        var data = await PipelineUserTransport.ReadIdentity(http, true);
        if (!string.Equals(Text(data["authenticatedUser"], "id"), session.TfsId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Personal TFS identity changed; reconnect your own account.");
        logger.LogInformation("Pipeline personal CD action: PortalSid={PortalSid}, TfsActor={TfsActor}, Reference={Reference}.",
            session.PortalSid, session.TfsId, accessor.HttpContext?.TraceIdentifier);
        return await action(http, session.TfsId);
    }

    public static string PersonalCollection(PipelineWorkgroupOptions settings, TfsGitConnectionOptions connection)
    {
        var original = TfsGitClientConfiguration.GetBaseAddress(connection.BaseUrl);
        var personal = TfsGitClientConfiguration.GetBaseAddress(settings.PersonalCollectionUrl ?? connection.BaseUrl);
        if (personal.Scheme != "https" || !string.Equals(personal.Host, original.Host, StringComparison.OrdinalIgnoreCase)
            || personal.AbsolutePath != original.AbsolutePath)
            throw new InvalidOperationException("Personal TFS login requires HTTPS on the same configured collection host/path.");
        return personal.AbsoluteUri;
    }

    public static HttpClient CreatePersonalClient(string? url, string token)
    {
        var collection = TfsGitClientConfiguration.GetBaseAddress(url);
        if (collection.Scheme != "https") throw new InvalidOperationException("Personal TFS authentication requires a trusted HTTPS TFS collection; PATs are never sent over HTTP.");
        var header = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
        var guard = new PipelineActionGuard(collection, personal: true, header.ToString())
        { InnerHandler = new HttpClientHandler { UseDefaultCredentials = false, UseProxy = false, AllowAutoRedirect = false } };
        var client = new HttpClient(guard) { BaseAddress = collection, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = header;
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}

public sealed class PipelineActionGuard(Uri collection, bool personal, string? authorization = null, bool serviceCd = false) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        var path = uri is null ? "" : uri.AbsolutePath;
        var allowedWrite = personal || serviceCd
            ? request.Method == HttpMethod.Patch && (System.Text.RegularExpressions.Regex.IsMatch(path, @"/_apis/release/approvals/[1-9]\d*$")
                || serviceCd && System.Text.RegularExpressions.Regex.IsMatch(path, @"/_apis/release/releases/[1-9]\d*/environments/[1-9]\d*$"))
            : (request.Method == HttpMethod.Post && path.EndsWith("/_apis/build/builds", StringComparison.Ordinal))
                || (request.Method == HttpMethod.Patch && System.Text.RegularExpressions.Regex.IsMatch(path, @"/_apis/build/builds/[1-9]\d*$"));
        if (uri is null || !TfsGitClientConfiguration.IsWithinCollection(uri, collection)
            || (request.Method != HttpMethod.Get && !allowedWrite)
            || request.Headers.Authorization?.ToString() != authorization || request.Headers.Host is not null
            || request.Headers.Contains("Proxy-Authorization") || (personal && uri.Scheme != "https"))
            throw new InvalidOperationException("Untrusted Pipeline action request.");
        return base.SendAsync(request, cancellationToken);
    }
}
