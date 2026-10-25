using DeploymentPortal.Data;
using DeploymentPortal.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DeploymentPortal.Tests;

public class CloudMigrationTests
{
    [Fact]
    public void SnapshotMatchesModelAndMigrationOnlyCreatesCloudExecutionStorage()
    {
        // Metadata and SQL generation only: no connection is opened or migration applied.
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=unused.invalid;Database=MetadataOnly;Integrated Security=true;Encrypt=true").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToArray();
        const string current = "20261024174124_AddCloudExecution";
        var index = Array.IndexOf(migrations, current);
        Assert.True(index > 0);
        var sql = db.GetService<IMigrator>().GenerateScript(migrations[index - 1], current);
        Assert.Contains("CREATE TABLE [CloudExecutions]", sql);
        Assert.Contains("[RowVersion] rowversion", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_CloudExecutions_UniqueScope]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_CloudExecutions_DeploymentId]", sql);
        Assert.DoesNotContain("ALTER TABLE", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.True(db.Model.FindEntityType(typeof(Deployment))!.FindProperty(nameof(Deployment.Status))!.GetMaxLength() >= "CloudRunning".Length);
        var currentQueue = db.Deployments.Where(d => d.Status == DeploymentStatus.CloudQueued).ToQueryString();
        var legacyQueue = db.Deployments.Where(d => d.Status == DeploymentStatus.Queued).ToQueryString();
        Assert.Contains("N'CloudQueued'", currentQueue);
        Assert.Contains("N'Queued'", legacyQueue);
        Assert.DoesNotContain("CloudQueued", legacyQueue);
    }
}
