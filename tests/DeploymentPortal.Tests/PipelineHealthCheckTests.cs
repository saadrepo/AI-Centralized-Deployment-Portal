using System.Net;
using DeploymentPortal.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class PipelineHealthCheckTests
{
    [Fact]
    public async Task HealthIsGreenOnlyWhenPortalAndEveryRequiredDependencySucceed()
    {
        var responses = new Dictionary<string, HttpStatusCode> {
            ["https://portal.example/health"] = HttpStatusCode.OK,
            ["https://dependency.example/health"] = HttpStatusCode.OK };
        var check = Create(responses);
        var healthy = await check.Check("Portal", default);
        Assert.Equal("completed", healthy.State);
        Assert.Contains("1 required health dependencies", healthy.Detail);

        responses["https://dependency.example/health"] = HttpStatusCode.ServiceUnavailable;
        var failed = await check.Check("Portal", default);
        Assert.Equal("failed", failed.State);
        Assert.Contains("Dependency", failed.Detail);
    }

    [Fact]
    public async Task MissingOrUnsafeHealthConfigurationNeverReportsGreen()
    {
        var missing = new PipelineHealthCheck(new ConfigurationBuilder().Build(), new Factory([]));
        Assert.Equal("pending", (await missing.Check("Portal", default)).State);
        var unsafeConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Pipeline:HealthChecks:Portal:Url"] = "http://portal.example/health" }).Build();
        Assert.Equal("pending", (await new PipelineHealthCheck(unsafeConfiguration, new Factory([])).Check("Portal", default)).State);
    }

    private static PipelineHealthCheck Create(Dictionary<string, HttpStatusCode> responses)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Pipeline:HealthChecks:Portal:Url"] = "https://portal.example/health",
            ["Pipeline:HealthChecks:Portal:Dependencies:0:Name"] = "Dependency",
            ["Pipeline:HealthChecks:Portal:Dependencies:0:Url"] = "https://dependency.example/health" }).Build();
        return new(configuration, new Factory(responses));
    }

    private sealed class Factory(Dictionary<string, HttpStatusCode> responses) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(responses));
    }
    private sealed class Handler(Dictionary<string, HttpStatusCode> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(responses.GetValueOrDefault(request.RequestUri!.AbsoluteUri, HttpStatusCode.NotFound)));
    }
}
