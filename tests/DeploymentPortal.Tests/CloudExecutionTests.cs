using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public class CloudExecutionTests
{
    [Fact]
    public async Task AzureExecutionCannotBeEnabledByConfigurationOrProtectedTickets()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture("AzureDevOps");
        var readiness = Json(await f.Flow.ReadinessAsync(f.Selection, User()));
        Assert.False(readiness.GetProperty("canQueue").GetBoolean());
        Assert.False(readiness.GetProperty("canApprove").GetBoolean());
        var ticket = new CloudTicket(Guid.NewGuid(), f.Selection, "unused", CloudSecurity.Sid(User()), DateTimeOffset.UtcNow.AddMinutes(5));
        foreach (var action in new[] { "Queue", "Prepare" }) {
            var token = f.Protection.CreateProtector("CloudExecution." + action + ".v1").Protect(JsonSerializer.Serialize(ticket));
            var error = await Assert.ThrowsAsync<CloudExecutionException>(() => action == "Queue"
                ? f.Flow.QueueAsync(token, User(), default) : f.Flow.PrepareAsync(token, User(), default));
            Assert.Equal("AZURE_EXECUTION_NOT_IMPLEMENTED", error.Code);
        }
        Assert.Equal(0, f.Provider.Queues + f.Provider.Downloads + f.Provider.Reads);
        Assert.Empty(f.Db.CloudExecutions);
    }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static string Field(object value, string field) => Json(value).GetProperty(field).GetString()!;
    private static ClaimsPrincipal User() {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new WindowsPrincipal(WindowsIdentity.GetCurrent());
    }
    internal sealed class Registry : ISiteRegistry
    {
        public RegistryApplication App = new() { Key = "Portal", DisplayName = "Portal", Destinations = [new() { ServerId = 1, SiteName = "Portal", ApplicationName = "/", ApplicationPool = "PortalPool", TargetPath = @"C:\Sites\Portal", SiteHostName = "app.example" }] };
        public IReadOnlyList<RegistryApplication> List() => [App];
        public RegistryApplication? Find(string key) => key == App.Key ? App : null;
    }
    internal sealed class Provider(string provider) : ICloudProvider
    {
        public int Queues, Downloads, Reads; public bool FailQueue, ChangeArtifact, FailedRun, CorruptDownload;
        public CloudEvidence Evidence => new(new(42, "CI-42", new string('a', 40), "completed", FailedRun ? "failed" : provider == "GitHub" ? "success" : "succeeded", 1),
            new(ChangeArtifact ? 8 : 7, "drop", provider == "GitHub" ? "WorkflowArtifact" : "Container", 3, "sha256:" + Convert.ToHexString(SHA256.HashData("zip"u8)), "immutable-artifact"));
        public Task<long> QueueAsync(CloudBinding b, CloudSelection s, CancellationToken ct) { Queues++; if (FailQueue) throw new IOException("unconfirmed"); return Task.FromResult(42L); }
        public Task<CloudRun> RunAsync(CloudBinding b, CloudSelection s, CancellationToken ct) { Reads++; return Task.FromResult(Evidence.Run); }
        public Task<CloudEvidence> EvidenceAsync(CloudBinding b, CloudSelection s, CancellationToken ct) { Reads++; return Task.FromResult(Evidence); }
        public Task<byte[]> DownloadAsync(CloudBinding b, CloudSelection s, CloudEvidence e, CancellationToken ct) { Downloads++; return Task.FromResult(Encoding.UTF8.GetBytes(CorruptDownload ? "substitute" : "zip")); }
    }
    internal sealed class Packages : ICloudPackageStager
    {
        public int Calls;
        public Task<IReadOnlyList<CdTransferFile>> StageAsync(Guid id, byte[] bytes, CloudExecutionMapping m, CancellationToken ct) {
            Calls++; return Task.FromResult<IReadOnlyList<CdTransferFile>>([new("app.dll", @"C:\PrivateCloud\app.dll", 3, new string('a', 64))]);
        }
    }
    internal sealed class PowerShell : IPowerShellService
    {
        public int Stages, Previews; public int Deploys { get; private set; } public bool StageFails, BadManifest; public int HealthStatus = 200; public Action? AfterDeploy;
        public Task<RemoteResult> StageCdAsync(Server s, RemoteCredential c, CdStageRequest r) {
            Stages++; var manifest = JsonSerializer.Serialize(new[] { new { Path = "appsettings.json", Size = 1, Sha256 = new string('a', 64) }, new { Path = "web.config", Size = 1, Sha256 = new string('b', 64) } });
            return Task.FromResult(new RemoteResult(!StageFails, "synthetic", DeploymentMode: "Update", EffectiveManifestJson: manifest, ConfigurationManifestJson: manifest,
                ConfigurationDigest: BadManifest ? "bad" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)))));
        }
        public Task<RemoteResult> ExecuteAsync(RemoteOperation op, Server s, RemoteCredential c, Deployment? d = null) {
            if (op == RemoteOperation.Deploy) {
                Deploys++; Assert.NotNull(d!.CdPackage); Assert.Equal("https://app.example/health", d.CloudHealthUrl);
                AfterDeploy?.Invoke();
                return Task.FromResult(new RemoteResult(true, "synthetic worker result", Verified: true, Verification: "synthetic integrity evidence", HealthHttpStatus: HealthStatus));
            }
            Assert.Equal(RemoteOperation.PreviewDeployment, op); Previews++;
            return Task.FromResult(new RemoteResult(true, "preview", StateFingerprint: new string('c', 64), DeploymentMode: "Update"));
        }
    }
    internal sealed class Credentials : ICredentialProvider { public Task<RemoteCredential> GetAsync(Server s) => Task.FromResult(new RemoteCredential("fixture", "synthetic")); }
    internal sealed class Fixture : IDisposable
    {
        public ApplicationDbContext Db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Registry Registry = new(); public CloudBuildExplorerTests.Sources Sources = new(); public Provider Provider; public Packages Packages = new(); public PowerShell PowerShell = new();
        public CloudExecutionOptions Config = new(); public CloudExecutionMapping Mapping; public CloudExecutionFlow Flow; public CloudSelection Selection;
        public IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();
        public Fixture(string provider = "GitHub") {
            var sid = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : "S-1-5-21-1-2-3-1001";
            Mapping = new() { ApplicationKey = "Portal", Provider = provider, SourceId = 1, Repository = provider == "GitHub" ? "app" : "22222222-2222-2222-2222-222222222222",
                Project = provider == "GitHub" ? null : "11111111-1111-1111-1111-111111111111", Branch = "Dev", DefinitionId = 7, ArtifactName = "drop", ServerId = 1,
                Environment = "Test", HealthUrl = "https://app.example/health", BuilderSids = [sid], ApproverSids = [sid] };
            Config = new() { BuildExecutionEnabled = true, DeploymentExecutionEnabled = true, LiveAuthorizationReference = "synthetic-test-only", LiveAuthorizationExpires = DateTimeOffset.UtcNow.AddHours(1),
                WorkerCompatibilityConfirmed = true, WorkerHostName = Environment.MachineName,
                LocalStagingRoot = @"C:\PrivateCloud", RemoteStagingRoot = @"C:\RemoteCloud", Mappings = [Mapping] };
            if (provider == "AzureDevOps") { Sources.Values[0].Provider = InfrastructureSourceProvider.AzureDevOps; Sources.Values[0].Destination = "https://dev.azure.com/example/"; }
            Db.Servers.Add(new() { Id = 1, HostNameOrIp = "vm.example", Environment = "Test", UseSsl = true }); Db.SaveChanges();
            Provider = new(provider); Selection = new("Portal", provider, 1, Mapping.Project, Mapping.Repository, "Dev", 7, 42);
            Flow = new(Db, Registry, Sources, Provider, Packages, PowerShell, new Credentials(), Options.Create(Config), Options.Create(new AutomationOptions()), Protection);
        }
        public async Task<string> Ticket(bool approve = false) => Field(await Flow.ReadinessAsync(Selection, User()), approve ? "prepareTicket" : "queueTicket");
        public void Dispose() => Db.Dispose();
    }
    [Theory] [InlineData("GitHub")]
    public async Task ExplicitBuildTracksExactIdAndSuccessfulBuildNeverQueuesDeployment(string provider)
    {
        if (!OperatingSystem.IsWindows()) return; using var f = new Fixture(provider); var token = await f.Ticket();
        var result = await f.Flow.QueueAsync(token, User(), default); await f.Flow.QueueAsync(token, User(), default);
        Assert.Equal(1, f.Provider.Queues); Assert.Equal(42, Json(result).GetProperty("RunId").GetInt64());
        var row = Assert.Single(f.Db.CloudExecutions); await f.Flow.StatusAsync(row.Id, User(), default);
        Assert.Empty(f.Db.Deployments); Assert.Equal(0, f.Provider.Downloads); Assert.Equal(0, f.PowerShell.Stages); Assert.Null(row.UniqueScope);
    }
    [Theory] [InlineData("GitHub")]
    public async Task PreparedArtifactNeedsExplicitSingleUseApprovalAndWorkerRevalidation(string provider)
    {
        if (!OperatingSystem.IsWindows()) return; using var f = new Fixture(provider);
        var prepared = await f.Flow.PrepareAsync(await f.Ticket(true), User(), default);
        Assert.Empty(f.Db.Deployments); Assert.Equal(1, f.PowerShell.Stages); Assert.Equal(1, f.PowerShell.Previews);
        var token = Field(prepared, "approvalToken"); await f.Flow.ApproveAsync(token, User(), default); await f.Flow.ApproveAsync(token, User(), default);
        var deployment = Assert.Single(f.Db.Deployments); deployment.Server = (await f.Db.Servers.FindAsync(1))!;
        var snapshot = await f.Flow.LoadForExecutionAsync(deployment);
        Assert.Equal(CloudExecutionFlow.Mode, deployment.DeploymentMode); Assert.Equal("https://app.example/health", deployment.CloudHealthUrl);
        Assert.Equal(snapshot.Provenance.TargetPath, deployment.TargetPath); Assert.Equal(0, f.PowerShell.Deploys);
        Assert.Contains("ApprovedAndQueued", Assert.Single(f.Db.CloudExecutions).AuditJson);
    }
    [Theory] [InlineData("flags")] [InlineData("authorization")] [InlineData("source")] [InlineData("provider")] [InlineData("mapping")] [InlineData("approver")] [InlineData("target")]
    public async Task ChangedAuthorizationAndMappingBlockApprovalBeforeQueue(string field)
    {
        if (!OperatingSystem.IsWindows()) return; using var f = new Fixture(); var prepared = await f.Flow.PrepareAsync(await f.Ticket(true), User(), default);
        switch (field) {
            case "flags": f.Config.DeploymentExecutionEnabled = false; break;
            case "authorization": f.Config.LiveAuthorizationExpires = DateTimeOffset.UtcNow.AddMinutes(-1); break;
            case "source": f.Sources.Values[0].Enabled = false; break;
            case "provider": f.Sources.Values[0].Provider = InfrastructureSourceProvider.AzureDevOps; break;
            case "mapping": f.Mapping.Branch = "main"; break;
            case "approver": f.Mapping.ApproverSids = []; break;
            case "target": f.Registry.App.Destinations[0].TargetPath = @"C:\Other"; break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.Flow.ApproveAsync(Field(prepared, "approvalToken"), User(), default)); Assert.Empty(f.Db.Deployments);
    }
    [Fact] public async Task UnconfirmedDispatchIsPersistedAndNeverAutomaticallyRetried()
    {
        if (!OperatingSystem.IsWindows()) return; using var f = new Fixture(); f.Provider.FailQueue = true; var ticket = await f.Ticket();
        await f.Flow.QueueAsync(ticket, User(), default); await f.Flow.QueueAsync(ticket, User(), default);
        Assert.Equal(1, f.Provider.Queues); Assert.Equal("Unconfirmed", Assert.Single(f.Db.CloudExecutions).State);
        var nextTicket = await f.Ticket();
        await Assert.ThrowsAsync<CloudExecutionException>(() => f.Flow.QueueAsync(nextTicket, User(), default));
    }
    [Theory] [InlineData("failed-run")] [InlineData("digest")] [InlineData("remote")] [InlineData("manifest")]
    public async Task FailedOrSubstitutedArtifactsCannotProduceApproval(string failure)
    {
        if (!OperatingSystem.IsWindows()) return; using var f = new Fixture(); var ticket = await f.Ticket(true);
        f.Provider.FailedRun = failure == "failed-run"; f.Provider.CorruptDownload = failure == "digest";
        f.PowerShell.StageFails = failure == "remote"; f.PowerShell.BadManifest = failure == "manifest";
        await Assert.ThrowsAnyAsync<ArgumentException>(() => f.Flow.PrepareAsync(ticket, User(), default)); Assert.Empty(f.Db.Deployments);
        if (failure is "failed-run" or "digest") Assert.Equal(0, f.PowerShell.Stages);
    }
    [Fact] public async Task ArtifactChangeAfterReviewOrAfterQueueFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return; using var f = new Fixture(); var prepared = await f.Flow.PrepareAsync(await f.Ticket(true), User(), default); var token = Field(prepared, "approvalToken");
        f.Provider.ChangeArtifact = true; await Assert.ThrowsAsync<CloudExecutionException>(() => f.Flow.ApproveAsync(token, User(), default)); Assert.Empty(f.Db.Deployments);
        f.Provider.ChangeArtifact = false; await f.Flow.ApproveAsync(token, User(), default); f.Provider.ChangeArtifact = true;
        var deployment = Assert.Single(f.Db.Deployments); deployment.Server = (await f.Db.Servers.FindAsync(1))!;
        await Assert.ThrowsAsync<CloudExecutionException>(() => f.Flow.LoadForExecutionAsync(deployment)); Assert.Equal(0, f.PowerShell.Deploys);
    }
    [Fact] public async Task ExpiredApprovalCannotQueueAndTokenTamperingFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return; using var f = new Fixture(); var prepared = await f.Flow.PrepareAsync(await f.Ticket(true), User(), default); var token = Field(prepared, "approvalToken");
        await Assert.ThrowsAsync<CloudExecutionException>(() => f.Flow.ApproveAsync(token + "bad", User(), default));
        var protector = f.Protection.CreateProtector("CloudExecution.Approve.v1"); var approval = JsonSerializer.Deserialize<CloudApproval>(protector.Unprotect(token))!;
        var expired = protector.Protect(JsonSerializer.Serialize(approval with { Expires = DateTimeOffset.UtcNow.AddSeconds(-1) }));
        await Assert.ThrowsAsync<CloudExecutionException>(() => f.Flow.ApproveAsync(expired, User(), default)); Assert.Empty(f.Db.Deployments);
    }
    [Fact] public async Task DisabledLiveGateRejectsBeforeAnyProviderOrRemoteOperation()
    {
        using var f = new Fixture(); f.Config.LiveAuthorizationReference = "";
        await Assert.ThrowsAsync<CloudExecutionException>(() => f.Flow.QueueAsync("invalid", new(), default));
        await Assert.ThrowsAsync<CloudExecutionException>(() => f.Flow.PrepareAsync("invalid", new(), default));
        Assert.Equal(0, f.Provider.Queues + f.Provider.Reads + f.Provider.Downloads + f.PowerShell.Stages);
    }
    [Fact] public void ClaimsOnlyIdentityCannotApprove() => Assert.Throws<UnauthorizedAccessException>(() => CloudSecurity.Sid(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001")], "Windows"))));

    private sealed class Connections : IWinRmService {
        public int Calls;
        public Task<ConnectionResult> TestConnectionAsync(Server s) { Calls++; return Task.FromResult(new ConnectionResult(true, "synthetic", "fixture", DateTimeOffset.UtcNow)); }
        public Task<ConnectionResult> GetBasicServerInfoAsync(Server s) => throw new NotSupportedException();
    }
    [Theory] [InlineData(200)] [InlineData(0)]
    public async Task ExistingWorkerRequiresIntegrityAndHttpsHealthEvidenceForCloudSuccess(int health)
    {
        if (!OperatingSystem.IsWindows()) return; using var f = new Fixture();
        var prepared = await f.Flow.PrepareAsync(await f.Ticket(true), User(), default); await f.Flow.ApproveAsync(Field(prepared, "approvalToken"), User(), default);
        var deployment = Assert.Single(f.Db.Deployments); deployment.Server = (await f.Db.Servers.FindAsync(1))!; f.PowerShell.HealthStatus = health;
        var service = new DeploymentService(f.Db, Options.Create(new AutomationOptions()), f.Protection, new Connections(), new Credentials(), f.PowerShell, cloud: f.Flow);
        await service.ExecuteAsync(deployment);
        Assert.Equal(health == 200 ? DeploymentStatus.Successful : DeploymentStatus.Failed, deployment.Status);
        Assert.Equal(1, f.PowerShell.Deploys); Assert.Equal(deployment.Status.ToString(), Assert.Single(f.Db.CloudExecutions).State);
        await service.ExecuteAsync(deployment); Assert.Equal(1, f.PowerShell.Deploys); // Consumed approval never executes twice.
    }
    [Theory] [InlineData("target")] [InlineData("revoked")] [InlineData("guard")]
    public async Task WorkerRejectsTamperedRevokedOrUnguardedCloudRequestsBeforeRemoteConnection(string failure)
    {
        if (!OperatingSystem.IsWindows()) return; using var f = new Fixture();
        var prepared = await f.Flow.PrepareAsync(await f.Ticket(true), User(), default); await f.Flow.ApproveAsync(Field(prepared, "approvalToken"), User(), default);
        var deployment = Assert.Single(f.Db.Deployments); deployment.Server = (await f.Db.Servers.FindAsync(1))!;
        if (failure == "target") deployment.TargetPath = @"C:\Unauthorized";
        if (failure == "revoked") f.Config.LiveAuthorizationExpires = DateTimeOffset.UtcNow.AddMinutes(-1);
        var connections = new Connections(); var service = new DeploymentService(f.Db, Options.Create(new AutomationOptions()), f.Protection, connections, new Credentials(), f.PowerShell, cloud: failure == "guard" ? null : f.Flow);
        await service.ExecuteAsync(deployment);
        Assert.Equal(DeploymentStatus.Failed, deployment.Status); Assert.Equal(0, connections.Calls); Assert.Equal(0, f.PowerShell.Deploys);
    }
}
