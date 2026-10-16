using System.Security.Claims;
using System.Text.Json.Nodes;
using static DeploymentPortal.Services.PipelineState;

namespace DeploymentPortal.Services;

public sealed partial class PipelineService
{
    public async Task<object> PendingReleases(string key, int releaseDefinitionId, int environmentId, ClaimsPrincipal user, CancellationToken ct)
    {
        var app = App(key);
        var definition = await Definition(app, ct);
        if (!(await ReleaseDefinitions(app, definition, ct)).Any(r => Number(r, "id") == releaseDefinitionId))
            throw new ArgumentException("Release definition is not associated with this application.");
        var rd = await ReadAsync(client, Path(app, $"release/definitions/{releaseDefinitionId}", "4.1-preview.3"), ct);
        var configured = Items(rd["environments"]).SingleOrDefault(e => Number(e, "id") == environmentId)
            ?? throw new ArgumentException("Select a matching release environment.");
        var candidates = await List(Path(app, "release/releases", "4.1-preview.6")
            + $"&definitionId={releaseDefinitionId}&$expand=artifacts,environments&$top=100&queryOrder=descending", ct);
        var rows = new List<PendingReleaseRow>();
        foreach (var candidate in candidates.OrderByDescending(r => Number(r, "id")))
        {
            if (Number(candidate["releaseDefinition"], "id") != releaseDefinitionId) continue;
            var artifacts = Items(candidate["artifacts"]).Where(a => ArtifactMatches(a, app.TfsGitCiDefinitionId!.Value, Text(definition["project"], "id")!)).ToList();
            if (artifacts.Count != 1) continue;
            var buildId = Number(artifacts[0]["definitionReference"]?["version"], "id");
            if (buildId <= 0) continue;
            var release = await ReadAsync(client, Path(app, $"release/releases/{Number(candidate, "id")}", "4.1-preview.6"), ct);
            if (Number(release["releaseDefinition"], "id") != releaseDefinitionId || !Items(release["artifacts"]).Any(a => ArtifactMatches(a, app.TfsGitCiDefinitionId!.Value, Text(definition["project"], "id")!, buildId))) continue;
            var environment = Items(release["environments"]).SingleOrDefault(e => Number(e, "definitionEnvironmentId") == environmentId);
            if (environment is null) continue;
            await HydratePendingApprovals(app, release, environment, ct);
            var approvals = CurrentApprovals(environment).ToList();
            if (approvals.Count == 0)
                rows.Add(new(Number(release, "id"), Text(release, "name") ?? $"Release ID {Number(release, "id")}", buildId,
                    0, "not returned by TFS", Text(environment, "status") ?? "unknown", false, release, environment, new JsonObject()));
            foreach (var approval in approvals)
                rows.Add(new(Number(release, "id"), Text(release, "name") ?? $"Release ID {Number(release, "id")}", buildId,
                    Number(approval, "id"), Text(approval, "status") ?? "unknown", Text(environment, "status") ?? "unknown", false,
                    release, environment, approval));
        }
        if (CanExecute(user))
            await users.RunAsync(user, async (http, identity) => {
                foreach (var group in rows.Where(r => r.ApprovalStatus == "pending").GroupBy(r => r.ReleaseId))
                {
                    var eligible = await EligibleApprovals(http, app, group.Key, identity, ct);
                    foreach (var row in group)
                    {
                        row.RejectReason = ApprovalDenial(rd, row.Release, row.Environment, row.Approval, identity, eligible.Contains(row.ApprovalId)) ?? "TFS will revalidate this decision.";
                        row.CanReject = ApprovalAllowed(rd, row.Release, row.Environment, row.Approval, identity, eligible.Contains(row.ApprovalId));
                    }
                }
                return true;
            });
        return new {
            releases = rows.Select(r => new { r.ReleaseId, r.Name, r.BuildId, r.ApprovalId, r.ApprovalStatus, r.EnvironmentStatus, r.CanReject, r.RejectReason,
                operationStatus = Text(PipelineTimeline.LatestAttempt(r.Environment), "operationStatus"),
                deploymentQueueId = Number(PipelineTimeline.LatestAttempt(r.Environment), "queueId") }),
            executionReason = ExecutionReason(user),
            concurrencyCount = Text(configured["executionPolicy"], "concurrencyCount"),
            queueDepthCount = Text(configured["executionPolicy"], "queueDepthCount"),
            unresolvedReleaseIds = rows.Where(r => (r.EnvironmentStatus is "inProgress" or "queued" or "notStarted") && r.ApprovalStatus != "rejected")
                .Select(r => r.ReleaseId).Distinct().OrderBy(id => id).ToArray(),
            explanation = "Queue evidence: inspect unresolved releases, environment operation status and execution policy below. Earlier unresolved releases are possible queue blockers, not confirmed blockers unless TFS reports it. No queue policy was changed."
        };
    }

    private sealed record PendingReleaseRow(int ReleaseId, string Name, int BuildId, int ApprovalId, string ApprovalStatus,
        string EnvironmentStatus, bool Allowed, JsonNode Release, JsonNode Environment, JsonNode Approval)
    {
        public bool CanReject { get; set; } = Allowed;
        public string RejectReason { get; set; } = "Execution/Windows identity verification is required; see execution reason.";
    }
}
