using System.Text.Json;
using DeploymentPortal.Data;
using DeploymentPortal.Models;
using Microsoft.EntityFrameworkCore;

namespace DeploymentPortal.Services;

// Called only by the worker holding DeploymentPortal.Worker, or controlled fixtures.
// One SaveChanges atomically persists deployment state and its cloud audit/state.
public sealed class CloudExecutionLifecycle(ApplicationDbContext db, ILogger logger)
{
    private void Alert(CloudExecutionRecord row, string message, Guid id)
    {
        try { logger.LogCritical("Cloud recovery alert: {Reason}. RecordId={RecordId}; operator inspection required.", message, id); }
        catch {
            // A broken external logger must not prevent durable failure finalization.
            // Retain the entire previous audit, including malformed content, in SQL evidence.
            row.AuditJson = JsonSerializer.Serialize(new object[] { new { at = DateTimeOffset.UtcNow,
                state = "AuditSinkUnavailableInspectionRequired", previousAudit = row.AuditJson } });
        }
    }
    public async Task BeginAsync(Deployment deployment)
    {
        var row = await db.CloudExecutions.SingleOrDefaultAsync(r => r.DeploymentId == deployment.Id)
            ?? throw new CloudExecutionException("CLOUD_APPROVAL_MISSING");
        CloudSecurity.Require(row.Kind == "Deploy" && row.State == "Queued" && deployment.Status == DeploymentStatus.CloudQueued,
            "CLOUD_LIFECYCLE_STATE");
        // Audit errors block execution before any remote connection.
        CloudSecurity.Audit(row, "WorkerStarted", row.ActorSid);
        row.State = "CloudRunning";
        deployment.Status = DeploymentStatus.CloudRunning;
        deployment.StartedAt = DateTimeOffset.UtcNow;
        deployment.ResultMessage = "Cloud worker started; remote outcome is not yet confirmed.";
        await db.SaveChangesAsync();
    }

