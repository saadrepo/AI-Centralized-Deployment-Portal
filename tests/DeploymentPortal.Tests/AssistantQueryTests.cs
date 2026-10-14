using DeploymentPortal.Controllers;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public class AssistantQueryTests
{
    [Theory]
    [InlineData("Show latest Portal Dev build", AssistantIntent.ShowLatestBuild)]
    [InlineData("check Portal deployment status", AssistantIntent.CheckDeploymentStatus)]
    public void ParsesReadOnlyIntents(string text, AssistantIntent intent)
    {
        var command = new AssistantCommandParser().Parse(text);
        Assert.Equal(intent, command.Intent);
        Assert.Equal("Portal", command.Application);
    }

    [Theory]
    [InlineData("Deploy Portal latest successful Dev build on Deployment_1")]
    [InlineData("Deploy Portal build 3 on Dev Server")]
    public void ArtifactDeploymentNeverFallsThroughToLegacyDeployment(string text)
    {
        Assert.Equal(AssistantIntent.PrepareArtifact, new AssistantCommandParser().Parse(text).Intent);
    }

    [Fact]
    public async Task LatestBuildRequiresExplicitAssociationAndDoesNotGuess()
    {
        var registry = new Registry(); registry.App.TfsGitCiDefinitionId = null;
        var builds = new Builds();
        await Assert.ThrowsAsync<ArgumentException>(() => Queries(registry, builds).ResolveAsync(new(AssistantIntent.ShowLatestBuild, "Portal", "")));
        Assert.Equal(0, builds.Requests);
    }

    [Fact]
    public async Task LatestBuildReportsSuccessfulBuildAndArtifactMetadata()
    {
        var builds = new Builds();
        var result = await Queries(new Registry(), builds).ResolveAsync(new(AssistantIntent.ShowLatestBuild, "Portal", ""));
        Assert.Equal(3, result.BuildId);
        Assert.Contains("succeeded", result.Message);
        Assert.Contains("drop (Container)", result.Message);
        Assert.Contains(new string('a', 40), result.Message);
        Assert.Contains("no deployment was prepared", result.Message);
    }

    [Fact]
    public async Task StatusUsesRegistrySiteAndApplicationRatherThanDisplayName()
    {
        var result = await Queries(new Registry(), new Builds()).ResolveAsync(new(AssistantIntent.CheckDeploymentStatus, "Portal", ""));
        Assert.Equal(History.Id, result.DeploymentId);
        Assert.Contains("Queued", result.Message);
        Assert.Contains("no live IIS check", result.Message);
    }

    [Fact]
    public async Task AmbiguousApplicationAndServerRequestClarification()
    {
        var registry = new Registry { Duplicate = true };
        await Assert.ThrowsAsync<ArgumentException>(() => Queries(registry, new Builds()).ResolveAsync(new(AssistantIntent.ShowLatestBuild, "Portal", "")));
        registry.Duplicate = false;
        registry.App.Destinations.Add(new() { ServerId = 2, SiteName = "Portal", ApplicationName = "/" });
        await Assert.ThrowsAsync<ArgumentException>(() => Queries(registry, new Builds()).ResolveAsync(new(AssistantIntent.CheckDeploymentStatus, "Portal", "")));
        var result = await Queries(registry, new Builds()).ResolveAsync(new(AssistantIntent.CheckDeploymentStatus, "Portal", "dev-host"));
        Assert.Equal(History.Id, result.DeploymentId);
    }

    [Fact]
    public async Task ControllerReturnsQueryWithoutReviewFieldsOrApprovalToken()
    {
        var registry = new Registry(); var servers = new Servers();
        var resolver = new AssistantDeploymentResolver(registry, servers, new SiteRegistryDraftService(registry, servers, [new PreStagedSourceProvider()], Options.Create(new AutomationOptions())));
        var controller = new AssistantController(new AssistantCommandParser(), resolver, Queries(registry, new Builds()), NullLogger<AssistantController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }, Url = new Urls() };
        var result = Assert.IsType<JsonResult>(await controller.Prepare("Show latest Portal Dev build"));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("ShowLatestBuild", json);
        Assert.DoesNotContain("fields", json);
        Assert.DoesNotContain("ConfirmationToken", json);
    }

    private static AssistantQueries Queries(Registry registry, Builds builds) => new(registry, new Servers(), builds, new History());
    private sealed class Registry : ISiteRegistry
    {
        public bool Duplicate { get; set; }
        public RegistryApplication App { get; } = new() { Key = "Portal", DisplayName = "Portal", TfsGitCiDefinitionId = 1, Destinations = [new() { ServerId = 1, SiteName = "ActualSite", ApplicationName = "/" }] };
        public IReadOnlyList<RegistryApplication> List() => Duplicate ? [App, App] : [App];
        public RegistryApplication? Find(string key) => App;
    }
    private sealed class Builds : ITfsGitBuildService
    {
        public int Requests { get; private set; }
        public Task<IReadOnlyList<TfsGitCiBuildViewModel>> GetRecentBuildsAsync() { Requests++; return Task.FromResult<IReadOnlyList<TfsGitCiBuildViewModel>>([new() { Id = 4, Status = "completed", Result = "failed" }, new() { Id = 3, Status = "completed", Result = "succeeded", SourceBranch = "refs/heads/Dev", SourceVersion = new string('a', 40) }]); }
        public Task<IReadOnlyList<TfsGitCiArtifactViewModel>> GetArtifactsAsync(int id) { Assert.Equal(3, id); return Task.FromResult<IReadOnlyList<TfsGitCiArtifactViewModel>>([new() { Name = "drop", Resource = new() { Type = "Container" } }]); }
        public Task<TfsGitCiBuildViewModel?> GetBuildAsync(int id) => throw new NotSupportedException();
    }
    private sealed class Servers : IServerService
    {
        public Task<List<Server>> ListAsync() => Task.FromResult(new List<Server> { new() { Id = 1, HostNameOrIp = "dev-host" }, new() { Id = 2, HostNameOrIp = "other-host" } });
        public Task<Server?> FindAsync(int id) => throw new NotSupportedException();
        public Task<int> SaveAsync(ServerForm form) => throw new NotSupportedException();
        public Task<ConnectionResult?> TestAsync(int id) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id) => throw new NotSupportedException();
    }
    // Every mutation throws: these tests fail if a query crosses the execution boundary.
    private sealed class History : IDeploymentService
    {
        public static readonly Guid Id = Guid.NewGuid();
        public Task<List<Deployment>> ListAsync(int take = 100) => Task.FromResult(new List<Deployment> { new() { Id = Id, ServerId = 1, SiteName = "ActualSite", ApplicationName = "/", Status = DeploymentStatus.Queued, ResultMessage = "Confirmed and queued." }, new() { ServerId = 1, SiteName = "Unrelated", ApplicationName = "/", StartedAt = DateTimeOffset.UtcNow.AddMinutes(1) } });
        public Task<int> CountAsync() => throw new NotSupportedException();
        public Task<Deployment?> FindAsync(Guid id) => throw new NotSupportedException();
        public Task<Server> ValidateAsync(DeploymentForm form) => throw new NotSupportedException();
        public Task<string> ConfirmAsync(DeploymentForm form) => throw new NotSupportedException();
        public Task<Guid> QueueAsync(DeploymentForm form) => throw new NotSupportedException();
        public Task ExecuteAsync(Deployment deployment) => throw new NotSupportedException();
    }
    private sealed class Urls : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(Microsoft.AspNetCore.Mvc.Routing.UrlActionContext context) => "/TfsGitBuilds/Artifacts/3";
        public string? Content(string? path) => path;
        public bool IsLocalUrl(string? url) => true;
        public string? Link(string? name, object? values) => null;
        public string? RouteUrl(Microsoft.AspNetCore.Mvc.Routing.UrlRouteContext context) => null;
    }
}
