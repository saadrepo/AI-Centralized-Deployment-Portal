using System.ComponentModel.DataAnnotations;

namespace DeploymentPortal.Models;

public sealed class CdDeploymentSource
{
    public Guid PreparationId { get; set; }
    public Guid? DeploymentId { get; set; }
    public Deployment? Deployment { get; set; }
    public int BuildId { get; set; }
    [MaxLength(100)] public string BuildNumber { get; set; } = "";
    [MaxLength(100)] public string Branch { get; set; } = "";
    [MaxLength(40)] public string Commit { get; set; } = "";
    [MaxLength(80)] public string Artifact { get; set; } = "";
    public string ProtectedSnapshot { get; set; } = "";
    public DateTimeOffset Expires { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    [MaxLength(1000)] public string? Verification { get; set; }
    public string AuditJson { get; set; } = "[]";
}
