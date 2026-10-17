using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using static DeploymentPortal.Services.PipelineState;

namespace DeploymentPortal.Services;

public sealed partial class PipelineService
{
    private static readonly HashSet<string> CancellableEnvironmentStates = new(StringComparer.OrdinalIgnoreCase) { "queued", "inProgress", "scheduled" };
    private static readonly HashSet<string> CancellableOperationStates = new(StringComparer.OrdinalIgnoreCase)
        { "queued", "scheduled", "pending", "approved", "queuedForAgent", "queuedForPipeline", "phaseInProgress", "manualInterventionPending" };

    private bool CanCancelDeployment(ClaimsPrincipal user) => CanExecute(user) && users.AvailableForDeploymentCancellation(user);

    private static string? DeploymentCancellationDenial(JsonNode release, JsonNode environment, int attemptId, int attemptNumber)
    {
        if (Text(release, "status") != "active") return "Release is not active.";
        if (!CancellableEnvironmentStates.Contains(Text(environment, "status") ?? "")) return "Environment is not queued, scheduled or running.";
        var attempt = PipelineTimeline.LatestAttempt(environment);
        if (attempt is null || Number(attempt, "id") != attemptId || Number(attempt, "attempt") != attemptNumber)
            return "The selected deployment attempt is no longer current.";
        if (!CancellableOperationStates.Contains(Text(attempt, "operationStatus") ?? ""))
            return "The current deployment operation is not cancellable.";
        return null;
    }

    public async Task CancelDeployment(string key, int buildId, int releaseDefinitionId, int environmentId, int releaseId,
        int releaseEnvironmentId, int deploymentAttemptId, int deploymentAttempt, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!CanCancelDeployment(user))
            throw new UnauthorizedAccessException("Deployment cancellation is disabled or this Portal Windows SID is not authorized.");
        if (releaseId <= 0 || releaseEnvironmentId <= 0 || deploymentAttemptId <= 0 || deploymentAttempt <= 0)
            throw new ArgumentException("Select an exact current deployment attempt.");

        var app = App(key);
        var definition = await Definition(app, ct);
        await Build(app, buildId, definition, ct);
        var (_, release, environment) = await Release(app, definition, buildId, releaseDefinitionId, environmentId, ct, releaseId);
        if (release is null || environment is null || Number(environment, "id") != releaseEnvironmentId)
            throw new ArgumentException("Release environment changed; refresh before cancellation.");
        var denial = DeploymentCancellationDenial(release, environment, deploymentAttemptId, deploymentAttempt);
        if (denial is not null) throw new ArgumentException("Deployment cancellation blocked: " + denial);

        await users.RunDeploymentCancellationAsync(user, async (http, identity) =>
        {
            var path = Path(app, $"release/releases/{releaseId}/environments/{releaseEnvironmentId}", "4.1-preview.5");
            var current = await ReadAsync(http, path, ct);
            if (Number(current, "id") != releaseEnvironmentId || (Number(current, "releaseId") > 0 && Number(current, "releaseId") != releaseId))
                throw new ArgumentException("TFS returned a different release environment.");
            denial = DeploymentCancellationDenial(release, current, deploymentAttemptId, deploymentAttempt);
            if (denial is not null) throw new ArgumentException("Deployment cancellation blocked: " + denial);

            logger.LogInformation("Pipeline deployment cancellation requested: Application={Application}, BuildId={BuildId}, ReleaseId={ReleaseId}, EnvironmentId={EnvironmentId}, AttemptId={AttemptId}, Attempt={Attempt}, PortalSid={PortalSid}, TfsActor={TfsActor}.",
                app.Key, buildId, releaseId, releaseEnvironmentId, deploymentAttemptId, deploymentAttempt, PipelinePersonalSessions.Sid(user), identity);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { status = "canceled",
                comment = $"Deployment cancellation through Deployment Portal; attempt {deploymentAttempt}; TFS actor {identity}; Portal Windows SID {PipelinePersonalSessions.Sid(user) ?? "unavailable"}." });
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = content };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new PipelineDeploymentCancellationException((int)response.StatusCode, true);

            JsonNode confirmed;
            try { confirmed = await ReadAsync(http, path, ct); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
            { throw new PipelineDeploymentCancellationException((int?)(ex as HttpRequestException)?.StatusCode, true); }
            if (Number(confirmed, "id") != releaseEnvironmentId || Text(confirmed, "status") is not ("canceled" or "inProgress"))
                throw new PipelineDeploymentCancellationException((int)response.StatusCode, true);
            var confirmedAttempt = PipelineTimeline.LatestAttempt(confirmed);
            if (confirmedAttempt is null || Number(confirmedAttempt, "id") != deploymentAttemptId
                || Text(confirmedAttempt, "operationStatus") is not ("cancelling" or "canceled" or "phaseCanceled"))
                throw new PipelineDeploymentCancellationException((int)response.StatusCode, true);
            logger.LogInformation("Pipeline deployment cancellation confirmed: ReleaseId={ReleaseId}, EnvironmentId={EnvironmentId}, AttemptId={AttemptId}, PortalSid={PortalSid}, TfsActor={TfsActor}.",
                releaseId, releaseEnvironmentId, deploymentAttemptId, PipelinePersonalSessions.Sid(user), identity);
            return true;
        });
    }
}

public sealed class PipelineDeploymentCancellationException(int? httpStatus, bool mayHaveReachedTfs)
    : Exception("TFS deployment cancellation outcome is unconfirmed. Refresh release status before retrying.")
{
    public int? HttpStatus { get; } = httpStatus;
    public bool MayHaveReachedTfs { get; } = mayHaveReachedTfs;
}
