using System.Net;
using System.Text.Json;
using DeploymentPortal.Controllers;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeploymentPortal.Tests;

public class TfsArtifactExplorerTests
{
    private const string Artifact = """{"value":[{"name":"drop","resource":{"type":"Container","data":"#/12/drop","downloadUrl":"http://evil.invalid/secret"}}]}""";
    private const string Items = """{"value":[{"path":"drop","itemType":"folder"},{"path":"drop/publish/web.config","itemType":"file","status":"created","fileLength":123,"contentLocation":"http://evil.invalid/token"},{"path":"drop/publish/app.dll","itemType":"file","fileLength":456}]}""";

    [Fact]
    public async Task ReadsOnlyPinnedContainerMetadataAndOmitsSensitiveResources()
    {
        using var handler = new Handler(Artifact, Items);
        using var client = Client(handler);
        var result = await Explorer(client).InspectAsync(3);
        Assert.Equal(3, result.Build.Id);
        Assert.Equal("drop", result.Artifact);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(123, result.Entries.Single(x => x.Path.EndsWith("web.config")).Size);
        Assert.DoesNotContain("evil.invalid", JsonSerializer.Serialize(result));
        Assert.Equal(2, handler.Paths.Count);
        Assert.Contains("/_apis/resources/Containers/12?api-version=4.1-preview.4&itemPath=drop&$format=json&isShallow=false&includeDownloadTickets=false", handler.Paths[1]);
    }

