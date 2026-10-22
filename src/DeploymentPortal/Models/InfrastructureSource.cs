using System.ComponentModel.DataAnnotations;

namespace DeploymentPortal.Models;

public enum InfrastructureSourceProvider { Tfs2018, AzureDevOps, GitHub }
public sealed class InfrastructureSource
{
    public int Id { get; set; }
    [MaxLength(100)] public string DisplayName { get; set; } = "";
    public InfrastructureSourceProvider Provider { get; set; }
    [MaxLength(400)] public string Destination { get; set; } = "";
    [MaxLength(64)] public string CredentialReference { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [MaxLength(20)] public string ConnectionStatus { get; set; } = "Unknown";
    [MaxLength(200)] public string ConnectionMessage { get; set; } = "Not tested.";
    public DateTimeOffset? LastCheckedAt { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = [];
}
