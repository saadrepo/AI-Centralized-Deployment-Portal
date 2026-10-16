using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using DeploymentPortal.Controllers;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class PipelineTests
{
    private const string UserId = "ae0b13bc-c806-4c6d-9d2c-4af898fc9481";
    private static ClaimsPrincipal User => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "DOMAIN\\approver")], "Test"));

    [Theory]
    [InlineData("inProgress", null, "running")]
    [InlineData("completed", "succeeded", "completed")]
    [InlineData("completed", "failed", "failed")]
    [InlineData("completed", "partiallySucceeded", "failed")]
    [InlineData("pending", null, "pending")]
    [InlineData("notStarted", null, "pending")]
    public void MapsOnlyRealStates(string state, string? result, string expected) => Assert.Equal(expected, PipelineState.Map(state, result));

    [Fact]
    public void ArtifactCorrelationRequiresProjectDefinitionAndBuild()
    {
        var artifact = JsonNode.Parse("""{"type":"Build","definitionReference":{"definition":{"id":"1"},"project":{"id":"project"},"version":{"id":"14"}}} """)!;
        Assert.True(PipelineState.ArtifactMatches(artifact, 1, "project", 14));
        Assert.False(PipelineState.ArtifactMatches(artifact, 2, "project", 14));
        Assert.False(PipelineState.ArtifactMatches(artifact, 1, "other", 14));
        Assert.False(PipelineState.ArtifactMatches(artifact, 1, "project", 15));
    }

    [Fact]
    public async Task StatusIsReadOnlyAndApprovalPendingIsGrey()
    {
        var f = new Fixture();
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("Approval Pending", state.Status);
        Assert.Equal("pending", state.Stages[3].State);
        Assert.Equal("completed", state.Stages[2].State);
        Assert.Equal("pending", state.Stages[5].State);
        Assert.True(state.CanApprove);
        Assert.All(f.Read.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.All(f.Write.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal("CD", state.Phase);
        Assert.Empty(state.Logs);
        Assert.Contains(state.CiLogs, l => l.Contains("real build log"));
        Assert.Contains(state.CiEvents, e => e.Name == "Get sources");
        Assert.All(state.Events, e => Assert.Equal("Pre-deployment approval", e.Name));
    }

    [Fact]
    public async Task TriggerDelayNeverCreatesRelease()
    {
        var f = new Fixture { NoRelease = true };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Null(state.ReleaseId);
        Assert.False(state.CanApprove);
        Assert.Contains(state.Notices, n => n.Contains("Waiting for"));
        Assert.DoesNotContain(f.Read.Requests.Concat(f.Write.Requests), r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task DuplicateMatchingReleasesDisableApproval()
    {
        var f = new Fixture { DuplicateRelease = true };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.False(state.CanApprove);
        Assert.Contains(state.Notices, n => n.Contains("ambiguous"));
    }

    [Fact]
    public async Task WrongApplicationBuildRejectedBeforeLogs()
    {
        var f = new Fixture { WrongBuild = true };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.Status("Portal", 14, 7, 9, User, default));
        Assert.DoesNotContain(f.Read.Requests, r => r.Path.Contains("logs"));
    }

    [Fact]
    public async Task MissingCdDoesNotBorrowPortalRelease()
    {
        var f = new Fixture { NoDefinition = true };
        var options = await f.Service.Options("Portal", User, default);
        var json = System.Text.Json.JsonSerializer.SerializeToNode(options)!;
        Assert.Empty(json["environments"]!.AsArray());
        Assert.NotEmpty(json["branches"]!.AsArray());
    }

    [Fact]
    public async Task QueueUsesValidatedBranchAndNoReleaseMutation()
    {
        var f = new Fixture();
        Assert.Equal(15, await f.Service.Queue("Portal", "refs/heads/Dev", User, default));
        var request = Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Post);
        Assert.EndsWith("/build/builds", request.Path);
        Assert.Equal("refs/heads/Dev", JsonNode.Parse(request.Body!)!["sourceBranch"]!.ToString());
        Assert.DoesNotContain(f.Write.Requests, r => r.Path.Contains("release"));
    }

    [Fact]
    public async Task InvalidBranchCannotQueue()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.Queue("Portal", "refs/heads/forged", User, default));
        Assert.DoesNotContain(f.Write.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task DisabledGateRejectsBothActionsBeforeTfsMutation()
    {
        var f = new Fixture(enabled: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.Queue("Portal", "refs/heads/Dev", User, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.Approve("Portal", 14, 7, 9, 5, 21, User, default));
        Assert.Empty(f.Read.Requests); Assert.Empty(f.Write.Requests);
    }

    [Fact]
    public async Task MissingDelegatedIdentityFailsClosed()
    {
        var f = new Fixture(); f.Users.Delegated = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.Queue("Portal", "refs/heads/Dev", User, default));
        Assert.Empty(f.Write.Requests);
    }

    [Fact]
    public async Task CiCanQueueWithCdDisabledWithoutEnablingApproval()
    {
        var f = new Fixture(enabled: false, queueEnabled: true);
        Assert.True(f.Service.CanQueue(User));
        Assert.False(f.Service.CanExecute(User));
        Assert.Equal(15, await f.Service.Queue("Portal", "refs/heads/Dev", User, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.Approve("Portal", 14, 7, 9, 5, 21, User, default));
        Assert.DoesNotContain(f.Write.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task CiSwitchCannotBeBypassedByEnabledCd()
    {
        var f = new Fixture(enabled: true, queueEnabled: false);
        Assert.False(f.Service.CanQueue(User));
        Assert.True(f.Service.CanExecute(User));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.Queue("Portal", "refs/heads/Dev", User, default));
        Assert.Empty(f.Write.Requests);
    }

    [Fact]
    public async Task AnonymousUserCannotUseEnabledCi()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.Queue("Portal", "refs/heads/Dev", new ClaimsPrincipal(), default));
        Assert.Empty(f.Write.Requests);
    }

    [Fact]
    public async Task QueueUsesBufferedTfs2018JsonWithRealDefinitionQueue()
    {
        var f = new Fixture();
        await f.Service.Queue("Portal", "refs/heads/Dev", User, default);
        var post = Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("application/json", post.ContentType);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(post.Body!), post.ContentLength);
        var body = JsonNode.Parse(post.Body!)!;
        Assert.Equal(3, body["queue"]!["id"]!.GetValue<int>());
        Assert.Equal("project", body["project"]!["id"]!.ToString());
        Assert.DoesNotContain("requestedFor", post.Body!);
        Assert.Single(f.Read.Requests.Concat(f.Write.Requests), r => r.Path.EndsWith("/build/definitions/1"));
    }

    [Fact]
    public async Task ReadOnlyQueueReadinessNeverQueuesBuildOrApproves()
    {
        var f = new Fixture();
        var ready = await f.Service.QueueReadiness("Portal", User, default);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToNode(ready)!["ready"]!.GetValue<bool>());
        Assert.All(f.Read.Requests.Concat(f.Write.Requests), r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task PreflightFailureIsKnownNotSentRatherThanUnconfirmed()
    {
        var f = new Fixture { DefinitionFailure = HttpStatusCode.NotFound };
        var error = await Assert.ThrowsAsync<PipelineQueueException>(() => f.Service.Queue("Portal", "refs/heads/Dev", User, default));
        Assert.False(error.MayHaveQueued);
        Assert.Equal("build definition lookup", error.Stage);
        Assert.Equal("CI_TFS_HTTP_404", error.Code);
        Assert.DoesNotContain(f.Write.Requests, r => r.Method != HttpMethod.Get);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.LengthRequired)]
    public async Task QueueRejectionReportsStatusAndSafeTypeWithoutLeakingBody(HttpStatusCode status)
    {
        var f = new Fixture { QueueFailure = status };
        var error = await Assert.ThrowsAsync<PipelineQueueException>(() => f.Service.Queue("Portal", "refs/heads/Dev", User, default));
        Assert.Equal(status, error.UpstreamStatus);
        Assert.False(error.MayHaveQueued);
        Assert.Equal("BuildRequestValidationFailedException", error.UpstreamType);
        Assert.DoesNotContain("DO_NOT_EXPOSE", error.ToString());
        Assert.Contains($"HTTP {(int)status}", error.UserMessage);
        Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task QueueTimeoutIsAmbiguousAndNeverAutomaticallyRetried()
    {
        var f = new Fixture { QueueTimeout = true };
        var error = await Assert.ThrowsAsync<PipelineQueueException>(() => f.Service.Queue("Portal", "refs/heads/Dev", User, default));
        Assert.True(error.MayHaveQueued);
        Assert.Equal("CI_TIMEOUT", error.Code);
        Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AcceptedResponseWithoutIdIsAmbiguous()
    {
        var f = new Fixture { MalformedQueueResponse = true };
        var error = await Assert.ThrowsAsync<PipelineQueueException>(() => f.Service.Queue("Portal", "refs/heads/Dev", User, default));
        Assert.True(error.MayHaveQueued);
        Assert.Equal("reading accepted queue response", error.Stage);
        Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ControllerReturnsSafeDiagnosticStageAndReference()
    {
        var f = new Fixture { QueueFailure = HttpStatusCode.Forbidden };
        var http = new DefaultHttpContext { User = User, TraceIdentifier = "test-reference" };
        var controller = new PipelineController(f.Service, NullLogger<PipelineController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var response = Assert.IsType<ObjectResult>(await controller.Queue("Portal", "refs/heads/Dev", default));
        Assert.Equal(403, response.StatusCode);
        Assert.StartsWith("pipeline;dur=", http.Response.Headers["Server-Timing"].ToString());
        var json = System.Text.Json.JsonSerializer.SerializeToNode(response.Value)!;
        Assert.Equal("test-reference", json["reference"]!.ToString());
        Assert.False(json["mayHaveQueued"]!.GetValue<bool>());
        Assert.DoesNotContain("DO_NOT_EXPOSE", json.ToJsonString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task LegacyIdentityRouteFallbackIsReadOnly(HttpStatusCode status)
    {
        var handler = new Handler(request => request.RequestUri!.Query.Contains("api-version")
            ? new HttpResponseMessage(status) : Json("""{"authenticatedUser":{"id":"user"}}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://tfs.example/collection/") };
        await PipelineUserTransport.ReadIdentity(http, true);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task IdentityDenialNeverFallsBackOrRetries(HttpStatusCode status)
    {
        var handler = new Handler(_ => new HttpResponseMessage(status));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://tfs.example/collection/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => PipelineUserTransport.ReadIdentity(http, true));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ApprovalIdentityPathDoesNotUseNewBuildCompatibilityFallback()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://tfs.example/collection/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => PipelineUserTransport.ReadIdentity(http, false));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ApprovalUsesApprovalPatchAndNeverStartsEnvironment()
    {
        var f = new Fixture();
        await f.Service.Approve("Portal", 14, 7, 9, 5, 21, User, default);
        var patch = Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Patch);
        Assert.EndsWith("/release/approvals/21", patch.Path);
        Assert.Equal("approved", JsonNode.Parse(patch.Body!)!["status"]!.ToString());
        Assert.DoesNotContain(f.Write.Requests, r => r.Path.Contains("environments"));
    }

    [Fact]
    public async Task RequesterProhibitionCannotBeBypassed()
    {
        var f = new Fixture { CreatorIsUser = true };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.Approve("Portal", 14, 7, 9, 5, 21, User, default));
        Assert.DoesNotContain(f.Write.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task StaleReleaseCannotBeApproved()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.Approve("Portal", 14, 7, 9, 99, 21, User, default));
        Assert.DoesNotContain(f.Write.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task TfsPermissionDenialIsNotReportedAsApprovalSuccess()
    {
        var f = new Fixture { DenyApproval = true };
        var error = await Assert.ThrowsAsync<PipelineApprovalException>(() => f.Service.Approve("Portal", 14, 7, 9, 5, 21, User, default));
        Assert.Equal(403, error.HttpStatus);
        Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task SuccessfulDeploymentWithoutHealthEvidenceIsNotCompleted()
    {
        var f = new Fixture { Deployed = true };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("completed", state.Stages[4].State);
        Assert.Equal("pending", state.Stages[5].State);
        Assert.NotEqual("Completed", state.Status);
        Assert.Contains(state.Logs, log => log.Contains("real release log"));
        Assert.Contains(f.Read.Requests, r => r.Path.EndsWith("/deployPhases/8/tasks/11/logs"));
    }

    [Fact]
    public async Task VerifiedHealthCompletesPipeline()
    {
        var f = new Fixture { Deployed = true };
        f.Health.Result = new("https://portal.example/health", "completed", "HTTP 200");
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("Completed", state.Status);
        Assert.Equal("completed", state.Stages[5].State);
    }

    [Fact]
    public async Task AnonymousControllerRequestsNeverReadOrMutateTfs()
    {
        var f = new Fixture();
        var controller = new PipelineController(f.Service, NullLogger<PipelineController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.Equal(401, Assert.IsType<ObjectResult>(controller.Applications()).StatusCode);
        Assert.Equal(401, Assert.IsType<ObjectResult>(await controller.History("Portal", 14, default)).StatusCode);
        Assert.Equal(401, Assert.IsType<ObjectResult>(await controller.Queue("Portal", "refs/heads/Dev", default)).StatusCode);
        Assert.Equal(401, Assert.IsType<ObjectResult>(await controller.PendingReleases("Portal", 7, 9, default)).StatusCode);
        Assert.Equal(401, Assert.IsType<ObjectResult>(await controller.Reject("Portal", 14, 7, 9, 5, 21, default)).StatusCode);
        Assert.Empty(f.Read.Requests); Assert.Empty(f.Write.Requests);
    }

    [Fact]
    public void BothMutationActionsRequireAntiforgery()
    {
        foreach (var method in new[] { "Queue", "Approve", "Cancel", "Reject" })
            Assert.NotEmpty(typeof(PipelineController).GetMethod(method)!.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
    }

    [Theory]
    [InlineData("password=supersecret", "supersecret")]
    [InlineData("Authorization: Bearer abc123", "abc123")]
    [InlineData("https://user:secret@host/path", "user:secret")]
    [InlineData("api_key: 'private value'", "private value")]
    public void LogRedactionRemovesCommonCredentials(string value, string secret)
    {
        var result = PipelineLogRedactor.Redact(value);
        Assert.DoesNotContain(secret, result);
        Assert.Contains("[redacted]", result);
    }

    [Fact]
    public async Task RazorAndHttpRoutesRenderWithoutDatabaseOrWorkerAndRejectMissingAntiforgery()
    {
        var f = new Fixture();
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !Directory.Exists(System.IO.Path.Combine(source.FullName, "src", "DeploymentPortal", "Views"))) source = source.Parent;
        Assert.NotNull(source);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(PipelineController).Assembly.GetName().Name,
            ContentRootPath = System.IO.Path.Combine(source!.FullName, "src", "DeploymentPortal"),
            EnvironmentName = "Development"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(PipelineController).Assembly);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton(f.Service);
        await using var app = builder.Build();
        app.Use(async (context, next) => { if (context.Request.Headers.ContainsKey("X-Test-Identity")) context.User = User; await next(); });
        app.MapControllerRoute("default", "{controller=Pipeline}/{action=Index}/{id?}");
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/Pipeline")).StatusCode);
            http.DefaultRequestHeaders.Add("X-Test-Identity", "fixture");
            var html = await http.GetStringAsync("/Pipeline");
            Assert.Contains("Run CI Build", html);
            Assert.Contains("Approve &amp; Deploy", html);
            Assert.Contains("Refresh Status", html);
            Assert.Contains("/js/pipeline.js", html);
            Assert.Contains("/css/pipeline.css", html);
            Assert.Contains("not connected", html);
            Assert.DoesNotContain("Create Release", html);
            var response = await http.PostAsync("/Pipeline/Queue", new FormUrlEncodedContent(new Dictionary<string, string> { ["applicationKey"] = "Portal", ["branch"] = "refs/heads/Dev" }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var cancelResponse = await http.PostAsync("/Pipeline/Cancel", new FormUrlEncodedContent(new Dictionary<string, string> { ["applicationKey"] = "Portal", ["buildId"] = "14" }));
            Assert.Equal(HttpStatusCode.BadRequest, cancelResponse.StatusCode);
            Assert.Empty(f.Write.Requests);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public void EarlierPendingRankBlocksLaterApprover()
    {
        var f = new Fixture();
        var release = f.Release(); var env = release["environments"]![0]!;
        env["preDeployApprovals"]!.AsArray().Add(JsonNode.Parse("""{"id":20,"rank":0,"attempt":1,"status":"pending"}"""));
        Assert.False(PipelineService.ApprovalAllowed(f.ReleaseDefinition(), release, env, env["preDeployApprovals"]![0]!, UserId));
    }

    [Fact]
    public async Task LateTimelineDoesNotMarkBuildStageRunningBeforeSource()
    {
        var f = new Fixture { BuildStatus = "inProgress", BuildResult = null, TimelineMissing = true };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("CI running", state.Status);
        Assert.Equal("pending", state.Stages[0].State);
        Assert.Equal("Waiting for status", state.Stages[1].Detail);
        Assert.Equal("pending", state.Stages[1].State);
        Assert.Equal("completed", state.Stages[2].State);
        Assert.Null(state.ReleaseId);
    }

    [Fact]
    public async Task SourceRunningKeepsBuildStageWaiting()
    {
        var f = new Fixture { BuildStatus = "inProgress", BuildResult = null, SourceRunning = true, NoArtifact = true };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("running", state.Stages[0].State);
        Assert.Equal("pending", state.Stages[1].State);
        Assert.Equal("Waiting for source", state.Stages[1].Detail);
    }

    [Fact]
    public async Task EmptyTimelineWaitsWithoutInventingSourceCompletion()
    {
        var f = new Fixture { BuildStatus = "inProgress", BuildResult = null, NoSource = true };
        var state = await f.Service.Status("Portal", 14, 0, 0, User, default);
        Assert.Equal("Waiting for status", state.Stages[0].Detail);
        Assert.Equal("pending", state.Stages[1].State);
    }

    [Fact]
    public async Task ArtifactCanCompleteWhileOverallBuildRemainsRunning()
    {
        var f = new Fixture { BuildStatus = "inProgress", BuildResult = null };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("completed", state.Stages[0].State);
        Assert.Equal("running", state.Stages[1].State);
        Assert.Equal("completed", state.Stages[2].State);
        Assert.Equal("pending", state.Stages[3].State);
        Assert.True(state.BuildActive);
        Assert.True(state.CanCancel);
    }

    [Fact]
    public async Task PendingApprovalAlwaysKeepsDeploymentPending()
    {
        var f = new Fixture();
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("Approval Pending", state.Status);
        Assert.Equal("pending", state.Stages[4].State);
        Assert.Equal("Waiting for approval", state.Stages[4].Detail);
    }

    [Fact]
    public async Task CancelUsesOnlySelectedCiBuildAndWaitsForTfsConfirmation()
    {
        var f = new Fixture { BuildStatus = "inProgress", BuildResult = null };
        var response = System.Text.Json.JsonSerializer.SerializeToNode(await f.Service.CancelBuild("Portal", 14, User, default))!;
        Assert.False(response["cancelled"]!.GetValue<bool>());
        var patch = Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Patch);
        Assert.EndsWith("/build/builds/14", patch.Path);
        Assert.Equal("cancelling", JsonNode.Parse(patch.Body!)!["status"]!.ToString());
        Assert.DoesNotContain(f.Write.Requests, r => r.Path.Contains("release"));
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("Cancellation requested", state.Status);
        Assert.True(state.BuildActive);
        Assert.False(state.CanCancel);
    }

    [Fact]
    public async Task CancelledIsShownOnlyAfterTfsCompletedCancelledResult()
    {
        var f = new Fixture { BuildStatus = "notStarted", BuildResult = null, CancelCompletesImmediately = true };
        await f.Service.CancelBuild("Portal", 14, User, default);
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("Cancelled", state.Status);
        Assert.False(state.BuildActive);
        Assert.False(state.CanCancel);
        Assert.Equal("Cancelled", state.Stages[1].Detail);
        Assert.Contains(state.Logs, log => log.Contains("real build log"));
    }

    [Fact]
    public async Task StopBuildsPermissionDenialPreventsCancellationPatch()
    {
        var f = new Fixture { BuildStatus = "inProgress", BuildResult = null, StopAllowed = false };
        var error = await Assert.ThrowsAsync<PipelineCancelException>(() => f.Service.CancelBuild("Portal", 14, User, default));
        Assert.Equal("CI_STOP_PERMISSION_DENIED", error.Code);
        Assert.DoesNotContain(f.Write.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task CompletedBuildCannotBeCancelled()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<PipelineCancelException>(() => f.Service.CancelBuild("Portal", 14, User, default));
        Assert.DoesNotContain(f.Write.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task BuildCompletingDuringPermissionCheckCannotBeCancelled()
    {
        var f = new Fixture { BuildStatus = "inProgress", BuildResult = null, BuildCompletesDuringPermission = true };
        var error = await Assert.ThrowsAsync<PipelineCancelException>(() => f.Service.CancelBuild("Portal", 14, User, default));
        Assert.Equal("CI_BUILD_STATE_CHANGED", error.Code);
        Assert.DoesNotContain(f.Write.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task WrongApplicationBuildCannotBeCancelled()
    {
        var f = new Fixture { BuildStatus = "inProgress", WrongBuild = true };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CancelBuild("Portal", 14, User, default));
        Assert.DoesNotContain(f.Write.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task SelectedActiveBuildPreventsNewQueuePost()
    {
        var f = new Fixture { BuildStatus = "inProgress", BuildResult = null };
        var error = await Assert.ThrowsAsync<PipelineQueueException>(() => f.Service.Queue("Portal", "refs/heads/Dev", User, default, 14));
        Assert.Equal("CI_SELECTED_BUILD_ACTIVE", error.Code);
        Assert.False(error.MayHaveQueued);
        Assert.DoesNotContain(f.Write.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public void TimelineUsesTfsOrderRatherThanInputOrMissingTimestampZero()
    {
        var timeline = JsonNode.Parse("""
            {"records":[
            {"id":"final","type":"Task","name":"Finalize","order":4},
            {"id":"build","type":"Task","name":"Build","order":3,"startTime":"2026-10-15T10:02:00Z"},
            {"id":"source","type":"Task","name":"Get sources","order":2,"startTime":"2026-10-15T10:01:00Z"},
            {"id":"init","type":"Task","name":"Initialize","order":1,"startTime":"2026-10-15T10:00:00Z"}] }
            """);
        Assert.Equal(new[] { "Initialize", "Get sources", "Build", "Finalize" }, PipelineTimeline.BuildTasks(timeline).Select(n => n["name"]!.ToString()));
    }

    [Fact]
    public void TimelineWithoutOrderPlacesUnknownTimestampsLast()
    {
        var timeline = JsonNode.Parse("""
            {"records":[
            {"type":"Task","name":"Pending task"},
            {"type":"Task","name":"Build","startTime":"2026-10-15T10:02:00Z"},
            {"type":"Task","name":"Get sources","startTime":"2026-10-15T10:01:00Z"}]}
            """);
        Assert.Equal(new[] { "Get sources", "Build", "Pending task" }, PipelineTimeline.BuildTasks(timeline).Select(n => n["name"]!.ToString()));
    }

    [Fact]
    public void TimelineRespectsParentJobOrder()
    {
        var timeline = JsonNode.Parse("""
            {"records":[
            {"id":"b-task","parentId":"b","type":"Task","order":1,"name":"Second job task"},
            {"id":"b","type":"Job","order":2},
            {"id":"a-task","parentId":"a","type":"Task","order":5,"name":"First job task"},
            {"id":"a","type":"Job","order":1}]}
            """);
        Assert.Equal(new[] { "First job task", "Second job task" }, PipelineTimeline.BuildTasks(timeline).Select(n => n["name"]!.ToString()));
    }

    [Fact]
    public async Task PendingApprovalWithoutAttemptNeverTurnsDeploymentBlue()
    {
        var f = new Fixture();
        f.ReleaseTransform = release =>
        {
            var env = release["environments"]![0]!;
            env["status"] = "inProgress";
            env["preDeployApprovals"]![0]!.AsObject().Remove("attempt");
            return release;
        };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("Approval Pending", state.Status);
        Assert.Equal("pending", state.Stages[4].State);
        Assert.Equal("pending", state.Stages[5].State);
        Assert.Empty(state.Logs);
        Assert.DoesNotContain(state.Events, e => e.Name == "Deploy" || e.Name == "Get sources");
    }

    [Fact]
    public async Task LivePendingApprovalOverridesStaleExpandedEnvironment()
    {
        var f = new Fixture { Deployed = true };
        f.LiveApprovals = JsonNode.Parse("""{"value":[{"id":21,"rank":1,"approvalType":"preDeploy","status":"pending","release":{"id":5},"releaseEnvironment":{"id":90}}]}""");
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("Approval Pending", state.Status);
        Assert.Equal("pending", state.Stages[4].State);
        Assert.Empty(state.Logs);
    }

    [Fact]
    public async Task CiSuccessWaitingForReleaseSwitchesBothPanelsToCd()
    {
        var f = new Fixture { NoRelease = true };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("CD", state.Phase);
        Assert.Empty(state.Events);
        Assert.Empty(state.Logs);
        Assert.Contains("release", state.TimelineMessage);
        Assert.Contains(state.CiLogs, l => l.Contains("real build log"));
        Assert.Contains(state.CiEvents, e => e.Name == "Get sources");
        Assert.Contains(f.Read.Requests, r => r.Path.Contains("/build/builds/14/logs"));
    }

    [Fact]
    public async Task InitialHistoryLoadsWithoutWaitingForExecutionOrCdChecks()
    {
        var f = new Fixture(enabled: false) { BuildStatus = "inProgress", BuildResult = null };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default, historyOnly: true);
        Assert.Equal("CI", state.Phase);
        Assert.Contains(state.CiEvents, e => e.Name == "Get sources");
        Assert.Contains(state.CiLogs, l => l.Contains("real build log"));
        Assert.Empty(f.Write.Requests);
        Assert.DoesNotContain(f.Read.Requests, r => r.Path.Contains("/release/") || r.Path.Contains("securitynamespaces"));
        Assert.All(f.Read.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task ActiveCdContainsOnlyOrderedReleaseTasksAndReleaseLogs()
    {
        var f = new Fixture { Deployed = true };
        f.ReleaseTransform = release =>
        {
            var env = release["environments"]![0]!; env["status"] = "inProgress";
            env["deploySteps"]![0]!["releaseDeployPhases"]![0]!["deploymentJobs"]![0]!["tasks"] = JsonNode.Parse("""
                [
                {"id":12,"rank":3,"name":"Finalize","status":"pending"},
                {"id":11,"rank":2,"name":"Deploy","status":"inProgress"},
                {"id":10,"rank":1,"name":"Download artifact","status":"succeeded"}]
                """);
            return release;
        };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("CD running", state.Status);
        Assert.Equal("running", state.Stages[4].State);
        Assert.Equal(new[] { "Download artifact", "Deploy", "Finalize" }, state.Events.Select(e => e.Name));
        Assert.Contains(state.Logs, log => log.Contains("real release log"));
        Assert.DoesNotContain(state.Logs, log => log.Contains("real build log"));
        Assert.Contains(state.CiLogs, log => log.Contains("real build log"));
        Assert.Contains(state.CiEvents, e => e.Name == "Get sources");
        Assert.DoesNotContain(f.Read.Requests, r => r.Path.EndsWith("/tasks/12/logs"));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("scheduled")]
    public async Task ApprovedButQueuedEnvironmentIsStillGrey(string status)
    {
        var f = new Fixture { Deployed = true };
        f.ReleaseTransform = release => { release["environments"]![0]!["status"] = status; return release; };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("completed", state.Stages[3].State);
        Assert.Equal("pending", state.Stages[4].State);
        Assert.Equal("pending", state.Stages[5].State);
        Assert.Empty(state.Events);
        Assert.Empty(state.Logs);
    }

    [Fact]
    public void InProgressEnvironmentWithoutDeploymentEvidenceWaits()
    {
        var env = JsonNode.Parse("""{"status":"inProgress","deploySteps":[]}""")!;
        Assert.Equal("pending", PipelineTimeline.Deployment(env).State);
    }

    [Fact]
    public async Task PreviousCompletedAttemptCannotMakeNewDeploymentBlue()
    {
        var f = new Fixture { Deployed = true };
        f.ReleaseTransform = release =>
        {
            var env = release["environments"]![0]!;
            env["status"] = "inProgress";
            env["deploySteps"]![0]!["status"] = "succeeded";
            return release;
        };
        var state = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("pending", state.Stages[4].State);
        Assert.Empty(state.Events);
        Assert.Empty(state.Logs);
    }

    [Fact]
    public async Task EnabledCdConfigurationStillRequiresAvailableVerifiedIdentityForBothDecisions()
    {
        var f = new Fixture(enabled: true);
        f.Users.Delegated = false;
        Assert.False(f.Service.CanExecute(User));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.Approve("Portal", 14, 7, 9, 5, 21, User, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.Reject("Portal", 14, 7, 9, 5, 21, User, default));
        Assert.Empty(f.Read.Requests);
        Assert.Empty(f.Write.Requests);
    }

    [Fact]
    public async Task PendingReleaseListIsReadOnlyAndCorrelated()
    {
        var f = new Fixture();
        var data = System.Text.Json.JsonSerializer.SerializeToNode(await f.Service.PendingReleases("Portal", 7, 9, User, default))!;
        var row = Assert.Single(data["releases"]!.AsArray());
        Assert.Equal(5, row!["ReleaseId"]!.GetValue<int>());
        Assert.Equal(14, row["BuildId"]!.GetValue<int>());
        Assert.Equal("pending", row["ApprovalStatus"]!.ToString());
        Assert.All(f.Read.Requests.Concat(f.Write.Requests), r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task NotDeployedReleaseWithoutApprovalStillAppearsWithoutRejectPermission()
    {
        var f = new Fixture { ReleaseTransform = r => { r["environments"]![0]!["preDeployApprovals"] = new JsonArray(); return r; } };
        var data = System.Text.Json.JsonSerializer.SerializeToNode(await f.Service.PendingReleases("Portal", 7, 9, User, default))!;
        var row = Assert.Single(data["releases"]!.AsArray())!;
        Assert.Equal("notStarted", row["EnvironmentStatus"]!.ToString());
        Assert.False(row["CanReject"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RejectUsesOnlySelectedApprovalAndConfirmsTfsResult()
    {
        var f = new Fixture { ConfirmedApprovalStatus = "rejected" };
        await f.Service.Reject("Portal", 14, 7, 9, 5, 21, User, default);
        var patch = Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Patch);
        Assert.EndsWith("/release/approvals/21", patch.Path);
        Assert.Equal("rejected", JsonNode.Parse(patch.Body!)!["status"]!.ToString());
        Assert.DoesNotContain(f.Write.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task RejectWrongReleaseCannotMutate()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.Reject("Portal", 14, 7, 9, 12, 21, User, default));
        Assert.DoesNotContain(f.Write.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task RejectCannotBypassRequesterPolicy()
    {
        var f = new Fixture { CreatorIsUser = true };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.Reject("Portal", 14, 7, 9, 5, 21, User, default));
        Assert.DoesNotContain(f.Write.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task RejectedApprovalCannotBeApprovedAgain()
    {
        var f = new Fixture { ReleaseTransform = r => { r["environments"]![0]!["preDeployApprovals"]![0]!["status"] = "rejected"; return r; } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.Approve("Portal", 14, 7, 9, 5, 21, User, default));
        var status = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.Equal("Approval rejected", status.Status);
        Assert.Equal("pending", status.Stages[4].State);
        Assert.False(status.CanApprove);
        Assert.DoesNotContain(f.Write.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task RejectionMustBeConfirmedByTfs()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<PipelineApprovalException>(() => f.Service.Reject("Portal", 14, 7, 9, 5, 21, User, default));
    }

    [Fact]
    public async Task RejectUsesBufferedJsonAndDoesNotRequireRetainedBuildArtifact()
    {
        var f = new Fixture { NoArtifact = true, ConfirmedApprovalStatus = "rejected" };
        await f.Service.Reject("Portal", 14, 7, 9, 5, 21, User, default);
        var patch = Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(patch.Body!), patch.ContentLength);
        Assert.Equal("application/json", patch.ContentType);
        Assert.DoesNotContain(f.Read.Requests, r => r.Path.EndsWith("/artifacts"));
    }

    [Fact]
    public async Task LiveApprovalWithoutAttemptCanAuthorizeQueuedRelease()
    {
        var f = new Fixture { ReleaseTransform = r => { r["environments"]![0]!["status"] = "queued"; r["environments"]![0]!["preDeployApprovals"] = new JsonArray(); return r; } };
        f.LiveApprovals = JsonNode.Parse("""{"value":[{"id":21,"rank":1,"revision":3,"approvalType":"preDeploy","status":"pending","release":{"id":5},"releaseEnvironment":{"id":90}}]}""");
        var status = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.True(status.CanApprove);
        Assert.Equal("pending", status.Stages[4].State);
        await f.Service.Approve("Portal", 14, 7, 9, 5, 21, User, default);
        var patch = Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Equal(3, JsonNode.Parse(patch.Body!)!["revision"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task ApprovalErrorsExposeOnlySafeTfsDiagnostics(HttpStatusCode failure)
    {
        var f = new Fixture { ApprovalFailure = failure };
        var error = await Assert.ThrowsAsync<PipelineApprovalException>(() => f.Service.Reject("Portal", 14, 7, 9, 5, 21, User, default));
        Assert.Equal((int)failure, error.HttpStatus);
        Assert.Equal("ReleaseApprovalException", error.UpstreamType);
        Assert.Equal("TF400123", error.TfsCode);
        Assert.DoesNotContain("DO_NOT_EXPOSE", error.Message);
        Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task RejectExactReleaseDoesNotDependOnUniqueBuildReleaseSearch()
    {
        var f = new Fixture { DuplicateRelease = true, ConfirmedApprovalStatus = "rejected" };
        await f.Service.Reject("Portal", 14, 7, 9, 5, 21, User, default);
        Assert.DoesNotContain(f.Read.Requests, r => r.Path.EndsWith("/release/releases"));
        Assert.Single(f.Write.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task QueuedEnvironmentWithoutLiveApprovalCannotBeApproved()
    {
        var f = new Fixture { ReleaseTransform = r => { r["environments"]![0]!["status"] = "queued"; r["environments"]![0]!["preDeployApprovals"] = new JsonArray(); return r; } };
        var status = await f.Service.Status("Portal", 14, 7, 9, User, default);
        Assert.False(status.CanApprove);
        Assert.Contains("No current pending approval", status.ApprovalReason);
        Assert.Contains("queued", status.ApprovalReason);
    }

    [Fact]
    public async Task ApprovalPreflightHttpFailureIsKnownNotSubmitted()
    {
        var f = new Fixture { DefinitionFailure = HttpStatusCode.Forbidden };
        var error = await Assert.ThrowsAsync<PipelineApprovalException>(() => f.Service.Reject("Portal", 14, 7, 9, 5, 21, User, default));
        Assert.False(error.MayHaveReachedTfs);
        Assert.Equal(403, error.HttpStatus);
        Assert.Empty(f.Write.Requests);
    }

    [Fact]
    public async Task OtherEnvironmentLiveApprovalCannotEnableSelectedEnvironment()
    {
        var f = new Fixture { ReleaseTransform = r => { r["environments"]![0]!["preDeployApprovals"] = new JsonArray(); return r; } };
        f.LiveApprovals = JsonNode.Parse("""{"value":[{"id":21,"approvalType":"preDeploy","status":"pending","release":{"id":5},"releaseEnvironment":{"id":91}}]}""");
        Assert.False((await f.Service.Status("Portal", 14, 7, 9, User, default)).CanApprove);
    }

    private sealed class Fixture
    {
        public string ConfirmedApprovalStatus = "approved";
        public bool NoRelease, DuplicateRelease, WrongBuild, NoDefinition, CreatorIsUser, DenyApproval, Deployed, MalformedQueueResponse, QueueTimeout;
        public string BuildStatus = "completed";
        public string? BuildResult = "succeeded";
        public bool StopAllowed = true, CancelCompletesImmediately, BuildCompletesDuringPermission, TimelineMissing, SourceRunning, NoSource, NoArtifact;
        public HttpStatusCode? QueueFailure, DefinitionFailure, ApprovalFailure;
        public Func<JsonNode, JsonNode>? ReleaseTransform;
        public JsonNode? LiveApprovals;
        public Handler Read { get; }
        public Handler Write { get; }
        public FakeUsers Users { get; }
        public FakeHealth Health { get; } = new();
        public PipelineService Service { get; }
        private const string Artifact = """{"type":"Build","alias":"_Portal","definitionReference":{"definition":{"id":"1"},"project":{"id":"project"},"version":{"id":"14"}}} """;
        public Fixture(bool enabled = true, bool? queueEnabled = null)
        {
            Read = new Handler(Respond); Write = new Handler(Respond);
            Users = new FakeUsers(new HttpClient(Write) { BaseAddress = new Uri("https://tfs.example/collection/") });
            var registry = new ConfigurationSiteRegistry(new Snapshot<SiteRegistryOptions>(new() { Applications = [new() { Key = "Portal", DisplayName = "Portal", TfsGitCiDefinitionId = 1, TfsGitProject = "project-name", TfsGitRepository = "repo" }] }));
            Service = new PipelineService(new HttpClient(Read) { BaseAddress = new Uri("https://tfs.example/collection/") }, registry,
                Microsoft.Extensions.Options.Options.Create(new TfsCdOptions { Enabled = true, ExecutionEnabled = enabled }), Users, NullLogger<PipelineService>.Instance, Health,
                Microsoft.Extensions.Options.Options.Create(new PipelineCiOptions { QueueEnabled = queueEnabled ?? enabled }));
        }
        public JsonNode ReleaseDefinition() => JsonNode.Parse("""{"id":7,"name":"Portal-CD","artifacts":[ARTIFACT],"environments":[{"id":9,"name":"SOURCE-Portal","preDeployApprovals":{"approvalOptions":{"releaseCreatorCanBeApprover":false}}}]}""".Replace("ARTIFACT", Artifact))!;
        private JsonNode ReleaseData() => JsonNode.Parse("""{"id":5,"status":"active","releaseDefinition":{"id":7},"createdBy":{"id":"CREATOR"},"artifacts":[ARTIFACT],"environments":[{"id":90,"definitionEnvironmentId":9,"name":"SOURCE-Portal","status":"DEPLOYSTATUS","preDeployApprovals":[{"id":21,"revision":1,"rank":1,"attempt":1,"approvalType":"preDeploy","status":"APPROVALSTATUS","approver":{"id":"USERID"}}],"deploySteps":[{"id":8,"attempt":1,"releaseDeployPhases":[{"id":8,"deploymentJobs":[{"tasks":[{"id":11,"name":"Deploy","status":"succeeded"}]}]}]}]}]}"""
            .Replace("CREATOR", CreatorIsUser ? UserId : "other-user").Replace("ARTIFACT", Artifact).Replace("DEPLOYSTATUS", Deployed ? "succeeded" : "notStarted")
            .Replace("APPROVALSTATUS", Deployed ? "approved" : "pending").Replace("USERID", UserId))!;
        public JsonNode Release() { var release = ReleaseData(); return ReleaseTransform?.Invoke(release) ?? release; }
        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Patch && path.EndsWith("/build/builds/14"))
            {
                BuildStatus = CancelCompletesImmediately ? "completed" : "cancelling";
                BuildResult = CancelCompletesImmediately ? "canceled" : null;
                return Json("{}");
            }
            if (request.Method == HttpMethod.Patch && ApprovalFailure is { } approvalFailure)
                return new(approvalFailure) { Content = new StringContent("""{"typeKey":"ReleaseApprovalException","message":"TF400123 password=DO_NOT_EXPOSE"}""") };
            if (request.Method == HttpMethod.Patch) return DenyApproval ? new(HttpStatusCode.Forbidden) : Json("{}");
            if (request.Method == HttpMethod.Post)
            {
                if (QueueTimeout) throw new TaskCanceledException();
                if (QueueFailure is { } status) return new HttpResponseMessage(status) { Content = new StringContent("""{"typeKey":"BuildRequestValidationFailedException","message":"password=DO_NOT_EXPOSE"}""", System.Text.Encoding.UTF8, "application/json") };
                return MalformedQueueResponse ? Json("{}") : Json("""{"id":15}""");
            }
            if (path.EndsWith("/build/definitions/1")) return DefinitionFailure is { } failure ? new(failure) : Json("""{"id":1,"name":"Portal-CI","queueStatus":"enabled","queue":{"id":3},"project":{"id":"project","name":"project-name"},"repository":{"id":"repo","name":"repo","type":"TfsGit","defaultBranch":"refs/heads/Dev"}}""");
            if (path.EndsWith("/refs")) return Json("""{"value":[{"name":"refs/heads/Dev"}]}""");
            if (path.Contains("/_apis/securitynamespaces/")) return Json("""{"value":[{"namespaceId":"33344d9c-fc72-4d6f-aba5-fa317101a7e9","name":"Build","separatorValue":"/","actions":[{"name":"StopBuilds","bit":512}]}]}""");
            if (path.Contains("/_apis/permissions/"))
            {
                if (BuildCompletesDuringPermission) { BuildStatus = "completed"; BuildResult = "succeeded"; }
                return Json(StopAllowed ? """{"value":[true]}""" : """{"value":[false]}""");
            }
            if (path.EndsWith("/build/builds/14"))
            {
                var node = JsonNode.Parse("""{"id":14,"buildNumber":"14","sourceBranch":"refs/heads/Dev","definition":{"id":DEFINITION},"project":{"id":"project"},"repository":{"id":"repo"}}""".Replace("DEFINITION", WrongBuild ? "2" : "1"))!;
                node["status"] = BuildStatus; node["result"] = BuildResult;
                return Json(node.ToJsonString());
            }
            if (path.EndsWith("/timeline")) return TimelineMissing ? new(HttpStatusCode.NotFound) : Json(NoSource ? """{"records":[]}""" : SourceRunning
                ? """{"records":[{"name":"Get sources","type":"Task","state":"inProgress","result":null}]}"""
                : """{"records":[{"name":"Get sources","type":"Task","state":"completed","result":"succeeded"}]}""");
            if (path.EndsWith("/artifacts")) return Json(NoArtifact ? """{"value":[]}""" : """{"value":[{"name":"drop"}]}""");
            if (path.EndsWith("/build/builds/14/logs")) return Json("""{"value":[{"id":1,"lineCount":3}]}""");
            if (path.EndsWith("/logs/1")) return Plain("real build log\n<script>not HTML</script>");
            if (path.EndsWith("/tasks/11/logs")) return Plain("real release log");
            if (path.EndsWith("/release/definitions")) return Json($"{{\"value\":[{(NoDefinition ? "" : ReleaseDefinition().ToJsonString())}]}}");
            if (path.EndsWith("/release/definitions/7")) return Json(ReleaseDefinition().ToJsonString());
            if (path.EndsWith("/release/releases")) return Json($"{{\"value\":[{(NoRelease ? "" : Release().ToJsonString() + (DuplicateRelease ? "," + Release().ToJsonString() : ""))}]}}");
            if (path.EndsWith("/release/releases/12"))
            {
                var wrong = Release(); wrong["id"] = 12;
                wrong["artifacts"]![0]!["definitionReference"]!["version"]!["id"] = "999";
                return Json(wrong.ToJsonString());
            }
            if (path.EndsWith("/release/releases/5")) return Json(Release().ToJsonString());
            if (path.EndsWith("/release/approvals")) return Json(LiveApprovals?.ToJsonString() ?? """{"value":[]}""");
            if (path.EndsWith("/release/approvals/21")) return Json($"{{\"id\":21,\"status\":\"{ConfirmedApprovalStatus}\"}}");
            if (path.EndsWith("/build/builds")) return Json("""{"value":[]}""");
            throw new InvalidOperationException("Unexpected test request: " + path);
        }
    }
    private sealed class FakeHealth : IPipelineHealthCheck
    {
        public PipelineHealthResult Result = new(null, "pending", "Health endpoint not configured");
        public Task<PipelineHealthResult> Check(string key, CancellationToken ct) => Task.FromResult(Result);
    }
    private sealed class FakeUsers(HttpClient client) : IPipelineUserTransport
    {
        public bool Delegated = true;
        public bool Available(ClaimsPrincipal user) => Delegated;
        public Task<T> RunAsync<T>(ClaimsPrincipal user, Func<HttpClient, string, Task<T>> action) => action(client, UserId);
    }
    private sealed class Snapshot<T>(T value) : IOptionsSnapshot<T> where T : class
    { public T Value => value; public T Get(string? name) => value; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, long? ContentLength, string? ContentType)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct), request.Content?.Headers.ContentLength, request.Content?.Headers.ContentType?.MediaType));
            return respond(request);
        }
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Plain(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, System.Text.Encoding.UTF8, "text/plain") };
}
