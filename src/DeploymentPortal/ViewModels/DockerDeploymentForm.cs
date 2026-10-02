using System.ComponentModel.DataAnnotations;

namespace DeploymentPortal.ViewModels;

public class DockerDeploymentForm : IValidatableObject
{
    [Required, Display(Name = "Docker server")] public string Context { get; set; } = "";
    [Required, Display(Name = "Image")] public string Image { get; set; } = "";
    [Required, StringLength(80), RegularExpression(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$"), Display(Name = "Container name")]
    public string ContainerName { get; set; } = "";
    [Range(1, 65535), Display(Name = "Host port")] public int HostPort { get; set; }
    [Range(1, 65535), Display(Name = "Container port")] public int ContainerPort { get; set; }
    [StringLength(4000), Display(Name = "Environment variables (KEY=value, one per line)")]
    public string? EnvironmentVariables { get; set; }
    [StringLength(100), RegularExpression(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$"), Display(Name = "Docker volume name")]
    public string? VolumeName { get; set; }
    [StringLength(250), Display(Name = "Volume mount path in container")]
    public string? MountPath { get; set; }
    public string? ConfirmationToken { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(VolumeName) != string.IsNullOrWhiteSpace(MountPath))
            yield return new("Enter both a volume name and a container mount path, or leave both blank.", [nameof(VolumeName), nameof(MountPath)]);
        if (!string.IsNullOrWhiteSpace(MountPath) && (!MountPath.StartsWith('/') || MountPath.Contains(',') || MountPath.Contains(':')))
            yield return new("Use an absolute Linux container path without commas or colons.", [nameof(MountPath)]);
        if (EnvironmentVariables is null) yield break;
        var lines = EnvironmentVariables.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 20) yield return new("Use at most 20 environment variables.", [nameof(EnvironmentVariables)]);
        foreach (var line in lines)
        {
            var equals = line.IndexOf('=');
            if (equals < 1 || !System.Text.RegularExpressions.Regex.IsMatch(line[..equals], @"^[A-Za-z_][A-Za-z0-9_]*$") || line.Contains('\r'))
            { yield return new("Each environment variable must be KEY=value, one per line.", [nameof(EnvironmentVariables)]); break; }
        }
    }
}

public record DockerContextInfo(string Name, string Description, bool Current);
public record DockerImageInfo(string Name, string Id, string Size);
public record DockerContainerInfo(string Id, string Name, string Image, string Status, string Ports, bool CanRollback = false);
public record DockerDashboardViewModel(IReadOnlyList<DockerContextInfo> Contexts, string Context,
    IReadOnlyList<DockerContainerInfo> Containers, IReadOnlyList<DockerImageInfo> Images, string? Error = null);
public record DockerDeploymentReview(DockerDeploymentForm Form, string ImageId, string? ExistingContainerId);
