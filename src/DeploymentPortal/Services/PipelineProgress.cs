using System.Text.Json.Nodes;
using static DeploymentPortal.Services.PipelineState;

namespace DeploymentPortal.Services;

public static class PipelineProgress
{
    public static bool IsActive(string? status) => status is "notStarted" or "queued" or "postponed" or "inProgress" or "cancelling";
    public static bool IsCancellable(string? status) => IsActive(status) && status != "cancelling";
    public static List<PipelineStage> Stages(JsonNode build, JsonNode? timeline, bool published)
    {
        var sourceTasks = Items(timeline?["records"]).Where(r => Text(r, "type") == "Task"
            && (Text(r, "name")?.StartsWith("Get sources", StringComparison.OrdinalIgnoreCase) == true
                || Text(r, "name")?.StartsWith("Checkout ", StringComparison.OrdinalIgnoreCase) == true)).ToList();
        var states = sourceTasks.Select(r => Map(Text(r, "state"), Text(r, "result"))).ToList();
        var source = states.Count == 0 ? "pending" : states.Contains("failed") ? "failed"
            : states.All(s => s == "completed") ? "completed" : states.Contains("running") ? "running" : "pending";
        var status = Text(build, "status");
        var buildState = Map(status, status == "completed" ? Text(build, "result") : null);
        var detail = Text(build, "result") is "canceled" or "cancelled" ? "Cancelled" : status == "cancelling" ? "Cancellation requested" : status ?? "Waiting for status";
        if (IsActive(status) && source != "completed")
        {
            buildState = "pending";
            detail = source == "running" ? "Waiting for source" : "Waiting for status";
        }
        return [new("Source", source, states.Count == 0 ? "Waiting for status" : source == "completed" ? "Completed" : source == "running" ? "In progress" : source == "failed" ? "Failed" : "Waiting for source"),
            new("Build", buildState, detail),
            new("Publish Artifact", published ? "completed" : "pending", published ? "Artifact published" : "Waiting for artifact"),
            new("Approval", "pending", "Waiting for successful CI and release"),
            new("Deploy to IIS", "pending", "Pending"), new("Verify Health", "pending", "Pending")];
    }
}
