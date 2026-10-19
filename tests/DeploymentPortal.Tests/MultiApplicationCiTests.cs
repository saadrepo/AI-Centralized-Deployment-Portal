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

public class MultiApplicationCiTests
{
    [Fact]
    public void ShippedDemoMappingPreservesExistingRegistryOrderAndDisabledCdFlags()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,"src","DeploymentPortal","sites-registry.json"))) root=root.Parent;
        Assert.NotNull(root);
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(root!.FullName,"src","DeploymentPortal","sites-registry.json"))
            .AddJsonFile(Path.Combine(root.FullName,"src","DeploymentPortal","tfs-cd.json")).Build();
        var apps = config.GetSection("SitesRegistry").Get<SiteRegistryOptions>()!.Applications;
        Assert.Equal(new[] { "Portal","API","Finance","Demo" },apps.Select(x=>x.Key));
        Assert.Equal(1,apps[0].TfsGitCiDefinitionId);
        var demo=apps[3]; Assert.Equal(2,demo.TfsGitCiDefinitionId); Assert.Equal("Demo-App-Repo",demo.TfsGitRepository);
        Assert.Equal("192.168.4.12",demo.Destinations.Single().ServerHostNameOrIp); Assert.Equal("CdDemo",demo.Destinations.Single().SiteName);
        Assert.False(config.GetValue<bool>("TfsCd:ExplorerEnabled")); Assert.False(config.GetValue<bool>("TfsCd:Enabled")); Assert.False(config.GetValue<bool>("TfsCd:ExecutionEnabled"));
    }
    private static RegistryApplication Demo() => new() { Key="Demo", DisplayName="Demo", TfsGitCiDefinitionId=2, TfsGitRepository="Demo-App-Repo" };
    private static TfsGitCiBuildViewModel Build(int definition = 2, string repo = "Demo-App-Repo") => new() {
        Id=20, BuildNumber="Demo-20", Definition=new() { Id=definition }, SourceBranch="refs/heads/Dev", SourceVersion=new string('a',40), Status="completed", Result="succeeded",
        Repository=new() { Id=Guid.NewGuid().ToString(),Name=repo,Type="TfsGit" },Project=new() { Id=Guid.NewGuid(),Name="deployment-portal-poc" }
    };
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["TfsGit:Project"]="deployment-portal-poc",["TfsGit:ApiVersion"]="4.1" }).Build();
    private static HttpClient Client(Handler handler) => new(new TfsGitRequestGuard(new("http://netserver:8080/tfs/DefaultCollection/")) { InnerHandler=handler }) { BaseAddress=new("http://netserver:8080/tfs/DefaultCollection/") };

    [Fact]
    public async Task DemoRecentAndLatestQueriesUseDefinitionTwoAndFilterForeignBuilds()
    {
        using var handler = new Handler { Builds=[Build(),Build(1,"deployment-portal-poc"),Build(2,"OtherRepo")] };
        using var client = Client(handler);
        var service = new TfsGitBuildService(client,Configuration());
        Assert.Single(await service.GetRecentBuildsAsync(Demo()));
        Assert.Contains("definitions=2", handler.Paths[0]); Assert.Contains("branchName=refs%2Fheads%2FDev", handler.Paths[0]);
        handler.Builds=[Build()];
        Assert.Equal(20,(await service.GetLatestSuccessfulBuildAsync(Demo()))!.Id);
        Assert.Contains("definitions=2",handler.Paths[1]); Assert.Contains("statusFilter=completed&resultFilter=succeeded",handler.Paths[1]);
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("repository")]
    [InlineData("project")]
    [InlineData("branch")]
    [InlineData("type")]
    public async Task ForeignBuildCannotExposeArtifactsOrContainerMetadata(string field)
    {
        using var handler = new Handler();
        switch (field) {
            case "definition": handler.Selected.Definition!.Id=1; break;
            case "repository": handler.Selected.Repository!.Name="deployment-portal-poc"; break;
            case "project": handler.Selected.Project!.Name="OtherProject"; break;
            case "branch": handler.Selected.SourceBranch="refs/heads/Main"; break;
            case "type": handler.Selected.Repository!.Type="GitHub"; break;
        }
        using var client=Client(handler); var builds=new TfsGitBuildService(client,Configuration());
        Assert.Null(await builds.GetBuildAsync(20,Demo()));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>builds.GetArtifactsAsync(20,Demo()));
        Assert.Equal(CdFailureCode.BuildScope,(await Assert.ThrowsAsync<CdPreparationException>(()=>new TfsArtifactExplorer(client,builds,Configuration()).InspectAsync(20,Demo()))).Code);
        Assert.All(handler.Paths,path=>Assert.DoesNotContain("artifacts",path));
    }

    [Fact]
    public async Task DemoArtifactExplorerUsesItsVerifiedBuildAndExistingContainerGuard()
    {
        using var handler=new Handler(); using var client=Client(handler);
        var listing=await new TfsArtifactExplorer(client,new TfsGitBuildService(client,Configuration()),Configuration()).InspectAsync(20,Demo());
        Assert.Equal(2,listing.Build.Definition!.Id); Assert.Equal("Demo-App-Repo",listing.Build.Repository!.Name); Assert.Equal("drop",listing.Artifact);
        Assert.Single(listing.Entries); Assert.Equal(3,handler.Paths.Count);
        Assert.Contains("includeDownloadTickets=false",handler.Paths[2]);
        Assert.All(handler.Paths,path=>Assert.StartsWith("/tfs/DefaultCollection/",path));
    }

    [Fact]
    public async Task PortalDefaultStillQueriesDefinitionOne()
    {
        using var handler=new Handler { Builds=[Build(1,"deployment-portal-poc"),Build()] }; using var client=Client(handler);
        Assert.Single(await new TfsGitBuildService(client,Configuration()).GetRecentBuildsAsync());
        Assert.Contains("definitions=1",handler.Paths[0]);
    }

    [Fact]
    public async Task ControllersResolveRegistryKeysAndRejectUnknownApplicationsWithoutNetwork()
    {
        using var handler=new Handler(); using var client=Client(handler);
        var registry=new Registry(); var builds=new TfsGitBuildService(client,Configuration());
        var controller=new TfsGitBuildsController(builds,NullLogger<TfsGitBuildsController>.Instance,registry) { ControllerContext=new() { HttpContext=new DefaultHttpContext() } };
        Assert.IsType<NotFoundResult>(await controller.Index("Unregistered")); Assert.Empty(handler.Paths);
        handler.Builds=[Build()];
        var model=Assert.IsType<TfsGitCiBuildsPageViewModel>(Assert.IsType<ViewResult>(await controller.Index("Demo")).Model);
        Assert.Equal("Demo",model.ApplicationKey); Assert.Equal(2,model.Applications.Count); Assert.Single(model.Builds);
        var artifacts=Assert.IsType<TfsGitCiArtifactsPageViewModel>(Assert.IsType<ViewResult>(await controller.Artifacts(20,"Portal")).Model);
        Assert.Null(artifacts.Build); Assert.Equal(404,controller.Response.StatusCode);
    }

    [Fact]
    public async Task DemoCannotBypassDisabledArtifactExplorer()
    {
        using var handler=new Handler(); using var client=Client(handler);
        var controller=new TfsArtifactsController(new TfsArtifactExplorer(client,new TfsGitBuildService(client,Configuration()),Configuration()),NullLogger<TfsArtifactsController>.Instance,
            Microsoft.Extensions.Options.Options.Create(new TfsCdOptions()),new Registry()) { ControllerContext=new() { HttpContext=new DefaultHttpContext() } };
        var page=Assert.IsType<ArtifactExplorerPage>(Assert.IsType<ViewResult>(await controller.Index(20,null,default,"Demo")).Model);
        Assert.Equal("Demo",page.ApplicationKey); Assert.Contains("disabled",page.Error); Assert.Empty(handler.Paths);
    }

    private sealed class Registry : ISiteRegistry
    {
        public IReadOnlyList<RegistryApplication> List()=>[new() { Key="Portal",TfsGitCiDefinitionId=1 },Demo()];
        public RegistryApplication? Find(string key)=>List().SingleOrDefault(app=>app.Key==key);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public TfsGitCiBuildViewModel Selected=Build();
        public IReadOnlyList<TfsGitCiBuildViewModel> Builds=[Build()];
        public List<string> Paths=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var path=request.RequestUri!.PathAndQuery; Paths.Add(path);
            var json=path.Contains("/Containers/") ? """{"value":[{"path":"drop/publish/web.config","itemType":"file","fileLength":2}]}""" :
                path.Contains("/artifacts?") ? """{"value":[{"name":"drop","resource":{"type":"Container","data":"#/12/drop"}}]}""" :
                path.Contains("/builds/20?") ? JsonSerializer.Serialize(Selected) : JsonSerializer.Serialize(new { value=Builds });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json") });
        }
    }
}
