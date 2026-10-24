using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using DeploymentPortal.Controllers;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class CloudBuildExplorerEndpointTests
{
    private sealed class Cloud : ICloudBuildExplorer
    {
        public int Calls; public bool Fail; public bool Empty;
        public Task<CloudExplorerPage> BrowseAsync(CloudExplorerQuery query, CancellationToken token) {
            Calls++; if (Fail) throw new HttpRequestException("PAT=synthetic-secret https://internal-host/private");
            return Task.FromResult(new CloudExplorerPage { Items = Empty ? [] : [new("1", "Registered source")] });
        }
    }
    private sealed class Tfs : ITfsGitBuildService
    {
        public int Calls;
        public Task<IReadOnlyList<TfsGitCiBuildViewModel>> GetRecentBuildsAsync() { Calls++; return Task.FromResult<IReadOnlyList<TfsGitCiBuildViewModel>>([]); }
        public Task<TfsGitCiBuildViewModel?> GetBuildAsync(int id) => throw new Exception("Must not change TFS scope");
        public Task<IReadOnlyList<TfsGitCiArtifactViewModel>> GetArtifactsAsync(int id) => throw new Exception("Must not change TFS scope");
    }
    private static WebApplication Host(Cloud cloud, Tfs tfs, ClaimsPrincipal? principal = null)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "src", "DeploymentPortal", "Views"))) repo = repo.Parent;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(TfsGitBuildsController).Assembly.GetName().Name, EnvironmentName = Environments.Production,
            ContentRootPath = Path.Combine(repo!.FullName, "src", "DeploymentPortal")
        });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(TfsGitBuildsController).Assembly);
        builder.Services.AddSourcesManagementAuthorization();
        builder.Services.Configure<SourcesManagementOptions>(o => o.AllowedUserSids = OperatingSystem.IsWindows() && principal?.Identity is WindowsIdentity identity ? [identity.User!.Value] : ["S-1-5-21-1-2-3-1001"]);
        builder.Services.AddAuthentication().AddScheme<SourcesManagementAuthorizationTests.NativeOptions, SourcesManagementAuthorizationTests.NativeHandler>(IISDefaults.AuthenticationScheme, o => o.Principal = principal);
        builder.Services.AddSingleton<ICloudBuildExplorer>(cloud); builder.Services.AddSingleton<ITfsGitBuildService>(tfs);
        var app = builder.Build(); app.UseRouting(); app.UseAuthorization();
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}"); return app;
    }
    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseDefaultCredentials = false }) {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
    };
    [Theory]
    [InlineData("/TfsGitBuilds?provider=GitHub")]
    [InlineData("/TfsGitBuilds?provider=AzureDevOps")]
    [InlineData("/TfsGitBuilds/Browse?provider=GitHub&operation=sources")]
    [InlineData("/TfsGitBuilds/Browse?provider=AzureDevOps&operation=artifacts&sourceId=1")]
    public async Task CloudRoutesRejectAnonymousBeforeAnySourceOrTfsAccess(string path)
    {
        var cloud = new Cloud(); var tfs = new Tfs(); await using var app = Host(cloud, tfs);
        await app.StartAsync(); using var client = Client(app);
        client.DefaultRequestHeaders.Add("X-User-Sid", "S-1-5-21-1-2-3-1001");
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal(0, cloud.Calls); Assert.Equal(0, tfs.Calls); await app.StopAsync();
    }
    [Fact]
    public async Task ForgedClaimsCannotUseCloudIndexOrDataEndpoints()
    {
        var cloud = new Cloud(); var tfs = new Tfs();
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001")], "Windows"));
        await using var app = Host(cloud, tfs, forged); await app.StartAsync(); using var client = Client(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/TfsGitBuilds?provider=GitHub")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/TfsGitBuilds/Browse?provider=GitHub")).StatusCode);
        Assert.Equal(0, cloud.Calls); Assert.Equal(0, tfs.Calls); await app.StopAsync();
    }
    [Fact]
    public async Task DefaultTfsPagePreservesExistingAccessAndCallsOnlyExistingTfsClient()
    {
        var cloud = new Cloud(); var tfs = new Tfs(); await using var app = Host(cloud, tfs);
        await app.StartAsync(); using var client = Client(app);
        var html = await client.GetStringAsync("/TfsGitBuilds");
        Assert.Contains("Source Provider", html); Assert.Contains("Application", html); Assert.Contains("No CI builds found for Dev.", html);
        Assert.DoesNotContain("cloud-build-explorer.js", html); Assert.Equal(1, tfs.Calls); Assert.Equal(0, cloud.Calls);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/TfsGitBuilds?applicationKey=Unknown")).StatusCode);
        await app.StopAsync();
    }
    [Theory]
    [InlineData("GitHub")] [InlineData("AzureDevOps")]
    public async Task EmptyRegistrationsAndDatabaseFailuresHaveDistinctResults(string provider)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent(); var cloud = new Cloud { Empty = true }; var tfs = new Tfs();
        await using var app = Host(cloud, tfs, new WindowsPrincipal(identity)); await app.StartAsync(); using var client = Client(app);
        var path = $"/TfsGitBuilds/Browse?provider={provider}&operation=sources&sourceId=0&project=&repository=&branch=";
        using var empty = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        cloud.Fail = true;
        using var failure = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        var message = await failure.Content.ReadAsStringAsync();
        Assert.Contains("database", message); Assert.DoesNotContain("Invalid explorer selection", message);
        Assert.DoesNotContain("synthetic-secret", message); Assert.Equal(0, tfs.Calls);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("provider=GitHub&operation=sources&sourceId=0&project=&repository=&branch=")]
    [InlineData("provider=GitHub&operation=repositories&sourceId=1&project=&repository=&branch=")]
    [InlineData("provider=GitHub&operation=branches&sourceId=1&project=&repository=app&branch=")]
    [InlineData("provider=GitHub&operation=builds&sourceId=1&project=&repository=app&branch=main")]
    [InlineData("provider=AzureDevOps&operation=sources&sourceId=0&project=&repository=&branch=")]
    [InlineData("provider=AzureDevOps&operation=projects&sourceId=1&project=&repository=&branch=")]
    public async Task OptionalEmptySelectorsDoNotRejectAuthorizedExplorerRequests(string query)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent(); var cloud = new Cloud(); var tfs = new Tfs();
        await using var app = Host(cloud, tfs, new WindowsPrincipal(identity)); await app.StartAsync(); using var client = Client(app);
        using var response = await client.GetAsync("/TfsGitBuilds/Browse?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Invalid explorer selection", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, cloud.Calls); Assert.Equal(0, tfs.Calls);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("GitHub")] [InlineData("AzureDevOps")]
    public async Task AuthorizedProviderSwitchRendersWithinExistingPageAndNoStoreMetadata(string provider)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent(); var cloud = new Cloud(); var tfs = new Tfs();
        await using var app = Host(cloud, tfs, new WindowsPrincipal(identity)); await app.StartAsync(); using var client = Client(app);
        var html = await client.GetStringAsync("/TfsGitBuilds?provider=" + provider);
        Assert.Contains("id=\"cloud-build-explorer\"", html); Assert.Contains("cloud-build-explorer.js", html);
        Assert.Contains("Repository", html); Assert.Contains("Branch", html); Assert.Contains("Refresh Builds", html);
        Assert.Equal(provider == "AzureDevOps", html.Contains("id=\"cloud-project\""));
        Assert.DoesNotContain("CredentialReference", html); Assert.DoesNotContain("type=\"password\"", html);
        using var response = await client.GetAsync("/TfsGitBuilds/Browse?provider=" + provider);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(1, cloud.Calls); Assert.Equal(0, tfs.Calls);
        cloud.Fail = true;
        using var failed = await client.GetAsync("/TfsGitBuilds/Browse?provider=" + provider);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var error = await failed.Content.ReadAsStringAsync(); Assert.DoesNotContain("synthetic-secret", error); Assert.DoesNotContain("internal-host", error);
        await app.StopAsync();
    }
}
