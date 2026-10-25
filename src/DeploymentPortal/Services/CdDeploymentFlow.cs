using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeploymentPortal.Services;

public sealed record CdTransferFile(string Path, string LocalPath, long Size, string Sha256);
public sealed record CdStageRequest(Guid PreparationId, string RemoteRoot, string SourcePath, string TargetPath,
    string SiteName, string ApplicationName, string? AppPoolName, IReadOnlyList<CdTransferFile> Files, IReadOnlyList<string> PreservedConfigurationFiles);
public sealed record CdRemoteSnapshot(Guid PreparationId, CdPreparation Provenance, int ServerId, string ServerFingerprint,
    string RemoteRoot, string SourcePath, string? AppPoolName, int SitePort, string? SiteHostName,
    string EffectiveManifestJson, string ConfigurationManifestJson, string ConfigurationDigest, string Policy, IReadOnlyList<string> PreservedConfigurationFiles);
public sealed record CdAuditEvent(DateTimeOffset At, string Stage, string Message);

public interface ICdDeploymentFlow
{
    Task StageRemoteAsync(Guid id);
    Task<DeploymentForm> DraftAsync(Guid id);
    Task<CdRemoteSnapshot?> ValidateAsync(DeploymentForm form, Server server, bool forExecution = false);
    void ValidateManualPaths(string source, string target);
    Task<CdRemoteSnapshot> LoadForExecutionAsync(Deployment deployment);
}

