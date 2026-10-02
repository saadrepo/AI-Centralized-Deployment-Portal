using System.ComponentModel.DataAnnotations;

namespace DeploymentPortal.ViewModels;

public class DockerContainerForm
{
    [Required, StringLength(500), Display(Name = "Source folder on dashboard host")]
    public string SourcePath { get; set; } = "";

    [Required, StringLength(80), RegularExpression(@"^[a-z0-9][a-z0-9_.-]*$", ErrorMessage = "Use lowercase letters, numbers, dots, underscores or hyphens, starting with a letter or number."), Display(Name = "Container name")]
    public string ContainerName { get; set; } = "";

    [Range(1, 65535), Display(Name = "Host port")]
    public int HostPort { get; set; }

    [Range(1, 65535), Display(Name = "Container port")]
    public int ContainerPort { get; set; }

    public string? ConfirmationToken { get; set; }
}
