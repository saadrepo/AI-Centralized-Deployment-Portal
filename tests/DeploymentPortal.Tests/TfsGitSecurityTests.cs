using System.Net;
using System.Net.Http.Headers;
using DeploymentPortal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public class TfsGitSecurityTests
{
    private const string Endpoint = "http://netserver:8080/tfs/DefaultCollection";
    private const string RepositoryPath = "/tfs/DefaultCollection/deployment-portal-poc/_apis/git/repositories";

    [Theory]
    [InlineData(Endpoint)]
    [InlineData(Endpoint + "/")]
    [InlineData("http://NETSERVER:8080/tfs/DefaultCollection")]
    [InlineData("https://approved.example/tfs/DefaultCollection")]
    public void AllowedConfigurationResolvesTypedClient(string endpoint)
    {
        using var provider = Provider(endpoint);
        provider.GetRequiredService<IStartupValidator>().Validate();
        Assert.IsType<TfsGitService>(provider.GetRequiredService<ITfsGitService>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/tfs/DefaultCollection")]
    [InlineData("http://external.example:8080/tfs/DefaultCollection")]
    [InlineData("http://netserver.attacker.example:8080/tfs/DefaultCollection")]
    [InlineData("http://netserver.fff:8080/tfs/DefaultCollection")]
    [InlineData("http://192.168.0.234:8080/tfs/DefaultCollection")]
    [InlineData("http://netserver:80/tfs/DefaultCollection")]
    [InlineData("http://netserver:8080/tfs/OtherCollection")]
    [InlineData("http://netserver:8080/tfs/DefaultCollectionExtra")]
    [InlineData(Endpoint + "/child")]
    [InlineData(Endpoint + "?token=private-token")]
    [InlineData(Endpoint + "#private-token")]
    [InlineData("http://user:private-token@netserver:8080/tfs/DefaultCollection")]
    [InlineData("https://user:private-token@approved.example/tfs/DefaultCollection")]
    [InlineData("ftp://netserver:8080/tfs/DefaultCollection")]
    public void UnapprovedConfigurationFailsAtStartupWithoutEchoingItsValue(string? endpoint)
    {
        using var provider = Provider(endpoint);
        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.DoesNotContain("private-token", error.Message);
        Assert.DoesNotContain("user:", error.Message);
    }

    [Fact]
    public void WindowsCredentialsAreScopedAndRedirectsAndProxiesAreDisabled()
    {
        using var handler = TfsGitClientConfiguration.CreatePrimaryHandler(Endpoint);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseDefaultCredentials);
        Assert.False(handler.PreAuthenticate);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
        var credentials = Assert.IsAssignableFrom<ICredentials>(handler.Credentials);
        var repository = new Uri("http://netserver:8080" + RepositoryPath);
        Assert.Same(CredentialCache.DefaultNetworkCredentials, credentials.GetCredential(repository, "Negotiate"));
        Assert.Same(CredentialCache.DefaultNetworkCredentials, credentials.GetCredential(repository, "NTLM"));
        Assert.Null(credentials.GetCredential(repository, "Basic"));
        Assert.Null(credentials.GetCredential(repository, "Digest"));
        foreach (var destination in new[]
        {
            "http://external.example:8080" + RepositoryPath,
            "http://netserver:80" + RepositoryPath,
            "https://netserver:8080" + RepositoryPath,
            "http://netserver:8080/tfs/OtherCollection/project/_apis/git/repositories",
            "http://netserver:8080/tfs/DefaultCollectionExtra/project/_apis/git/repositories"
        })
        {
            Assert.Null(credentials.GetCredential(new Uri(destination), "Negotiate"));
            Assert.Null(credentials.GetCredential(new Uri(destination), "NTLM"));
        }
    }

    [Theory]
    [InlineData("http://external.example:8080" + RepositoryPath)]
    [InlineData("http://netserver:80" + RepositoryPath)]
    [InlineData("https://netserver:8080" + RepositoryPath)]
    [InlineData("http://netserver:8080/tfs/OtherCollection/project/_apis/git/repositories")]
    [InlineData("http://netserver:8080/tfs/DefaultCollectionExtra/project/_apis/git/repositories")]
    [InlineData(Endpoint + "/../../private")]
    [InlineData(Endpoint + "/%2e%2e%2f%2e%2e%2fprivate")]
    [InlineData(Endpoint + "/%252e%252e%252fprivate")]
    [InlineData("http://user:private-token@netserver:8080" + RepositoryPath)]
    [InlineData("http://netserver:8080" + RepositoryPath + "#private-token")]
    public async Task OutOfScopeRequestsAreRejectedBeforeNetworkAccess(string destination)
    {
        using var handler = new RecordingHandler();
        using var client = GuardedClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(destination));
        Assert.Equal(0, handler.RequestCount);
        Assert.DoesNotContain("private-token", error.Message);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task WriteMethodsAreRejectedBeforeNetworkAccess(string method)
    {
        using var handler = new RecordingHandler();
        using var client = GuardedClient(handler);
        using var request = new HttpRequestMessage(new HttpMethod(method), "deployment-portal-poc/_apis/git/repositories");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ExplicitAuthorizationIsRejectedBeforeNetworkAccess()
    {
        using var handler = new RecordingHandler();
        using var client = GuardedClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "private-token");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("deployment-portal-poc/_apis/git/repositories"));
        Assert.Equal(0, handler.RequestCount);
        Assert.DoesNotContain("private-token", error.Message);
    }

    [Theory]
    [InlineData("Host", "external.example")]
    [InlineData("Proxy-Authorization", "Basic private-token")]
    public async Task HostAndProxyAuthorizationOverridesAreRejectedBeforeNetworkAccess(string header, string value)
    {
        using var handler = new RecordingHandler();
        using var client = GuardedClient(handler);
        client.DefaultRequestHeaders.TryAddWithoutValidation(header, value);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("deployment-portal-poc/_apis/git/repositories"));
        Assert.Equal(0, handler.RequestCount);
        Assert.DoesNotContain(value, error.Message);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task GitApiRedirectsFailWithoutFollowingLocation(int status)
    {
        using var handler = new RecordingHandler { Status = (HttpStatusCode)status };
        using var client = GuardedClient(handler);
        var service = new TfsGitService(client, Configuration(Endpoint));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetRepositoriesAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetBranchesAsync("repo-id"));
        Assert.Equal(2, handler.RequestCount);
        Assert.All(handler.Destinations, uri => Assert.Equal("netserver", uri.Host));
    }

    [Fact]
    public async Task RepositoryAndBranchReadsStayInConfiguredCollection()
    {
        using var handler = new RecordingHandler();
        using var client = GuardedClient(handler);
        var service = new TfsGitService(client, Configuration(Endpoint));
        Assert.Empty(await service.GetRepositoriesAsync());
        Assert.Empty(await service.GetBranchesAsync("repo-id"));
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(RepositoryPath, handler.Destinations[0].AbsolutePath);
        Assert.Equal(RepositoryPath + "/repo-id/refs", handler.Destinations[1].AbsolutePath);
        Assert.Equal("?api-version=4.1&filter=heads/", handler.Destinations[1].Query);
    }

    private static ServiceProvider Provider(string? endpoint)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration(endpoint));
        services.AddTfsGitClient();
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(string? endpoint) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TfsGit:BaseUrl"] = endpoint,
            ["TfsGit:Project"] = "deployment-portal-poc",
            ["TfsGit:ApiVersion"] = "4.1"
        }).Build();

    private static HttpClient GuardedClient(HttpMessageHandler handler)
    {
        var baseAddress = TfsGitClientConfiguration.GetBaseAddress(Endpoint);
        return new HttpClient(new TfsGitRequestGuard(baseAddress) { InnerHandler = handler }) { BaseAddress = baseAddress };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public int RequestCount { get; private set; }
        public List<Uri> Destinations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Destinations.Add(request.RequestUri!);
            var response = new HttpResponseMessage(Status) { Content = new StringContent("{\"value\":[]}") };
            response.Headers.Location = new Uri("http://external.example/private-token");
            return Task.FromResult(response);
        }
    }
}
