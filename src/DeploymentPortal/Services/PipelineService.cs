using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using static DeploymentPortal.Services.PipelineState;

namespace DeploymentPortal.Services;

public sealed partial class PipelineService(HttpClient client, ISiteRegistry registry, IOptions<TfsCdOptions> gates,
    IPipelineUserTransport users, ILogger<PipelineService> logger, IPipelineHealthCheck health, IOptions<PipelineCiOptions> ci)
{
    public IReadOnlyList<PipelineOption> Applications() => registry.List()
        .Where(x => x.TfsGitCiDefinitionId > 0).Select(x => new PipelineOption(x.Key, x.DisplayName)).ToList();

    private RegistryApplication App(string key) => registry.Find(key) is { TfsGitCiDefinitionId: > 0 } app
        && !string.IsNullOrWhiteSpace(app.TfsGitProject) && !string.IsNullOrWhiteSpace(app.TfsGitRepository)
            ? app : throw new ArgumentException("Select an application with a configured TFS project, repository and CI definition.");
    private static string Path(RegistryApplication app, string resource, string version = "4.1") =>
        $"{Uri.EscapeDataString(app.TfsGitProject)}/_apis/{resource}?api-version={version}";

    public static async Task<JsonNode> ReadAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + count > 2_000_000) throw new InvalidOperationException("TFS response exceeds the display limit.");
            memory.Write(buffer, 0, count);
        }
        return JsonNode.Parse(memory.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 64 })
            ?? throw new JsonException("Empty TFS response.");
    }

    private async Task<List<JsonNode>> List(string path, CancellationToken ct, int? limit = null)
    {
        var result = new List<JsonNode>();
        string? token = null;
        for (var page = 0; page < 10; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path + (token is null ? "" : "&continuationToken=" + Uri.EscapeDataString(token)));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            // Metadata pages are bounded independently of log responses.
            await response.Content.LoadIntoBufferAsync(2_000_000, ct);
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (node?["value"] is not JsonArray) throw new JsonException("Invalid TFS collection.");
            result.AddRange(Items(node["value"]));
            if (limit is { } maximum && result.Count >= maximum) return result.Take(maximum).ToList();
            if (!response.Headers.TryGetValues("x-ms-continuationtoken", out var values)) return result;
            var next = values.FirstOrDefault();
            if (string.IsNullOrEmpty(next)) return result;
            if (next == token) throw new InvalidOperationException("TFS repeated a continuation token.");
            token = next;
        }
        throw new InvalidOperationException("TFS returned too many metadata pages; narrow the selection.");
    }

    private async Task<JsonNode> Definition(RegistryApplication app, CancellationToken ct, HttpClient? http = null)
    {
        var definition = await ReadAsync(http ?? client, Path(app, $"build/definitions/{app.TfsGitCiDefinitionId}"), ct);
        var repo = definition["repository"];
        if (Number(definition, "id") != app.TfsGitCiDefinitionId || Text(repo, "type") != "TfsGit"
            || !(string.Equals(Text(repo, "id"), app.TfsGitRepository, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Text(repo, "name"), app.TfsGitRepository, StringComparison.OrdinalIgnoreCase))
            || !string.Equals(Text(definition["project"], "name"), app.TfsGitProject, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TFS definition does not match the registered application.");
        return definition;
    }

    private async Task<List<PipelineOption>> Branches(RegistryApplication app, JsonNode definition, CancellationToken ct) =>
        (await List(Path(app, $"git/repositories/{Uri.EscapeDataString(Text(definition["repository"], "id")!)}/refs") + "&filter=heads/&$top=100", ct))
        .Where(x => Text(x, "name")?.StartsWith("refs/heads/", StringComparison.Ordinal) == true)
        .Select(x => new PipelineOption(Text(x, "name")!, Text(x, "name")![11..])).ToList();

    private async Task<List<JsonNode>> ReleaseDefinitions(RegistryApplication app, JsonNode definition, CancellationToken ct) =>
        (await List(Path(app, "release/definitions", "4.1-preview.3") + "&$expand=artifacts&$top=100", ct))
        .Where(x => Items(x["artifacts"]).Any(a => ArtifactMatches(a, app.TfsGitCiDefinitionId!.Value, Text(definition["project"], "id")!))).ToList();

    public async Task<object> Options(string key, ClaimsPrincipal user, CancellationToken ct)
    {
        var app = App(key);
        var definition = await Definition(app, ct);
        var branches = await Branches(app, definition, ct);
        var environments = new List<PipelineEnvironment>();
        string? releaseWarning = null;
        try
        {
            foreach (var release in await ReleaseDefinitions(app, definition, ct))
            {
                var detail = await ReadAsync(client, Path(app, $"release/definitions/{Number(release, "id")}", "4.1-preview.3"), ct);
                if (!Items(detail["artifacts"]).Any(a => ArtifactMatches(a, app.TfsGitCiDefinitionId!.Value, Text(definition["project"], "id")!))) continue;
                environments.AddRange(Items(detail["environments"]).Select(e => new PipelineEnvironment(Number(detail, "id"), Number(e, "id"), $"{Text(detail, "name")} / {Text(e, "name")}")));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        { releaseWarning = "Release metadata unavailable; CI selection remains available."; }
        var recent = await List(Path(app, "build/builds") + $"&definitions={app.TfsGitCiDefinitionId}&$top=25&queryOrder=queueTimeDescending", ct, 25);
        var successful = await List(Path(app, "build/builds") + $"&definitions={app.TfsGitCiDefinitionId}&statusFilter=completed&resultFilter=succeeded&$top=1&queryOrder=finishTimeDescending", ct, 1);
        foreach (var success in successful.Where(s => recent.All(b => Number(b, "id") != Number(s, "id")))) recent.Add(success);
        return new
        {
            definition = new PipelineOption(app.TfsGitCiDefinitionId!.Value.ToString(), Text(definition, "name")!),
            branches, environments,
            defaultBranch = Text(definition["repository"], "defaultBranch"),
            builds = recent.Select(b => new { id = Number(b, "id"), name = Text(b, "buildNumber"), branch = Text(b, "sourceBranch"), status = Text(b, "status"), result = Text(b, "result"), time = Text(b, "queueTime") }),
            canQueue = CanQueue(user),
            executionReason = QueueReason(user),
            releaseWarning
        };
    }

    public bool CanExecute(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && gates.Value.Enabled
        && gates.Value.ExecutionEnabled && users.Available(user);
    public bool CanQueue(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && ci.Value.QueueEnabled && users.AvailableForBuild(user);
    private string QueueReason(ClaimsPrincipal user) => !ci.Value.QueueEnabled
        ? "CI queueing is disabled. Configure Pipeline:Ci:QueueEnabled for this environment."
        : !users.AvailableForBuild(user) ? "CI requires your delegated Windows identity, or the same Windows user running local Development."
        : "CI is enabled; TFS validates the signed-in user's Queue builds permission. Deployment approval remains separately protected.";
    private string ExecutionReason(ClaimsPrincipal user) => !gates.Value.Enabled || !gates.Value.ExecutionEnabled
        ? "TfsCd execution is disabled by effective server configuration."
        : !users.Available(user) ? "CD requires a delegated Windows login, or the same Windows user running local Development; TFS must verify that SID." : "TFS checks the signed-in user's permissions.";
    private void RequireExecution(ClaimsPrincipal user)
    {
        if (!CanExecute(user)) throw new UnauthorizedAccessException(ExecutionReason(user));
    }

    public async Task<object> QueueReadiness(string key, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!CanQueue(user)) throw new UnauthorizedAccessException(QueueReason(user));
        var app = App(key);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        ct = deadline.Token;
        try
        {
            return await users.RunBuildAsync(user, async (http, identity) =>
            {
                var definition = await Definition(app, ct, http);
                if (Text(definition, "queueStatus") != "enabled")
                    throw new PipelineQueueException("CI_DEFINITION_NOT_ENABLED", "read-only queue readiness", "TFS definition is paused, disabled or its queue status is unavailable.");
                if (Number(definition["queue"], "id") <= 0)
                    throw new PipelineQueueException("CI_AGENT_QUEUE_MISSING", "read-only queue readiness", "TFS definition has no valid agent queue.");
                return (object)new { ready = true, message = "TFS Windows identity and definition verified. TFS enforces Queue builds permission when you submit a build; no build was queued by this check." };
            });
        }
        catch (PipelineQueueException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { throw PipelineQueueException.From(ex, "read-only queue readiness"); }
    }

    private async Task<JsonNode> Build(RegistryApplication app, int id, JsonNode definition, CancellationToken ct, HttpClient? http = null)
    {
        if (id <= 0) throw new ArgumentException("Select a valid build.");
        var build = await ReadAsync(http ?? client, Path(app, $"build/builds/{id}"), ct);
        if (Number(build, "id") != id || Number(build["definition"], "id") != app.TfsGitCiDefinitionId
            || !string.Equals(Text(build["project"], "id"), Text(definition["project"], "id"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Text(build["repository"], "id"), Text(definition["repository"], "id"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Build does not belong to this application.");
        return build;
    }

    public async Task<int> Queue(string key, string branch, ClaimsPrincipal user, CancellationToken ct, int selectedBuildId = 0)
    {
        if (!CanQueue(user)) throw new UnauthorizedAccessException(QueueReason(user));
        var app = App(key);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        ct = deadline.Token;
        logger.LogInformation("CI queue requested: Application={Application}, DefinitionId={DefinitionId}, User={User}.", app.Key, app.TfsGitCiDefinitionId, user.Identity?.Name);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var operation = Guid.NewGuid().ToString("N");
        using var timingScope = logger.BeginScope("CI queue {QueueOperation}", operation);
        async Task<T> Timed<T>(string phase, Func<Task<T>> work)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { return await work(); }
            finally { logger.LogInformation("CI queue timing: Phase={Phase}, ElapsedMs={ElapsedMs}, TotalMs={TotalMs}.", phase, clock.ElapsedMilliseconds, elapsed.ElapsedMilliseconds); }
        }
        var stage = "build definition lookup";
        var sent = false;
        try
        {
            stage = "TFS Windows identity verification";
            var identityClock = System.Diagnostics.Stopwatch.StartNew();
            return await users.RunBuildAsync(user, async (http, identity) =>
            {
                logger.LogInformation("CI queue timing: Phase=WindowsIdentity, ElapsedMs={ElapsedMs}.", identityClock.ElapsedMilliseconds);
                stage = "build definition lookup";
                // One definition lookup under the verified caller; no duplicate service-account lookup.
                var current = await Timed("Definition", () => Definition(app, ct, http));
                stage = "branch and selected build validation";
                var branchesTask = Timed("Branches", () => Branches(app, current, ct));
                var selectedTask = selectedBuildId > 0
                    ? Timed("SelectedBuild", () => Build(app, selectedBuildId, current, ct, http))
                    : Task.FromResult<JsonNode>(new JsonObject());
                await Task.WhenAll(branchesTask, selectedTask);
                if (!(await branchesTask).Any(x => x.Id == branch)) throw new ArgumentException("Select an existing branch.");
                if (selectedBuildId > 0 && PipelineProgress.IsActive(Text(await selectedTask, "status")))
                    throw new PipelineQueueException("CI_SELECTED_BUILD_ACTIVE", "selected build validation", "Selected CI build is still queued/running. Wait for completion or cancel it before queueing another build.");
                if (!string.Equals(Text(current, "queueStatus"), "enabled", StringComparison.OrdinalIgnoreCase))
                    throw new PipelineQueueException("CI_DEFINITION_NOT_ENABLED", stage, "The TFS build definition is paused, disabled or its queue status is unavailable.");
                var queueId = Number(current["queue"], "id");
                if (queueId <= 0) throw new PipelineQueueException("CI_AGENT_QUEUE_MISSING", stage, "The selected TFS definition has no valid agent queue. Configure its queue in TFS; Portal did not change it.");
                var path = Path(app, "build/builds");
                // Classic TFS/IIS must receive a buffered UTF-8 JSON body with a known Content-Length.
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    definition = new { id = app.TfsGitCiDefinitionId },
                    queue = new { id = queueId }, project = new { id = Text(current["project"], "id") }, sourceBranch = branch
                });
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
                request.Headers.Accept.ParseAdd("application/json");
                logger.LogInformation("CI queue sending: Application={Application}, DefinitionId={DefinitionId}, QueueId={QueueId}, Endpoint={Endpoint}, ApiVersion=4.1, PayloadBytes={PayloadBytes}, User={User}.",
                    app.Key, app.TfsGitCiDefinitionId, queueId, http.BaseAddress is null ? "unavailable" : new Uri(http.BaseAddress, path).GetLeftPart(UriPartial.Path), payload.Length, user.Identity?.Name);
                stage = "sending queue POST";
                sent = true; // No mutation retries; after this point an interrupted response may be ambiguous.
                using var response = await Timed("QueuePostHeaders", () => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct));
                if (!response.IsSuccessStatusCode)
                {
                    string? safeType = null;
                    try
                    {
                        await response.Content.LoadIntoBufferAsync(16_384, ct);
                        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                        var type = Text(error, "typeKey");
                        if (type is { Length: > 0 and <= 120 } && type.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_')) safeType = type;
                    }
                    catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException or OperationCanceledException or IOException) { }
                    throw PipelineQueueException.Http(stage, response.StatusCode, true, safeType);
                }
                stage = "reading accepted queue response";
                var queued = await Timed("QueueResponseBody", async () => {
                    await response.Content.LoadIntoBufferAsync(131_072, ct);
                    return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                });
                var id = Number(queued, "id");
                if (id <= 0) throw new JsonException("Queue response did not contain a build ID.");
                logger.LogInformation("CI queue accepted: BuildId={BuildId}, DefinitionId={DefinitionId}, HttpStatus={HttpStatus}, User={User}.", id, app.TfsGitCiDefinitionId, (int)response.StatusCode, user.Identity?.Name);
                return id;
            });
        }
        catch (PipelineQueueException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        { throw PipelineQueueException.From(ex, stage, sent); }
        finally { logger.LogInformation("CI queue timing: Phase=Total, LastStage={Stage}, Sent={Sent}, ElapsedMs={ElapsedMs}.", stage, sent, elapsed.ElapsedMilliseconds); }
    }

    private async Task<(JsonNode Definition, JsonNode? Release, JsonNode? Environment)> Release(RegistryApplication app, JsonNode definition,
        int buildId, int releaseDefinitionId, int environmentId, CancellationToken ct, int? exactReleaseId = null)
    {
        if (!(await ReleaseDefinitions(app, definition, ct)).Any(x => Number(x, "id") == releaseDefinitionId))
            throw new ArgumentException("Release definition is not associated with this application's CI pipeline.");
        var rd = await ReadAsync(client, Path(app, $"release/definitions/{releaseDefinitionId}", "4.1-preview.3"), ct);
        if (!Items(rd["artifacts"]).Any(a => ArtifactMatches(a, app.TfsGitCiDefinitionId!.Value, Text(definition["project"], "id")!))
            || !Items(rd["environments"]).Any(e => Number(e, "id") == environmentId)) throw new ArgumentException("Invalid release environment mapping.");
        var candidates = exactReleaseId is > 0
            ? new List<JsonNode> { await ReadAsync(client, Path(app, $"release/releases/{exactReleaseId.Value}", "4.1-preview.6"), ct) }
            : await List(Path(app, "release/releases", "4.1-preview.6") + $"&definitionId={releaseDefinitionId}&artifactVersionId={buildId}&$expand=artifacts,environments&$top=100&queryOrder=descending", ct);
        var matches = candidates.Where(r => Number(r["releaseDefinition"], "id") == releaseDefinitionId
            && Items(r["artifacts"]).Any(a => ArtifactMatches(a, app.TfsGitCiDefinitionId!.Value, Text(definition["project"], "id")!, buildId))).ToList();
        if (matches.Count > 1) throw new InvalidOperationException("Multiple releases match this build; approval is disabled until the ambiguity is resolved.");
        if (matches.Count == 0) return (rd, null, null);
        var release = await ReadAsync(client, Path(app, $"release/releases/{Number(matches[0], "id")}", "4.1-preview.6"), ct);
        if (Number(release["releaseDefinition"], "id") != releaseDefinitionId
            || !Items(release["artifacts"]).Any(a => ArtifactMatches(a, app.TfsGitCiDefinitionId!.Value, Text(definition["project"], "id")!, buildId)))
            throw new ArgumentException("Release artifact changed; refresh the selection.");
        var environment = Items(release["environments"]).SingleOrDefault(e => Number(e, "definitionEnvironmentId") == environmentId)
            ?? throw new ArgumentException("Selected environment is missing from release.");
        return (rd, release, environment);
    }

    public async Task<PipelineSnapshot> Status(string key, int buildId, int releaseDefinitionId, int environmentId, ClaimsPrincipal user, CancellationToken ct, bool historyOnly = false)
    {
        var app = App(key);
        var definition = await Definition(app, ct);
        var build = await Build(app, buildId, definition, ct);
        if (DateTimeOffset.TryParse(Text(build, "queueTime"), out var queuedAt))
        {
            var started = DateTimeOffset.TryParse(Text(build, "startTime"), out var startedAt);
            logger.LogInformation("CI agent queue timing: BuildId={BuildId}, Started={Started}, WaitMs={WaitMs}, BuildStatus={BuildStatus}.",
                buildId, started, Math.Max(0, ((started ? startedAt : DateTimeOffset.UtcNow) - queuedAt).TotalMilliseconds), Text(build, "status"));
        }
        var result = new PipelineSnapshot { BuildId = buildId, BuildNumber = Text(build, "buildNumber"), Branch = Text(build, "sourceBranch"), Status = Text(build, "result") ?? Text(build, "status") ?? "Unknown", RefreshedAt = DateTimeOffset.UtcNow };
        JsonNode? timeline = null;
        try { timeline = await ReadAsync(client, Path(app, $"build/builds/{buildId}/timeline"), ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { result.Notices.Add("Build timeline is not available yet."); }
        foreach (var record in PipelineTimeline.BuildTasks(timeline))
        {
            result.Events.Add(new(Text(record, "name") ?? "Build task", Map(Text(record, "state"), Text(record, "result")), Text(record, "startTime"), Text(record, "finishTime")));
        }
        var artifacts = new List<JsonNode>();
        try { artifacts = await List(Path(app, $"build/builds/{buildId}/artifacts"), ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { result.Notices.Add("Build artifact metadata is not available yet."); }
        result.Stages = PipelineProgress.Stages(build, timeline, artifacts.Any(a => Text(a, "name") == app.TfsGitArtifactName));
        result.BuildActive = PipelineProgress.IsActive(Text(build, "status"));
        if (Text(build, "status") == "completed" && Text(build, "result") is "canceled" or "cancelled") result.Status = "Cancelled";
        else if (result.BuildActive) result.Status = Text(build, "status") == "cancelling" ? "Cancellation requested" : Text(build, "status") == "inProgress" ? "CI running" : "CI queued";
        // Fetch actual CI history independently of release readiness and keep it when CD becomes active.
        await BuildLogs(app, buildId, result, ct);
        result.CiEvents = result.Events.ToList();
        result.CiLogs = result.Logs.ToList();
        if (historyOnly) return result;
        await CancellationEligibility(app, definition, build, user, result, ct);
        if (Text(build, "status") != "completed" || Text(build, "result") != "succeeded")
        { return result; }
        if (releaseDefinitionId <= 0 || environmentId <= 0)
        { result.Notices.Add("Select a discovered release environment. If none exists, CD is not configured for this application."); return result; }
        result.Phase = "CD";
        result.Status = "Waiting for release status";
        result.TimelineMessage = "Waiting for the matching TFS release.";
        result.Events.Clear(); // Active CD remains separate; CiEvents/CiLogs retain real CI history.
        result.Logs.Clear();
        try
        {
            var (rd, release, environment) = await Release(app, definition, buildId, releaseDefinitionId, environmentId, ct);
            if (release is null || environment is null) { result.Notices.Add("Waiting for the existing TFS CD trigger to create the matching release. No release was created by the Portal."); return result; }
            result.ReleaseId = Number(release, "id"); result.EnvironmentId = Number(environment, "id");
            await HydratePendingApprovals(app, release, environment, ct);
            var approvals = PipelineTimeline.DisplayApprovals(environment).ToList();
            var pending = approvals.Where(a => Text(a, "status") == "pending").OrderBy(a => Number(a, "rank")).ToList();
            if (approvals.Any(a => Text(a, "status") == "rejected"))
            {
                result.Status = "Approval rejected";
                result.TimelineMessage = "Pre-deployment approval was rejected. Deployment is blocked.";
                result.Stages[3] = new("Approval", "failed", "Rejected");
                result.Stages[4] = new("Deploy to IIS", "pending", "Approval rejected");
                result.Stages[5] = new("Verify Health", "pending", "Deployment blocked");
                return result;
            }
            if (pending.Count > 0)
            {
                result.Status = "Approval Pending";
                result.TimelineMessage = "Approval pending. Deployment has not started.";
                foreach (var approval in pending.OrderBy(a => Number(a, "rank")))
                    result.Events.Add(new("Pre-deployment approval", "pending", Text(approval, "createdOn"), null));
                result.Stages[3] = new("Approval", "pending", "Approval Pending");
                result.ApprovalId = Number(pending[0], "id");
                result.ApprovalReason = ExecutionReason(user);
                if (CanExecute(user) && Text(build, "status") == "completed" && Text(build, "result") == "succeeded" && result.Stages[2].State == "completed")
                    await users.RunAsync(user, async (http, identity) =>
                    {
                        var eligible = await EligibleApprovals(http, app, Number(release, "id"), identity, ct);
                        var approval = pending.FirstOrDefault(a => ApprovalAllowed(rd, release, environment, a, identity, eligible.Contains(Number(a, "id"))));
                        result.CanApprove = approval is not null;
                        if (approval is not null) result.ApprovalId = Number(approval, "id");
                        result.ApprovalReason = result.CanApprove ? "TFS will revalidate approval permissions."
                            : ApprovalDenial(rd, release, environment, pending[0], identity, eligible.Contains(Number(pending[0], "id"))) ?? "TFS approval eligibility could not be confirmed.";
                        return true;
                    });
            }
            else if (approvals.Any(a => Text(a, "status") == "rejected")) result.Stages[3] = new("Approval", "failed", "Rejected");
            else if (approvals.Count > 0 && approvals.All(a => Text(a, "status") == "approved")) result.Stages[3] = new("Approval", "completed", "Approved in TFS");
            else
            {
                result.Stages[3] = new("Approval", "pending", "No completed manual approval evidence");
                result.ApprovalReason = $"No current pending approval is available from TFS. Environment: {Text(environment, "status") ?? "unknown"}; operation: {Text(PipelineTimeline.LatestAttempt(environment), "operationStatus") ?? "unavailable"}. Waiting for TFS queue/approval state.";
            }
            if (pending.Count > 0)
            {
                result.Stages[4] = new("Deploy to IIS", "pending", "Waiting for approval");
                result.Stages[5] = new("Verify Health", "pending", "Waiting for deployment");
                return result;
            }
            if (result.Stages[3].State != "completed")
            {
                result.Status = result.Stages[3].State == "failed" ? "Approval rejected" : "Waiting for approval status";
                result.TimelineMessage = result.Status;
                result.Stages[4] = new("Deploy to IIS", "pending", "Waiting for approval status");
                return result;
            }
            result.Stages[4] = PipelineTimeline.Deployment(environment);
            result.Status = result.Stages[4].State == "running" ? "CD running" : result.Stages[4].State == "failed" ? "Deployment failed" : result.Stages[4].Detail;
            result.TimelineMessage = "Waiting for deployment task status.";
            if (result.Stages[4].State == "pending")
            { result.TimelineMessage = result.Stages[4].Detail; return result; }
            await ReleaseLogs(app, result.ReleaseId.Value, environment, result, ct);
            if (result.Stages[4].State == "completed")
            {
                var check = await health.Check(app.Key, ct);
                result.HealthUrl = check.Url;
                result.Stages[5] = new("Verify Health", check.State, check.Detail);
                result.Status = check.State == "completed" ? (result.Stages.All(s => s.State == "completed") ? "Completed" : "Deployment succeeded; endpoint healthy") : check.State == "failed" ? "Health Failed" : "Deployment succeeded; health unverified";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            result.ApprovalReason = ex is HttpRequestException httpError
                ? $"TFS approval verification failed (HTTP {(int?)httpError.StatusCode}). Current-user permission is unverified."
                : ex is UnauthorizedAccessException ? "Windows/TFS identity could not be verified for approval."
                : "Release/approval details are unavailable or ambiguous.";
            result.Notices.Add(result.ApprovalReason + " Approval disabled; build data remains available."); result.CanApprove = false;
        }
        return result;
    }

    private async Task HydratePendingApprovals(RegistryApplication app, JsonNode release, JsonNode environment, CancellationToken ct)
    {
        var live = await List(Path(app, "release/approvals", "4.1-preview.2")
            + $"&releaseIdsFilter={Number(release, "id")}&typeFilter=preDeploy&statusFilter=pending&top=100", ct);
        var approvals = environment["preDeployApprovals"] as JsonArray;
        if (approvals is null) { approvals = new JsonArray(); environment["preDeployApprovals"] = approvals; }
        foreach (var approval in live.Where(a => Number(a, "id") > 0 && Text(a, "status") == "pending" && Text(a, "approvalType") == "preDeploy"
            && Number(a["release"], "id") == Number(release, "id") && Number(a["releaseEnvironment"], "id") == Number(environment, "id")))
        {
            var existing = Items(approvals).SingleOrDefault(a => Number(a, "id") == Number(approval, "id"));
            if (existing is null) approvals.Add(approval.DeepClone());
            else if (Text(existing, "status") != "rejected")
                foreach (var field in approval.AsObject())
                    if (field.Value is not null) existing[field.Key] = field.Value.DeepClone();
        }
    }

    private static async Task<HashSet<int>> EligibleApprovals(HttpClient http, RegistryApplication app, int releaseId, string identity, CancellationToken ct)
    {
        var node = await ReadAsync(http, Path(app, "release/approvals", "4.1-preview.2")
            + $"&assignedToFilter={Uri.EscapeDataString(identity)}&includeMyGroupApprovals=true&statusFilter=pending&typeFilter=preDeploy&releaseIdsFilter={releaseId}&top=100", ct);
        return Items(node["value"]).Where(a => Text(a, "status") == "pending" && Text(a, "approvalType") == "preDeploy"
            && Number(a["release"], "id") == releaseId).Select(a => Number(a, "id")).ToHashSet();
    }

    private static IEnumerable<JsonNode> CurrentApprovals(JsonNode environment)
    {
        // TFS can omit attempt on a live approval. Use the same current/unknown-attempt
        // rule for display and authorization; TFS still validates the decision/revision.
        return PipelineTimeline.DisplayApprovals(environment);
    }

    public static bool ApprovalAllowed(JsonNode rd, JsonNode release, JsonNode environment, JsonNode approval, string identity, bool eligibleGroup = false) =>
        ApprovalDenial(rd, release, environment, approval, identity, eligibleGroup) is null;

    private static string? ApprovalDenial(JsonNode rd, JsonNode release, JsonNode environment, JsonNode approval, string identity, bool eligibleGroup)
    {
        var definitionEnvironment = Items(rd["environments"]).SingleOrDefault(e => Number(e, "id") == Number(environment, "definitionEnvironmentId"));
        if (definitionEnvironment is null) return "Environment is not in the release definition.";
        if (Text(release, "status") != "active") return "Release is not active.";
        if (Text(approval, "status") != "pending" || Text(approval, "approvalType") != "preDeploy") return "No pending pre-deployment approval is available.";
        if (CurrentApprovals(environment).Any(a => Text(a, "status") == "rejected")) return "A current pre-deployment approval was rejected.";
        if (!CurrentApprovals(environment).Any(a => Number(a, "id") == Number(approval, "id"))) return "Approval belongs to a different deployment attempt.";
        if (!eligibleGroup && !string.Equals(Text(approval["approver"], "id"), identity, StringComparison.OrdinalIgnoreCase)) return "Current TFS user is not an eligible approver.";
        if (CurrentApprovals(environment).Any(a => Text(a, "status") == "pending" && Number(a, "rank") < Number(approval, "rank"))) return "An earlier approval rank is pending for this release.";
        var creatorAllowed = definitionEnvironment["preDeployApprovals"]?["approvalOptions"]?["releaseCreatorCanBeApprover"]?.ToString();
        if (!string.Equals(creatorAllowed, "true", StringComparison.OrdinalIgnoreCase)
            && (release["createdBy"]?["id"] is null || string.Equals(Text(release["createdBy"], "id"), identity, StringComparison.OrdinalIgnoreCase)))
            return "TFS requester/self-approval restriction prohibits this user, or requester identity is unavailable.";
        return null;
    }

    public Task Approve(string key, int buildId, int releaseDefinitionId, int environmentId, int expectedReleaseId, int approvalId, ClaimsPrincipal user, CancellationToken ct) =>
        DecideApproval(key, buildId, releaseDefinitionId, environmentId, expectedReleaseId, approvalId, user, ct, "approved");

    public Task Reject(string key, int buildId, int releaseDefinitionId, int environmentId, int expectedReleaseId, int approvalId, ClaimsPrincipal user, CancellationToken ct) =>
        DecideApproval(key, buildId, releaseDefinitionId, environmentId, expectedReleaseId, approvalId, user, ct, "rejected");

    private async Task DecideApproval(string key, int buildId, int releaseDefinitionId, int environmentId, int expectedReleaseId, int approvalId, ClaimsPrincipal user, CancellationToken ct, string decision)
    {
        RequireExecution(user);
        var stage = "definition/build validation";
        var submitted = false;
        try
        {
        var app = App(key); var definition = await Definition(app, ct);
        var build = await Build(app, buildId, definition, ct);
        if (decision == "approved" && (Text(build, "status") != "completed" || Text(build, "result") != "succeeded")) throw new ArgumentException("CI must succeed before approval.");
        stage = "release/build/environment correlation";
        var (rd, release, environment) = await Release(app, definition, buildId, releaseDefinitionId, environmentId, ct, decision == "rejected" ? expectedReleaseId : null);
        if (release is null || environment is null || Number(release, "id") != expectedReleaseId) throw new ArgumentException("Release changed; refresh before approval.");
        if (decision == "approved")
        {
            var artifact = await List(Path(app, $"build/builds/{buildId}/artifacts"), ct);
            if (!artifact.Any(a => Text(a, "name") == app.TfsGitArtifactName)) throw new ArgumentException("Expected artifact has not been published.");
        }
        stage = "current approval lookup";
        await HydratePendingApprovals(app, release, environment, ct);
        stage = "Windows identity verification";
        await users.RunAsync(user, async (http, identity) =>
        {
            var approval = Items(environment["preDeployApprovals"]).SingleOrDefault(a => Number(a, "id") == approvalId)
                ?? throw new ArgumentException("Approval no longer exists for this environment.");
            stage = "current-user approval eligibility";
            var eligible = await EligibleApprovals(http, app, expectedReleaseId, identity, ct);
            var denial = ApprovalDenial(rd, release, environment, approval, identity, eligible.Contains(approvalId));
            if (denial is not null) throw new UnauthorizedAccessException("CD approval blocked: " + denial);
            // TFS enforces group membership, rank, revision and its remaining approval policies.
            logger.LogInformation("Pipeline approval decision {Decision} for {ApprovalId} requested by {User} for release {Release}, environment {Environment}.", decision, approvalId, user.Identity?.Name, expectedReleaseId, Number(environment, "id"));
            // Buffer JSON: classic TFS/IIS must receive a known Content-Length, not chunked JSON.
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { status = decision, revision = Number(approval, "revision"), comments = $"Decision: {decision} by the signed-in user through Deployment Portal." });
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var request = new HttpRequestMessage(HttpMethod.Patch, Path(app, $"release/approvals/{approvalId}", "4.1-preview.2")) { Content = content };
            stage = "approval PATCH";
            submitted = true;
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            { throw new PipelineApprovalException("approval PATCH transport/timeout", (int?)(ex as HttpRequestException)?.StatusCode, null, null, true); }
            using var responseLifetime = response;
            if (!response.IsSuccessStatusCode)
            {
                string? safeType = null, safeCode = null;
                try {
                    await response.Content.LoadIntoBufferAsync(16_384, ct);
                    var error = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                    var type = Text(error, "typeKey");
                    if (type is { Length: > 0 and <= 120 } && type.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_')) safeType = type;
                    var match = System.Text.RegularExpressions.Regex.Match(Text(error, "message") ?? "", @"\bTF\d{3,10}\b");
                    if (match.Success) safeCode = match.Value;
                } catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException or IOException or OperationCanceledException) { }
                throw new PipelineApprovalException("approval PATCH", (int)response.StatusCode, safeType, safeCode, true);
            }
            // Re-read as the same identity; do not report a successful approval on an ambiguous response.
            JsonNode confirmed;
            try { confirmed = await ReadAsync(http, Path(app, $"release/approvals/{approvalId}", "4.1-preview.2"), ct); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
            { throw new PipelineApprovalException("decision confirmation read", (int?)(ex as HttpRequestException)?.StatusCode, null, null, true); }
            if (Number(confirmed, "id") != approvalId || Text(confirmed, "status") != decision)
                throw new PipelineApprovalException("decision confirmation mismatch", (int)response.StatusCode, null, null, true);
            logger.LogInformation("Pipeline approval {ApprovalId} confirmed for {User}.", approvalId, user.Identity?.Name);
            return true;
        });
        }
        catch (PipelineApprovalException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
        { throw new PipelineApprovalException(stage, (int?)(ex as HttpRequestException)?.StatusCode, null, null, submitted); }
    }

    private async Task BuildLogs(RegistryApplication app, int buildId, PipelineSnapshot result, CancellationToken ct)
    {
        try
        {
            var logs = await List(Path(app, $"build/builds/{buildId}/logs"), ct);
            foreach (var log in logs.OrderByDescending(l => Number(l, "id")).Take(3).Reverse())
            {
                var start = Math.Max(0, Number(log, "lineCount") - 150);
                result.Logs.Add($"--- Build log {Number(log, "id")} (last 150 lines) ---\n" + await LogText(Path(app, $"build/builds/{buildId}/logs/{Number(log, "id")}") + $"&startLine={start}&endLine={start + 149}", ct));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        { result.Notices.Add("Build logs are unavailable."); }
    }

    private async Task ReleaseLogs(RegistryApplication app, int releaseId, JsonNode environment, PipelineSnapshot result, CancellationToken ct)
    {
        // Expanded release details contain deployment attempts, phases, jobs and task log IDs.
        var attempt = PipelineTimeline.LatestAttempt(environment);
        if (attempt is null) { result.Notices.Add("No deployment attempt logs are available yet."); return; }
        var tasks = PipelineTimeline.ReleaseTasks(attempt);
        foreach (var entry in tasks) result.Events.Add(new(Text(entry.Task, "name") ?? "Release task", Map(Text(entry.Task, "status")), Text(entry.Task, "startTime"), Text(entry.Task, "finishTime")));
        foreach (var entry in tasks.Where(t => Number(t.Task, "id") > 0 && t.Phase > 0
            && Text(t.Task, "status") is "inProgress" or "succeeded" or "failed" or "canceled" or "partiallySucceeded").TakeLast(3))
        {
            var task = entry.Task;
            try
            {
                // Task logUrl is not followed: external upstream URLs must never receive credentials.
                var path = Path(app, $"release/releases/{releaseId}/environments/{Number(environment, "id")}/deployPhases/{entry.Phase}/tasks/{Number(task, "id")}/logs", "4.1-preview.2");
                result.Logs.Add($"--- Release task: {Text(task, "name")} ---\n" + await LogText(path, ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            { result.Notices.Add("A release task log is unavailable or exceeds the display limit."); }
        }
        if (tasks.Count == 0) result.Notices.Add("TFS did not include release task details in this response.");
    }

    private async Task<string> LogText(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/plain");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "text/plain") throw new InvalidOperationException("TFS did not return plain text logs.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[100_001]; var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
        }
        if (length > 100_000) throw new InvalidOperationException("Log exceeds display limit.");
        return PipelineLogRedactor.Redact(new string(buffer, 0, length));
    }
}
