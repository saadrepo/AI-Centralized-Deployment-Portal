using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeploymentPortal.ViewModels.Tfs;

namespace DeploymentPortal.Services;

// Uses the CI client's collection URL, Windows credentials, timeout and redirect policy.
// TFS 2018 uses preview.3 for release definitions and preview.6 for releases.
public class TfsReleaseService(HttpClient client, IConfiguration configuration, TfsReleaseConfirmationStore confirmations, ILogger<TfsReleaseService> logger) : ITfsReleaseService
{
    public Task<IReadOnlyList<TfsReleaseDefinitionViewModel>> GetDefinitionsAsync(string? search = null)
        => GetListAsync<TfsReleaseDefinitionViewModel>(Path("definitions")
            + "&$top=50&$expand=artifacts&queryOrder=nameAscending"
            + (string.IsNullOrWhiteSpace(search) ? "" : "&searchText=" + Uri.EscapeDataString(search.Trim())), 50);

    public Task<TfsReleaseDefinitionViewModel> GetDefinitionAsync(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return GetDefinitionWithDiagnosticsAsync(id);
    }

    public Task<IReadOnlyList<TfsReleaseViewModel>> GetReleasesAsync(int definitionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(definitionId);
        return GetListAsync<TfsReleaseViewModel>(Path("releases")
            + $"&definitionId={definitionId}&$top=20&queryOrder=descending&$expand=artifacts,environments", 20);
    }

    public Task<TfsReleaseViewModel> GetReleaseAsync(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return GetAsync<TfsReleaseViewModel>(Path($"releases/{id}"), id);
    }

    public async Task<TfsReleaseViewModel> GetReleaseDetailsAsync(int id)
    {
        var release = await GetReleaseAsync(id);
        await LoadDeploymentHistoryAsync(release);
        return release;
    }

