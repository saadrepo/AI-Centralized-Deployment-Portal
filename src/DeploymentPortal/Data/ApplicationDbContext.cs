using DeploymentPortal.Models;
using Microsoft.EntityFrameworkCore;

namespace DeploymentPortal.Data;
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<InfrastructureSource> InfrastructureSources => Set<InfrastructureSource>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<CdDeploymentSource> CdDeploymentSources => Set<CdDeploymentSource>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InfrastructureSource>().Property(x => x.Provider).HasConversion<string>().HasMaxLength(20);
        modelBuilder.Entity<InfrastructureSource>().HasIndex(x => new { x.Provider, x.Destination }).IsUnique();
        modelBuilder.Entity<Server>().Property(x => x.LastConnectionStatus).HasConversion<string>().HasMaxLength(20);
        modelBuilder.Entity<Deployment>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        modelBuilder.Entity<Deployment>().Ignore(x => x.Duration);
        modelBuilder.Entity<Deployment>().HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Deployment>().HasIndex(x => x.StartedAt);
        modelBuilder.Entity<Server>().HasIndex(x => new { x.HostNameOrIp, x.WinRmPort }).IsUnique();
        modelBuilder.Entity<CdDeploymentSource>().HasKey(x => x.PreparationId);
        modelBuilder.Entity<CdDeploymentSource>().HasOne(x => x.Deployment).WithOne(x => x.CdSource).HasForeignKey<CdDeploymentSource>(x => x.DeploymentId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CdDeploymentSource>().HasIndex(x => x.DeploymentId).IsUnique().HasFilter("[DeploymentId] IS NOT NULL");
    }
}
