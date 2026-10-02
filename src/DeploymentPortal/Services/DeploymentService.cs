using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeploymentPortal.Services;
public interface IDeploymentService
{
    Task<List<Deployment>> ListAsync(int take = 100);
    Task<int> CountAsync();
    Task<Deployment?> FindAsync(Guid id);
    Task<Server> ValidateAsync(DeploymentForm form);
    Task<string> ConfirmAsync(DeploymentForm form);
    Task<Guid> QueueAsync(DeploymentForm form);
    Task ExecuteAsync(Deployment deployment);
}
public class DeploymentService(ApplicationDbContext db, IOptions<AutomationOptions> options, IDataProtectionProvider protection, IWinRmService winRm, ICredentialProvider credentials, IPowerShellService powerShell) : IDeploymentService
{
    private readonly IDataProtector protector = protection.CreateProtector("DeploymentConfirmation.v2");
    private record Confirmation(Guid Id, DateTimeOffset Expires, string Request, string Server, string State, string Mode);
    private static string Request(DeploymentForm f) => JsonSerializer.Serialize(new { f.ServerId, f.Environment, f.ApplicationName, f.SiteName, f.AppPoolName, f.SourcePath, f.TargetPath, f.SitePort, f.SiteHostName });
    private static string Fingerprint(Server s) => JsonSerializer.Serialize(new { s.Id, s.HostNameOrIp, s.WinRmPort, s.UseSsl, s.Username, s.Environment });
    public Task<List<Deployment>> ListAsync(int take = 100) => db.Deployments.AsNoTracking().Include(x => x.Server).OrderByDescending(x => x.StartedAt).Take(take).ToListAsync();
    public Task<int> CountAsync() => db.Deployments.CountAsync();
    public Task<Deployment?> FindAsync(Guid id) => db.Deployments.AsNoTracking().Include(x => x.Server).SingleOrDefaultAsync(x => x.Id == id);
    public async Task<Server> ValidateAsync(DeploymentForm form)
    {
        Validator.ValidateObject(form, new(form), true);
        DeploymentPaths.Validate(form.SourcePath, form.TargetPath, options.Value);
        var server = await db.Servers.FindAsync(form.ServerId) ?? throw new ArgumentException("Select an existing server.");
        if (server.Environment != form.Environment) throw new ArgumentException("Deployment environment must match the selected server.");
        return server;
    }
    public async Task<string> ConfirmAsync(DeploymentForm form)
    {
        var server = await ValidateAsync(form);
        var id = Guid.NewGuid();
        var previewRequest = new Deployment { Id = id, SiteName = form.SiteName, ApplicationName = form.ApplicationName,
            AppPoolName = form.AppPoolName, SourcePath = form.SourcePath, TargetPath = form.TargetPath,
            BackupPath = options.Value.BackupRoot.TrimEnd('\\') + "\\" + id.ToString("N"), SitePort = form.SitePort, SiteHostName = form.SiteHostName };
        RemoteResult preview;
        try { preview = await powerShell.ExecuteAsync(RemoteOperation.PreviewDeployment, server, await credentials.GetAsync(server), previewRequest); }
        catch (InvalidOperationException ex) { throw new ArgumentException(ex.Message); }
        if (!preview.Success) throw new ArgumentException(preview.Message);
        if (string.IsNullOrWhiteSpace(preview.StateFingerprint) || preview.DeploymentMode is not ("CreateSite" or "CreateApplication" or "Update"))
            throw new ArgumentException("Could not verify the IIS deployment plan. Try reviewing again.");
        form.PlanMessage = preview.Message; form.PlanMode = preview.DeploymentMode;
        return protector.Protect(JsonSerializer.Serialize(new Confirmation(id, DateTimeOffset.UtcNow.AddMinutes(10), Request(form), Fingerprint(server), preview.StateFingerprint, preview.DeploymentMode)));
    }
    public async Task<Guid> QueueAsync(DeploymentForm form)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
        var server = await ValidateAsync(form);
        Confirmation? confirmation;
        try { confirmation = JsonSerializer.Deserialize<Confirmation>(protector.Unprotect(form.ConfirmationToken ?? "")); }
        catch { throw new ArgumentException("Review and confirm the deployment again."); }
        if (confirmation is null || confirmation.Expires < DateTimeOffset.UtcNow || confirmation.Request != Request(form) || confirmation.Server != Fingerprint(server))
            throw new ArgumentException("Confirmation expired or deployment details changed. Review the deployment again.");
        if (await db.Deployments.AnyAsync(x => x.Id == confirmation.Id)) return confirmation.Id;
        var deployment = new Deployment
        {
            Id = confirmation.Id, ServerId = server.Id, ApplicationName = form.ApplicationName, Environment = form.Environment,
            SiteName = form.SiteName, AppPoolName = form.AppPoolName, SourcePath = DeploymentPaths.Normalize(form.SourcePath), TargetPath = DeploymentPaths.Normalize(form.TargetPath),
            SitePort = form.SitePort, SiteHostName = form.SiteHostName, ExpectedState = confirmation.State, DeploymentMode = confirmation.Mode,
            BackupPath = options.Value.BackupRoot.TrimEnd('\\') + "\\" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + confirmation.Id.ToString("N"),
            Status = DeploymentStatus.Queued, ResultMessage = "Confirmed and queued for deployment."
        };
        db.Deployments.Add(deployment);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.Entry(deployment).State = EntityState.Detached;
            if (!await db.Deployments.AnyAsync(x => x.Id == confirmation.Id)) throw;
        }
        if (transaction is not null) await transaction.CommitAsync();
        return deployment.Id;
    }
    public async Task ExecuteAsync(Deployment deployment)
    {
        deployment.Status = DeploymentStatus.Running; deployment.StartedAt = DateTimeOffset.UtcNow;
        deployment.ResultMessage = "Checking connectivity and preparing deployment.";
        await db.SaveChangesAsync();
        try
        {
            DeploymentPaths.Validate(deployment.SourcePath, deployment.TargetPath, options.Value);
            if (deployment.Environment != deployment.Server.Environment) throw new ArgumentException("The server environment changed. Create a new deployment.");
            var connection = await winRm.TestConnectionAsync(deployment.Server);
            deployment.Server.LastCheckedAt = connection.Timestamp;
            deployment.Server.LastConnectionStatus = connection.Success ? ConnectionStatus.Online : ConnectionStatus.Offline;
            if (!connection.Success) { deployment.Status = DeploymentStatus.Failed; deployment.ResultMessage = connection.Message; }
            else
            {
                var result = await powerShell.ExecuteAsync(RemoteOperation.Deploy, deployment.Server, await credentials.GetAsync(deployment.Server), deployment);
                deployment.Status = result.Success ? DeploymentStatus.Successful : DeploymentStatus.Failed;
                deployment.ResultMessage = result.Message;
            }
        }
        catch (ArgumentException ex) { deployment.Status = DeploymentStatus.Failed; deployment.ResultMessage = ex.Message; }
        catch { deployment.Status = DeploymentStatus.Failed; deployment.ResultMessage = "Deployment could not complete. Verify the target state and backup before retrying."; }
        deployment.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}
