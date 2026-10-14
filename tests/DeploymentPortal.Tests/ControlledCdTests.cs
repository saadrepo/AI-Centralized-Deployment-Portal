using System.Text.Json;
using DeploymentPortal.Controllers;
using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public class ControlledCdTests
{
    [Fact] public async Task FlagsOffRejectBeforeRemoteOrQueue()
    {
        using var f = new Fixture(); f.Options.ExecutionEnabled = false;
        await Assert.ThrowsAsync<CdPreparationException>(() => f.Flow.StageRemoteAsync(f.Id));
        await Assert.ThrowsAsync<CdPreparationException>(() => f.Flow.DraftAsync(f.Id));
        Assert.Equal(0, f.PowerShell.Calls); Assert.Empty(f.Db.Deployments);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task FixedRemotePreparationMustSucceedBeforeReviewAndNeverQueues(bool success)
    {
        using var f = new Fixture(); f.Db.CdDeploymentSources.Remove(f.Row); await f.Db.SaveChangesAsync();
        f.UsePreparation(); f.PowerShell.StageSuccess = success;
        if (!success) { await Assert.ThrowsAsync<CdPreparationException>(() => f.Flow.StageRemoteAsync(f.Id)); Assert.Empty(f.Db.CdDeploymentSources); }
        else
        {
            await f.Flow.StageRemoteAsync(f.Id);
            var draft = await f.Flow.DraftAsync(f.Id);
            Assert.Equal(f.Id, draft.PreparationId); Assert.Equal(3, draft.CdProvenance!.Provenance.BuildId);
            Assert.Null(draft.ConfirmationToken);
            await Assert.ThrowsAsync<CdPreparationException>(() => f.Flow.StageRemoteAsync(f.Id));
        }
        Assert.Equal(1, f.PowerShell.StageCalls); Assert.Equal(0, f.PowerShell.Calls); Assert.Empty(f.Db.Deployments);
    }
    [Fact] public async Task InvalidRemoteIntegrityEvidenceCannotBecomeReviewable()
    {
        using var f = new Fixture(); f.Db.CdDeploymentSources.Remove(f.Row); await f.Db.SaveChangesAsync();
        f.UsePreparation(); f.PowerShell.StageSuccess = true; f.PowerShell.BadDigest = true;
        await Assert.ThrowsAsync<CdPreparationException>(() => f.Flow.StageRemoteAsync(f.Id));
        Assert.Empty(f.Db.CdDeploymentSources); Assert.Empty(f.Db.Deployments);
    }
    [Fact] public async Task ManualPreparationHandoffUsesSameReviewAndHumanQueue()
    {
        using var f = new Fixture();
        var draft = await f.Flow.DraftAsync(f.Id);
        var controller = new DeploymentsController(f.Service, f.Servers);
        var reviewed = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(await controller.Review(draft));
        var review = Assert.IsType<ConfirmDeploymentViewModel>(reviewed.Model);
        Assert.Equal(3, review.Form.CdProvenance!.Provenance.BuildId);
        Assert.NotNull(review.Form.ConfirmationToken); Assert.Empty(f.Db.Deployments);
        Assert.Equal(0, f.PowerShell.Calls);
        var id = await f.Service.QueueAsync(review.Form);
        Assert.Equal(id, await f.Service.QueueAsync(review.Form));
        Assert.Single(f.Db.Deployments); Assert.Equal(id, f.Row.DeploymentId);
        Assert.NotNull(f.Row.ApprovedAt); Assert.Contains("Confirmed", f.Row.AuditJson);
        Assert.Equal(0, f.PowerShell.Calls);
    }
    [Fact] public async Task AssistantResolvesSamePreparationAndReadsCdRecordedStatusWithoutExecuting()
    {
        using var f = new Fixture();
        var preparation = new Preparation(f.Snapshot.Provenance, f.Server, f.Registry.App);
        var resolver = new AssistantArtifactResolver(f.Registry, f.Servers, new UnusedBuilds(), preparation);
        var parser = new AssistantCommandParser();
        var selection = await resolver.ResolveAsync(parser.Parse("Deploy Portal build 3 on Deployment_1"));
        Assert.Equal(3, selection.Listing.Build.Id); Assert.Equal(f.Server.Id, selection.Server.Id);
        Assert.Empty(f.Db.Deployments); Assert.Equal(0, f.PowerShell.Calls); Assert.Equal(0, f.PowerShell.StageCalls);
        var form = await f.Flow.DraftAsync(f.Id); form.ConfirmationToken = await f.Service.ConfirmAsync(form);
        var id = await f.Service.QueueAsync(form);
        var query = await new AssistantQueries(f.Registry, f.Servers, new UnusedBuilds(), f.Service).ResolveAsync(parser.Parse("Check Portal deployment status"));
        Assert.Equal(id, query.DeploymentId); Assert.Contains("TFS build 3", query.Message);
        Assert.Contains(new string('a',40), query.Message); Assert.Contains("Recorded status only", query.Message);
        Assert.Equal(0, f.PowerShell.Calls); Assert.Equal(0, f.WinRm.Calls);
    }
    [Fact] public async Task MultipleReviewTokensConsumeOnePreparationOnly()
    {
        using var f = new Fixture(); var first = await f.Flow.DraftAsync(f.Id); var second = await f.Flow.DraftAsync(f.Id);
        first.ConfirmationToken = await f.Service.ConfirmAsync(first); second.ConfirmationToken = await f.Service.ConfirmAsync(second);
        Assert.Equal(await f.Service.QueueAsync(first), await f.Service.QueueAsync(second)); Assert.Single(f.Db.Deployments);
    }
    [Theory] [InlineData("source")] [InlineData("target")] [InlineData("server")] [InlineData("id")] [InlineData("strip")]
    public async Task RequestTamperingFailsBeforeQueue(string field)
    {
        using var f = new Fixture(); var form = await f.Flow.DraftAsync(f.Id); form.ConfirmationToken = await f.Service.ConfirmAsync(form);
        switch (field) { case "source": form.SourcePath += "\\other"; break; case "target": form.TargetPath += "\\other"; break; case "server": f.Server.Username = "changed"; break; case "id": form.PreparationId = Guid.NewGuid(); break; case "strip": form.PreparationId = null; break; }
        await Assert.ThrowsAnyAsync<ArgumentException>(() => f.Service.QueueAsync(form)); Assert.Empty(f.Db.Deployments); Assert.Equal(0, f.PowerShell.Calls);
    }
    [Fact] public async Task ManifestChangeInvalidatesExistingConfirmation()
    {
        using var f = new Fixture(); var form = await f.Flow.DraftAsync(f.Id); form.ConfirmationToken = await f.Service.ConfirmAsync(form);
        f.Seal(f.Snapshot with { EffectiveManifestJson = "changed" });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => f.Service.QueueAsync(form)); Assert.Empty(f.Db.Deployments);
    }
    [Fact] public async Task InvalidProtectedSnapshotIsRejected()
    {
        using var f = new Fixture(); f.Row.ProtectedSnapshot = "tampered";
        await Assert.ThrowsAsync<CdPreparationException>(() => f.Flow.DraftAsync(f.Id));
    }
    [Fact] public async Task ExpiredPreparationCannotObtainApproval()
    {
        using var f = new Fixture(); f.Seal(f.Snapshot with { Provenance = f.Snapshot.Provenance with { Expires = DateTimeOffset.UtcNow.AddMinutes(-1) } });
        await Assert.ThrowsAsync<CdPreparationException>(() => f.Flow.DraftAsync(f.Id));
    }
    [Fact] public async Task RegistryPreservationChangeInvalidatesApproval()
    {
        using var f = new Fixture(); var form = await f.Flow.DraftAsync(f.Id); form.ConfirmationToken = await f.Service.ConfirmAsync(form);
        f.Registry.App.Destinations[0].PreservedConfigurationFiles.Add("secrets.json");
        await Assert.ThrowsAsync<CdPreparationException>(() => f.Service.QueueAsync(form));
    }
    [Theory] [InlineData("CreateSite")] [InlineData("CreateApplication")]
    public async Task NewIisObjectsAreBlocked(string mode)
    {
        using var f = new Fixture(); f.PowerShell.Mode = mode;
        var form = await f.Flow.DraftAsync(f.Id);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => f.Service.ConfirmAsync(form)); Assert.Empty(f.Db.Deployments);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task WorkerRequiresVerificationEvidenceAndRecordsProvenance(bool verified)
    {
        using var f = new Fixture(); var form = await f.Flow.DraftAsync(f.Id); form.ConfirmationToken = await f.Service.ConfirmAsync(form);
        var id = await f.Service.QueueAsync(form); f.PowerShell.Verified = verified;
        var deployment = await f.Db.Deployments.Include(x => x.Server).Include(x => x.CdSource).SingleAsync(x => x.Id == id);
        await f.Service.ExecuteAsync(deployment);
        Assert.Equal(verified ? DeploymentStatus.Successful : DeploymentStatus.Failed, deployment.Status);
        Assert.Equal(1, f.PowerShell.Calls); Assert.NotNull(f.PowerShell.Package);
        Assert.Equal(verified, f.Row.VerifiedAt.HasValue); Assert.Contains("Running", f.Row.AuditJson);
        Assert.Equal(3, (await f.Service.FindAsync(id))!.CdSource!.BuildId);
    }
    [Fact] public async Task DisableAfterApprovalFailsBeforeAnyWinRm()
    {
        using var f = new Fixture(); var form = await f.Flow.DraftAsync(f.Id); form.ConfirmationToken = await f.Service.ConfirmAsync(form);
        var id = await f.Service.QueueAsync(form); f.Options.ExecutionEnabled = false;
        var deployment = await f.Db.Deployments.Include(x => x.Server).Include(x => x.CdSource).SingleAsync(x => x.Id == id);
        await f.Service.ExecuteAsync(deployment);
        Assert.Equal(DeploymentStatus.Failed, deployment.Status); Assert.Equal(0, f.PowerShell.Calls); Assert.Equal(0, f.WinRm.Calls);
    }
    [Theory] [InlineData(@"C:\PrivateCd")] [InlineData(@"C:\PrivateCd\other")] [InlineData(@"C:\")]
    public void ReservedPathsCannotFallThroughToLegacyManualDeployment(string path)
    {
        using var f = new Fixture(); Assert.ThrowsAny<ArgumentException>(() => f.Flow.ValidateManualPaths(path, @"C:\Sites\Portal"));
        f.Flow.ValidateManualPaths(@"C:\ManualPackages\publish", @"C:\Sites\Portal");
    }
    [Fact] public void AdditiveMigrationContainsOnlyNewAuditTable()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var migrations = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly>();
        var item = migrations.Migrations.Single(x => x.Key.EndsWith("_AddControlledCd"));
        var migration = migrations.CreateMigration(item.Value, db.Database.ProviderName!);
        Assert.Single(migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation>());
        Assert.DoesNotContain(migration.UpOperations, x => x is Microsoft.EntityFrameworkCore.Migrations.Operations.DropTableOperation or Microsoft.EntityFrameworkCore.Migrations.Operations.AlterColumnOperation);
    }
    private sealed class Fixture : IDisposable
    {
        public Guid Id = Guid.NewGuid(); public ApplicationDbContext Db; public Server Server; public ServerService Servers;
        public TfsCdOptions Options = new() { Enabled = true, ExecutionEnabled = true, RemoteStagingRoot = @"C:\PrivateCd" };
        public Registry Registry = new(); public FakePowerShell PowerShell = new(); public FakeWinRm WinRm = new();
        public CdDeploymentSource Row; public CdRemoteSnapshot Snapshot; public CdDeploymentFlow Flow; public DeploymentService Service;
        private readonly EphemeralDataProtectionProvider protection = new();
        public Fixture()
        {
            Db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Server = new() { Id = 42, DisplayName = "Deployment_1", HostNameOrIp = "dev-host", Environment = "Development", Username = "user" };
            Db.Servers.Add(Server); Servers = new(Db, WinRm);
            var preparation = new CdPreparation(Id, DateTimeOffset.UtcNow.AddHours(1), "Portal", "dev-host", "Development", "Portal", "/", @"C:\Sites\Portal", 3, "build3", "refs/heads/Dev", new string('a',40), Guid.NewGuid().ToString(), "drop", 12, "drop", "publish", new string('b',64), [], "Prepared");
            Snapshot = new(Id, preparation, Server.Id, CdDeploymentFlow.Fingerprint(Server), Options.RemoteStagingRoot, Options.RemoteStagingRoot + "\\" + Id.ToString("N") + "\\publish", null, 8080, null, "[]", "[]", new string('c',64), CdDeploymentFlow.Policy, []);
            Row = new() { PreparationId = Id, BuildId = 3, BuildNumber = "build3", Branch = preparation.Branch, Commit = preparation.Commit, Artifact = "drop" };
            Seal(Snapshot); Db.CdDeploymentSources.Add(Row); Db.SaveChanges();
            Flow = new(Db, new NoPreparation(), Servers, Registry, PowerShell, new FakeCredentials(), Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(new AutomationOptions()), protection);
            Service = new(Db, Microsoft.Extensions.Options.Options.Create(new AutomationOptions()), protection, WinRm, new FakeCredentials(), PowerShell, Flow);
        }
        public void Seal(CdRemoteSnapshot snapshot) { Snapshot = snapshot; Row.Expires = snapshot.Provenance.Expires; Row.ProtectedSnapshot = protection.CreateProtector("CdDeploymentSnapshot.v1").Protect(JsonSerializer.Serialize(snapshot)); }
        public void UsePreparation() => Flow = new(Db, new Preparation(Snapshot.Provenance, Server, Registry.App), Servers, Registry, PowerShell, new FakeCredentials(), Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(new AutomationOptions()), protection);
        public void Dispose() => Db.Dispose();
    }
    private sealed class Registry : ISiteRegistry
    {
        public RegistryApplication App = new() { Key = "Portal", TfsGitCiDefinitionId = 1, Destinations = [new() { ServerHostNameOrIp = "dev-host", SiteName = "Portal", TargetPath = @"C:\Sites\Portal" }] };
        public RegistryApplication? Find(string key) => key == App.Key ? App : null;
        public IReadOnlyList<RegistryApplication> List() => [App];
    }
    private sealed class NoPreparation : ITfsCdPreparation
    {
        public bool Enabled => true;
        public Task<CdSelection> SelectAsync(string key,int id,string host,CancellationToken token=default) => throw new NotSupportedException();
        public Task<CdPreparation> StageAsync(string key,int id,string host,CancellationToken token=default) => throw new NotSupportedException();
        public Task<CdPreparation> FindAsync(Guid id,CancellationToken token=default) => throw new NotSupportedException();
    }
    private sealed class FakePowerShell : IPowerShellService
    {
        public int Calls; public bool Verified; public string Mode = "Update"; public CdRemoteSnapshot? Package;
        public int StageCalls; public bool StageSuccess; public bool BadDigest;
        public Task<RemoteResult> StageCdAsync(Server server, RemoteCredential credential, CdStageRequest request)
        {
            StageCalls++;
            Assert.Equal(@"C:\Sites\Portal", request.TargetPath);
            Assert.StartsWith(@"C:\PrivateCd\", request.SourcePath);
            var manifest = JsonSerializer.Serialize(new[] { new { Path="appsettings.json",Size=1,Sha256=new string('a',64) }, new { Path="web.config",Size=1,Sha256=new string('b',64) } });
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest)));
            return Task.FromResult(new RemoteResult(StageSuccess,"Fixed staging result",DeploymentMode:"Update",EffectiveManifestJson:manifest,ConfigurationManifestJson:manifest,ConfigurationDigest:BadDigest ? new string('f',64) : digest));
        }
        public Task<RemoteResult> ExecuteAsync(RemoteOperation op, Server server, RemoteCredential credential, Deployment? deployment = null)
        {
            if (op == RemoteOperation.PreviewDeployment) return Task.FromResult(new RemoteResult(true,"IIS preflight",StateFingerprint:new string('d',64),DeploymentMode:Mode));
            Calls++; Package = deployment?.CdPackage;
            return Task.FromResult(new RemoteResult(true,"Executed",Verified:Verified,Verification:Verified ? "IIS and files verified; no HTTP check." : null));
        }
    }
    private sealed class Preparation(CdPreparation preparation, Server server, RegistryApplication app) : ITfsCdPreparation
    {
        public bool Enabled => true;
        public Task<CdPreparation> FindAsync(Guid id,CancellationToken token=default) => Task.FromResult(preparation);
        public Task<CdSelection> SelectAsync(string key,int id,string host,CancellationToken token=default) => Task.FromResult(new CdSelection(app,server,app.Destinations[0],new(new() { Id=3, SourceVersion=preparation.Commit, SourceBranch=preparation.Branch, Repository=new() { Id=preparation.RepositoryId } },"drop",[]) { ContainerId=preparation.ContainerId,ContainerRoot=preparation.ContainerRoot }));
        public Task<CdPreparation> StageAsync(string key,int id,string host,CancellationToken token=default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CdTransferFile>> TransferFilesAsync(Guid id,CancellationToken token=default) => Task.FromResult<IReadOnlyList<CdTransferFile>>([new("app.dll",@"C:\private-local\app.dll",1,new string('a',64))]);
    }
    private sealed class FakeCredentials : ICredentialProvider { public Task<RemoteCredential> GetAsync(Server server) => Task.FromResult(new RemoteCredential("test","test")); }
    private sealed class UnusedBuilds : ITfsGitBuildService
    {
        public Task<IReadOnlyList<DeploymentPortal.ViewModels.Tfs.TfsGitCiBuildViewModel>> GetRecentBuildsAsync() => throw new NotSupportedException();
        public Task<DeploymentPortal.ViewModels.Tfs.TfsGitCiBuildViewModel?> GetBuildAsync(int id) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeploymentPortal.ViewModels.Tfs.TfsGitCiArtifactViewModel>> GetArtifactsAsync(int id) => throw new NotSupportedException();
    }
    private sealed class FakeWinRm : IWinRmService
    {
        public int Calls;
        public Task<ConnectionResult> TestConnectionAsync(Server server) { Calls++; return Task.FromResult(new ConnectionResult(true,"Connected","dev-host",DateTimeOffset.UtcNow)); }
        public Task<ConnectionResult> GetBasicServerInfoAsync(Server server) => TestConnectionAsync(server);
    }
}
