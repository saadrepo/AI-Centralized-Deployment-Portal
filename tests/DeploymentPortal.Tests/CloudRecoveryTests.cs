using System.Security.Principal;
using System.Text.Json;
using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public class CloudRecoveryTests
{
    private sealed class Connections : IWinRmService
    {
        public int Calls;
        public Task<ConnectionResult> TestConnectionAsync(Server s) { Calls++; return Task.FromResult(new ConnectionResult(true, "fixture", "fixture", DateTimeOffset.UtcNow)); }
        public Task<ConnectionResult> GetBasicServerInfoAsync(Server s) => throw new NotSupportedException();
    }
    private static WindowsPrincipal User() {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new(WindowsIdentity.GetCurrent());
    }
    private static async Task<Deployment> Approved(CloudExecutionTests.Fixture f)
    {
        var prepared = JsonSerializer.SerializeToElement(await f.Flow.PrepareAsync(await f.Ticket(true), User(), default));
        await f.Flow.ApproveAsync(prepared.GetProperty("approvalToken").GetString()!, User(), default);
        var d = Assert.Single(f.Db.Deployments); d.Server = (await f.Db.Servers.FindAsync(1))!; return d;
    }
    private static CloudExecutionLifecycle Lifecycle(ApplicationDbContext db) => new(db, NullLogger.Instance);

    [Theory] [InlineData("missing")] [InlineData("audit")]
    public async Task MissingOrInvalidAuditBlocksBeforeRemoteAndPersistsFailure(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new CloudExecutionTests.Fixture(); var d = await Approved(f);
        var row = Assert.Single(f.Db.CloudExecutions);
        if (failure == "missing") f.Db.CloudExecutions.Remove(row); else row.AuditJson = "corrupt-evidence";
        await f.Db.SaveChangesAsync();
        var connections = new Connections();
        await new DeploymentService(f.Db, Options.Create(new AutomationOptions()), f.Protection, connections,
            new CloudExecutionTests.Credentials(), f.PowerShell, cloud: f.Flow).ExecuteAsync(d);
        Assert.Equal(0, connections.Calls); Assert.Equal(0, f.PowerShell.Deploys);
        Assert.Equal(DeploymentStatus.Failed, d.Status); Assert.Equal("Failed", Assert.Single(f.Db.CloudExecutions).State);
        Assert.NotNull(d.CompletedAt);
        if (failure == "audit") Assert.Contains("corrupt-evidence", Assert.Single(f.Db.CloudExecutions).AuditJson);
        if (failure == "missing") {
            var error = await Assert.ThrowsAsync<CloudExecutionException>(() => f.Flow.ReadinessAsync(f.Selection, User()));
            Assert.Equal("CLOUD_RECOVERY_INSPECTION_REQUIRED", error.Code);
        }
    }

    [Theory] [InlineData("missing")] [InlineData("audit")]
    public async Task FinalizationFailureNeverReportsSuccessOrRetriesRemoteWork(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new CloudExecutionTests.Fixture(); var d = await Approved(f);
        f.PowerShell.AfterDeploy = () => {
            var row = Assert.Single(f.Db.CloudExecutions);
            if (failure == "missing") f.Db.CloudExecutions.Remove(row); else row.AuditJson = "damaged-audit";
            f.Db.SaveChanges();
        };
        var service = new DeploymentService(f.Db, Options.Create(new AutomationOptions()), f.Protection, new Connections(),
            new CloudExecutionTests.Credentials(), f.PowerShell, cloud: f.Flow);
        await service.ExecuteAsync(d);
        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal("Failed", Assert.Single(f.Db.CloudExecutions).State);
        await service.ExecuteAsync(d); Assert.Equal(1, f.PowerShell.Deploys);
    }

    [Theory]
    [InlineData(DeploymentStatus.CloudRunning)] [InlineData(DeploymentStatus.Running)]
    [InlineData(DeploymentStatus.Queued)]
    public async Task RestartQuarantinesInterruptedOrLegacyQueuedCloudWork(DeploymentStatus status)
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var d = new Deployment { DeploymentMode = CloudExecutionFlow.Mode, Status = status };
        db.Deployments.Add(d); db.CloudExecutions.Add(new() { Kind = "Deploy", DeploymentId = d.Id, State = "Queued" }); await db.SaveChangesAsync();
        await Lifecycle(db).ReconcileAsync(default);
        Assert.Equal(DeploymentStatus.Failed, d.Status); Assert.Equal("Failed", Assert.Single(db.CloudExecutions).State);
        var audit = Assert.Single(db.CloudExecutions).AuditJson;
        await Lifecycle(db).ReconcileAsync(default); Assert.Equal(audit, Assert.Single(db.CloudExecutions).AuditJson);
        Assert.Contains("No automatic retry", d.ResultMessage);
    }
    [Fact]
    public async Task ReservedCloudStatusWithWrongModeIsQuarantinedRatherThanExecutedAsTfs()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var d = new Deployment { DeploymentMode = "Update", Status = DeploymentStatus.CloudQueued };
        db.Deployments.Add(d); db.CloudExecutions.Add(new() { Kind = "Deploy", DeploymentId = d.Id, State = "Queued" }); await db.SaveChangesAsync();
        await Lifecycle(db).ReconcileAsync(default);
        Assert.Equal(DeploymentStatus.Failed, d.Status); Assert.Equal("Failed", Assert.Single(db.CloudExecutions).State);
    }

    [Fact]
    public async Task RecoveryRepairsTerminalMismatchMissingRecordsAndOrphansWithoutChangingTfs()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var done = new Deployment { DeploymentMode = CloudExecutionFlow.Mode, Status = DeploymentStatus.Successful };
        var missing = new Deployment { DeploymentMode = CloudExecutionFlow.Mode, Status = DeploymentStatus.CloudQueued };
        var tfs = new Deployment { Status = DeploymentStatus.Running };
        db.Deployments.AddRange(done, missing, tfs);
        db.CloudExecutions.AddRange(new() { Kind = "Deploy", DeploymentId = done.Id, State = "CloudRunning" },
            new() { Kind = "Deploy", DeploymentId = Guid.NewGuid(), State = "Queued" }); await db.SaveChangesAsync();
        await Lifecycle(db).ReconcileAsync(default);
        Assert.Equal("Successful", db.CloudExecutions.Single(r => r.DeploymentId == done.Id).State);
        Assert.Equal(DeploymentStatus.Failed, missing.Status); Assert.Equal(DeploymentStatus.Running, tfs.Status);
        Assert.Single(db.CloudExecutions, r => r.State == "Orphaned");
        Assert.Equal("", db.CloudExecutions.Single(r => r.DeploymentId == missing.Id).ProtectedData);
    }

    private sealed class SaveFailure : SaveChangesInterceptor
    {
        public bool Fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (Fail) throw new IOException("synthetic database outage"); return ValueTask.FromResult(result); }
    }
    [Fact]
    public async Task FailedFinalSaveLeavesDurableRunningIntentWhichFreshContextReconciles()
    {
        var store = new InMemoryDatabaseRoot(); var name = Guid.NewGuid().ToString(); var failure = new SaveFailure();
        DbContextOptions<ApplicationDbContext> Options(bool intercept) {
            var builder = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name, store);
            if (intercept) builder.AddInterceptors(failure); return builder.Options;
        }
        using (var db = new ApplicationDbContext(Options(true))) {
            var d = new Deployment { DeploymentMode = CloudExecutionFlow.Mode, Status = DeploymentStatus.CloudQueued };
            db.Deployments.Add(d); db.CloudExecutions.Add(new() { Kind = "Deploy", DeploymentId = d.Id, State = "Queued" }); await db.SaveChangesAsync();
            await Lifecycle(db).BeginAsync(d); d.Status = DeploymentStatus.Successful; failure.Fail = true;
            await Assert.ThrowsAsync<IOException>(() => Lifecycle(db).CompleteAsync(d));
        }
        using var restart = new ApplicationDbContext(Options(false));
        Assert.Equal(DeploymentStatus.CloudRunning, Assert.Single(restart.Deployments).Status);
        Assert.Equal("CloudRunning", Assert.Single(restart.CloudExecutions).State);
        await Lifecycle(restart).ReconcileAsync(default);
        Assert.Equal(DeploymentStatus.Failed, Assert.Single(restart.Deployments).Status);
        Assert.Equal("Failed", Assert.Single(restart.CloudExecutions).State);
    }

    [Theory] [InlineData(false, "")] [InlineData(true, "wrong-host")]
    public async Task CompatibilityMustBeExplicitAndBoundToThisHost(bool confirmed, string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new CloudExecutionTests.Fixture(); var ticket = await f.Ticket(true);
        f.Config.WorkerCompatibilityConfirmed = confirmed; f.Config.WorkerHostName = host;
        var error = await Assert.ThrowsAsync<CloudExecutionException>(() => f.Flow.PrepareAsync(ticket, User(), default));
        Assert.Equal("CLOUD_WORKER_COMPATIBILITY_REQUIRED", error.Code); Assert.Equal(0, f.PowerShell.Stages);
        Assert.Empty(f.Db.Deployments);
    }

    [Fact]
    public async Task CloudQueueAndRunningStatusesCannotBeSelectedByLegacyWorker()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new CloudExecutionTests.Fixture(); var d = await Approved(f);
        Assert.Equal(DeploymentStatus.CloudQueued, d.Status);
        Assert.Empty(await f.Db.Deployments.Where(x => x.Status == DeploymentStatus.Queued).ToListAsync());
        await Lifecycle(f.Db).BeginAsync(d);
        Assert.Equal(DeploymentStatus.CloudRunning, d.Status);
        Assert.Empty(await f.Db.Deployments.Where(x => x.Status == DeploymentStatus.Running || x.Status == DeploymentStatus.Queued).ToListAsync());
    }
    [Fact]
    public async Task LegacyCloudQueueArrivingAfterRecoveryIsRejectedWithoutRemoteWorkOrQueueStarvation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new CloudExecutionTests.Fixture(); var d = await Approved(f);
        d.Status = DeploymentStatus.Queued; await f.Db.SaveChangesAsync(); var connections = new Connections();
        await new DeploymentService(f.Db, Options.Create(new AutomationOptions()), f.Protection, connections,
            new CloudExecutionTests.Credentials(), f.PowerShell, cloud: f.Flow).ExecuteAsync(d);
        Assert.Equal(DeploymentStatus.Failed, d.Status); Assert.Equal(0, connections.Calls); Assert.Equal(0, f.PowerShell.Deploys);
        Assert.Equal("Failed", Assert.Single(f.Db.CloudExecutions).State);
        Assert.Empty(await f.Db.Deployments.Where(x => x.Status == DeploymentStatus.Queued).ToListAsync());
    }

    private sealed class BrokenLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state,
            Exception? error, Func<TState, Exception?, string> formatter) => throw new IOException("synthetic logging outage");
    }
    [Fact]
    public async Task BrokenLoggingSinkCannotPreventDurableFailureFinalization()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var d = new Deployment { DeploymentMode = CloudExecutionFlow.Mode, Status = DeploymentStatus.Successful };
        db.Deployments.Add(d); await db.SaveChangesAsync();
        await new CloudExecutionLifecycle(db, new BrokenLogger()).CompleteAsync(d);
        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal("Failed", Assert.Single(db.CloudExecutions).State);
        Assert.Contains("AuditSinkUnavailableInspectionRequired", Assert.Single(db.CloudExecutions).AuditJson);
    }
    [Fact]
    public async Task RestartQuarantinesAbandonedWebOperationsButLeavesRecentWorkAlone()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var old = new CloudExecutionRecord { Kind = "Deploy", State = "Staging", CreatedAt = DateTimeOffset.UtcNow.AddHours(-3), UniqueScope = "reserved" };
        var recent = new CloudExecutionRecord { Kind = "Deploy", State = "Staging" };
        var build = new CloudExecutionRecord { Kind = "Build", State = "Dispatching", CreatedAt = DateTimeOffset.UtcNow.AddHours(-3) };
        db.CloudExecutions.AddRange(old, recent, build); await db.SaveChangesAsync();
        await Lifecycle(db).ReconcileAsync(default);
        Assert.Equal("PreparationUnconfirmed", old.State); Assert.Equal("reserved", old.UniqueScope);
        Assert.Equal("Staging", recent.State); Assert.Equal("Unconfirmed", build.State);
    }
    [Fact]
    public async Task LargeRecoveryBacklogIsBoundedAndBlocksUntilAllBatchesReconcile()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Deployments.AddRange(Enumerable.Range(0, 201).Select(_ => new Deployment { DeploymentMode = CloudExecutionFlow.Mode, Status = DeploymentStatus.CloudRunning }));
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<CloudExecutionException>(() => Lifecycle(db).ReconcileAsync(default));
        Assert.Equal("CLOUD_RECOVERY_BACKLOG", error.Code);
        Assert.Equal(200, await db.Deployments.CountAsync(d => d.Status == DeploymentStatus.Failed));
        await Lifecycle(db).ReconcileAsync(default);
        Assert.Equal(201, await db.Deployments.CountAsync(d => d.Status == DeploymentStatus.Failed));
    }
}
