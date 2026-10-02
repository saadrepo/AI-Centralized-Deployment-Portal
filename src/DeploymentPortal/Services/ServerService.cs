using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace DeploymentPortal.Services;
public interface IServerService
{
    Task<List<Server>> ListAsync();
    Task<Server?> FindAsync(int id);
    Task<int> SaveAsync(ServerForm form);
    Task<ConnectionResult?> TestAsync(int id);
    Task<bool> DeleteAsync(int id);
}
public class ServerService(ApplicationDbContext db, IWinRmService winRm) : IServerService
{
    public Task<List<Server>> ListAsync() => db.Servers.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync();
    public Task<Server?> FindAsync(int id) => db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    public async Task<int> SaveAsync(ServerForm form)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
        System.ComponentModel.DataAnnotations.Validator.ValidateObject(form, new(form), true);
        var incoming = form.ToServer();
        if (await db.Servers.AnyAsync(x => x.Id != form.Id && x.HostNameOrIp == incoming.HostNameOrIp && x.WinRmPort == incoming.WinRmPort))
            throw new ArgumentException("This host and WinRM port are already registered.");
        if (form.Id == 0) db.Servers.Add(incoming);
        else
        {
            var existing = await db.Servers.FindAsync(form.Id) ?? throw new ArgumentException("Server not found.");
            if (await db.Deployments.AnyAsync(x => x.ServerId == form.Id && (x.Status == DeploymentStatus.Running || x.Status == DeploymentStatus.Queued)))
                throw new ArgumentException("Wait for the pending deployment before editing this server.");
            existing.DisplayName = incoming.DisplayName; existing.Environment = incoming.Environment;
            existing.HostNameOrIp = incoming.HostNameOrIp; existing.WinRmPort = incoming.WinRmPort;
            existing.UseSsl = incoming.UseSsl; existing.Username = incoming.Username;
            existing.LastConnectionStatus = ConnectionStatus.Unknown; existing.LastCheckedAt = null; existing.OperatingSystem = "Not checked";
            existing.TotalMemoryBytes = null;
        }
        await db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
        return form.Id == 0 ? incoming.Id : form.Id;
    }
    public async Task<ConnectionResult?> TestAsync(int id)
    {
        var server = await db.Servers.FindAsync(id);
        if (server is null) return null;
        var result = await winRm.GetBasicServerInfoAsync(server);
        server.LastConnectionStatus = result.Success ? ConnectionStatus.Online : ConnectionStatus.Offline;
        server.LastCheckedAt = result.Timestamp;
        if (result.Success)
        {
            server.OperatingSystem = result.OperatingSystem[..Math.Min(result.OperatingSystem.Length, 200)];
            server.TotalMemoryBytes = result.TotalMemoryBytes is > 0 ? result.TotalMemoryBytes : null;
        }
        await db.SaveChangesAsync();
        return result;
    }
    public async Task<bool> DeleteAsync(int id)
    {
        var server = await db.Servers.FindAsync(id);
        if (server is null) return false;
        if (await db.Deployments.AnyAsync(x => x.ServerId == id)) throw new ArgumentException("Servers with deployment history cannot be deleted.");
        db.Servers.Remove(server); await db.SaveChangesAsync(); return true;
    }
}
