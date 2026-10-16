namespace DeploymentPortal.Services;

public sealed record PipelineHealthResult(string? Url, string State, string Detail);
public interface IPipelineHealthCheck
{
    Task<PipelineHealthResult> Check(string applicationKey, CancellationToken ct);
}

public sealed class PipelineHealthCheck(IConfiguration configuration, IHttpClientFactory clients) : IPipelineHealthCheck
{
    public async Task<PipelineHealthResult> Check(string applicationKey, CancellationToken ct)
    {
        // Only an administrator-configured endpoint is accepted, never a URL supplied by the browser/TFS logs.
        var configured = configuration[$"Pipeline:HealthChecks:{applicationKey}:Url"];
        if (string.IsNullOrWhiteSpace(configured)) return new(null, "pending", "Health endpoint not configured");
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            return new(null, "pending", "Configure a trusted HTTPS health endpoint without credentials or query parameters");
        try
        {
            using var client = clients.CreateClient("PipelineHealth");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var expected = configuration.GetValue($"Pipeline:HealthChecks:{applicationKey}:ExpectedStatusCode", 200);
            if (expected is < 200 or > 299) return new(url.AbsoluteUri, "pending", "Expected health status must be a successful HTTP status");
            var healthy = (int)response.StatusCode == expected;
            return new(url.AbsoluteUri, healthy ? "completed" : "failed", $"Endpoint HTTP {(int)response.StatusCode}; deployed version not verified");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { return new(url.AbsoluteUri, "failed", "Health endpoint could not be reached"); }
    }
}