    public async Task<TfsReleaseViewModel> GetLatestDeployableReleaseAsync(int definitionId)
    {
        logger.LogError("CD deploy resolve started. DefinitionId: {DefinitionId}.", definitionId);
        var releasesPath = Path("releases")
            + $"&definitionId={definitionId}&$top=20&queryOrder=descending";
        var releases = await GetListWithDiagnosticsAsync<TfsReleaseViewModel>(releasesPath, 20, "CD deploy resolve release list");
        logger.LogError("CD deploy resolve loaded release list. DefinitionId: {DefinitionId}. ReleaseCount: {ReleaseCount}. ActiveReleaseCount: {ActiveReleaseCount}.",
            definitionId, releases.Count, releases.Count(x => string.Equals(x.Status, "active", StringComparison.OrdinalIgnoreCase)));
        foreach (var candidate in releases.Where(x => string.Equals(x.Status, "active", StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogError("CD deploy resolve evaluating release. DefinitionId: {DefinitionId}. ReleaseId: {ReleaseId}. ReleaseName: {ReleaseName}. ReleaseStatus: {ReleaseStatus}.",
                definitionId, candidate.Id, candidate.Name, candidate.Status);
            var release = await GetReleaseDetailsWithDiagnosticsAsync(candidate.Id, "CD deploy resolve release details");
            if (release.ReleaseDefinition?.Id != definitionId)
            {
                logger.LogError("CD deploy resolve rejected release due to definition mismatch. ExpectedDefinitionId: {ExpectedDefinitionId}. ReleaseId: {ReleaseId}. ActualDefinitionId: {ActualDefinitionId}.",
                    definitionId, release.Id, release.ReleaseDefinition?.Id);
                throw new JsonException("TFS returned a release for the wrong definition.");
            }
            if (ContainsProductionEnvironment(release))
            {
                logger.LogError("CD deploy resolve skipped release because it contains a Production/Prod environment. DefinitionId: {DefinitionId}. ReleaseId: {ReleaseId}. Environments: {Environments}.",
                    definitionId, release.Id, string.Join(", ", (release.Environments ?? []).Select(x => $"{x.Name}({x.Status})")));
                continue;
            }
            var deployableEnvironment = (release.Environments ?? []).FirstOrDefault(environment =>
            {
                var reason = ExplainDeployEligibility(environment);
                var action = TfsReleaseDeploymentEligibility.GetAction(environment);
                var eligible = action is not TfsReleaseEnvironmentAction.None;
                logger.LogError("CD deploy resolve environment eligibility. DefinitionId: {DefinitionId}. ReleaseId: {ReleaseId}. EnvironmentId: {EnvironmentId}. DefinitionEnvironmentId: {DefinitionEnvironmentId}. EnvironmentName: {EnvironmentName}. EnvironmentStatus: {EnvironmentStatus}. LatestDeploymentStatus: {LatestDeploymentStatus}. LatestOperationStatus: {LatestOperationStatus}. PendingApprovalCount: {PendingApprovalCount}. Eligible: {Eligible}. Reason: {Reason}.",
                    definitionId, release.Id, environment.Id, environment.DefinitionEnvironmentId, environment.Name, environment.Status,
                    environment.LatestDeployment?.Status, environment.LatestDeployment?.OperationStatus, environment.PendingApprovals.Count,
                    eligible, $"{action}: {reason}");
                return eligible;
            });
            if (deployableEnvironment is not null)
            {
                logger.LogError("CD deploy resolve found deployable release environment. DefinitionId: {DefinitionId}. ReleaseId: {ReleaseId}. EnvironmentId: {EnvironmentId}. EnvironmentName: {EnvironmentName}.",
                    definitionId, release.Id, deployableEnvironment.Id, deployableEnvironment.Name);
                return release;
            }
        }
        logger.LogError("CD deploy resolve found no eligible release. DefinitionId: {DefinitionId}.", definitionId);
        throw new ArgumentException("No existing active release currently has an environment that is safe to deploy.");
    }

    public async Task<TfsReleaseDeployReviewViewModel> ReviewDeployAsync(int releaseId, int environmentId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(releaseId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(environmentId);
        var release = await GetReleaseDetailsAsync(releaseId);
        var environment = ValidateDeployableEnvironment(release, environmentId);
        var latest = environment.LatestDeployment;
        var definitionId = release.ReleaseDefinition?.Id ?? 0;
        var review = new TfsReleaseDeployConfirmation(release.Id, definitionId, environment.DefinitionEnvironmentId,
            environment.Id, release.Name!, environment.Name!, environment.Status!, latest?.Attempt,
            latest?.Status, latest?.OperationStatus);
        return new TfsReleaseDeployReviewViewModel
        {
            ReleaseId = release.Id,
            ReleaseDefinitionId = definitionId,
            DefinitionEnvironmentId = environment.DefinitionEnvironmentId,
            EnvironmentId = environment.Id,
            ReleaseName = release.Name,
            EnvironmentName = environment.Name,
            EnvironmentStatus = environment.Status,
            LatestAttempt = latest?.Attempt,
            LatestDeploymentStatus = latest?.Status,
            LatestOperationStatus = latest?.OperationStatus,
            ConfirmationToken = confirmations.Add(review)
        };
    }

    public async Task<TfsReleaseViewModel> DeployEnvironmentAsync(string confirmationToken)
    {
        var review = confirmations.Take(confirmationToken);
        TfsReleaseViewModel release;
        try { release = await GetReleaseDetailsAsync(review.ReleaseId); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            throw new TfsReleaseRequestException(review.ReleaseId, review.EnvironmentId, "release validation", false,
                (ex as HttpRequestException)?.StatusCode, inner: ex);
        }

        var environment = ValidateDeployableEnvironment(release, review.EnvironmentId);
        var latest = environment.LatestDeployment;
        if (release.ReleaseDefinition?.Id != review.ReleaseDefinitionId
            || environment.DefinitionEnvironmentId != review.DefinitionEnvironmentId
            || release.Name != review.ReleaseName
            || environment.Name != review.EnvironmentName
            || !string.Equals(environment.Status, review.EnvironmentStatus, StringComparison.OrdinalIgnoreCase)
            || latest?.Attempt != review.LatestAttempt
            || !string.Equals(latest?.Status, review.LatestDeploymentStatus, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(latest?.OperationStatus, review.LatestOperationStatus, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The release environment changed after review. Refresh the release details and review the deployment again.");

        HttpResponseMessage response;
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { status = "inProgress", comment = "Deployment started from Deployment Portal after confirmation." });
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            response = await client.PatchAsync(Path($"releases/{review.ReleaseId}/environments/{review.EnvironmentId}", "4.1-preview.5"), content);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new TfsReleaseRequestException(review.ReleaseId, review.EnvironmentId, "sending deployment request", true, inner: ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new TfsReleaseRequestException(review.ReleaseId, review.EnvironmentId, "TFS response",
                    (int)response.StatusCode >= 500, response.StatusCode);
        }

        return await GetReleaseDetailsAsync(review.ReleaseId);
    }

    private async Task<T> GetAsync<T>(string path, int id) where T : TfsReleaseResourceViewModel
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var model = await response.Content.ReadFromJsonAsync<T>();
        if (model is null || model.Id != id || string.IsNullOrWhiteSpace(model.Name))
            throw new JsonException("TFS returned an invalid release response.");
        return model;
    }

    private async Task<TfsReleaseViewModel> GetReleaseDetailsWithDiagnosticsAsync(int id, string operation)
    {
        var release = await GetAsyncWithDiagnosticsAsync<TfsReleaseViewModel>(Path($"releases/{id}"), id, operation);
        await LoadDeploymentHistoryWithDiagnosticsAsync(release, operation + " deployment history");
        return release;
    }

    private async Task<T> GetAsyncWithDiagnosticsAsync<T>(string path, int id, string operation) where T : TfsReleaseResourceViewModel
    {
        var model = await GetJsonWithDiagnosticsAsync<T>(path, operation);
        if (model is null || model.Id != id || string.IsNullOrWhiteSpace(model.Name))
        {
            var ex = new JsonException("TFS returned an invalid release response.");
            logger.LogError(ex, "{Operation} JSON validation failed. RequestUri: {RequestUri}. ExpectedId: {ExpectedId}. ActualId: {ActualId}. ActualName: {ActualName}. Exception: {ExceptionType}: {ExceptionMessage}",
                operation, SafeRequestUri(path), id, model?.Id, model?.Name, ex.GetType().FullName, ex.Message);
            throw ex;
        }
        return model;
    }

    private async Task<TfsReleaseDefinitionViewModel> GetDefinitionWithDiagnosticsAsync(int id)
    {
        var path = Path($"definitions/{id}") + "&$expand=artifacts,environments";
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(path);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            logger.LogError(ex, "CD release definition GET failed before receiving a response. RequestUri: {RequestUri}. Exception: {ExceptionType}: {ExceptionMessage}",
                SafeRequestUri(path), ex.GetType().FullName, ex.Message);
            throw;
        }

        using (response)
        {
            var body = await ReadBodyForJsonAsync(response);
            var diagnosticBody = TruncateForLog(body);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("CD release definition GET returned non-success. RequestUri: {RequestUri}. StatusCode: {StatusCode}. ReasonPhrase: {ReasonPhrase}. ResponseBody: {ResponseBody}",
                    SafeRequestUri(path), (int)response.StatusCode, response.ReasonPhrase, diagnosticBody);
                response.EnsureSuccessStatusCode();
            }

            TfsReleaseDefinitionViewModel? model;
            try
            {
                model = JsonSerializer.Deserialize<TfsReleaseDefinitionViewModel>(body, JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                logger.LogError(ex, "CD release definition JSON deserialization failed. RequestUri: {RequestUri}. StatusCode: {StatusCode}. ReasonPhrase: {ReasonPhrase}. Exception: {ExceptionType}: {ExceptionMessage}. ResponseBody: {ResponseBody}",
                    SafeRequestUri(path), (int)response.StatusCode, response.ReasonPhrase, ex.GetType().FullName, ex.Message, diagnosticBody);
                throw;
            }

            if (model is null || model.Id != id || string.IsNullOrWhiteSpace(model.Name))
            {
                var ex = new JsonException("TFS returned an invalid release definition response.");
                logger.LogError(ex, "CD release definition JSON validation failed. RequestUri: {RequestUri}. StatusCode: {StatusCode}. ReasonPhrase: {ReasonPhrase}. Exception: {ExceptionType}: {ExceptionMessage}. ResponseBody: {ResponseBody}",
                    SafeRequestUri(path), (int)response.StatusCode, response.ReasonPhrase, ex.GetType().FullName, ex.Message, diagnosticBody);
                throw ex;
            }

            return model;
        }
    }

    private static async Task<string> ReadBodyForJsonAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return $"[Unable to read response body: {ex.GetType().FullName}: {ex.Message}]";
        }
    }

    private string SafeRequestUri(string path)
    {
        if (client.BaseAddress is null)
            return path;
        var uri = new Uri(client.BaseAddress, path);
        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        return builder.Uri.AbsoluteUri;
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, int limit) where T : TfsReleaseResourceViewModel
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<ListResponse<T>>();
        if (page?.Value is null || page.Value.Any(x => x is null || x.Id <= 0 || string.IsNullOrWhiteSpace(x.Name)))
            throw new JsonException("TFS returned an invalid release list response.");
        return page.Value.Take(limit).ToList();
    }

