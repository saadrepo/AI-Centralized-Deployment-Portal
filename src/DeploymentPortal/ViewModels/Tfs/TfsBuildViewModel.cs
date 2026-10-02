using System.Text.Json.Serialization;

namespace DeploymentPortal.ViewModels.Tfs;

public class TfsBuildViewModel
{
    public int Id { get; set; }
    public string? BuildNumber { get; set; }
    public string? Status { get; set; }
    public string? Result { get; set; }
    public string? BuildAction => Status?.ToLowerInvariant() == "completed"
        ? Result?.ToLowerInvariant() switch { "succeeded" or "failed" => "Rebuild", "canceled" => "Build", _ => null }
        : null;
    public TfsIdentityViewModel? RequestedBy { get; set; }
    public string? SourceBranch { get; set; }
    [JsonPropertyName("sourceVersion")]
    public string? SourceVersion { get; set; }
    public DateTimeOffset? QueueTime { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? FinishTime { get; set; }
}

public class TfsIdentityViewModel
{
    public string? DisplayName { get; set; }
}
