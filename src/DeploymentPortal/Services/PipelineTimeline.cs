using System.Globalization;
using System.Text.Json.Nodes;
using static DeploymentPortal.Services.PipelineState;

namespace DeploymentPortal.Services;

public static class PipelineTimeline
{
    private static DateTimeOffset Started(JsonNode n) => DateTimeOffset.TryParse(Text(n, "startTime") ?? Text(n, "startedOn"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : DateTimeOffset.MaxValue;
    private static int Position(JsonNode n) => int.TryParse(Text(n, "order") ?? Text(n, "rank"), out var value) ? value : int.MaxValue;
    public static IEnumerable<JsonNode> Sort(IEnumerable<JsonNode> nodes) => nodes.OrderBy(Position).ThenBy(Started);

    public static IReadOnlyList<JsonNode> BuildTasks(JsonNode? timeline)
    {
        var records = Items(timeline?["records"]).ToList();
        var ids = records.Select(n => Text(n, "id")).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        var tasks = new List<JsonNode>();
        void Visit(JsonNode node)
        {
            if (!visited.Add(node)) return;
            if (Text(node, "type") == "Task") tasks.Add(node);
            if (Text(node, "id") is { Length: > 0 } id)
                foreach (var child in Sort(records.Where(n => string.Equals(Text(n, "parentId"), id, StringComparison.OrdinalIgnoreCase)))) Visit(child);
        }
        foreach (var root in Sort(records.Where(n => !ids.Contains(Text(n, "parentId"))))) Visit(root);
        foreach (var orphan in Sort(records)) Visit(orphan); // Bounded/cycle-safe handling of partial timelines.
        return tasks;
    }

    public static JsonNode? LatestAttempt(JsonNode environment) => Items(environment["deploySteps"])
        .OrderByDescending(n => Number(n, "attempt")).ThenByDescending(n => Number(n, "id")).FirstOrDefault();

    public static IReadOnlyList<(int Phase, JsonNode Task)> ReleaseTasks(JsonNode? attempt) => Sort(Items(attempt?["releaseDeployPhases"]))
        .SelectMany(phase => Sort(Items(phase["deploymentJobs"]))
            .SelectMany(job => Sort(Items(job["tasks"]))).Select(task => (Number(phase, "id"), task))).ToList();

    public static IReadOnlyList<JsonNode> DisplayApprovals(JsonNode environment)
    {
        var approvals = Items(environment["preDeployApprovals"]).ToList();
        var latest = Math.Max(Number(LatestAttempt(environment), "attempt"), approvals.Select(a => Number(a, "attempt")).DefaultIfEmpty(0).Max());
        // Unknown attempt numbers cannot prove that a pending approval is stale.
        return approvals.Where(a => latest == 0 || Number(a, "attempt") == 0 || Number(a, "attempt") == latest).ToList();
    }

    public static PipelineStage Deployment(JsonNode environment)
    {
        var status = Text(environment, "status");
        if (status == "succeeded") return new("Deploy to IIS", "completed", "Deployment succeeded");
        if (status is "failed" or "canceled" or "rejected" or "partiallySucceeded") return new("Deploy to IIS", "failed", status);
        var attempt = LatestAttempt(environment);
        var tasks = ReleaseTasks(attempt);
        // An old completed attempt must not make a new environment initialization look active.
        if (Text(attempt, "status") is "succeeded" or "failed" or "canceled" or "rejected" or "partiallySucceeded")
            return new("Deploy to IIS", "pending", "Waiting for current deployment status");
        var started = tasks.Any(t => Text(t.Task, "status") == "inProgress")
            || Items(attempt?["releaseDeployPhases"]).Any(p => Text(p, "status") == "inProgress")
            || Text(attempt, "operationStatus") == "phaseInProgress"
            || (Text(attempt, "status") == "inProgress" && tasks.Any(t => Text(t.Task, "status") is "succeeded" or "failed"));
        return status == "inProgress" && started ? new("Deploy to IIS", "running", "Deployment in progress")
            : new("Deploy to IIS", "pending", status is "queued" or "scheduled" ? "Waiting for deployment" : "Waiting for deployment status");
    }
}
