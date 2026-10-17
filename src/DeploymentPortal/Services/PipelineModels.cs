using System.Text.Json.Nodes;

namespace DeploymentPortal.Services;

public sealed record PipelineOption(string Id, string Name);
public sealed record PipelineEnvironment(int DefinitionId, int Id, string Name);
public sealed record PipelineStage(string Name, string State, string Detail);
public sealed record PipelineEvent(string Name, string State, string? Started, string? Finished);
public sealed class PipelineSnapshot
{
    public int BuildId { get; set; }
    public string? BuildNumber { get; set; }
    public string? Branch { get; set; }
    public string Status { get; set; } = "Pending";
    public string Phase { get; set; } = "CI";
    public string TimelineMessage { get; set; } = "Waiting for CI task status.";
    public int? ReleaseId { get; set; }
    public int? EnvironmentId { get; set; }
    public int? ApprovalId { get; set; }
    public bool CanApprove { get; set; }
    public bool BuildActive { get; set; }
    public bool CanCancel { get; set; }
    public string CancelReason { get; set; } = "Selected build is not queued or running.";
    public bool CanCancelDeployment { get; set; }
    public int? DeploymentAttemptId { get; set; }
    public int? DeploymentAttempt { get; set; }
    public string DeploymentCancelReason { get; set; } = "Deployment cancellation is disabled or this deployment is not cancellable.";
    public string? HealthUrl { get; set; }
    public string ApprovalReason { get; set; } = "No pending pre-deployment approval.";
    public List<PipelineStage> Stages { get; set; } = [];
    public List<PipelineEvent> Events { get; set; } = [];
    public List<string> Logs { get; set; } = [];
    public List<PipelineEvent> CiEvents { get; set; } = [];
    public List<string> CiLogs { get; set; } = [];
    public List<string> Notices { get; set; } = [];
    public DateTimeOffset RefreshedAt { get; set; }
}

public static class PipelineState
{
    public static string Map(string? status, string? result = null) => (result ?? status)?.ToLowerInvariant() switch
    {
        "succeeded" => "completed",
        "failed" or "canceled" or "cancelled" or "rejected" or "partiallysucceeded" => "failed",
        "inprogress" or "queued" or "scheduled" or "cancelling" => "running",
        _ => "pending"
    };
    public static string? Text(JsonNode? node, string key) => node?[key]?.ToString();
    public static int Number(JsonNode? node, string key) => int.TryParse(Text(node, key), out var value) ? value : 0;
    public static IEnumerable<JsonNode> Items(JsonNode? node) => node is JsonArray array ? array.OfType<JsonNode>() : [];
    public static bool ArtifactMatches(JsonNode? artifact, int definitionId, string projectId, int? buildId = null) =>
        string.Equals(Text(artifact, "type"), "Build", StringComparison.OrdinalIgnoreCase)
        && Text(artifact?["definitionReference"]?["definition"], "id") == definitionId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        && string.Equals(Text(artifact?["definitionReference"]?["project"], "id"), projectId, StringComparison.OrdinalIgnoreCase)
        && (buildId is null || Text(artifact?["definitionReference"]?["version"], "id") == buildId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
