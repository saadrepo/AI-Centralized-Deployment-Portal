using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using DeploymentPortal.Data;
using DeploymentPortal.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeploymentPortal.Services;

public sealed class CloudExecutionFlow(ApplicationDbContext db, ISiteRegistry registry, IInfrastructureSourceService sources,
    ICloudProvider provider, ICloudPackageStager packages, IPowerShellService powershell, ICredentialProvider credentials,
    IOptions<CloudExecutionOptions> options, IOptions<AutomationOptions> automation, IDataProtectionProvider protection) : ICloudDeploymentGuard
{
    public const string Mode = "CloudUpdate";
    // Fixed stripes bound memory; SQL application locks retain exact cross-process scope.
    private static readonly SemaphoreSlim[] locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly IDataProtector data = protection.CreateProtector("CloudExecution.Data.v1");
    private readonly IDataProtector queueTickets = protection.CreateProtector("CloudExecution.Queue.v1");
    private readonly IDataProtector prepareTickets = protection.CreateProtector("CloudExecution.Prepare.v1");
    private readonly IDataProtector approvalTokens = protection.CreateProtector("CloudExecution.Approve.v1");
    private void Gate(bool deploy) => CloudSecurity.Require(CloudSecurity.Live(options.Value) &&
        (deploy ? options.Value.DeploymentExecutionEnabled : options.Value.BuildExecutionEnabled), "LIVE_EXECUTION_NOT_AUTHORIZED");
    private void WorkerGate() => CloudSecurity.Require(options.Value.WorkerCompatibilityConfirmed &&
        options.Value.WorkerHostName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase), "CLOUD_WORKER_COMPATIBILITY_REQUIRED");
    private async Task Schema()
    {
        CloudSecurity.Require(!db.Database.IsRelational() || (await db.Database.GetAppliedMigrationsAsync()).Any(x => x.EndsWith("_AddCloudExecution", StringComparison.Ordinal)), "CLOUD_MIGRATION_REQUIRED");
    }
    private T Unprotect<T>(IDataProtector protector, string token)
    {
        try { return JsonSerializer.Deserialize<T>(protector.Unprotect(token)) ?? throw new JsonException(); }
        catch { throw new CloudExecutionException("PROTECTED_BINDING_INVALID"); }
    }
    private async Task<CloudBinding> Bind(CloudSelection s, string sid, bool approve)
    {
        CloudSecurity.Require(s.Provider == "GitHub", "AZURE_EXECUTION_NOT_IMPLEMENTED");
        CloudSecurity.Require(s.Provider is "GitHub" or "AzureDevOps" && s.SourceId > 0 && s.DefinitionId > 0 && SourceExplorerRequestPolicy.BranchName(s.Branch), "SELECTION");
        CloudSecurity.Require(s.Provider == "GitHub" ? SourceExplorerRequestPolicy.RepositoryName(s.Repository) : Guid.TryParseExact(s.Project, "D", out _) && Guid.TryParseExact(s.Repository, "D", out _), "REPOSITORY");
        var mappings = options.Value.Mappings.Where(m => m.ApplicationKey == s.ApplicationKey && m.Provider == s.Provider && m.SourceId == s.SourceId &&
            m.Project == s.Project && m.Repository == s.Repository && m.Branch == s.Branch && m.DefinitionId == s.DefinitionId).ToArray();
        CloudSecurity.Require(mappings.Length == 1, "EXPLICIT_MAPPING_REQUIRED"); var mapping = mappings[0];
        var allowed = approve ? mapping.ApproverSids : mapping.BuilderSids;
        if (allowed.Length == 0 || allowed.Any(x => !SourcesManagementAuthorization.IsValidUserSid(x)) || !allowed.Contains(sid, StringComparer.Ordinal)) throw new UnauthorizedAccessException();
        var app = registry.Find(s.ApplicationKey) ?? throw new CloudExecutionException("APPLICATION");
        var source = await sources.FindAsync(s.SourceId) ?? throw new CloudExecutionException("SOURCE");
        CloudSecurity.Require(source.Enabled && source.Provider == (s.Provider == "GitHub" ? InfrastructureSourceProvider.GitHub : InfrastructureSourceProvider.AzureDevOps), "SOURCE_PROVIDER");
        var server = await db.Servers.FindAsync(mapping.ServerId) ?? throw new CloudExecutionException("TARGET");
        if (approve) CloudSecurity.Require(!await db.CloudExecutions.AnyAsync(r => r.Kind == "Deploy" && r.ProtectedData == "" &&
            r.DeploymentId != null && db.Deployments.Any(d => d.Id == r.DeploymentId && d.ServerId == server.Id)), "CLOUD_RECOVERY_INSPECTION_REQUIRED");
        var targets = app.Destinations.Where(d => d.Matches(server)).ToArray();
        CloudSecurity.Require(server.UseSsl && server.Environment == mapping.Environment && targets.Length == 1 && !string.IsNullOrWhiteSpace(targets[0].ApplicationPool), "TARGET_SCOPE");
        CloudSecurity.Require(System.Text.RegularExpressions.Regex.IsMatch(mapping.ArtifactName, @"\A[A-Za-z0-9_.-]{1,100}\z"), "ARTIFACT_MAPPING");
        CloudSecurity.Require(Uri.TryCreate(mapping.HealthUrl, UriKind.Absolute, out var health) && health.Scheme == "https" && health.Port == 443 &&
            health.UserInfo.Length == 0 && health.Fragment.Length == 0 && health.Query.Length == 0 &&
            health.Host.Equals(string.IsNullOrWhiteSpace(targets[0].SiteHostName) ? server.HostNameOrIp : targets[0].SiteHostName, StringComparison.OrdinalIgnoreCase), "HEALTH_MAPPING");
        var fingerprint = CloudSecurity.Hash(new { mapping, source.Id, source.Provider, source.Destination, source.CredentialReference, source.Enabled,
            server = CdDeploymentFlow.Fingerprint(server), target = targets[0], options.Value.LocalStagingRoot, options.Value.RemoteStagingRoot,
            options.Value.WorkerCompatibilityConfirmed, options.Value.WorkerHostName, automation.Value.BackupRoot, options.Value.LiveAuthorizationReference });
        return new(mapping, source, targets[0], server, fingerprint);
    }
    private async Task<T> Locked<T>(string scope, Func<Task<T>> action)
    {
        var gate = locks[(uint)StringComparer.Ordinal.GetHashCode(scope) % (uint)locks.Length]; await gate.WaitAsync();
        try {
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
            if (transaction is not null) {
                var resource = "CloudExecution:" + scope;
                await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=0; IF @r < 0 THROW 51000, 'Cloud operation busy', 1;");
            }
            var result = await action(); if (transaction is not null) await transaction.CommitAsync(); return result;
        } finally { gate.Release(); }
    }
    public async Task<object> ReadinessAsync(CloudSelection selection, ClaimsPrincipal user)
    {
        var sid = CloudSecurity.Sid(user); var config = options.Value;
        if (selection.Provider == "AzureDevOps") return new { canQueue = false, canApprove = false, reason = "Azure DevOps execution is not implemented. Read-only browsing remains available." };
        if (!CloudSecurity.Live(config) || !config.BuildExecutionEnabled && !config.DeploymentExecutionEnabled)
            return new { canQueue = false, canApprove = false, reason = "Cloud execution requires separate, unexpired operational authorization." };
        await Schema(); string? queue = null, prepare = null; CloudBinding? binding = null;
        if (config.BuildExecutionEnabled) {
            try { binding = await Bind(selection, sid, false); queue = queueTickets.Protect(JsonSerializer.Serialize(new CloudTicket(Guid.NewGuid(), selection with { RunId = 0 }, binding.Fingerprint, sid, DateTimeOffset.UtcNow.AddMinutes(5)))); }
            catch (UnauthorizedAccessException) { }
        }
        if (config.DeploymentExecutionEnabled && config.WorkerCompatibilityConfirmed &&
            config.WorkerHostName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && selection.RunId > 0) {
            try { binding = await Bind(selection, sid, true); prepare = prepareTickets.Protect(JsonSerializer.Serialize(new CloudTicket(Guid.NewGuid(), selection, binding.Fingerprint, sid, DateTimeOffset.UtcNow.AddMinutes(5)))); }
            catch (UnauthorizedAccessException) { }
        }
        Guid? operationId = null; string? operationState = null;
        var recent = await db.CloudExecutions.Where(r => r.ActorSid == sid && r.UniqueScope != null).OrderByDescending(r => r.CreatedAt).Take(50).ToListAsync();
        foreach (var row in recent) {
            var payload = Unprotect<CloudExecutionData>(data, row.ProtectedData);
            if (payload.Selection with { RunId = 0 } != selection with { RunId = 0 } || row.Kind == "Deploy" && payload.Selection.RunId != selection.RunId) continue;
            operationId = row.Id; operationState = row.State;
            if (row.Kind == "Build") queue = null;
            if (row.Kind == "Deploy" && row.State != "Prepared") prepare = null;
            break;
        }
        return new { canQueue = queue is not null, canApprove = prepare is not null, queueTicket = queue, prepareTicket = prepare,
            operationId, operationState, target = binding is null ? null : new { binding.Server.Id, binding.Server.DisplayName, binding.Server.Environment, binding.Target.SiteName, binding.Target.ApplicationName },
            reason = "Execution is limited to the selected configured mapping. Approval requires a successful run and an exact artifact review." };
    }
    public async Task<object> QueueAsync(string ticket, ClaimsPrincipal user, CancellationToken ct)
    {
        Gate(false); await Schema(); var sid = CloudSecurity.Sid(user); var request = Unprotect<CloudTicket>(queueTickets, ticket);
        CloudSecurity.Require(request.ActorSid == sid && request.Expires > DateTimeOffset.UtcNow, "BUILD_TICKET_EXPIRED");
        var binding = await Bind(request.Selection, sid, false); CloudSecurity.Require(binding.Fingerprint == request.Binding, "MAPPING_CHANGED");
        var scope = CloudSecurity.Hash(new { action = "Build", request.Selection.ApplicationKey, request.Selection.Provider, request.Selection.SourceId, request.Selection.Project, request.Selection.Repository, request.Selection.Branch, request.Selection.DefinitionId });
        var started = await Locked(scope, async () => {
            var existing = await db.CloudExecutions.FindAsync(request.Id); if (existing is not null) return false;
            CloudSecurity.Require(!await db.CloudExecutions.AnyAsync(r => r.UniqueScope == scope), "BUILD_ALREADY_ACTIVE_OR_UNCONFIRMED");
            var row = new CloudExecutionRecord { Id = request.Id, Kind = "Build", UniqueScope = scope, ActorSid = sid, State = "Dispatching", ProtectedData = data.Protect(JsonSerializer.Serialize(new CloudExecutionData(request.Selection, binding.Fingerprint))) };
            CloudSecurity.Audit(row, "DispatchIntent", sid); db.CloudExecutions.Add(row); await db.SaveChangesAsync(); return true;
        });
        var record = (await db.CloudExecutions.FindAsync(request.Id))!;
        CloudSecurity.Require(record.ActorSid == sid, "BUILD_OWNER");
        if (started) {
            try { Gate(false); record.RunId = await provider.QueueAsync(binding, request.Selection, ct); record.State = "RunAccepted"; }
            catch { record.State = "Unconfirmed"; }
            CloudSecurity.Audit(record, record.State, sid); await db.SaveChangesAsync(CancellationToken.None);
        }
        return new { operationId = record.Id, record.RunId, record.State };
    }
    private object PreparedResponse(CloudExecutionRecord row, CloudPrepared? prepared)
    {
        var token = row.State == "Prepared" && prepared is not null ? approvalTokens.Protect(JsonSerializer.Serialize(new CloudApproval(row.Id, CloudSecurity.Hash(prepared), prepared.ApproverSid, prepared.Expires))) : null;
        return new { operationId = row.Id, row.State, approvalToken = token, evidence = prepared is null ? null : new {
            prepared.Selection.Provider, prepared.Selection.Repository, prepared.Selection.Branch, prepared.Selection.RunId,
            prepared.Evidence.Run.Commit, prepared.Evidence.Run.Attempt, prepared.Evidence.Artifact.Name, prepared.Evidence.Artifact.Id,
            archiveSha256 = prepared.ArchiveHash, prepared.Remote.Provenance.Environment, prepared.Remote.Provenance.ServerHost,
            prepared.Remote.Provenance.SiteName, prepared.Remote.Provenance.ApplicationPath, prepared.Expires } };
    }
    private static void RequireEvidence(CloudBinding binding, CloudSelection selection, CloudEvidence evidence)
    {
        CloudSecurity.Require(evidence.Run.Id == selection.RunId && evidence.Run.Status == "completed" &&
            evidence.Run.Result == (selection.Provider == "GitHub" ? "success" : "succeeded") && evidence.Run.Commit.Length is 40 or 64 &&
            evidence.Run.Commit.All(Uri.IsHexDigit) && evidence.Artifact.Id > 0 && evidence.Artifact.Name == binding.Mapping.ArtifactName, "SUCCESSFUL_BOUND_ARTIFACT_REQUIRED");
    }
    public async Task<object> PrepareAsync(string ticket, ClaimsPrincipal user, CancellationToken ct)
    {
        Gate(true); WorkerGate(); await Schema(); var sid = CloudSecurity.Sid(user); var request = Unprotect<CloudTicket>(prepareTickets, ticket);
        CloudSecurity.Require(request.ActorSid == sid && request.Expires > DateTimeOffset.UtcNow && request.Selection.RunId > 0, "PREPARE_TICKET_EXPIRED");
        var binding = await Bind(request.Selection, sid, true); CloudSecurity.Require(binding.Fingerprint == request.Binding, "MAPPING_CHANGED");
        var evidence = await provider.EvidenceAsync(binding, request.Selection, ct);
        RequireEvidence(binding, request.Selection, evidence);
        var scope = CloudSecurity.Hash(new { action = "Deploy", request.Selection.Provider, request.Selection.SourceId, request.Selection.Project, request.Selection.Repository,
            request.Selection.Branch, request.Selection.RunId, evidence.Run.Attempt, artifactId = evidence.Artifact.Id, serverId = binding.Server.Id, binding.Target.TargetPath, binding.Server.Environment });
        var record = await Locked(scope, async () => {
            var existing = await db.CloudExecutions.SingleOrDefaultAsync(r => r.UniqueScope == scope); if (existing is not null) return existing;
            var row = new CloudExecutionRecord { Id = request.Id, Kind = "Deploy", UniqueScope = scope, ActorSid = sid, State = "Preparing", RunId = request.Selection.RunId,
                ProtectedData = data.Protect(JsonSerializer.Serialize(new CloudExecutionData(request.Selection, binding.Fingerprint))) };
            CloudSecurity.Audit(row, "PreparationIntent", sid); db.CloudExecutions.Add(row); await db.SaveChangesAsync(); return row;
        });
        CloudSecurity.Require(record.ActorSid == sid, "APPROVAL_OWNER");
        if (record.Id != request.Id || record.State != "Preparing") return PreparedResponse(record, Unprotect<CloudExecutionData>(data, record.ProtectedData).Preparation);
        // A second request using the same ticket must never repeat remote staging.
        var claim = await Locked(record.Id.ToString(), async () => {
            await db.Entry(record).ReloadAsync();
            if (record.State != "Preparing") return false;
            record.State = "Staging"; await db.SaveChangesAsync(); return true;
        });
        if (!claim) return PreparedResponse(record, null);
        try {
            var bytes = await provider.DownloadAsync(binding, request.Selection, evidence, ct);
            var archiveHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (request.Selection.Provider == "GitHub") CloudSecurity.Require(evidence.Artifact.Digest.Equals("sha256:" + archiveHash, StringComparison.OrdinalIgnoreCase), "ARCHIVE_DIGEST");
            var files = await packages.StageAsync(record.Id, bytes, binding.Mapping, ct);
            CloudSecurity.Require(await provider.EvidenceAsync(binding, request.Selection, ct) == evidence, "ARTIFACT_CHANGED_DURING_DOWNLOAD");
            Gate(true); CloudSecurity.Require((await Bind(request.Selection, sid, true)).Fingerprint == binding.Fingerprint, "MAPPING_CHANGED");
            var root = DeploymentPaths.Normalize(options.Value.RemoteStagingRoot); var path = root + "\\" + record.Id.ToString("N") + "\\publish";
            DeploymentPaths.Validate(path, binding.Target.TargetPath, automation.Value);
            var result = await powershell.StageCdAsync(binding.Server, await credentials.GetAsync(binding.Server), new(record.Id, root, path,
                binding.Target.TargetPath, binding.Target.SiteName, binding.Target.ApplicationName, binding.Target.ApplicationPool, files, binding.Target.PreservedConfigurationFiles));
            CloudSecurity.Require(result.Success && result.DeploymentMode == "Update", "REMOTE_PREPARATION"); CdDeploymentFlow.ValidateRemoteManifest(result);
            var expires = DateTimeOffset.UtcNow.AddMinutes(10);
            // Remote engine uses this only for path/configuration verification. Cloud identity stays in the protected envelope.
            var provenance = new CdPreparation(record.Id, expires, request.Selection.ApplicationKey, binding.Server.HostNameOrIp, binding.Server.Environment,
                binding.Target.SiteName, binding.Target.ApplicationName, binding.Target.TargetPath, 0, evidence.Run.Number, request.Selection.Branch,
                evidence.Run.Commit, request.Selection.Repository, evidence.Artifact.Name, 0, "", "", archiveHash,
                files.Select(f => new CdFile(f.Path, f.Size, f.Sha256)).ToList(), "CloudPrepared", 0, request.Selection.Project ?? "", request.Selection.Repository);
            var snapshot = new CdRemoteSnapshot(record.Id, provenance, binding.Server.Id, CdDeploymentFlow.Fingerprint(binding.Server), root, path,
                binding.Target.ApplicationPool, binding.Target.SitePort, binding.Target.SiteHostName, result.EffectiveManifestJson!, result.ConfigurationManifestJson!,
                result.ConfigurationDigest!, CdDeploymentFlow.Policy, binding.Target.PreservedConfigurationFiles.ToArray());
            var preview = await powershell.ExecuteAsync(RemoteOperation.PreviewDeployment, binding.Server, await credentials.GetAsync(binding.Server),
                new Deployment { Id = record.Id, SiteName = binding.Target.SiteName, ApplicationName = binding.Target.ApplicationName, AppPoolName = binding.Target.ApplicationPool,
                    SourcePath = path, TargetPath = binding.Target.TargetPath, BackupPath = automation.Value.BackupRoot.TrimEnd('\\') + "\\" + record.Id.ToString("N"),
                    SitePort = binding.Target.SitePort, SiteHostName = binding.Target.SiteHostName, CdPackage = snapshot });
            CloudSecurity.Require(preview.Success && preview.DeploymentMode == "Update" && preview.StateFingerprint is { Length: 64 } && preview.StateFingerprint.All(Uri.IsHexDigit), "IIS_PREVIEW");
            CloudSecurity.Require((await Bind(request.Selection, sid, true)).Fingerprint == binding.Fingerprint, "MAPPING_CHANGED");
            var prepared = new CloudPrepared(request.Selection, binding.Fingerprint, evidence, archiveHash, snapshot, preview.StateFingerprint!, expires, binding.Mapping.HealthUrl, sid);
            record.ProtectedData = data.Protect(JsonSerializer.Serialize(new CloudExecutionData(request.Selection, binding.Fingerprint, prepared)));
            record.State = "Prepared"; CloudSecurity.Audit(record, "PreparedNotApproved", sid); await db.SaveChangesAsync(ct);
            return PreparedResponse(record, prepared);
        } catch {
            record.State = "PreparationFailed"; CloudSecurity.Audit(record, "PreparationFailedNoDeployment", sid); await db.SaveChangesAsync(CancellationToken.None);
            throw new CloudExecutionException("PREPARATION_FAILED_NO_DEPLOYMENT");
        }
    }
    public async Task<object> ApproveAsync(string token, ClaimsPrincipal user, CancellationToken ct)
    {
        Gate(true); WorkerGate(); await Schema(); var sid = CloudSecurity.Sid(user); var approval = Unprotect<CloudApproval>(approvalTokens, token);
        CloudSecurity.Require(approval.Sid == sid && approval.Expires > DateTimeOffset.UtcNow, "APPROVAL_EXPIRED");
        return await Locked(approval.Id.ToString(), async () => {
            var row = await db.CloudExecutions.FindAsync(approval.Id) ?? throw new CloudExecutionException("APPROVAL_MISSING");
            var prepared = Unprotect<CloudExecutionData>(data, row.ProtectedData).Preparation ?? throw new CloudExecutionException("PREPARATION_MISSING");
            CloudSecurity.Require(row.ActorSid == sid && approval.Digest == CloudSecurity.Hash(prepared) && prepared.Expires > DateTimeOffset.UtcNow, "APPROVAL_BINDING");
            var binding = await Bind(prepared.Selection, sid, true); CloudSecurity.Require(binding.Fingerprint == prepared.Binding, "MAPPING_CHANGED");
            if (row.DeploymentId is { } existing) return (object)new { operationId = row.Id, deploymentId = existing, state = row.State };
            CloudSecurity.Require(row.State == "Prepared" && await provider.EvidenceAsync(binding, prepared.Selection, ct) == prepared.Evidence, "STALE_RUN_OR_ARTIFACT");
            Gate(true);
            var snapshot = prepared.Remote;
            var deployment = new Deployment { Id = row.Id, ServerId = binding.Server.Id, Environment = binding.Server.Environment,
                SiteName = snapshot.Provenance.SiteName, ApplicationName = snapshot.Provenance.ApplicationPath, AppPoolName = snapshot.AppPoolName,
                SourcePath = snapshot.SourcePath, TargetPath = snapshot.Provenance.TargetPath, SitePort = snapshot.SitePort, SiteHostName = snapshot.SiteHostName,
                BackupPath = automation.Value.BackupRoot.TrimEnd('\\') + "\\" + row.Id.ToString("N"), ExpectedState = prepared.ExpectedState,
                DeploymentMode = Mode, Status = DeploymentStatus.CloudQueued, ResultMessage = "Cloud artifact approved for one controlled deployment." };
            db.Deployments.Add(deployment); row.DeploymentId = deployment.Id; row.State = "Queued";
            CloudSecurity.Audit(row, "ApprovedAndQueued", sid); await db.SaveChangesAsync(ct);
            return new { operationId = row.Id, deploymentId = deployment.Id, state = row.State };
        });
    }
    public void ValidateManualPaths(string source, string target)
    {
        var root = options.Value.RemoteStagingRoot; if (string.IsNullOrWhiteSpace(root)) return;
        foreach (var path in new[] { source, target }) CloudSecurity.Require(!DeploymentPaths.Normalize(path).Equals(DeploymentPaths.Normalize(root), StringComparison.OrdinalIgnoreCase) &&
            !DeploymentPaths.IsChild(path, root) && !DeploymentPaths.IsChild(root, path), "CLOUD_RESERVED_STAGING");
    }
    public async Task<CdRemoteSnapshot> LoadForExecutionAsync(Deployment deployment)
    {
        Gate(true); WorkerGate(); await Schema();
        var row = await db.CloudExecutions.SingleOrDefaultAsync(r => r.DeploymentId == deployment.Id) ?? throw new CloudExecutionException("CLOUD_APPROVAL_MISSING");
        var prepared = Unprotect<CloudExecutionData>(data, row.ProtectedData).Preparation ?? throw new CloudExecutionException("CLOUD_PREPARATION_MISSING");
        var binding = await Bind(prepared.Selection, row.ActorSid, true); var snapshot = prepared.Remote;
        CloudSecurity.Require(row.Kind == "Deploy" && row.RunId == prepared.Selection.RunId && row.ActorSid == prepared.ApproverSid && row.Id == snapshot.PreparationId &&
            row.State == "Queued" && prepared.Expires > DateTimeOffset.UtcNow && binding.Fingerprint == prepared.Binding &&
            deployment.DeploymentMode == Mode && deployment.Status == DeploymentStatus.CloudQueued && deployment.ServerId == snapshot.ServerId && CdDeploymentFlow.Fingerprint(deployment.Server) == snapshot.ServerFingerprint &&
            deployment.SourcePath == snapshot.SourcePath && deployment.TargetPath == snapshot.Provenance.TargetPath && deployment.SiteName == snapshot.Provenance.SiteName &&
            deployment.ApplicationName == snapshot.Provenance.ApplicationPath && deployment.AppPoolName == snapshot.AppPoolName && deployment.Environment == snapshot.Provenance.Environment &&
            deployment.SitePort == snapshot.SitePort && deployment.SiteHostName == snapshot.SiteHostName && deployment.ExpectedState == prepared.ExpectedState &&
            deployment.BackupPath == automation.Value.BackupRoot.TrimEnd('\\') + "\\" + row.Id.ToString("N"), "WORKER_BINDING");
        CloudSecurity.Require(await provider.EvidenceAsync(binding, prepared.Selection, CancellationToken.None) == prepared.Evidence, "WORKER_ARTIFACT_CHANGED");
        deployment.CloudHealthUrl = prepared.HealthUrl; CloudSecurity.Audit(row, "WorkerRevalidated", row.ActorSid); return snapshot;
    }
    public async Task<object> StatusAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        await Schema(); var sid = CloudSecurity.Sid(user);
        var row = await db.CloudExecutions.FindAsync(id) ?? throw new CloudExecutionException("OPERATION_MISSING");
        if (row.ActorSid != sid) throw new UnauthorizedAccessException();
        var payload = Unprotect<CloudExecutionData>(data, row.ProtectedData);
        var binding = await Bind(payload.Selection, sid, row.Kind == "Deploy"); CloudRun? run = null;
        if (row.RunId is { } runId) {
            run = await provider.RunAsync(binding, payload.Selection with { RunId = runId }, ct);
            if (row.Kind == "Build" && run.Status == "completed") { row.State = "RunCompleted"; row.UniqueScope = null; await db.SaveChangesAsync(ct); }
        }
        var deployment = row.DeploymentId is { } deploymentId ? await db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deploymentId, ct) : null;
        return new { operationId = row.Id, row.Kind, row.State, row.RunId, run, row.DeploymentId,
            deploymentStatus = deployment?.Status.ToString(), deploymentMessage = deployment?.ResultMessage, row.CreatedAt };
    }
}