    [Theory]
    [InlineData("drop/../web.config")]
    [InlineData("other/web.config")]
    [InlineData("drop/C:/file")]
    [InlineData("drop/a%2fb")]
    [InlineData("drop/CON.txt")]
    [InlineData("drop/a\\b")]
    [InlineData("drop/name.")]
    public async Task UnsafeOrOutOfRootPathsFail(string path)
    {
        using var handler = new Handler(Artifact, JsonSerializer.Serialize(new { value = new[] { new { path, itemType = "file", fileLength = 1 } } }));
        using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Explorer(client).InspectAsync(3));
    }

    [Theory]
    [InlineData("drop", "drop")]
    [InlineData("drop/app.dll", "drop/APP.dll")]
    public async Task DuplicateArtifactOrWindowsPathCollisionFails(string first, string second)
    {
        var artifact = first == "drop" ? """{"value":[{"name":"drop"},{"name":"drop"}]}""" : Artifact;
        var items = JsonSerializer.Serialize(new { value = new[] { new { path = first, itemType = "file", fileLength = 1 }, new { path = second, itemType = "file", fileLength = 1 } } });
        using var handler = new Handler(artifact, items);
        using var client = Client(handler);
        if (first == "drop") await Assert.ThrowsAsync<ArgumentException>(() => Explorer(client).InspectAsync(3));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Explorer(client).InspectAsync(3));
    }

    [Theory]
    [InlineData("failed", "completed")]
    [InlineData("succeeded", "inProgress")]
    public async Task UnsuccessfulOrIncompleteBuildMakesNoArtifactRequests(string result, string status)
    {
        using var handler = new Handler(Artifact, Items);
        using var client = Client(handler);
        var build = Build(); build.Result = result; build.Status = status;
        await Assert.ThrowsAsync<ArgumentException>(() => Explorer(client, build).InspectAsync(3));
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task InvalidCommitMakesNoArtifactRequests()
    {
        using var handler = new Handler(Artifact, Items);
        using var client = Client(handler);
        var build = Build(); build.SourceVersion = "missing";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Explorer(client, build).InspectAsync(3));
        Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData("FilePath", "\\\\server\\share")]
    [InlineData("Container", "http://evil.invalid/")]
    [InlineData("Container", "#/12/../drop")]
    [InlineData("Container", "#/9999999999999999999999/drop")]
    public async Task UnsupportedOrUnsafeResourceNeverRequestsContainer(string type, string data)
    {
        var artifact = JsonSerializer.Serialize(new { value = new[] { new { name = "drop", resource = new { type, data } } } });
        using var handler = new Handler(artifact, Items);
        using var client = Client(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => Explorer(client).InspectAsync(3));
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task OversizedMetadataFails()
    {
        using var handler = new Handler(Artifact, new string(' ', TfsArtifactExplorer.MaximumResponseBytes + 1));
        using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Explorer(client).InspectAsync(3));
    }

    [Theory]
    [InlineData("""[{"path":"drop/publish/web.config","itemType":2,"status":1,"fileLength":123}]""")]
    [InlineData("""{"value":[{"path":"drop/publish/web.config","itemType":"File","status":"Created","fileLength":123}]}""")]
    public async Task AcceptsTfsContainerEnumAndCollectionWireFormats(string items)
    {
        using var handler = new Handler(Artifact, items);
        using var client = Client(handler);
        var result = await Explorer(client).InspectAsync(3);
        Assert.Equal("publish/web.config", Assert.Single(result.Entries).Path);
    }

    [Fact]
    public async Task DefaultOffExplorerMakesNoRequests()
    {
        using var handler = new Handler(Artifact, Items);
        using var client = Client(handler);
        var controller = new TfsArtifactsController(Explorer(client), NullLogger<TfsArtifactsController>.Instance);
        var result = Assert.IsType<ViewResult>(await controller.Index(3, null, default));
        Assert.Contains("disabled", Assert.IsType<ArtifactExplorerPage>(result.Model).Error!);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task RedirectIsNotFollowedAndNoUpstreamMessageIsDisplayed()
    {
        using var handler = new Handler(Artifact, Items) { Redirect = true };
        using var client = Client(handler);
        var controller = Controller(Explorer(client));
        var view = Assert.IsType<ViewResult>(await controller.Index(3, null, default));
        var model = Assert.IsType<ArtifactExplorerPage>(view.Model);
        Assert.Equal(502, controller.Response.StatusCode);
        Assert.DoesNotContain("evil", model.Error!);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task NavigationUsesOnlyValidatedTreeAndInfersParentFolders()
    {
        using var handler = new Handler(Artifact, Items);
        using var client = Client(handler);
        var controller = Controller(Explorer(client));
        var view = Assert.IsType<ViewResult>(await controller.Index(3, null, default));
        var model = Assert.IsType<ArtifactExplorerPage>(view.Model);
        Assert.Equal("publish", Assert.Single(model.Entries).Path);
        Assert.IsType<NotFoundResult>(await controller.Index(3, "../secret", default));
        Assert.All(handler.Paths.Where(x => x.Contains("Containers")), path => Assert.Contains("itemPath=drop&", path));
    }

    private static TfsArtifactsController Controller(ITfsArtifactExplorer explorer) => new(explorer, NullLogger<TfsArtifactsController>.Instance, Microsoft.Extensions.Options.Options.Create(new TfsCdOptions { ExplorerEnabled = true })) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    private static TfsGitCiBuildViewModel Build() => new() { Id = 3, SourceVersion = new string('a', 40), Status = "completed", Result = "succeeded", SourceBranch = "refs/heads/Dev", Definition = new() { Id = 1 } };
    private static HttpClient Client(HttpMessageHandler handler) => new(new TfsGitRequestGuard(new Uri("http://netserver:8080/tfs/DefaultCollection/")) { InnerHandler = handler }) { BaseAddress = new Uri("http://netserver:8080/tfs/DefaultCollection/") };
    private static TfsArtifactExplorer Explorer(HttpClient client, TfsGitCiBuildViewModel? build = null) => new(client, new Builds(build ?? Build()), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TfsGit:Project"] = "deployment-portal-poc", ["TfsGit:ApiVersion"] = "4.1" }).Build());
    private sealed class Builds(TfsGitCiBuildViewModel build) : ITfsGitBuildService
    {
        public Task<TfsGitCiBuildViewModel?> GetBuildAsync(int id) => Task.FromResult<TfsGitCiBuildViewModel?>(id == build.Id ? build : null);
        public Task<IReadOnlyList<TfsGitCiBuildViewModel>> GetRecentBuildsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<TfsGitCiArtifactViewModel>> GetArtifactsAsync(int id) => throw new NotSupportedException();
    }
    private sealed class Handler(string artifacts, string items) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public bool Redirect { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(Redirect ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://evil.invalid/token") } } : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("artifacts") ? artifacts : items) });
        }
    }
}