    private async Task<IReadOnlyList<T>> GetListWithDiagnosticsAsync<T>(string path, int limit, string operation) where T : TfsReleaseResourceViewModel
    {
        var page = await GetJsonWithDiagnosticsAsync<ListResponse<T>>(path, operation);
        if (page?.Value is null || page.Value.Any(x => x is null || x.Id <= 0 || string.IsNullOrWhiteSpace(x.Name)))
        {
            var ex = new JsonException("TFS returned an invalid release list response.");
            logger.LogError(ex, "{Operation} JSON validation failed. RequestUri: {RequestUri}. ItemCount: {ItemCount}. Exception: {ExceptionType}: {ExceptionMessage}",
                operation, SafeRequestUri(path), page?.Value?.Count, ex.GetType().FullName, ex.Message);
            throw ex;
        }
        return page.Value.Take(limit).ToList();
    }

    private async Task LoadDeploymentHistoryAsync(TfsReleaseViewModel release)
    {
        var definitionId = release.ReleaseDefinition?.Id ?? 0;
        if (definitionId <= 0 || release.Environments is not { Count: > 0 }) return;
        using var response = await client.GetAsync(Path("deployments", "4.1-preview.2")
            + $"&definitionId={definitionId}&$top=100&queryOrder=descending&latestAttemptsOnly=true");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<ListResponse<TfsReleaseDeploymentViewModel>>();
        if (page?.Value is null || page.Value.Any(x => x is null))
            throw new JsonException("TFS returned an invalid release deployments response.");
        foreach (var environment in release.Environments.Where(x => x is not null))
        {
            var deployment = page.Value.FirstOrDefault(x => x.Release?.IdMatches(release.Id) == true
                && x.ReleaseEnvironment?.IdMatches(environment.Id) == true
                && x.DefinitionEnvironmentId == environment.DefinitionEnvironmentId);
            if (deployment is null) continue;
            environment.DeploySteps ??= [];
            if (!environment.DeploySteps.Any(x => x.Id == deployment.Id))
                environment.DeploySteps.Add(new TfsReleaseDeploymentAttemptViewModel
                {
                    Id = deployment.Id,
                    Attempt = deployment.Attempt,
                    Status = deployment.DeploymentStatus,
                    OperationStatus = deployment.OperationStatus,
                    QueuedOn = deployment.QueuedOn,
                    LastModifiedOn = deployment.LastModifiedOn
                });
        }
    }

