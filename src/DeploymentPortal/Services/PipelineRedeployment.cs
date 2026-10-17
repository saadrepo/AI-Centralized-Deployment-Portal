using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using static DeploymentPortal.Services.PipelineState;

namespace DeploymentPortal.Services;

public sealed partial class PipelineService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RedeploymentLocks = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ActiveDeploymentStates = new(StringComparer.OrdinalIgnoreCase)
        { "queued", "scheduled", "inProgress" };

    private static string? RedeploymentDenial(JsonNode release, JsonNode environment, int attemptId, int attemptNumber,
        IEnumerable<PendingReleaseRow> releases)
    {
        if (Text(release, "status") != "active") return "Release is not active.";
        if (Text(environment, "status") != "canceled") return "Only a cancelled deployment can be redeployed.";
        var attempt = PipelineTimeline.LatestAttempt(environment);
        if (attempt is null || Number(attempt, "id") != attemptId || Number(attempt, "attempt") != attemptNumber)
            return "The selected cancelled deployment attempt is no longer current.";
        if (Text(attempt, "status") != "canceled" && Text(attempt, "operationStatus") is not ("canceled" or "phaseCanceled"))
            return "TFS has not confirmed that the current deployment attempt is cancelled.";
        var releaseId = Number(release, "id");
        if (releases.Any(r => r.ReleaseId != releaseId && ActiveDeploymentStates.Contains(r.EnvironmentStatus)))
            return "Another release is queued or deploying to this environment.";
        return null;
    }

    public async Task<object> Redeploy(string key, int buildId, int releaseDefinitionId, int environmentId, int releaseId,
        int releaseEnvironmentId, int deploymentAttemptId, int deploymentAttempt, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!CanExecute(user)) throw new UnauthorizedAccessException("CD execution is disabled or this Portal Windows SID is not authorized.");
        if (releaseId <= 0 || releaseEnvironmentId <= 0 || deploymentAttemptId <= 0 || deploymentAttempt <= 0)
            throw new ArgumentException("Select an exact cancelled deployment attempt.");

        var app = App(key);
        var definition = await Definition(app, ct);
        await Build(app, buildId, definition, ct);
        var (_, release, environment) = await Release(app, definition, buildId, releaseDefinitionId, environmentId, ct, releaseId);
        if (release is null || environment is null || Number(environment, "id") != releaseEnvironmentId)
            throw new ArgumentException("Release environment changed; refresh before redeployment.");

        var lockKey = $"{app.Key}:{releaseDefinitionId}:{environmentId}";
        var gate = RedeploymentLocks.GetOrAdd(lockKey, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new PipelineRedeploymentException(null, false,
            "A redeployment request for this environment is already in progress. Refresh release status.");
        try
        {
            return await users.RunAsync(user, async (http, identity) =>
            {
                var all = await RedeploymentRows(http, app, definition, releaseDefinitionId, environmentId, ct);
                var currentRow = all.FirstOrDefault(r => r.ReleaseId == releaseId && r.BuildId == buildId)
                    ?? throw new ArgumentException("The selected release is no longer available for redeployment.");
                var exactRelease = await ReadAsync(http, Path(app, $"release/releases/{releaseId}", "4.1-preview.6"), ct);
                if (Number(exactRelease["releaseDefinition"], "id") != releaseDefinitionId
                    || !Items(exactRelease["artifacts"]).Any(a => ArtifactMatches(a, app.TfsGitCiDefinitionId!.Value,
                        Text(definition["project"], "id")!, buildId)))
                    throw new ArgumentException("The selected release artifact changed; refresh before redeployment.");
                var current = Items(exactRelease["environments"]).SingleOrDefault(e => Number(e, "definitionEnvironmentId") == environmentId)
                    ?? throw new ArgumentException("The selected release environment is missing.");
                if (Number(current, "id") != releaseEnvironmentId)
                    throw new ArgumentException("TFS returned a different release environment.");
                var denial = RedeploymentDenial(exactRelease, current, deploymentAttemptId, deploymentAttempt, all);
                if (denial is not null) throw new ArgumentException("Redeployment blocked: " + denial);

                var path = Path(app, $"release/releases/{releaseId}/environments/{releaseEnvironmentId}", "4.1-preview.5");
                logger.LogInformation("Pipeline redeployment requested: Application={Application}, BuildId={BuildId}, ReleaseId={ReleaseId}, EnvironmentId={EnvironmentId}, PreviousAttemptId={AttemptId}, PreviousAttempt={Attempt}, PortalSid={PortalSid}, TfsActor={TfsActor}.",
                    app.Key, buildId, releaseId, releaseEnvironmentId, deploymentAttemptId, deploymentAttempt,
                    PipelinePersonalSessions.Sid(user), identity);
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { status = "inProgress",
                    comment = $"Redeploy cancelled release through Deployment Portal; previous attempt {deploymentAttempt}; TFS actor {identity}; Portal Windows SID {PipelinePersonalSessions.Sid(user) ?? "unavailable"}." });
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                using var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = content };
                HttpResponseMessage response;
                try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                { throw new PipelineRedeploymentException((int?)(ex as HttpRequestException)?.StatusCode, true); }
                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                        throw new PipelineRedeploymentException((int)response.StatusCode, true);
                }

                JsonNode confirmed;
                try { confirmed = await ReadAsync(http, path, ct); }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
                { throw new PipelineRedeploymentException((int?)(ex as HttpRequestException)?.StatusCode, true); }
                var latest = PipelineTimeline.LatestAttempt(confirmed);
                var approvalAttempt = Items(confirmed["preDeployApprovals"]).Select(a => Number(a, "attempt")).DefaultIfEmpty(0).Max();
                if (Number(confirmed, "id") != releaseEnvironmentId || Text(confirmed, "status") == "canceled"
                    || (Number(latest, "attempt") <= deploymentAttempt && approvalAttempt <= deploymentAttempt))
                    throw new PipelineRedeploymentException(200, true);
                logger.LogInformation("Pipeline redeployment confirmed: ReleaseId={ReleaseId}, EnvironmentId={EnvironmentId}, NewAttempt={Attempt}, PortalSid={PortalSid}, TfsActor={TfsActor}.",
                    releaseId, releaseEnvironmentId, Math.Max(Number(latest, "attempt"), approvalAttempt), PipelinePersonalSessions.Sid(user), identity);
                return (object)new { releaseId, releaseEnvironmentId, deploymentAttempt = Math.Max(Number(latest, "attempt"), approvalAttempt),
                    environmentStatus = Text(confirmed, "status") ?? "unknown" };
            });
        }
        finally { gate.Release(); }
    }

    private async Task<List<PendingReleaseRow>> RedeploymentRows(HttpClient http, RegistryApplication app, JsonNode definition,
        int releaseDefinitionId, int environmentId, CancellationToken ct)
    {
        var candidates = await ReadListAsync(http, Path(app, "release/releases", "4.1-preview.6")
            + $"&definitionId={releaseDefinitionId}&$expand=artifacts,environments&$top=100&queryOrder=descending", ct);
        var rows = new List<PendingReleaseRow>();
        foreach (var release in candidates)
        {
            if (Number(release["releaseDefinition"], "id") != releaseDefinitionId) continue;
            var artifact = Items(release["artifacts"]).SingleOrDefault(a => ArtifactMatches(a, app.TfsGitCiDefinitionId!.Value, Text(definition["project"], "id")!));
            var buildId = Number(artifact?["definitionReference"]?["version"], "id");
            var environment = Items(release["environments"]).SingleOrDefault(e => Number(e, "definitionEnvironmentId") == environmentId);
            if (buildId <= 0 || environment is null) continue;
            rows.Add(new PendingReleaseRow(Number(release, "id"), Text(release, "name") ?? $"Release ID {Number(release, "id")}", buildId,
                0, "not applicable", Text(environment, "status") ?? "unknown", false, release, environment, new JsonObject()));
        }
        return rows;
    }

    private static async Task<List<JsonNode>> ReadListAsync(HttpClient http, string path, CancellationToken ct)
    {
        var node = await ReadAsync(http, path, ct);
        return Items(node["value"]).ToList();
    }
}

public sealed class PipelineRedeploymentException(int? httpStatus, bool mayHaveReachedTfs, string? message = null)
    : Exception(message ?? "TFS redeployment outcome is unconfirmed. Refresh release status before retrying.")
{
    public int? HttpStatus { get; } = httpStatus;
    public bool MayHaveReachedTfs { get; } = mayHaveReachedTfs;
}
