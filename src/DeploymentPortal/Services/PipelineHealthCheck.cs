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
        var endpoints = new List<(string Name, Uri Url, int Expected)> { ("Portal", url, configuration.GetValue($"Pipeline:HealthChecks:{applicationKey}:ExpectedStatusCode", 200)) };
        foreach (var dependency in configuration.GetSection($"Pipeline:HealthChecks:{applicationKey}:Dependencies").GetChildren())
        {
            var dependencyUrl = dependency["Url"];
            if (string.IsNullOrWhiteSpace(dependencyUrl)) continue;
            if (!Uri.TryCreate(dependencyUrl, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
                return new(url.AbsoluteUri, "pending", $"Dependency {dependency.Key} needs a trusted HTTPS endpoint without credentials or query parameters");
            endpoints.Add((dependency["Name"] ?? dependency.Key, parsed, dependency.GetValue("ExpectedStatusCode", 200)));
        }
        if (endpoints.Any(endpoint => endpoint.Expected is < 200 or > 299))
            return new(url.AbsoluteUri, "pending", "Expected health statuses must be successful HTTP statuses");
        try
        {
            using var client = clients.CreateClient("PipelineHealth");
            foreach (var endpoint in endpoints)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode != endpoint.Expected)
                    return new(url.AbsoluteUri, "failed", $"{endpoint.Name} health check returned HTTP {(int)response.StatusCode}");
            }
            return new(url.AbsoluteUri, "completed", endpoints.Count == 1
                ? "Deployed Portal health endpoint succeeded"
                : $"Deployed Portal and {endpoints.Count - 1} required health dependencies succeeded");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { return new(url.AbsoluteUri, "failed", "Health endpoint could not be reached"); }
    }
}
