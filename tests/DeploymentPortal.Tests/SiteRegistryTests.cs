using DeploymentPortal.Models;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public class SiteRegistryTests
{
    private static RegistryApplication Application() => new()
    {
        Key = "Portal", DisplayName = "Portal", SourceReference = @"C:\Sites-Registry\Portal",
        Destinations = [new() { ServerId = 7, SiteName = "Portal", TargetPath = @"C:\Sites\Portal", ApplicationPool = "PortalPool" }]
    };
    private static ConfigurationSiteRegistry Registry(params RegistryApplication[] apps) =>
        new(new Snapshot(new SiteRegistryOptions { Applications = apps.ToList() }));
    private static SiteRegistryDraftService Drafts(ISiteRegistry registry) =>
        new(registry, new ReadOnlyServers(), [new PreStagedSourceProvider()], Options.Create(new AutomationOptions()));

    [Fact]
    public void LookupIsCaseInsensitiveAndMissingKeyReturnsNull()
    {
        var app = Application();
        var registry = Registry(app);
        Assert.Same(app, registry.Find("portal"));
        Assert.Null(registry.Find("Missing"));
        Assert.Single(registry.List());
    }

    [Fact]
    public async Task DraftUsesRegisteredEnvironmentAndCompleteDestinationWithoutApproval()
    {
        var app = Application();
        app.Destinations[0].SitePort = 8090;
        app.Destinations[0].SiteHostName = "portal.test";
        var row = await Drafts(Registry(app)).PrepareAsync("Portal", 7);
        var form = Assert.IsType<DeploymentForm>(row.Form);
        Assert.Equal(7, form.ServerId);
        Assert.Equal("Development", form.Environment);
        Assert.Equal("Portal", form.SiteName);
        Assert.Equal("/", form.ApplicationName);
        Assert.Equal("PortalPool", form.AppPoolName);
        Assert.Equal(@"C:\Sites-Registry\Portal", form.SourcePath);
        Assert.Equal(@"C:\Sites\Portal", form.TargetPath);
        Assert.Equal(8090, form.SitePort);
        Assert.Equal("portal.test", form.SiteHostName);
        Assert.Null(form.ConfirmationToken);
        Assert.Null(form.PlanMode);
    }

    [Fact]
    public async Task DestinationIsSelectedByServerAndEnvironmentIsNotStoredInRegistry()
    {
        var app = Application();
        app.Destinations.Add(new() { ServerId = 8, SiteName = "PortalProd", TargetPath = @"D:\Web\Portal" });
        var form = (await Drafts(Registry(app)).PrepareAsync("Portal", 8)).Form!;
        Assert.Equal("Production", form.Environment);
        Assert.Equal("PortalProd", form.SiteName);
        Assert.Equal(@"D:\Web\Portal", form.TargetPath);
    }

    [Theory]
    [InlineData("missing-source")]
    [InlineData("relative-source")]
    [InlineData("overlapping-paths")]
    [InlineData("missing-target")]
    [InlineData("missing-site")]
    [InlineData("missing-name")]
    [InlineData("unknown-provider")]
    [InlineData("unknown-server")]
    [InlineData("duplicate-destination")]
    [InlineData("duplicate-key")]
    public async Task InvalidConfigurationDisablesDeploy(string problem)
    {
        var app = Application();
        switch (problem)
        {
            case "missing-source": app.SourceReference = ""; break;
            case "relative-source": app.SourceReference = "Portal"; break;
            case "overlapping-paths": app.Destinations[0].TargetPath = app.SourceReference; break;
            case "missing-target": app.Destinations[0].TargetPath = ""; break;
            case "missing-site": app.Destinations[0].SiteName = ""; break;
            case "missing-name": app.DisplayName = ""; break;
            case "unknown-provider": app.SourceProvider = "DevOps"; break;
            case "unknown-server": app.Destinations[0].ServerId = 99; break;
            case "duplicate-destination": app.Destinations.Add(app.Destinations[0]); break;
        }
        var registry = problem == "duplicate-key" ? Registry(app, Application()) : Registry(app);
        var rows = await Drafts(registry).ListAsync();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => { Assert.Null(row.Form); Assert.StartsWith("Not ready:", row.Readiness); });
    }

    [Fact]
    public async Task EmptyRegistryAndMissingDestinationAreSafe()
    {
        Assert.Empty(await Drafts(Registry()).ListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => Drafts(Registry()).PrepareAsync("Portal", 7));
        var app = Application();
        app.Destinations.Clear();
        var row = Assert.Single(await Drafts(Registry(app)).ListAsync());
        Assert.Null(row.Form);
        Assert.Contains("add a server destination", row.Readiness);
    }

    private sealed class Snapshot(SiteRegistryOptions options) : IOptionsSnapshot<SiteRegistryOptions>
    {
        public SiteRegistryOptions Value => options;
        public SiteRegistryOptions Get(string? name) => options;
    }
    // Any unexpected operational use fails the test; no SQL, credentials, or remoting dependencies.
    private sealed class ReadOnlyServers : IServerService
    {
        public Task<Server?> FindAsync(int id) => Task.FromResult<Server?>(id is 7 or 8
            ? new Server { Id = id, DisplayName = "Server " + id, Environment = id == 7 ? "Development" : "Production" } : null);
        public Task<List<Server>> ListAsync() => throw new NotSupportedException();
        public Task<int> SaveAsync(ServerForm form) => throw new NotSupportedException();
        public Task<ConnectionResult?> TestAsync(int id) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id) => throw new NotSupportedException();
    }
}
