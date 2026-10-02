using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DeploymentPortal.Tests;
public class ServerInventoryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectionCheckUpdatesHardwareOnlyOnSuccess(bool success)
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Servers.Add(new Server { Id = 1, OperatingSystem = "Previous OS", TotalMemoryBytes = 8L * 1073741824 });
        await db.SaveChangesAsync();
        var service = new ServerService(db, new InventoryWinRm(success));
        await service.TestAsync(1);
        db.ChangeTracker.Clear();
        var saved = await db.Servers.SingleAsync();
        Assert.Equal(success ? "Windows Server 2022" : "Previous OS", saved.OperatingSystem);
        Assert.Equal((success ? 32L : 8L) * 1073741824, saved.TotalMemoryBytes);
        Assert.NotNull(saved.LastCheckedAt);
        Assert.Equal(success ? ConnectionStatus.Online : ConnectionStatus.Offline, saved.LastConnectionStatus);
    }
    private sealed class InventoryWinRm(bool success) : IWinRmService
    {
        public Task<ConnectionResult> TestConnectionAsync(Server server) => GetBasicServerInfoAsync(server);
        public Task<ConnectionResult> GetBasicServerInfoAsync(Server server) => Task.FromResult(new ConnectionResult(success, "Test result", "server", DateTimeOffset.UtcNow, "Windows Server 2022", 32L * 1073741824));
    }
}
