using System.ComponentModel.DataAnnotations;

namespace DeploymentPortal.Models;

public enum ConnectionStatus { Unknown, Online, Offline }
public class Server
{
    public int Id { get; set; }
    [MaxLength(100)] public string DisplayName { get; set; } = "";
    [MaxLength(253)] public string HostNameOrIp { get; set; } = "";
    [MaxLength(30)] public string Environment { get; set; } = "Development";
    [MaxLength(200)] public string OperatingSystem { get; set; } = "Not checked";
    public long? TotalMemoryBytes { get; set; }
    [MaxLength(30)] public string ConnectionType { get; set; } = "Windows (WinRM)";
    public int WinRmPort { get; set; } = 5986;
    public bool UseSsl { get; set; } = true;
    [MaxLength(150)] public string Username { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ConnectionStatus LastConnectionStatus { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
}
