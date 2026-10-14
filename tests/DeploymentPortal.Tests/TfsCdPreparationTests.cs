using System.Net;
using System.Text;
using DeploymentPortal.Controllers;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public class TfsCdPreparationTests
{
    [Fact]
    public async Task DemoPreparationPinsDefinitionRepositoryAndDestinationWithoutQueueing()
    {
        using var fixture = new Fixture(useRealExplorer: true, demo: true);
        var prepared = await fixture.Service.StageAsync("Demo", 3, "dev-host");
        Assert.Equal("Demo", prepared.ApplicationKey);
        Assert.Equal(2, prepared.DefinitionId);
        Assert.Equal("Demo-App-Repo", prepared.RepositoryName);
        Assert.Equal("deployment-portal-poc", prepared.ProjectName);
        Assert.Equal("CdDemo", prepared.SiteName);
        Assert.Equal(@"C:\Sites\CdDemo", prepared.TargetPath);
        var loaded = await fixture.Service.FindAsync(prepared.Id);
        Assert.Equal(prepared.DefinitionId, loaded.DefinitionId);
        Assert.Equal(prepared.RepositoryName, loaded.RepositoryName);
        Assert.Equal(prepared.ManifestSha256, loaded.ManifestSha256);
    }

    [Fact]
    public async Task DemoMissingPhysicalPathCannotInspectOrDownloadPackage()
    {
        using var fixture = new Fixture(demo: true);
        fixture.Registry.App.Destinations[0].TargetPath = "";
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => fixture.Service.StageAsync("Demo",3,"dev-host"));
        Assert.Equal(0,fixture.Explorer.Calls); Assert.Equal(0,fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("repository")]
    [InlineData("project")]
    [InlineData("branch")]
    public async Task DemoRejectsMismatchedBuildBeforeDownloading(string field)
    {
        using var fixture = new Fixture(demo: true);
        var build = fixture.Explorer.Listing.Build;
        switch (field) {
            case "definition": build.Definition!.Id = 1; break;
            case "repository": build.Repository!.Name = "deployment-portal-poc"; break;
            case "project": build.Project!.Name = "OtherProject"; break;
            case "branch": build.SourceBranch = "refs/heads/Main"; break;
        }
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Service.StageAsync("Demo", 3, "dev-host"));
        Assert.Equal(0, fixture.Handler.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultOffRejectsDirectPostBeforeNetworkOrWrites(bool demo)
    {
        using var fixture = new Fixture(demo: demo);
        fixture.Options.Enabled = false;
        var key = demo ? "Demo" : "Portal";
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Service.SelectAsync(key, 3, "dev-host"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Service.StageAsync(key, 3, "dev-host"));
        Assert.Equal(0, fixture.Explorer.Calls);
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.False(Directory.Exists(fixture.Root));
        var controller = new TfsCdController(fixture.Service, fixture.Registry, fixture.Servers, NullLogger<TfsCdController>.Instance) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<ViewResult>(await controller.Stage(3, key, "dev-host", default));
        Assert.Equal(400, controller.Response.StatusCode);
    }

    [Fact]
    public async Task PreparationPinsProvenanceChecksIntegrityAndNeverQueues()
    {
        using var fixture = new Fixture();
        var prepared = await fixture.Service.StageAsync("Portal", 3, "dev-host");
        var loaded = await fixture.Service.FindAsync(prepared.Id);
        Assert.Equal(prepared.ManifestSha256, loaded.ManifestSha256);
        Assert.Equal(new string('a', 40), loaded.Commit);
        Assert.Equal("dev-host", loaded.ServerHost);
        Assert.Contains("Blocked", loaded.Status);
        Assert.Equal(2, fixture.Explorer.Calls);
        Assert.Equal(2, fixture.Handler.Calls);
        Assert.Equal(2, loaded.Files.Count);
        Assert.All(loaded.Files, file => Assert.Equal(64, file.Sha256.Length));
        Assert.DoesNotContain("web.config", File.ReadAllText(Path.Combine(fixture.Root, prepared.Id.ToString("N"), "preparation.dat")));
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, prepared.Id.ToString("N"), "content", "publish", "app.dll"), "changed");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Service.FindAsync(prepared.Id));
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("C:/escape")]
    [InlineData("publish/a:stream")]
    [InlineData("publish/CON")]
    public async Task UnsafePathsFailBeforeDownload(string path)
    {
        using var fixture = new Fixture();
        fixture.Explorer.Listing = fixture.Explorer.Listing with { Entries = [new(path, false, 1), new("publish/web.config", false, 1)] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.StageAsync("Portal", 3, "dev-host"));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("http://evil.invalid/")]
    [InlineData("missing-host")]
    public async Task ArbitraryServerOrUrlIsNeverAccepted(string host)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Service.StageAsync("Portal", 3, host));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task RegistryHostSurvivesServerIdChange()
    {
        using var fixture = new Fixture();
        fixture.Servers.Id = 137;
        var selection = await fixture.Service.SelectAsync("Portal", 3, "dev-host");
        Assert.Equal(137, selection.Server.Id);
        Assert.Equal("ActualSite", selection.Destination.SiteName);
    }

    [Fact]
    public async Task ChangedProvenanceAfterTransferDeletesOnlyOwnedPreparation()
    {
        using var fixture = new Fixture();
        fixture.Explorer.ChangeAfterFirst = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.StageAsync("Portal", 3, "dev-host"));
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("oversize")]
    [InlineData("truncated")]
    [InlineData("login")]
    public async Task RedirectAndInvalidTransfersFailClosed(string failure)
    {
        using var fixture = new Fixture();
        fixture.Handler.Failure = failure;
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.StageAsync("Portal", 3, "dev-host"));
        Assert.Single(fixture.Handler.Paths);
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    [Fact]
    public async Task SizePolicyFailsBeforeDownload()
    {
        using var fixture = new Fixture();
        fixture.Options.MaximumFileBytes = 1;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Service.StageAsync("Portal", 3, "dev-host"));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task MissingRepositoryIdentityFailsBeforeDownload()
    {
        using var fixture = new Fixture();
        fixture.Explorer.Listing.Build.Repository = null;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Service.StageAsync("Portal", 3, "dev-host"));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task StagingInWebRootIsForbidden()
    {
        using var fixture = new Fixture();
        fixture.Options.LocalStagingRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Service.StageAsync("Portal", 3, "dev-host"));
        Assert.Equal(0, fixture.Explorer.Calls);
    }

    [Fact]
    public async Task AssistantAndManualResolveSamePinnedBuildAndHostname()
    {
        using var fixture = new Fixture();
        var command = new AssistantCommandParser().Parse("Deploy Portal latest Dev build on Deployment_1");
        var resolver = new AssistantArtifactResolver(fixture.Registry, fixture.Servers, new Builds(), fixture.Service);
        var assistant = await resolver.ResolveAsync(command);
        var manual = await fixture.Service.SelectAsync("Portal", 3, "dev-host");
        Assert.Equal(manual.Listing.Build.Id, assistant.Listing.Build.Id);
        Assert.Equal(manual.Server.HostNameOrIp, assistant.Server.HostNameOrIp);
        Assert.Equal(manual.Destination.TargetPath, assistant.Destination.TargetPath);
        Assert.Equal(0, fixture.Handler.Calls); // selection doesn't download or execute.
    }

    [Fact]
    public async Task BuildArtifactContainerRetrievalIntegrationUsesRealScopedServices()
    {
        using var fixture = new Fixture(useRealExplorer: true);
        var prepared = await fixture.Service.StageAsync("Portal", 3, "dev-host");
        Assert.Equal(3, prepared.BuildId);
        Assert.Equal("drop", prepared.Artifact);
        Assert.Equal(2, prepared.Files.Count);
        Assert.Contains(fixture.Handler.Paths, x => x.Contains("/build/builds/3?api-version=4.1"));
        Assert.Contains(fixture.Handler.Paths, x => x.Contains("api-version=4.1-preview.4"));
        Assert.All(fixture.Handler.Paths, x => Assert.StartsWith("/tfs/DefaultCollection/", x));
    }

    [Fact]
    public async Task JsonConfigurationMimeTypeIsValidArtifactContent()
    {
        using var fixture = new Fixture();
        fixture.Explorer.Listing = fixture.Explorer.Listing with { Entries = [.. fixture.Explorer.Listing.Entries, new("publish/appsettings.json", false, 2)] };
        fixture.Handler.JsonConfiguration = true;
        var prepared = await fixture.Service.StageAsync("Portal", 3, "dev-host");
        Assert.Contains(prepared.Files, x => x.Path == "publish/appsettings.json");
        Assert.Contains("Blocked", prepared.Status);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "cd-test-staging", Guid.NewGuid().ToString("N"));
        public Registry Registry { get; } = new();
        public Servers Servers { get; } = new();
        public Explorer Explorer { get; } = new();
        public Handler Handler { get; } = new();
        public TfsCdOptions Options { get; }
        public TfsCdPreparation Service { get; }
        private readonly HttpClient client;
        public Fixture(bool useRealExplorer = false, bool demo = false)
        {
            if (demo) {
                Registry.App.Key = "Demo"; Registry.App.DisplayName = "Demo"; Registry.App.TfsGitCiDefinitionId = 2; Registry.App.TfsGitRepository = "Demo-App-Repo";
                Registry.App.Destinations[0].SiteName = "CdDemo"; Registry.App.Destinations[0].TargetPath = @"C:\Sites\CdDemo";
                Explorer.Listing.Build.Definition!.Id = 2; Explorer.Listing.Build.Repository!.Name = "Demo-App-Repo";
            }
            Options = new() { Enabled = true, LocalStagingRoot = Root };
            var address = new Uri("http://netserver:8080/tfs/DefaultCollection/");
            client = new(new TfsGitRequestGuard(address) { InnerHandler = Handler }) { BaseAddress = address };
            ITfsArtifactExplorer inspector = Explorer;
            if (useRealExplorer)
            {
                Handler.MetadataBuild = Explorer.Listing.Build;
                var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TfsGit:Project"] = "deployment-portal-poc", ["TfsGit:ApiVersion"] = "4.1" }).Build();
                inspector = new TfsArtifactExplorer(client, new TfsGitBuildService(client, config), config);
            }
            Service = new(client, inspector, Registry, Servers, Microsoft.Extensions.Options.Options.Create(Options), new EphemeralDataProtectionProvider(), new Environment(), Microsoft.Extensions.Options.Options.Create(new AutomationOptions()), new TestPrivateRoot());
        }
        public void Dispose()
        {
            client.Dispose();
            if (Root.StartsWith(Path.GetFullPath(AppContext.BaseDirectory) + "cd-test-staging" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private sealed class TestPrivateRoot : IPrivateCdStagingRoot
    {
        public void Validate(string root) => Directory.CreateDirectory(root);
    }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "DeploymentPortal";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Registry : ISiteRegistry
    {
        public RegistryApplication App { get; } = new() { Key = "Portal", DisplayName = "Portal", TfsGitCiDefinitionId = 1, Destinations = [new() { ServerHostNameOrIp = "dev-host", SiteName = "ActualSite", ApplicationName = "/", TargetPath = @"C:\Sites\Portal" }] };
        public RegistryApplication? Find(string key) => key == App.Key ? App : null;
        public IReadOnlyList<RegistryApplication> List() => [App];
    }
    private sealed class Servers : IServerService
    {
        public int Id { get; set; } = 42;
        public Task<List<Server>> ListAsync() => Task.FromResult(new List<Server> { new() { Id = Id, DisplayName = "Deployment_1", HostNameOrIp = "dev-host", Environment = "Development" } });
        public Task<Server?> FindAsync(int id) => throw new NotSupportedException();
        public Task<int> SaveAsync(ServerForm form) => throw new NotSupportedException();
        public Task<ConnectionResult?> TestAsync(int id) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id) => throw new NotSupportedException();
    }
    private sealed class Explorer : ITfsArtifactExplorer
    {
        public Task<TfsArtifactListing> InspectAsync(int id, RegistryApplication app, CancellationToken cancellationToken = default) => InspectAsync(id, cancellationToken);
        public int Calls { get; private set; }
        public bool ChangeAfterFirst { get; set; }
        public TfsArtifactListing Listing { get; set; } = new(new() { Id = 3, Definition = new() { Id = 1 }, SourceBranch = "refs/heads/Dev", Status = "completed", Result = "succeeded", SourceVersion = new string('a', 40), Repository = new() { Id = Guid.NewGuid().ToString(), Name = "deployment-portal-poc", Type = "TfsGit" }, Project = new() { Id = Guid.NewGuid(), Name = "deployment-portal-poc" } }, "drop", [new("publish/web.config", false, 2), new("publish/app.dll", false, 2)]) { ContainerId = 12, ContainerRoot = "drop" };
        public Task<TfsArtifactListing> InspectAsync(int id, CancellationToken cancellationToken = default)
        {
            Assert.Equal(3, id); Calls++;
            return Task.FromResult(ChangeAfterFirst && Calls > 1 ? Listing with { ContainerId = 99 } : Listing);
        }
    }
    private sealed class Builds : ITfsGitBuildService
    {
        public Task<IReadOnlyList<TfsGitCiBuildViewModel>> GetRecentBuildsAsync() => Task.FromResult<IReadOnlyList<TfsGitCiBuildViewModel>>([new() { Id = 3, Status = "completed", Result = "succeeded" }]);
        public Task<TfsGitCiBuildViewModel?> GetBuildAsync(int id) => throw new NotSupportedException();
        public Task<IReadOnlyList<TfsGitCiArtifactViewModel>> GetArtifactsAsync(int id) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Failure { get; set; }
        public TfsGitCiBuildViewModel? MetadataBuild { get; set; }
        public bool JsonConfiguration { get; set; }
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("netserver", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            Paths.Add(request.RequestUri.PathAndQuery); Calls++;
            if (MetadataBuild is not null && (request.RequestUri.AbsolutePath.Contains("/build/") || request.RequestUri.Query.Contains("$format=json")))
            {
                var json = request.RequestUri.AbsolutePath.EndsWith("/artifacts") ? """{"value":[{"name":"drop","resource":{"type":"Container","data":"#/12/drop"}}]}""" : request.RequestUri.Query.Contains("$format=json") ? """[{"path":"drop/publish/web.config","itemType":2,"status":1,"fileLength":2},{"path":"drop/publish/app.dll","itemType":2,"status":1,"fileLength":2}]""" : System.Text.Json.JsonSerializer.Serialize(MetadataBuild);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            Assert.Equal("application/octet-stream", request.Headers.Accept.Single().MediaType);
            if (Failure == "redirect") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://evil.invalid/token") } });
            var bytes = Encoding.UTF8.GetBytes(Failure == "oversize" ? "abc" : Failure == "truncated" ? "a" : "ab");
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new(Failure == "login" ? "text/html" : JsonConfiguration && request.RequestUri.Query.Contains("appsettings.json") ? "application/json" : "application/octet-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
