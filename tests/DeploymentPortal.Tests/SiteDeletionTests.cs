using DeploymentPortal.Models;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace DeploymentPortal.Tests;

public class SiteDeletionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankApplicationPathUsesRoot(string? value)
    {
        var form = new DeploymentPortal.ViewModels.DeploymentForm { ApplicationName = value! };
        Assert.Equal("/", form.ApplicationName);
        var results = new System.Collections.Generic.List<System.ComponentModel.DataAnnotations.ValidationResult>();
        Assert.True(System.ComponentModel.DataAnnotations.Validator.TryValidateProperty(form.ApplicationName,
            new System.ComponentModel.DataAnnotations.ValidationContext(form) { MemberName = nameof(form.ApplicationName) }, results));
    }

    [Fact]
    public async Task InventoryUsesLiveRemoteOperation()
    {
        var shell = new Shell();
        var service = new SiteDeletionService(shell, new Credentials(), new EphemeralDataProtectionProvider());
        var sites = await service.ListAsync(Site().Server);
        Assert.Equal("Actual IIS Site", Assert.Single(sites).Name);
        Assert.Equal(0, shell.Deletes);
    }

    private sealed class Credentials : ICredentialProvider
    {
        public Task<RemoteCredential> GetAsync(Server server) => Task.FromResult(new RemoteCredential("test", "test"));
    }
    private sealed class Shell : IPowerShellService
    {
        public int Deletes;
        public string? State;
        public Task<RemoteResult> ExecuteAsync(RemoteOperation operation, Server server, RemoteCredential credential, Deployment? deployment = null)
        {
            if (operation == RemoteOperation.ListSites) return Task.FromResult(new RemoteResult(true, "Loaded", Sites: [new IisSite("Actual IIS Site", "Started", "*:8080:")]));
            if (operation == RemoteOperation.DeleteSite) { Deletes++; State = deployment?.ExpectedState; }
            return Task.FromResult(new RemoteResult(true, "Preview", StateFingerprint: "live-site-fingerprint"));
        }
    }
    private static Deployment Site() => new() { ServerId = 1, SiteName = "Test Site", Server = new Server { HostNameOrIp = "test-host", Username = "test-user" } };

    [Fact]
    public async Task ApprovedDeletePassesLiveFingerprint()
    {
        var shell = new Shell();
        var service = new SiteDeletionService(shell, new Credentials(), new EphemeralDataProtectionProvider());
        var d = Site();
        var preview = await service.PreviewAsync(d);
        Assert.True((await service.DeleteAsync(d, preview.Token, d.SiteName)).Success);
        Assert.Equal(1, shell.Deletes);
        Assert.Equal("live-site-fingerprint", shell.State);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("token")]
    [InlineData("host")]
    [InlineData("site")]
    public async Task InvalidConfirmationNeverDeletes(string change)
    {
        var shell = new Shell();
        var service = new SiteDeletionService(shell, new Credentials(), new EphemeralDataProtectionProvider());
        var d = Site();
        var preview = await service.PreviewAsync(d);
        if (change == "host") d.Server.HostNameOrIp = "different-host";
        if (change == "site") d.SiteName = "Different Site";
        var result = await service.DeleteAsync(d, change == "token" ? "tampered" : preview.Token, change == "name" ? "wrong" : d.SiteName);
        Assert.False(result.Success);
        Assert.Equal(0, shell.Deletes);
    }
}
