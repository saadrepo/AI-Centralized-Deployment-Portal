using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;
public class DeploymentSafetyTests
{
    [Theory]
    [InlineData(@"C:\DeploymentPackages\app", @"C:\Windows\Temp\app")]
    [InlineData(@"C:\DeploymentPackages\..\secret", @"C:\inetpub\apps\app")]
    [InlineData(@"\\server\share\app", @"C:\inetpub\apps\app")]
    [InlineData(@"C:\DeploymentPackages\app", @"C:\DeploymentPackages\app\target")]
    [InlineData(@"C:\DeploymentPackages\app", @"C:\inetpub\apps\app:stream")]
    [InlineData(@"C:\DeploymentPackages\app", @"C:\inetpub\apps\app.\nested")]
    [InlineData(@"C:\DeploymentPackages\app", @"C:\inetpub\apps\*\nested")]
    [InlineData(@"C:\DeploymentPackages\app", @"C:\inetpub\apps\CON.txt")]
    public void RejectsUnsafePaths(string source, string target) => Assert.Throws<ArgumentException>(() => DeploymentPaths.Validate(source, target, new()));

    [Fact] public void AllowsCustomFolders() => DeploymentPaths.Validate(@"D:\Releases\publish", @"E:\CustomerSites\Portal", new());
    [Fact] public void AllowsPublishedFolderWithinSeparateRoots() => DeploymentPaths.Validate(@"c:\DeploymentPackages\release-001", @"C:\inetpub\apps\portal", new());
    [Fact] public void RejectsOverlappingPolicyRoots() => Assert.Throws<ArgumentException>(() => DeploymentPaths.Validate(@"C:\DeploymentPackages\release", @"C:\inetpub\apps\portal", new() { BackupRoot = @"C:\inetpub\apps\portal\backups" }));

