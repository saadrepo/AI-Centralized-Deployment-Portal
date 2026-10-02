using DeploymentPortal.Data;
using DeploymentPortal.Models;
using Microsoft.EntityFrameworkCore;

namespace DeploymentPortal.Services;
// SQL session lock serializes deployments across portal instances and protects restart recovery.
public class DeploymentWorker(IServiceScopeFactory scopes, ILogger<DeploymentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Database.OpenConnectionAsync(stoppingToken);
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource='DeploymentPortal.Worker', @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @r;";
                var result = Convert.ToInt32(await command.ExecuteScalarAsync(stoppingToken));
                if (result >= 0)
                {
                    try
                    {
                    await db.Deployments.Where(x => x.Status == DeploymentStatus.Running).ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status, DeploymentStatus.Failed)
                        .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow)
                        .SetProperty(x => x.ResultMessage, "Worker interrupted. Remote state and rollback are unconfirmed. Inspect the target and retained backup before retrying."), stoppingToken);
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var deployment = await db.Deployments.Include(x => x.Server).Where(x => x.Status == DeploymentStatus.Queued).OrderBy(x => x.StartedAt).FirstOrDefaultAsync(stoppingToken);
                        if (deployment is not null) await scope.ServiceProvider.GetRequiredService<IDeploymentService>().ExecuteAsync(deployment);
                        db.ChangeTracker.Clear();
                        await Task.Delay(2000, stoppingToken);
                    }
                    }
                    finally
                    {
                        command.CommandText = "EXEC sp_releaseapplock @Resource='DeploymentPortal.Worker', @LockOwner='Session';";
                        await command.ExecuteNonQueryAsync(CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Deployment worker unavailable. Check SQL Server availability and apply migrations."); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
