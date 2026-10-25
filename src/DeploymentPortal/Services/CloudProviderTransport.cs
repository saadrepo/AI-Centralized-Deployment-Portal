using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeploymentPortal.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace DeploymentPortal.Services;

public interface ICloudProviderTransport
{
    Task<JsonDocument> JsonAsync(InfrastructureSource source, HttpMethod method, Uri uri, object? body, CancellationToken ct);
    Task<byte[]> ArchiveAsync(InfrastructureSource source, Uri uri, string[] approvedHosts, CancellationToken ct);
}

// A distinct capability from the existing read-only explorer and connection test.
public static class CloudExecutionRequestPolicy
{
    public static bool Allows(InfrastructureSource source, HttpMethod method, Uri uri, bool archive = false)
    {
        var provider = InfrastructureSourceService.TokenProvider(source.Provider);
        if (method == HttpMethod.Get && !archive && SourceExplorerRequestPolicy.Allows(provider, source.Destination, uri)) return true;
        var canonical = new Uri(SourceCredentialProvider.CanonicalDestination(provider, source.Destination));
        if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Host != canonical.Host ||
            uri.OriginalString.Split('?')[0].Contains('%') || uri.OriginalString.Contains('\\')) return false;
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.Any(p => p.Value.Count != 1)) return false;
        if (provider == SourceTokenProvider.GitHub) {
            var owner = Regex.Escape(canonical.Segments.Last().Trim('/'));
            var path = uri.AbsolutePath;
            if (query.Count != 0) return false;
            if (method == HttpMethod.Post && !archive) return Regex.IsMatch(path, $@"\A/repos/{owner}/[A-Za-z0-9_.-]{{1,100}}/actions/workflows/[1-9][0-9]{{0,17}}/dispatches\z", RegexOptions.IgnoreCase);
            return method == HttpMethod.Get && archive && Regex.IsMatch(path, $@"\A/repos/{owner}/[A-Za-z0-9_.-]{{1,100}}/actions/artifacts/[1-9][0-9]{{0,17}}/zip\z", RegexOptions.IgnoreCase);
        }
        // Azure DevOps remains read-only, regardless of execution configuration.
        return false;
    }
    public static bool SignedDownload(Uri uri, string[] hosts) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Port == 443 &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && !IPAddress.TryParse(uri.Host, out _) &&
        hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) &&
        new[] { ".blob.core.windows.net", ".actions.githubusercontent.com", ".vsblob.vsassets.io" }.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
}

public sealed partial class SourceCredentialProvider : ICloudProviderTransport
{
    private HttpRequestMessage CloudRequest(InfrastructureSource source, HttpMethod method, Uri uri, byte[] secret)
    {
        var provider = InfrastructureSourceService.TokenProvider(source.Provider);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("DeploymentPortal/1.0"); request.Headers.Accept.ParseAdd("application/json");
        if (provider == SourceTokenProvider.GitHub) {
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.ASCII.GetString(secret));
        } else request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + Encoding.ASCII.GetString(secret))));
        return request;
    }
    public async Task<JsonDocument> JsonAsync(InfrastructureSource source, HttpMethod method, Uri uri, object? body, CancellationToken ct)
    {
        CloudSecurity.Require(source.Enabled && CloudExecutionRequestPolicy.Allows(source, method, uri), "TRANSPORT_SCOPE");
        var secret = Read(source.CredentialReference, InfrastructureSourceService.TokenProvider(source.Provider), source.Destination);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try {
            using var client = new HttpClient(CreateHandler()); using var request = CloudRequest(source, method, uri, secret);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            CloudSecurity.Require(response.StatusCode == HttpStatusCode.OK, "PROVIDER_RESPONSE_UNCONFIRMED");
            var bytes = await BoundedAsync(response, 2 * 1024 * 1024, timeout.Token);
            CloudSecurity.Require(!Encoding.UTF8.GetString(bytes).Contains(Encoding.ASCII.GetString(secret), StringComparison.Ordinal), "PROVIDER_SECRET_REDACTED");
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch { throw new CloudExecutionException("PROVIDER_RESPONSE_UNCONFIRMED"); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public async Task<byte[]> ArchiveAsync(InfrastructureSource source, Uri uri, string[] approvedHosts, CancellationToken ct)
    {
        CloudSecurity.Require(source.Enabled && CloudExecutionRequestPolicy.Allows(source, HttpMethod.Get, uri, true), "ARTIFACT_ENDPOINT");
        var secret = Read(source.CredentialReference, InfrastructureSourceService.TokenProvider(source.Provider), source.Destination);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try {
            using var client = new HttpClient(CreateHandler()); using var request = CloudRequest(source, HttpMethod.Get, uri, secret);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect) {
                var location = response.Headers.Location;
                CloudSecurity.Require(location is not null && CloudExecutionRequestPolicy.SignedDownload(location, approvedHosts), "ARTIFACT_REDIRECT");
                // A separate unauthenticated request: never forward provider authorization to storage.
                using var download = new HttpRequestMessage(HttpMethod.Get, location);
                using var downloaded = await client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                CloudSecurity.Require(downloaded.StatusCode == HttpStatusCode.OK, "ARTIFACT_DOWNLOAD");
                return await BoundedAsync(downloaded, 64 * 1024 * 1024, timeout.Token);
            }
            CloudSecurity.Require(response.StatusCode == HttpStatusCode.OK, "ARTIFACT_DOWNLOAD");
            return await BoundedAsync(response, 64 * 1024 * 1024, timeout.Token);
        }
        catch { throw new CloudExecutionException("ARTIFACT_DOWNLOAD_UNCONFIRMED"); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    private static async Task<byte[]> BoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        CloudSecurity.Require(response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength <= limit, "RESPONSE_SIZE");
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[65536];
        int read; while ((read = await input.ReadAsync(buffer, ct)) > 0) {
            CloudSecurity.Require(output.Length + read <= limit, "RESPONSE_SIZE"); await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }
}
