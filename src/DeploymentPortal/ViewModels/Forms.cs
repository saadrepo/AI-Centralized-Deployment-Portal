using System.ComponentModel.DataAnnotations;
using DeploymentPortal.Models;

namespace DeploymentPortal.ViewModels;
public static class Environments
{
    public static readonly string[] All = ["Development", "Test", "Staging", "Production"];
}
public class ServerForm : IValidatableObject
{
    public int Id { get; set; }
    [Required, StringLength(100), Display(Name = "Display name")] public string DisplayName { get; set; } = "";
    [Required, StringLength(253), RegularExpression(@"^[a-zA-Z0-9](?:[a-zA-Z0-9.\-]*[a-zA-Z0-9])?$"), Display(Name = "Hostname / IPv4 address")] public string HostNameOrIp { get; set; } = "";
    [Required] public string Environment { get; set; } = "Development";
    [Range(1, 65535), Display(Name = "WinRM port")] public int WinRmPort { get; set; } = 5986;
    [Display(Name = "Use HTTPS / SSL")] public bool UseSsl { get; set; } = true;
    [Required, StringLength(150), RegularExpression(@"^[a-zA-Z0-9_.@\\\-]+$")] public string Username { get; set; } = "";
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (!Environments.All.Contains(Environment)) yield return new("Select a valid environment.", [nameof(Environment)]);
    }
    public Server ToServer() => new() { Id = Id, DisplayName = DisplayName.Trim(), HostNameOrIp = HostNameOrIp.Trim().ToLowerInvariant(), Environment = Environment, WinRmPort = WinRmPort, UseSsl = UseSsl, Username = Username.Trim() };
    public static ServerForm From(Server x) => new() { Id = x.Id, DisplayName = x.DisplayName, HostNameOrIp = x.HostNameOrIp, Environment = x.Environment, WinRmPort = x.WinRmPort, UseSsl = x.UseSsl, Username = x.Username };
}
public class DeploymentForm : IValidatableObject
{
    [Range(1, int.MaxValue), Display(Name = "Target server")] public int ServerId { get; set; }
    [Required] public string Environment { get; set; } = "Development";
    [Required, StringLength(80), RegularExpression(@"^[a-zA-Z0-9][a-zA-Z0-9 _.-]*$"), Display(Name = "IIS site name")] public string SiteName { get; set; } = "";
    [Required, StringLength(80), RegularExpression(@"^([a-zA-Z0-9][a-zA-Z0-9_-]*|/)$"), Display(Name = "Application path")] public string ApplicationName { get => applicationName; set => applicationName = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim(); }
    private string applicationName = "/";
    [StringLength(80), RegularExpression(@"^[a-zA-Z0-9][a-zA-Z0-9 _.-]*$"), Display(Name = "Application pool (optional)")] public string? AppPoolName { get; set; }
    [Required, StringLength(500), Display(Name = "Package folder on target server")] public string SourcePath { get; set; } = "";
    [Required, StringLength(500), Display(Name = "Target physical path")] public string TargetPath { get; set; } = "";
    public string? ConfirmationToken { get; set; }
    [Range(1, 65535), Display(Name = "HTTP port for a new site")] public int SitePort { get; set; } = 8080;
    [StringLength(253), RegularExpression(@"^[a-zA-Z0-9](?:[a-zA-Z0-9.\-]*[a-zA-Z0-9])?$"), Display(Name = "Hostname for a new site (optional)")] public string? SiteHostName { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public string? PlanMessage { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public string? PlanMode { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (!Environments.All.Contains(Environment)) yield return new("Select a valid environment.", [nameof(Environment)]);
    }
}
public record DashboardViewModel(IReadOnlyList<Server> Servers, IReadOnlyList<Deployment> Deployments, int TotalDeployments);
public record ConfirmDeploymentViewModel(DeploymentForm Form, Server Server);
