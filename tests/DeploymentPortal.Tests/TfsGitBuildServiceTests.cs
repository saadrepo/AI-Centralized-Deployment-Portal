using System.Net;
using System.Text.Json;
using DeploymentPortal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace DeploymentPortal.Tests;

public class TfsGitBuildServiceTests
{
    private const string BuildJson = """{"id":42,"buildNumber":"CI-42","sourceBranch":"refs/heads/Dev","sourceVersion":"abcdef123456","status":"completed","result":"succeeded","definition":{"id":1}}""";

    [Fact]
    public async Task RecentBuildsUseConfiguredProjectAndApiWithDefinitionBranchLimitAndOrder()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("/tfs/DefaultCollection/deployment-portal-poc/_apis/build/builds", request.RequestUri!.AbsolutePath);
            Assert.Equal("?api-version=4.1&definitions=1&branchName=refs%2Fheads%2FDev&$top=25&queryOrder=queueTimeDescending", request.RequestUri.Query);
            return Json("{\"value\":[" + BuildJson + "," + BuildJson.Replace("\"id\":1", "\"id\":99") + "," + BuildJson.Replace("refs/heads/Dev", "refs/heads/main") + "]}");
        });
        using var client = Client(handler);
        var build = Assert.Single(await Service(client).GetRecentBuildsAsync());
        Assert.Equal("CI-42", build.BuildNumber);
        Assert.Equal("Dev", build.BranchName);
        Assert.Equal("abcdef123456", build.SourceVersion);
        Assert.Equal("completed", build.Status);
        Assert.Equal("succeeded", build.Result);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task BuildAndArtifactsParseMetadataAndUseOnlyGet()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/artifacts")
            ? Json("""{"value":[{"name":"drop","resource":{"type":"Container","downloadUrl":"http://external.example/secret","url":"http://external.example/secret","data":"private-path"}},{"name":"logs","resource":null}]}""")
            : Json(BuildJson));
        using var client = Client(handler);
        var service = Service(client);
        Assert.Equal(42, (await service.GetBuildAsync(42))?.Id);
        var artifacts = await service.GetArtifactsAsync(42);
        Assert.Equal(2, artifacts.Count);
        Assert.Equal("drop", artifacts[0].Name);
        Assert.Equal("Container", artifacts[0].Resource?.Type);
        Assert.Null(artifacts[1].Resource);
        Assert.DoesNotContain("external.example", JsonSerializer.Serialize(artifacts));
        Assert.DoesNotContain("private-path", JsonSerializer.Serialize(artifacts));
        Assert.Equal(3, handler.Count);
    }

    [Fact]
    public async Task LatestSuccessfulUsesServerFiltersRatherThanRecentBuildWindow()
    {
        using var handler = new Handler(request =>
        {
            Assert.Contains("statusFilter=completed&resultFilter=succeeded&$top=1&queryOrder=finishTimeDescending", request.RequestUri!.Query);
            Assert.Contains("definitions=1&branchName=refs%2Fheads%2FDev", request.RequestUri.Query);
            return Json("{\"value\":[" + BuildJson + "]}");
        });
        using var client = Client(handler);
        Assert.Equal(42, (await Service(client).GetLatestSuccessfulBuildAsync())?.Id);
    }

    [Fact]
    public async Task LatestSuccessfulRejectsUnexpectedFailedBuild()
    {
        using var handler = new Handler(_ => Json("{\"value\":[" + BuildJson.Replace("succeeded", "failed") + "]}"));
        using var client = Client(handler);
        await Assert.ThrowsAsync<JsonException>(() => Service(client).GetLatestSuccessfulBuildAsync());
    }

    [Theory]
    [InlineData("{\"value\":[]}")]
    public async Task EmptyCollectionsAreSuccessful(string json)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(BuildJson) : Json(json));
        using var client = Client(handler);
        Assert.Empty(await Service(client).GetRecentBuildsAsync());
        Assert.Empty(await Service(client).GetArtifactsAsync(42));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidBuildIdsMakeNoRequests(int id)
    {
        using var handler = new Handler(_ => throw new Exception("Unexpected request"));
        using var client = Client(handler);
        var service = Service(client);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetBuildAsync(id));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetArtifactsAsync(id));
        Assert.Equal(0, handler.Count);
    }

    [Theory]
    [InlineData("\"id\":1", "\"id\":2")]
    [InlineData("refs/heads/Dev", "refs/heads/main")]
    [InlineData("refs/heads/Dev", "refs/heads/dev")]
    [InlineData("\"id\":42", "\"id\":43")]
    public async Task OutOfScopeBuildsCannotLoadArtifacts(string before, string after)
    {
        using var handler = new Handler(request =>
        {
            Assert.EndsWith("/build/builds/42", request.RequestUri!.AbsolutePath);
            return Json(BuildJson.Replace(before, after));
        });
        using var client = Client(handler);
        var service = Service(client);
        Assert.Null(await service.GetBuildAsync(42));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetArtifactsAsync(42));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task MissingBuildDoesNotLoadArtifacts()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = Client(handler);
        Assert.Null(await Service(client).GetBuildAsync(42));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(client).GetArtifactsAsync(42));
        Assert.Equal(2, handler.Count);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(302)]
    [InlineData(500)]
    public async Task ApiAndAuthenticationFailuresAreNotEmptyResults(int status)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/42")
            ? Json(BuildJson) : new HttpResponseMessage((HttpStatusCode)status));
        using var client = Client(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => Service(client).GetRecentBuildsAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => Service(client).GetArtifactsAsync(42));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":null}")]
    [InlineData("{\"value\":[null]}")]
    [InlineData("<html>login</html>")]
    public async Task MalformedCollectionsFailSafely(string json)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(BuildJson) : Json(json));
        using var client = Client(handler);
        await Assert.ThrowsAsync<JsonException>(() => Service(client).GetRecentBuildsAsync());
        await Assert.ThrowsAsync<JsonException>(() => Service(client).GetArtifactsAsync(42));
    }

    [Fact]
    public async Task ProductionRegistrationSharesGitSecurityPipelineAndRejectsRedirects()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("http://external.example/private-token") }
        });
        var filter = new SecurityPipelineFilter(handler);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration());
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(filter);
        services.AddTfsGitClient();
        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ITfsGitBuildService>();
        Assert.IsType<TfsGitBuildService>(service);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetRecentBuildsAsync());
        Assert.Equal(1, handler.Count);
        Assert.True(filter.Checked);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["TfsGit:BaseUrl"] = "http://netserver:8080/tfs/DefaultCollection",
        ["TfsGit:Project"] = "deployment-portal-poc", ["TfsGit:ApiVersion"] = "4.1",
        ["Tfs:Project"] = "PMSNZ", ["Tfs:ApiVersion"] = "wrong-version"
    }).Build();

    private static HttpClient Client(HttpMessageHandler handler)
    {
        var address = TfsGitClientConfiguration.GetBaseAddress(Configuration()["TfsGit:BaseUrl"]);
        return new HttpClient(new TfsGitRequestGuard(address) { InnerHandler = handler }) { BaseAddress = address };
    }

    private static TfsGitBuildService Service(HttpClient client) => new(client, Configuration());
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("netserver", request.RequestUri!.Host);
            Assert.Equal(8080, request.RequestUri.Port);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class SecurityPipelineFilter(HttpMessageHandler replacement) : IHttpMessageHandlerBuilderFilter
    {
        public bool Checked { get; private set; }
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            var handler = Assert.IsType<HttpClientHandler>(builder.PrimaryHandler);
            Assert.False(handler.AllowAutoRedirect);
            Assert.False(handler.UseProxy);
            Assert.False(handler.UseDefaultCredentials);
            Assert.Null(handler.Credentials!.GetCredential(new Uri("http://external.example/"), "NTLM"));
            Assert.Contains(builder.AdditionalHandlers, item => item is TfsGitRequestGuard);
            Checked = true;
            handler.Dispose();
            builder.PrimaryHandler = replacement;
        };
    }
}