    private async Task LoadDeploymentHistoryWithDiagnosticsAsync(TfsReleaseViewModel release, string operation)
    {
        var definitionId = release.ReleaseDefinition?.Id ?? 0;
        if (definitionId <= 0 || release.Environments is not { Count: > 0 })
        {
            logger.LogError("{Operation} skipped. ReleaseId: {ReleaseId}. DefinitionId: {DefinitionId}. EnvironmentCount: {EnvironmentCount}.",
                operation, release.Id, definitionId, release.Environments?.Count ?? 0);
            return;
        }

        var path = Path("deployments", "4.1-preview.2")
            + $"&definitionId={definitionId}&$top=100&queryOrder=descending&latestAttemptsOnly=true";
        var page = await GetJsonWithDiagnosticsAsync<ListResponse<TfsReleaseDeploymentViewModel>>(path, operation);
        if (page?.Value is null || page.Value.Any(x => x is null))
        {
            var ex = new JsonException("TFS returned an invalid release deployments response.");
            logger.LogError(ex, "{Operation} JSON validation failed. RequestUri: {RequestUri}. DeploymentCount: {DeploymentCount}. Exception: {ExceptionType}: {ExceptionMessage}",
                operation, SafeRequestUri(path), page?.Value?.Count, ex.GetType().FullName, ex.Message);
            throw ex;
        }

        logger.LogError("{Operation} loaded deployments. ReleaseId: {ReleaseId}. DefinitionId: {DefinitionId}. DeploymentCount: {DeploymentCount}.",
            operation, release.Id, definitionId, page.Value.Count);
        foreach (var environment in release.Environments.Where(x => x is not null))
        {
            var deployment = page.Value.FirstOrDefault(x => x.Release?.IdMatches(release.Id) == true
                && x.ReleaseEnvironment?.IdMatches(environment.Id) == true
                && x.DefinitionEnvironmentId == environment.DefinitionEnvironmentId);
            if (deployment is null)
            {
                logger.LogError("{Operation} found no matching latest deployment for environment. ReleaseId: {ReleaseId}. EnvironmentId: {EnvironmentId}. DefinitionEnvironmentId: {DefinitionEnvironmentId}. EnvironmentName: {EnvironmentName}.",
                    operation, release.Id, environment.Id, environment.DefinitionEnvironmentId, environment.Name);
                continue;
            }
            logger.LogError("{Operation} matched latest deployment. ReleaseId: {ReleaseId}. EnvironmentId: {EnvironmentId}. DeploymentId: {DeploymentId}. Attempt: {Attempt}. DeploymentStatus: {DeploymentStatus}. OperationStatus: {OperationStatus}.",
                operation, release.Id, environment.Id, deployment.Id, deployment.Attempt, deployment.DeploymentStatus, deployment.OperationStatus);
            environment.DeploySteps ??= [];
            if (!environment.DeploySteps.Any(x => x.Id == deployment.Id))
                environment.DeploySteps.Add(new TfsReleaseDeploymentAttemptViewModel
                {
                    Id = deployment.Id,
                    Attempt = deployment.Attempt,
                    Status = deployment.DeploymentStatus,
                    OperationStatus = deployment.OperationStatus,
                    QueuedOn = deployment.QueuedOn,
                    LastModifiedOn = deployment.LastModifiedOn
                });
        }
    }

