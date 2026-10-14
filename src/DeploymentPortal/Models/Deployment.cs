using System.ComponentModel.DataAnnotations;

namespace DeploymentPortal.Models;
public enum DeploymentStatus { Queued, Running, Successful, Failed }
public class Deployment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int ServerId { get; set; }
    public Server Server { get; set; } = null!;
    public CdDeploymentSource? CdSource { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public Services.CdRemoteSnapshot? CdPackage { get; set; }
    [MaxLength(80)] public string ApplicationName { get; set; } = "";
    [MaxLength(30)] public string Environment { get; set; } = "";
    [MaxLength(80)] public string SiteName { get; set; } = "";
    [MaxLength(80)] public string? AppPoolName { get; set; }
    [MaxLength(500)] public string SourcePath { get; set; } = "";
    [MaxLength(500)] public string TargetPath { get; set; } = "";
    [MaxLength(500)] public string BackupPath { get; set; } = "";
    public int SitePort { get; set; } = 8080;
    [MaxLength(253)] public string? SiteHostName { get; set; }
    [MaxLength(64)] public string? ExpectedState { get; set; }
    [MaxLength(30)] public string? DeploymentMode { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Running;
    [MaxLength(2000)] public string ResultMessage { get; set; } = "Deployment is running.";
    public string Duration => CompletedAt is { } end ? $"{(end - StartedAt).TotalSeconds:N0}s" : "In progress";
}
