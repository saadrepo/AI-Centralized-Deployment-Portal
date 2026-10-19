using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DeploymentPortal.Services;

public sealed class TfsGitConnectionOptions
{
    public string? BaseUrl { get; set; }
}

public static class TfsGitClientConfiguration
{
    private const string InvalidBaseUrlMessage = "TfsGit BaseUrl must use HTTPS or the approved internal HTTP PoC endpoint, without credentials, query, or fragment.";
    private static readonly Uri TrustedHttpEndpoint = new("http://netserver:8080/tfs/DefaultCollection/");

    public static IServiceCollection AddTfsGitClient(this IServiceCollection services)
    {
        services.TryAddSingleton<ITfsWindowsCredentialProvider, TfsWindowsCredentialProvider>();
        services.TryAddSingleton<IPipelineReleaseDiagnosticFile, PipelineReleaseDiagnosticFile>();
        services.AddOptions<TfsGitConnectionOptions>().BindConfiguration("TfsGit")
            .Validate(options => IsAllowedBaseUrl(options.BaseUrl), InvalidBaseUrlMessage)
            .ValidateOnStart();
        services.AddHttpClient<ITfsGitService, TfsGitService>((provider, client) =>
        {
            client.BaseAddress = GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(provider => CreatePrimaryHandler(
            provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl,
            () => provider.GetRequiredService<ITfsWindowsCredentialProvider>().GetCredential(
                GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl))))
            .AddHttpMessageHandler(provider => new TfsGitRequestGuard(
                GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl)))
            .AddTypedClient<ITfsGitBuildService, TfsGitBuildService>()
            .AddTypedClient<ITfsArtifactExplorer, TfsArtifactExplorer>()
            .AddTypedClient<ITfsCdPreparation, TfsCdPreparation>();
        // Pipeline reads retain the same credential and collection guard as the Git explorer.
        services.AddHttpClient<PipelineService>((provider, client) =>
        {
            client.BaseAddress = GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(provider => CreatePrimaryHandler(
            provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl,
            () => provider.GetRequiredService<ITfsWindowsCredentialProvider>().GetCredential(
                GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl))))
            .AddHttpMessageHandler(provider => new TfsGitRequestGuard(
                GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl)));
        services.AddHttpContextAccessor();
        services.AddOptions<PipelineWorkgroupOptions>().BindConfiguration("Pipeline:Workgroup");
        services.TryAddSingleton<PipelinePersonalSessions>();
        services.TryAddSingleton<IPipelinePersonalClientFactory, PipelinePersonalClientFactory>();
        services.TryAddScoped<PipelineUserTransport>();
        services.AddHttpClient("PipelineServiceCi", (provider, http) => {
            http.BaseAddress = GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }).ConfigurePrimaryHttpMessageHandler(provider => CreatePrimaryHandler(
            provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl,
            () => provider.GetRequiredService<ITfsWindowsCredentialProvider>().GetCredential(
                GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl))))
            .AddHttpMessageHandler(provider => new PipelineActionGuard(
                GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl), personal: false));
        services.TryAddScoped<IPipelineUserTransport, PipelineWorkgroupTransport>();
        // Separate CD client reuses protected Windows credentials; CI's guard is unchanged.
        services.AddHttpClient("PipelineServiceCd", (provider, http) => {
            http.BaseAddress = GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }).ConfigurePrimaryHttpMessageHandler(provider => CreatePrimaryHandler(
            provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl,
            () => provider.GetRequiredService<ITfsWindowsCredentialProvider>().GetCredential(
                GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl))))
            .AddHttpMessageHandler(provider => new PipelineActionGuard(
                GetBaseAddress(provider.GetRequiredService<IOptions<TfsGitConnectionOptions>>().Value.BaseUrl), personal: false, serviceCd: true));
        services.TryAddScoped<IPipelineHealthCheck, PipelineHealthCheck>();
        services.TryAddScoped<IDemoTargetHealthCheck, DemoTargetHealthCheck>();
        services.AddHttpClient("PipelineHealth", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseDefaultCredentials = false });
        return services;
    }

    public static bool IsAllowedBaseUrl(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return uri.Scheme == Uri.UriSchemeHttp &&
            string.Equals(uri.Host, TrustedHttpEndpoint.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == TrustedHttpEndpoint.Port &&
            (uri.AbsolutePath == TrustedHttpEndpoint.AbsolutePath ||
             uri.AbsolutePath == TrustedHttpEndpoint.AbsolutePath.TrimEnd('/'));
    }

    public static Uri GetBaseAddress(string? baseUrl)
    {
        if (!IsAllowedBaseUrl(baseUrl)) throw new InvalidOperationException(InvalidBaseUrlMessage);
        return new Uri(baseUrl!.TrimEnd('/') + "/");
    }

    public static HttpClientHandler CreatePrimaryHandler(string? baseUrl, Func<NetworkCredential>? credentials = null)
    {
        var baseAddress = GetBaseAddress(baseUrl);
        return new HttpClientHandler
        {
            Credentials = new CollectionWindowsCredentials(baseAddress, credentials),
            AllowAutoRedirect = false,
            UseProxy = false,
            PreAuthenticate = false
        };
    }

    internal static bool IsWithinCollection(Uri uri, Uri baseAddress) =>
        uri.IsAbsoluteUri && uri.Scheme == baseAddress.Scheme &&
        string.Equals(uri.Host, baseAddress.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == baseAddress.Port && uri.AbsolutePath.StartsWith(baseAddress.AbsolutePath, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && !HasUnsafePath(uri);

    private static bool HasUnsafePath(Uri uri)
    {
        var decodedPath = Uri.UnescapeDataString(uri.AbsolutePath);
        // Reject encoded traversal/separators and nested escaping before the server can normalize them.
        return decodedPath.Contains('\\') || decodedPath.Contains('%') ||
            decodedPath.Split('/').Any(segment => segment is "." or "..") ||
            uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CollectionWindowsCredentials(Uri baseAddress, Func<NetworkCredential>? credentials) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType) =>
            IsWithinCollection(uri, baseAddress) &&
            (string.Equals(authType, "Negotiate", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(authType, "NTLM", StringComparison.OrdinalIgnoreCase))
                ? credentials is null ? CredentialCache.DefaultNetworkCredentials : credentials.Invoke() : null;
    }
}

public sealed class TfsGitRequestGuard(Uri baseAddress) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (request.Method != HttpMethod.Get || uri is null ||
            !TfsGitClientConfiguration.IsWithinCollection(uri, baseAddress) || request.Headers.Authorization is not null ||
            request.Headers.Contains("Proxy-Authorization") || request.Headers.Host is not null)
            throw new InvalidOperationException("TFS Git requests must be read-only and stay within the configured collection; explicit authorization headers are not permitted.");

        return base.SendAsync(request, cancellationToken);
    }
}