public sealed class CdDeploymentFlow(ApplicationDbContext db, ITfsCdPreparation preparations, IServerService servers,
    ISiteRegistry registry, IPowerShellService powerShell, ICredentialProvider credentials, IOptions<TfsCdOptions> options,
    IOptions<AutomationOptions> automation, IDataProtectionProvider protection) : ICdDeploymentFlow
{
    private readonly IDataProtector protector = protection.CreateProtector("CdDeploymentSnapshot.v1");
    public const string Policy = "PreserveExistingRequired.v1";
    private void RequireEnabled()
    {
        if (!options.Value.Enabled || !options.Value.ExecutionEnabled) throw new CdPreparationException("CD execution integration is disabled. No remote operation or deployment was started.");
    }
    public static string Fingerprint(Server server) => JsonSerializer.Serialize(new { server.Id, server.HostNameOrIp, server.Environment, server.WinRmPort, server.UseSsl, server.Username });
    public static string Digest(CdRemoteSnapshot snapshot) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot))));
    public static void Audit(CdDeploymentSource source, string stage, string message)
    {
        var events = JsonSerializer.Deserialize<List<CdAuditEvent>>(source.AuditJson) ?? [];
        events.Add(new(DateTimeOffset.UtcNow, stage, message));
        source.AuditJson = JsonSerializer.Serialize(events);
    }
    public async Task StageRemoteAsync(Guid id)
    {
        RequireEnabled();
        if (!await CdSchema.AvailableAsync(db)) throw new CdPreparationException("The controlled CD migration must be applied by an administrator before CD can be enabled.");
        if (await db.CdDeploymentSources.AnyAsync(x => x.PreparationId == id)) throw new CdPreparationException("This preparation has already been staged. Use its Review link or prepare a new package.");
        var local = await preparations.FindAsync(id);
        var selection = await preparations.SelectAsync(local.ApplicationKey, local.BuildId, local.ServerHost);
        if (local.DefinitionId != selection.Application.TfsGitCiDefinitionId || local.ProjectName != selection.Application.TfsGitProject || local.RepositoryName != selection.Application.TfsGitRepository)
            throw new CdPreparationException("Preparation application CI mapping changed. Prepare again.");
        if (local.BuildId != selection.Listing.Build.Id || local.Branch != selection.Listing.Build.SourceBranch || local.RepositoryId != selection.Listing.Build.Repository?.Id || local.Artifact != selection.Listing.Artifact || local.Commit != selection.Listing.Build.SourceVersion || local.ContainerId != selection.Listing.ContainerId || local.ContainerRoot != selection.Listing.ContainerRoot || local.SiteName != selection.Destination.SiteName || local.TargetPath != selection.Destination.TargetPath || local.ApplicationPath != selection.Destination.ApplicationName || local.Environment != selection.Server.Environment)
            throw new CdPreparationException("Preparation provenance or destination changed. Prepare again.");
        var root = DeploymentPaths.Normalize(options.Value.RemoteStagingRoot);
        var sourcePath = root + "\\" + id.ToString("N") + "\\publish";
        DeploymentPaths.Validate(sourcePath, local.TargetPath, automation.Value);
        var files = await preparations.TransferFilesAsync(id);
        var preserved = selection.Destination.PreservedConfigurationFiles.ToArray();
        foreach (var path in preserved) TfsArtifactExplorer.ValidatePath(path.Replace('\\', '/'));
        var result = await powerShell.StageCdAsync(selection.Server, await credentials.GetAsync(selection.Server), new(id, root, sourcePath, local.TargetPath, local.SiteName, local.ApplicationPath, selection.Destination.ApplicationPool, files, preserved));
        if (!result.Success || result.DeploymentMode != "Update" || string.IsNullOrWhiteSpace(result.EffectiveManifestJson) || string.IsNullOrWhiteSpace(result.ConfigurationManifestJson) || string.IsNullOrWhiteSpace(result.ConfigurationDigest))
            throw new CdPreparationException(result.Success ? "Remote package validation was incomplete. No deployment was queued." : result.Message);
        ValidateRemoteManifest(result);
        var snapshot = new CdRemoteSnapshot(id, local, selection.Server.Id, Fingerprint(selection.Server), root, sourcePath,
            selection.Destination.ApplicationPool, selection.Destination.SitePort, selection.Destination.SiteHostName,
            result.EffectiveManifestJson, result.ConfigurationManifestJson, result.ConfigurationDigest, Policy, preserved);
        var currentServer = await servers.FindAsync(selection.Server.Id);
        if (currentServer is null || Fingerprint(currentServer) != snapshot.ServerFingerprint) throw new CdPreparationException("Server registration changed during staging. Prepare again.");
        var row = new CdDeploymentSource { PreparationId = id, BuildId = local.BuildId, BuildNumber = local.BuildNumber,
            Branch = local.Branch, Commit = local.Commit, Artifact = local.Artifact, Expires = local.Expires,
            ProtectedSnapshot = protector.Protect(JsonSerializer.Serialize(snapshot)) };
        Audit(row, "RemotePrepared", "Verified artifact staged privately; target settings preserved. No deployment approved.");
        db.CdDeploymentSources.Add(row);
        await db.SaveChangesAsync();
    }
    public static void ValidateRemoteManifest(RemoteResult result)
    {
        try
        {
            Dictionary<string, (long Size, string Hash)> Read(string json)
            {
                if (json.Length > 1024 * 1024) throw new JsonException();
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 or > 2000) throw new JsonException();
                var files = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (var file in document.RootElement.EnumerateArray())
                {
                    var path = file.GetProperty("Path").GetString() ?? throw new JsonException();
                    TfsArtifactExplorer.ValidatePath(path.Replace('\\', '/'));
                    var size = file.GetProperty("Size").GetInt64();
                    var hash = file.GetProperty("Sha256").GetString() ?? throw new JsonException();
                    if (size is < 0 or > 67108864 || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)) || !files.TryAdd(path, (size, hash))) throw new JsonException();
                    total += size;
                    if (total > 268435456) throw new JsonException();
                }
                return files;
            }
            var effective = Read(result.EffectiveManifestJson!);
            var configuration = Read(result.ConfigurationManifestJson!);
            if (!configuration.ContainsKey("appsettings.json") || !configuration.ContainsKey("web.config") || configuration.Any(x => !effective.TryGetValue(x.Key, out var file) || file != x.Value)) throw new JsonException();
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.ConfigurationManifestJson!)));
            if (!digest.Equals(result.ConfigurationDigest, StringComparison.OrdinalIgnoreCase)) throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new CdPreparationException("Remote package/configuration integrity evidence is invalid. No deployment was queued.");
        }
    }
    private async Task<(CdDeploymentSource Row, CdRemoteSnapshot Snapshot)> LoadAsync(Guid id, bool queued)
    {
        RequireEnabled();
        if (!await CdSchema.AvailableAsync(db)) throw new CdPreparationException("The controlled CD migration is not applied.");
        var row = await db.CdDeploymentSources.SingleOrDefaultAsync(x => x.PreparationId == id) ?? throw new CdPreparationException("Remote preparation was not found. Stage it first.");
        CdRemoteSnapshot snapshot;
        try { snapshot = JsonSerializer.Deserialize<CdRemoteSnapshot>(protector.Unprotect(row.ProtectedSnapshot)) ?? throw new JsonException(); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { throw new CdPreparationException("Protected CD provenance is invalid. Prepare again."); }
        if (snapshot.PreparationId != id || snapshot.Policy != Policy || row.BuildId != snapshot.Provenance.BuildId || row.BuildNumber != snapshot.Provenance.BuildNumber || row.Commit != snapshot.Provenance.Commit || row.Artifact != snapshot.Provenance.Artifact || row.Branch != snapshot.Provenance.Branch || row.Expires != snapshot.Provenance.Expires || (!queued && row.Expires < DateTimeOffset.UtcNow)) throw new CdPreparationException("CD preparation expired or its provenance/policy changed.");
        return (row, snapshot);
    }
    public async Task<DeploymentForm> DraftAsync(Guid id)
    {
        var (_, snapshot) = await LoadAsync(id, false);
        return Form(snapshot);
    }
    private static DeploymentForm Form(CdRemoteSnapshot s) => new() { PreparationId = s.PreparationId, ServerId = s.ServerId,
        Environment = s.Provenance.Environment, SiteName = s.Provenance.SiteName, ApplicationName = s.Provenance.ApplicationPath,
        AppPoolName = s.AppPoolName, SourcePath = s.SourcePath, TargetPath = s.Provenance.TargetPath, SitePort = s.SitePort,
        SiteHostName = s.SiteHostName, CdProvenance = s };
    private static string Fields(DeploymentForm f) => JsonSerializer.Serialize(new { f.PreparationId, f.ServerId, f.Environment, f.SiteName, f.ApplicationName, f.AppPoolName, f.SourcePath, f.TargetPath, f.SitePort, f.SiteHostName });
    public async Task<CdRemoteSnapshot?> ValidateAsync(DeploymentForm form, Server server, bool forExecution = false)
    {
        if (form.PreparationId is not { } id) { ValidateManualPaths(form.SourcePath, form.TargetPath); form.CdProvenance = null; return null; }
        var (_, snapshot) = await LoadAsync(id, forExecution);
        if (Fingerprint(server) != snapshot.ServerFingerprint || Fields(form) != Fields(Form(snapshot))) throw new CdPreparationException("CD destination or request changed. Prepare and Review again.");
        var app = registry.Find(snapshot.Provenance.ApplicationKey);
        var destinations = app?.Destinations.Where(x => x.Matches(server)).ToList();
        if (app is null || !TfsApplicationScope.IsConfigured(app) || app.TfsGitCiDefinitionId != snapshot.Provenance.DefinitionId || app.TfsGitProject != snapshot.Provenance.ProjectName || app.TfsGitRepository != snapshot.Provenance.RepositoryName || app.TfsGitCiBranch != snapshot.Provenance.Branch || app.TfsGitArtifactName != snapshot.Provenance.Artifact || destinations?.Count != 1 || destinations[0].SiteName != form.SiteName || destinations[0].TargetPath != form.TargetPath || destinations[0].ApplicationName != form.ApplicationName || destinations[0].ApplicationPool != form.AppPoolName || destinations[0].SitePort != form.SitePort || destinations[0].SiteHostName != form.SiteHostName || snapshot.RemoteRoot != DeploymentPaths.Normalize(options.Value.RemoteStagingRoot))
            throw new CdPreparationException("CD registry mapping or staging policy changed. Prepare again.");
        form.CdProvenance = snapshot;
        if (!destinations[0].PreservedConfigurationFiles.SequenceEqual(snapshot.PreservedConfigurationFiles)) throw new CdPreparationException("Registered configuration preservation paths changed. Prepare again.");
        return snapshot;
    }
    public void ValidateManualPaths(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(options.Value.RemoteStagingRoot)) return;
        var root = DeploymentPaths.Normalize(options.Value.RemoteStagingRoot);
        if (new[] { source, target }.Any(x => x.Equals(root, StringComparison.OrdinalIgnoreCase) || DeploymentPaths.IsChild(x, root) || DeploymentPaths.IsChild(root, x)))
            throw new CdPreparationException("Reserved CD staging paths require protected CD provenance.");
    }
    public async Task<CdRemoteSnapshot> LoadForExecutionAsync(Deployment deployment)
    {
        var source = deployment.CdSource ?? throw new CdPreparationException("CD audit source is missing.");
        if (source.DeploymentId != deployment.Id || source.ApprovedAt is null) throw new CdPreparationException("CD approval has not been consumed by this deployment.");
        var (_, snapshot) = await LoadAsync(source.PreparationId, true);
        var form = Form(snapshot);
        form.ServerId = deployment.ServerId; form.SourcePath = deployment.SourcePath; form.TargetPath = deployment.TargetPath;
        form.Environment = deployment.Environment; form.SiteName = deployment.SiteName; form.ApplicationName = deployment.ApplicationName;
        form.AppPoolName = deployment.AppPoolName; form.SitePort = deployment.SitePort; form.SiteHostName = deployment.SiteHostName;
        return (await ValidateAsync(form, deployment.Server, true))!;
    }
}
