using DeploymentPortal.Models;
using Microsoft.EntityFrameworkCore;

namespace DeploymentPortal.Data;
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Server>().Property(x => x.LastConnectionStatus).HasConversion<string>().HasMaxLength(20);
        modelBuilder.Entity<Deployment>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        modelBuilder.Entity<Deployment>().Ignore(x => x.Duration);
        modelBuilder.Entity<Deployment>().HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Deployment>().HasIndex(x => x.StartedAt);
        modelBuilder.Entity<Server>().HasIndex(x => new { x.HostNameOrIp, x.WinRmPort }).IsUnique();
    }
}
