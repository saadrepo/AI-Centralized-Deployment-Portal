using System.ComponentModel.DataAnnotations;

namespace DeploymentPortal.Models;

public sealed class CloudExecutionRecord
{
    public Guid Id { get; set; }
    [MaxLength(20)] public string Kind { get; set; } = "";
    [MaxLength(64)] public string? UniqueScope { get; set; }
    [MaxLength(200)] public string ActorSid { get; set; } = "";
    [MaxLength(30)] public string State { get; set; } = "";
    public string ProtectedData { get; set; } = "";
    public long? RunId { get; set; }
    public Guid? DeploymentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AuditJson { get; set; } = "[]";
    public byte[] RowVersion { get; set; } = [];
}