    public async Task CompleteAsync(Deployment deployment)
    {
        var row = await db.CloudExecutions.SingleOrDefaultAsync(r => r.DeploymentId == deployment.Id);
        if (row is null) {
            // Recovery evidence only. Empty ProtectedData cannot authorize execution.
            row = new CloudExecutionRecord { Id = Guid.NewGuid(), Kind = "Deploy", DeploymentId = deployment.Id };
            db.CloudExecutions.Add(row);
            deployment.Status = DeploymentStatus.Failed;
            deployment.ResultMessage = "Cloud approval record missing. Execution blocked or outcome unconfirmed; operator inspection required.";
            Alert(row, "Approval record missing", deployment.Id);
        }
        try { CloudSecurity.Audit(row, deployment.Status.ToString(), row.ActorSid); }
        catch {
            // Preserve the previous audit verbatim as evidence, never silently discard it.
            var previous = row.AuditJson;
            row.AuditJson = JsonSerializer.Serialize(new object[] { new { at = DateTimeOffset.UtcNow,
                state = "AuditRecoveryRequired", previousAudit = previous, previousSha256 = CloudSecurity.Hash(previous) } });
            deployment.Status = DeploymentStatus.Failed;
            deployment.ResultMessage = "Cloud audit could not be finalized normally. Remote outcome requires operator inspection; do not retry.";
            Alert(row, "Audit recovery required; remote outcome unconfirmed", deployment.Id);
        }
        row.State = deployment.Status.ToString();
        deployment.CompletedAt = DateTimeOffset.UtcNow;
        // SQL failure propagates to the worker. Durable CloudRunning is reconciled on restart;
        // no remote operation is retried and success is never reported on an unsaved outcome.
        await db.SaveChangesAsync();
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (db.Database.IsRelational() && !(await db.Database.GetAppliedMigrationsAsync(ct))
            .Contains("20261024174124_AddCloudExecution", StringComparer.Ordinal)) return;
        // Fetch only inconsistent records in bounded batches. An oversized backlog blocks
        // the worker with an alert rather than leaving a partially checked queue executable.
        var deployments = await db.Deployments.Where(d => (d.DeploymentMode == CloudExecutionFlow.Mode ||
            d.Status == DeploymentStatus.CloudQueued || d.Status == DeploymentStatus.CloudRunning) &&
            (d.DeploymentMode != CloudExecutionFlow.Mode || d.Status == DeploymentStatus.CloudRunning || d.Status == DeploymentStatus.Running || d.Status == DeploymentStatus.Queued ||
             !db.CloudExecutions.Any(r => r.DeploymentId == d.Id &&
                (d.Status == DeploymentStatus.CloudQueued && r.Kind == "Deploy" && r.State == "Queued" ||
                 d.Status == DeploymentStatus.Successful && r.State == "Successful" || d.Status == DeploymentStatus.Failed && r.State == "Failed"))))
            .Take(201).ToListAsync(ct);
        var backlog = deployments.Count > 200;
        foreach (var deployment in deployments.Take(200)) {
            var row = await db.CloudExecutions.SingleOrDefaultAsync(r => r.DeploymentId == deployment.Id, ct);
            if (deployment.DeploymentMode != CloudExecutionFlow.Mode || deployment.Status is DeploymentStatus.CloudRunning or DeploymentStatus.Running ||
                deployment.Status == DeploymentStatus.Queued || row is null ||
                deployment.Status == DeploymentStatus.CloudQueued && (row.Kind != "Deploy" || row.State != "Queued")) {
                deployment.Status = DeploymentStatus.Failed;
                deployment.ResultMessage = "Cloud worker interrupted or incompatible approval state. Remote state and rollback are unconfirmed; inspect target and backup. No automatic retry.";
                await CompleteAsync(deployment);
            } else if (deployment.Status is DeploymentStatus.Successful or DeploymentStatus.Failed && row.State != deployment.Status.ToString()) {
                // A recovered interrupted record must never become a runnable approval again.
                await CompleteAsync(deployment);
            }
        }
        var orphaned = await db.CloudExecutions.Where(r => r.Kind == "Deploy" && r.State != "Orphaned" && r.DeploymentId != null &&
            !db.Deployments.Any(d => d.Id == r.DeploymentId)).Take(201).ToListAsync(ct);
        backlog |= orphaned.Count > 200;
        foreach (var row in orphaned.Take(200)) {
            if (row.State == "Orphaned") continue;
            row.State = "Orphaned";
            Alert(row, "Execution has no deployment", row.Id);
            try { CloudSecurity.Audit(row, "OrphanedInspectionRequired", row.ActorSid); }
            catch { Alert(row, "Orphaned execution audit invalid", row.Id); }
        }
        // The configured remote operation timeout is at most one hour. Two hours avoids
        // racing another active web preparation while conservatively identifying abandoned work.
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
        var abandoned = await db.CloudExecutions.Where(r => r.DeploymentId == null && r.CreatedAt < cutoff &&
            (r.Kind == "Build" && r.State == "Dispatching" || r.Kind == "Deploy" &&
             (r.State == "Preparing" || r.State == "Staging" || r.State == "Prepared"))).Take(201).ToListAsync(ct);
        backlog |= abandoned.Count > 200;
        foreach (var row in abandoned.Take(200)) {
            row.State = row.Kind == "Build" ? "Unconfirmed" : "PreparationUnconfirmed";
            try { CloudSecurity.Audit(row, "RestartRecoveryNoAutomaticRetry", "worker"); }
            catch { Alert(row, "Abandoned operation audit invalid", row.Id); }
            Alert(row, "Abandoned operation quarantined; inspect remote state and staging", row.Id);
        }
        await db.SaveChangesAsync(ct);
        if (backlog) {
            logger.LogCritical("Cloud reconciliation backlog requires further recovery; worker remains blocked until reconciliation completes.");
            throw new CloudExecutionException("CLOUD_RECOVERY_BACKLOG");
        }
    }
}
