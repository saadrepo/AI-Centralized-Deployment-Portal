using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DeploymentPortal.Models;

namespace DeploymentPortal.Services;

public sealed class CloudExecutionOptions
{
    public bool BuildExecutionEnabled { get; set; }
    public bool DeploymentExecutionEnabled { get; set; }
    // Explicit, default-off single-host rollout attestation. Not an encryption change.
    public bool WorkerCompatibilityConfirmed { get; set; }
    public string WorkerHostName { get; set; } = "";
    // Separate, expiring operational authorization. No shipped configuration enables this.
    public string LiveAuthorizationReference { get; set; } = "";
    public DateTimeOffset LiveAuthorizationExpires { get; set; }
    public string LocalStagingRoot { get; set; } = "";
    public string RemoteStagingRoot { get; set; } = "";
    public List<CloudExecutionMapping> Mappings { get; set; } = [];
}
public sealed record CloudSelection(string ApplicationKey, string Provider, int SourceId, string? Project,
    string Repository, string Branch, long DefinitionId, long RunId = 0);
public sealed class CloudExecutionMapping
{
    public string ApplicationKey { get; set; } = "";
    public string Provider { get; set; } = "";
    public int SourceId { get; set; }
    public string? Project { get; set; }
    public string Repository { get; set; } = "";
    public string Branch { get; set; } = "";
    public long DefinitionId { get; set; }
    public string ArtifactName { get; set; } = "";
    public string ArchiveRoot { get; set; } = "";
    public int ServerId { get; set; }
    public string Environment { get; set; } = "";
    public string HealthUrl { get; set; } = "";
    public string[] BuilderSids { get; set; } = [];
    public string[] ApproverSids { get; set; } = [];
    public string[] ArtifactHosts { get; set; } = [];
}
public sealed record CloudRun(long Id, string Number, string Commit, string Status, string Result, long Attempt);
public sealed record CloudArtifact(long Id, string Name, string Type, long Size, string Digest, string Identity);
public sealed record CloudEvidence(CloudRun Run, CloudArtifact Artifact);
public sealed record CloudBinding(CloudExecutionMapping Mapping, InfrastructureSource Source, RegistryDestination Target,
    Server Server, string Fingerprint);
public sealed record CloudPrepared(CloudSelection Selection, string Binding, CloudEvidence Evidence, string ArchiveHash,
    CdRemoteSnapshot Remote, string ExpectedState, DateTimeOffset Expires, string HealthUrl, string ApproverSid);
public sealed record CloudBuildIntent(CloudSelection Selection, string Binding);
public sealed record CloudExecutionData(CloudSelection Selection, string Binding, CloudPrepared? Preparation = null);
public sealed record CloudTicket(Guid Id, CloudSelection Selection, string Binding, string ActorSid, DateTimeOffset Expires);
public sealed record CloudApproval(Guid Id, string Digest, string Sid, DateTimeOffset Expires);
public sealed class CloudExecutionException(string code) : ArgumentException("Cloud operation blocked or unconfirmed: " + code + ". Refresh status; do not retry an uncertain operation.")
{
    public string Code { get; } = code;
}
public static class CloudSecurity
{
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static string Sid(ClaimsPrincipal user) => OperatingSystem.IsWindows() && user.Identity is WindowsIdentity { IsAuthenticated: true } identity
        && identity.User is not null ? identity.User.Value : throw new UnauthorizedAccessException();
    public static void Require(bool condition, string code) { if (!condition) throw new CloudExecutionException(code); }
    public static bool Live(CloudExecutionOptions options) => !string.IsNullOrWhiteSpace(options.LiveAuthorizationReference)
        && options.LiveAuthorizationExpires > DateTimeOffset.UtcNow;
    public static void Audit(CloudExecutionRecord row, string state, string sid)
    {
        var events = JsonSerializer.Deserialize<List<object>>(row.AuditJson) ?? [];
        Require(events.Count < 1000, "AUDIT_CAPACITY");
        events.Add(new { at = DateTimeOffset.UtcNow, state, sid }); row.AuditJson = JsonSerializer.Serialize(events);
    }
}
public interface ICloudProvider
{
    Task<long> QueueAsync(CloudBinding binding, CloudSelection selection, CancellationToken ct);
    Task<CloudRun> RunAsync(CloudBinding binding, CloudSelection selection, CancellationToken ct);
    Task<CloudEvidence> EvidenceAsync(CloudBinding binding, CloudSelection selection, CancellationToken ct);
    Task<byte[]> DownloadAsync(CloudBinding binding, CloudSelection selection, CloudEvidence evidence, CancellationToken ct);
}
public interface ICloudPackageStager
{
    Task<IReadOnlyList<CdTransferFile>> StageAsync(Guid id, byte[] archive, CloudExecutionMapping mapping, CancellationToken ct);
}
public interface ICloudDeploymentGuard
{
    void ValidateManualPaths(string source, string target);
    Task<CdRemoteSnapshot> LoadForExecutionAsync(Deployment deployment);
}