    private async Task<T?> GetJsonWithDiagnosticsAsync<T>(string path, string operation)
    {
        HttpResponseMessage response;
        try
        {
            logger.LogError("{Operation} TFS GET. RequestUri: {RequestUri}.", operation, SafeRequestUri(path));
            response = await client.GetAsync(path);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            logger.LogError(ex, "{Operation} TFS GET failed before receiving a response. RequestUri: {RequestUri}. Exception: {ExceptionType}: {ExceptionMessage}",
                operation, SafeRequestUri(path), ex.GetType().FullName, ex.Message);
            throw;
        }

        using (response)
        {
            var body = await ReadBodyForJsonAsync(response);
            var diagnosticBody = TruncateForLog(body);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("{Operation} TFS GET returned non-success. RequestUri: {RequestUri}. StatusCode: {StatusCode}. ReasonPhrase: {ReasonPhrase}. ResponseBody: {ResponseBody}",
                    operation, SafeRequestUri(path), (int)response.StatusCode, response.ReasonPhrase, diagnosticBody);
                response.EnsureSuccessStatusCode();
            }

            try
            {
                return JsonSerializer.Deserialize<T>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "{Operation} JSON deserialization failed. RequestUri: {RequestUri}. StatusCode: {StatusCode}. ReasonPhrase: {ReasonPhrase}. JsonPath: {JsonPath}. BytePositionInLine: {BytePositionInLine}. LineNumber: {LineNumber}. Exception: {ExceptionType}: {ExceptionMessage}. ResponseBody: {ResponseBody}",
                    operation, SafeRequestUri(path), (int)response.StatusCode, response.ReasonPhrase, ex.Path, ex.BytePositionInLine,
                    ex.LineNumber, ex.GetType().FullName, ex.Message, diagnosticBody);
                throw;
            }
            catch (NotSupportedException ex)
            {
                logger.LogError(ex, "{Operation} JSON deserialization failed. RequestUri: {RequestUri}. StatusCode: {StatusCode}. ReasonPhrase: {ReasonPhrase}. Exception: {ExceptionType}: {ExceptionMessage}. ResponseBody: {ResponseBody}",
                    operation, SafeRequestUri(path), (int)response.StatusCode, response.ReasonPhrase, ex.GetType().FullName, ex.Message, diagnosticBody);
                throw;
            }
        }
    }

    private static string TruncateForLog(string body)
        => body.Length <= 64 * 1024 ? body : body[..(64 * 1024)] + " [truncated]";

    private static TfsReleaseEnvironmentViewModel ValidateDeployableEnvironment(TfsReleaseViewModel release, int environmentId)
    {
        if (release.Id <= 0 || string.IsNullOrWhiteSpace(release.Name) || release.ReleaseDefinition?.Id is not > 0)
            throw new ArgumentException("Select a valid active release before deploying.");
        if (!string.Equals(release.Status, "active", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only active releases can be deployed.");
        var environment = (release.Environments ?? []).SingleOrDefault(x => x.Id == environmentId)
            ?? throw new ArgumentException("This environment was not found on the selected release.");
        if (TfsReleaseDeploymentEligibility.IsProduction(environment.Name))
            throw new ArgumentException("Production environments cannot be deployed from this portal.");
        if (ContainsProductionEnvironment(release, environment.Id))
            throw new ArgumentException("This release includes a Production environment. Deployment is disabled until production advancement rules are explicitly modeled.");
        if (TfsReleaseDeploymentEligibility.HasConflictingWorkflow(environment))
            throw new ArgumentException("This environment already has an active deployment, pending approval, gate, or manual intervention.");
        if (!TfsReleaseDeploymentEligibility.CanDeploy(environment))
            throw new ArgumentException("This environment is not in a deployable or redeployable state.");
        return environment;
    }

    private static bool ContainsProductionEnvironment(TfsReleaseViewModel release, int exceptEnvironmentId = 0)
        => (release.Environments ?? []).Any(x => x.Id != exceptEnvironmentId && TfsReleaseDeploymentEligibility.IsProduction(x.Name));

    private static string ExplainDeployEligibility(TfsReleaseEnvironmentViewModel environment)
    {
        if (environment.Id <= 0) return "environment id is missing";
        if (environment.DefinitionEnvironmentId <= 0) return "definition environment id is missing";
        if (TfsReleaseDeploymentEligibility.IsProduction(environment.Name)) return "environment name is Production/Prod";
        if (environment.PendingApprovals.Count > 0) return "pending approvals exist";
        if (environment.Action is TfsReleaseEnvironmentAction.Redeploy) return "latest deployment attempt is terminal and can be redeployed";
        if (environment.Status is "inProgress" or "queued" or "scheduled") return "environment has active status";
        var latest = environment.LatestDeployment;
        if (latest is null) return "eligible: no prior deployment attempt";
        if (latest.Status is "inProgress") return "latest deployment is in progress";
        if (!string.IsNullOrWhiteSpace(latest.Status)
            && !string.Equals(latest.Status, "notDeployed", StringComparison.OrdinalIgnoreCase))
            return "latest deployment status is not deployable";
        if (!string.Equals(environment.Status, "notStarted", StringComparison.OrdinalIgnoreCase))
            return "environment status is not notStarted";
        if (!string.IsNullOrWhiteSpace(latest.OperationStatus)
            && !string.Equals(latest.OperationStatus, "undefined", StringComparison.OrdinalIgnoreCase))
            return "latest operation status is not undefined";
        return "eligible";
    }

    private string Path(string resource, string? apiVersion = null)
    {
        var project = configuration["Tfs:Project"];
        if (string.IsNullOrWhiteSpace(project)) throw new InvalidOperationException("TFS project must be configured.");
        var version = apiVersion ?? (resource.StartsWith("definitions", StringComparison.Ordinal) ? "4.1-preview.3" : "4.1-preview.6");
        return $"{Uri.EscapeDataString(project)}/_apis/release/{resource}?api-version={version}";
    }

    private sealed class ListResponse<T> { public List<T>? Value { get; set; } }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class TfsReleaseDeploymentViewModel
    {
        public int Id { get; set; }
        public int Attempt { get; set; }
        public int DefinitionEnvironmentId { get; set; }
        public string? DeploymentStatus { get; set; }
        public string? OperationStatus { get; set; }
        public DateTimeOffset? QueuedOn { get; set; }
        public DateTimeOffset? LastModifiedOn { get; set; }
        public TfsReleaseDeploymentReference? Release { get; set; }
        public TfsReleaseDeploymentReference? ReleaseEnvironment { get; set; }
    }

    private sealed class TfsReleaseDeploymentReference
    {
        public JsonElement Id { get; set; }
        public string? Name { get; set; }

        public bool IdMatches(int expected)
        {
            if (Id.ValueKind == JsonValueKind.Number && Id.TryGetInt32(out var number))
                return number == expected;
            if (Id.ValueKind == JsonValueKind.String)
                return string.Equals(Id.GetString(), expected.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            return false;
        }
    }
}

public class TfsReleaseRequestException(
    int releaseId,
    int environmentId,
    string stage,
    bool mayHaveStarted,
    System.Net.HttpStatusCode? statusCode = null,
    Exception? inner = null)
    : Exception($"TFS release deployment request failed during {stage}.", inner)
{
    public int ReleaseId { get; } = releaseId;
    public int EnvironmentId { get; } = environmentId;
    public string Stage { get; } = stage;
    public bool MayHaveStarted { get; } = mayHaveStarted;
    public System.Net.HttpStatusCode? StatusCode { get; } = statusCode;
}