    private static (ApplicationDbContext Db, DeploymentService Service, FakePowerShell PowerShell) Setup()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Servers.Add(new Server { Id = 1, DisplayName = "Test", HostNameOrIp = "test-host", Environment = "Test" }); db.SaveChanges();
        var ps = new FakePowerShell();
        return (db, new(db, Options.Create(new AutomationOptions()), new EphemeralDataProtectionProvider(), new FakeWinRm(), new FakeCredentials(), ps), ps);
    }
    private static DeploymentForm Form() => new() { ServerId = 1, Environment = "Test", SiteName = "Test Site", ApplicationName = "portal", SourcePath = @"C:\DeploymentPackages\release", TargetPath = @"C:\inetpub\apps\portal" };

    [Fact] public async Task RequiresConfirmation()
    {
        var (db, service, _) = Setup(); using (db) { await Assert.ThrowsAsync<ArgumentException>(() => service.QueueAsync(Form())); Assert.Empty(db.Deployments); }
    }
    [Fact] public async Task RejectsTamperedConfirmation()
    {
        var (db, service, _) = Setup(); using (db) { var form = Form(); form.ConfirmationToken = await service.ConfirmAsync(form); form.TargetPath = @"C:\inetpub\apps\different"; await Assert.ThrowsAsync<ArgumentException>(() => service.QueueAsync(form)); }
    }
    [Theory]
    [InlineData("CreateSite")]
    [InlineData("CreateApplication")]
    [InlineData("Update")]
    public async Task ConfirmationPreservesInspectedActionAndState(string mode)
    {
        var (db, service, ps) = Setup(); using (db)
        {
            ps.Preview = new(true, "Explicit approval required", StateFingerprint: new string('b', 64), DeploymentMode: mode);
            var form = Form(); form.ConfirmationToken = await service.ConfirmAsync(form);
            Assert.Equal(mode, form.PlanMode); Assert.Equal("Explicit approval required", form.PlanMessage);
            var id = await service.QueueAsync(form);
            var saved = await db.Deployments.FindAsync(id);
            Assert.Equal(mode, saved!.DeploymentMode); Assert.Equal(new string('b', 64), saved.ExpectedState);
            Assert.Equal(0, ps.Calls); // Reviewing and approving only queues; no automation execution here.
        }
    }
    [Fact] public async Task CannotChangeNewSiteBindingAfterReview()
    {
        var (db, service, _) = Setup(); using (db)
        {
            var form = Form(); form.ConfirmationToken = await service.ConfirmAsync(form);
            form.SitePort = 9099;
            await Assert.ThrowsAsync<ArgumentException>(() => service.QueueAsync(form));
        }
    }
    [Fact] public async Task PreviewFailureDoesNotIssueApproval()
    {
        var (db, service, ps) = Setup(); using (db)
        {
            ps.Preview = new(false, "Missing web.config");
            var error = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(Form()));
            Assert.Equal("Missing web.config", error.Message); Assert.Empty(db.Deployments); Assert.Equal(0, ps.Calls);
        }
    }
    [Fact] public async Task RejectsChangedServerAfterReview()
    {
        var (db, service, _) = Setup(); using (db) { var form = Form(); form.ConfirmationToken = await service.ConfirmAsync(form); (await db.Servers.FindAsync(1))!.HostNameOrIp = "different-host"; await db.SaveChangesAsync(); await Assert.ThrowsAsync<ArgumentException>(() => service.QueueAsync(form)); }
    }
    [Fact] public async Task RejectsEnvironmentMismatch()
    {
        var (db, service, _) = Setup(); using (db) { var form = Form(); form.Environment = "Production"; await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(form)); }
    }
    [Fact] public async Task RepeatedConfirmationCreatesOnlyOneDeployment()
    {
        var (db, service, _) = Setup(); using (db) { var form = Form(); form.ConfirmationToken = await service.ConfirmAsync(form); var id = await service.QueueAsync(form); Assert.Equal(id, await service.QueueAsync(form)); Assert.Single(db.Deployments); }
    }
    [Fact] public async Task WorkerExecutionStoresFinalResult()
    {
        var (db, service, ps) = Setup(); using (db)
        {
            var form = Form(); form.ConfirmationToken = await service.ConfirmAsync(form); var id = await service.QueueAsync(form);
            var deployment = await db.Deployments.Include(x => x.Server).SingleAsync(x => x.Id == id);
            Assert.Equal(DeploymentStatus.Queued, deployment.Status);
            await service.ExecuteAsync(deployment);
            Assert.Equal(DeploymentStatus.Successful, deployment.Status); Assert.NotNull(deployment.CompletedAt); Assert.Equal(1, ps.Calls);
        }
    }
    [Fact] public async Task FailedRemoteResultIsPersisted()
    {
        var (db, service, ps) = Setup(); using (db)
        {
            ps.Result = new(false, "Deployment failed. Rollback succeeded.");
            var form = Form(); form.ConfirmationToken = await service.ConfirmAsync(form); var id = await service.QueueAsync(form);
            var deployment = await db.Deployments.Include(x => x.Server).SingleAsync(x => x.Id == id); await service.ExecuteAsync(deployment);
            Assert.Equal(DeploymentStatus.Failed, deployment.Status); Assert.Contains("Rollback succeeded", deployment.ResultMessage);
        }
    }
    [Fact] public async Task DevelopmentProviderFailsClosedInProduction()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["DevelopmentCredentials:Passwords:test-host"] = "never-display-this" }).Build();
        var provider = new DevelopmentCredentialProvider(config, new FakeEnvironment());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync(new Server { HostNameOrIp = "test-host" }));
        Assert.DoesNotContain("never-display-this", error.Message);
    }
    private class FakePowerShell : IPowerShellService
    {
        public int Calls; public RemoteResult Result = new(true, "Deployment successful.");
        public RemoteResult Preview = new(true, "Site exists. Approve update.", StateFingerprint: new string('a', 64), DeploymentMode: "Update");
        public Task<RemoteResult> ExecuteAsync(RemoteOperation operation, Server server, RemoteCredential credential, Deployment? deployment = null)
        {
            if (operation == RemoteOperation.PreviewDeployment) return Task.FromResult(Preview);
            Calls++; return Task.FromResult(Result);
        }
    }
    private class FakeCredentials : ICredentialProvider { public Task<RemoteCredential> GetAsync(Server server) => Task.FromResult(new RemoteCredential("test", "test")); }
    private class FakeWinRm : IWinRmService
    {
        public Task<ConnectionResult> TestConnectionAsync(Server server) => Task.FromResult(new ConnectionResult(true, "Connected", "test-host", DateTimeOffset.UtcNow));
        public Task<ConnectionResult> GetBasicServerInfoAsync(Server server) => TestConnectionAsync(server);
    }
    private class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
