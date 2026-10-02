using DeploymentPortal.Models;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public class AssistantDeploymentTests
{
    [Theory]
    [InlineData("Deploy Portal on DEV server", "Portal", "DEV server")]
    [InlineData(" deploy API on Dev Server ", "API", "Dev Server")]
    public void ParsesStructuredIntent(string text, string app, string server)
    {
        var command = new AssistantCommandParser().Parse(text);
        Assert.Equal(AssistantIntent.DeploySite, command.Intent);
        Assert.Equal(app, command.Application);
        Assert.Equal(server, command.Server);
    }
    [Theory]
    [InlineData("")]
    [InlineData("Deploy Portal")]
    [InlineData("Deploy on DEV server")]
    [InlineData("Restart IIS for Portal")]
    public void RejectsIncompleteOrUnsupportedCommands(string text) =>
        Assert.Throws<ArgumentException>(() => new AssistantCommandParser().Parse(text));

    [Theory]
    [InlineData("DEV server")]
    [InlineData("Dev Server")]
    [InlineData("Development")]
    [InlineData("dev-host")]
    public async Task ReusesDraftWithDynamicServerIdAndNoApproval(string selector)
    {
        var form = await Resolver().ResolveAsync(new(AssistantIntent.DeploySite, "portal", selector));
        Assert.Equal(42, form.ServerId);
        Assert.Equal("Development", form.Environment);
        Assert.Equal(@"C:\Packages\Portal", form.SourcePath);
        Assert.Equal(@"C:\Sites\Portal", form.TargetPath);
        Assert.Equal("Portal", form.SiteName);
        Assert.Null(form.ConfirmationToken);
        Assert.Null(form.PlanMode);
    }
    [Fact]
    public async Task AmbiguousEnvironmentRequiresSpecificServer()
    {
        var resolver = Resolver(secondServer: true);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(new(AssistantIntent.DeploySite, "Portal", "DEV server")));
        Assert.Contains("ambiguous", error.Message);
        Assert.Equal(42, (await resolver.ResolveAsync(new(AssistantIntent.DeploySite, "Portal", "dev-host"))).ServerId);
    }
    [Theory]
    [InlineData("Missing", "DEV", "Application")]
    [InlineData("Portal", "missing-host", "No registered destination")]
    [InlineData("", "DEV", "Specify both")]
    [InlineData("Portal", "", "Specify both")]
    public async Task MissingAndUnknownInputsReturnActionableErrors(string app, string server, string expected)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Resolver().ResolveAsync(new(AssistantIntent.DeploySite, app, server)));
        Assert.Contains(expected, error.Message);
    }
    [Fact]
    public async Task InvalidRegistryMappingIsRejectedByExistingDraftLogic()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Resolver(invalidPath: true).ResolveAsync(new(AssistantIntent.DeploySite, "Portal", "DEV")));
        Assert.Contains("absolute local Windows path", error.Message);
    }
    [Fact]
    public async Task DuplicateApplicationsAreNotGuessed()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Resolver(duplicate: true).ResolveAsync(new(AssistantIntent.DeploySite, "Portal", "DEV")));
        Assert.Contains("ambiguous", error.Message);
    }
    private static AssistantDeploymentResolver Resolver(bool secondServer = false, bool invalidPath = false, bool duplicate = false)
    {
        var app = new RegistryApplication { Key = "Portal", DisplayName = "Portal", SourceReference = invalidPath ? "" : @"C:\Packages\Portal",
            Destinations = [new() { ServerId = 42, SiteName = "Portal", TargetPath = @"C:\Sites\Portal" }] };
        var list = new List<Server> { new() { Id = 42, DisplayName = "Development One", HostNameOrIp = "dev-host", Environment = "Development" } };
        if (secondServer)
        {
            list.Add(new() { Id = 93, DisplayName = "Development Two", HostNameOrIp = "dev-two", Environment = "Development" });
            app.Destinations.Add(new() { ServerId = 93, SiteName = "Portal", TargetPath = @"C:\Sites\Portal" });
        }
        var registry = new Registry(duplicate ? [app, app] : [app]);
        var servers = new Servers(list);
        return new(registry, servers, new(registry, servers, [new PreStagedSourceProvider()], Options.Create(new AutomationOptions())));
    }
    private sealed class Registry(List<RegistryApplication> apps) : ISiteRegistry
    {
        public IReadOnlyList<RegistryApplication> List() => apps;
        public RegistryApplication? Find(string key) => apps.SingleOrDefault(x => x.Key == key);
    }
    private sealed class Servers(List<Server> servers) : IServerService
    {
        public Task<List<Server>> ListAsync() => Task.FromResult(servers);
        public Task<Server?> FindAsync(int id) => Task.FromResult(servers.SingleOrDefault(x => x.Id == id));
        public Task<int> SaveAsync(ServerForm form) => throw new NotSupportedException();
        public Task<ConnectionResult?> TestAsync(int id) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id) => throw new NotSupportedException();
    }
}
