using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeploymentPortal.Tests;

public class TfsGitServiceTests
{
    [Fact]
    public async Task GetRepositoriesAndBranches_ParsesResponses()
    {
        // Arrange - fake responses for repositories and refs
        var handler = new TestMessageHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["TfsGit:Project"] = "MyProject",
            ["TfsGit:ApiVersion"] = "4.1"
        }).Build();
        var service = new TfsGitService(client, config);

        var reposTask = service.GetRepositoriesAsync();
        var repos = await reposTask;
        Assert.Single(repos);
        Assert.Equal("repo-id-1", repos[0].Id);
        Assert.Equal("MyRepo", repos[0].Name);

        var branches = await service.GetBranchesAsync(repos[0].Id!);
        Assert.Contains(branches, b => b.Name == "Dev");
        Assert.Contains(branches, b => b.Name == "main" || b.Name == "Main");
    }

    private sealed class TestMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath + request.RequestUri.Query;
            if (path.Contains("/_apis/git/repositories/repo-id-1/refs"))
            {
                var json = "{ \"value\": [ { \"name\": \"refs/heads/Dev\" }, { \"name\": \"refs/heads/main\" } ] }";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } } });
            }
            if (path.Contains("/_apis/git/repositories"))
            {
                var json = "{ \"value\": [ { \"id\": \"repo-id-1\", \"name\": \"MyRepo\", \"defaultBranch\": \"refs/heads/main\" } ] }";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } } });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
