using System.Net;
using System.Text;
using System.Text.Json;
using DeploymentPortal.Controllers;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeploymentPortal.Tests;

public class TfsReleaseTests
{
    private const string Artifact = """{"alias":"CI","type":"Build","definitionReference":{"definition":{"id":"232","name":"PMSDEV-V2-CI"},"version":{"id":"456","name":"20260928.1"}}}""";
    private static readonly string Definition = """{"id":275,"name":"DEVPMSV2-CD","artifacts":[ARTIFACT],"environments":[{"id":1,"name":"DEVPMSV2","preDeployApprovals":{"approvals":[],"approvalOptions":{"requiredApproverCount":0,"executionOrder":"beforeGates"}},"postDeployApprovals":{"approvals":[],"approvalOptions":{"requiredApproverCount":0,"executionOrder":"afterSuccessfulGates"}}},{"id":2,"name":"DevMercyUATV3","preDeployApprovals":{"approvals":[],"approvalOptions":{"requiredApproverCount":0}}},{"id":3,"name":"Dev CCR","preDeployApprovals":{"approvals":[{"id":188,"approvalType":"preDeploy","status":"undefined","rank":1}],"approvalOptions":{"requiredApproverCount":1,"releaseCreatorCanBeApprover":false}}}]}""".Replace("ARTIFACT", Artifact);
    private static readonly string Release = """{"id":900,"name":"Release-900","status":"active","releaseDefinition":{"id":275,"name":"DEVPMSV2-CD"},"artifacts":[ARTIFACT],"environments":[{"id":10,"definitionEnvironmentId":1,"name":"DEVPMSV2","status":"succeeded","deploySteps":[{"id":100,"attempt":1,"status":"succeeded","operationStatus":"phaseSucceeded"}]},{"id":11,"definitionEnvironmentId":2,"name":"DevMercyUATV3","status":"notStarted","deploySteps":[{"id":101,"attempt":1,"status":"notDeployed","operationStatus":"undefined"}],"preDeployApprovals":[],"postDeployApprovals":[]},{"id":12,"definitionEnvironmentId":3,"name":"Dev CCR","status":"failed","preDeployApprovals":[{"id":88,"approvalType":"preDeploy","status":"pending","rank":1,"approver":{"id":"u1","name":"Operator"}}],"postDeployApprovals":[{"id":89,"approvalType":"postDeploy","status":"approved","rank":1,"approver":{"id":"u2","name":"Lead"}}]}]}""".Replace("ARTIFACT", Artifact);
    private const string Deployments = """{"value":[{"id":101,"attempt":1,"definitionEnvironmentId":2,"deploymentStatus":"notDeployed","operationStatus":"undefined","release":{"id":"900","name":"Release-900"},"releaseEnvironment":{"id":"11","name":"DevMercyUATV3"}}]}""";

    [Fact]
    public async Task FullReadOnlyFlowUses41ReleaseEndpointsAndMapsKnownData()
    {
        var calls = new List<string>();
        using var handler = new Handler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            calls.Add(path);
            Assert.StartsWith("/tfs/Collection/Project%20%26%20One/_apis/release/", path);
            var version = request.RequestUri.AbsolutePath.Contains("/definitions") ? "4.1-preview.3"
                : request.RequestUri.AbsolutePath.Contains("/deployments") ? "4.1-preview.2" : "4.1-preview.6";
            Assert.Contains("api-version=" + version, path);
            return Json(request.RequestUri.AbsolutePath.Split('/').Last() switch
            {
                "definitions" => "{\"value\":[" + Definition + "]}",
                "275" => Definition,
                "releases" => "{\"value\":[" + Release + "]}",
                "900" => Release,
                "deployments" => Deployments,
                _ => throw new Exception("Unexpected request")
            });
        });
        using var client = Client(handler);
        var controller = Controller(Service(client));
        var index = Model(await controller.Index(" DEVPMS & V2 "));
        Assert.Equal(275, Assert.Single(index.Definitions).Id);
        Assert.Contains("searchText=DEVPMS%20%26%20V2", calls[0]);
        Assert.Contains("$expand=artifacts", calls[0]);
        Assert.Contains("queryOrder=nameAscending", calls[0]);
        var definition = Model(await controller.Definition(275));
        Assert.Equal("DEVPMSV2-CD", definition.Definition!.Name);
        var artifact = Assert.Single(definition.Definition.Artifacts!);
        Assert.Equal("232", artifact.Definition!.Id);
        Assert.Equal("PMSDEV-V2-CI", artifact.Definition.Name);
        Assert.Equal(1, definition.Definition.Environments![2].PreDeployApprovals!.ApprovalOptions!.RequiredApproverCount);
        Assert.False(definition.Definition.Environments![2].PreDeployApprovals!.ApprovalOptions!.ReleaseCreatorCanBeApprover);
        Assert.Equal(188, Assert.Single(definition.Definition.Environments![2].PreDeployApprovals!.Approvals!).Id);
        Assert.Equal(900, Assert.Single(definition.Releases).Id);
        Assert.Contains("definitionId=275&$top=20&queryOrder=descending&$expand=artifacts,environments", calls[2]);
        var release = Model(await controller.Details(900)).Release!;
        Assert.Equal("20260928.1", Assert.Single(release.Artifacts!).Version!.Name);
        Assert.Equal(new[] { "DEVPMSV2", "DevMercyUATV3", "Dev CCR" }, release.Environments!.Select(x => x.Name));
        Assert.Equal(new[] { "succeeded", "notStarted", "failed" }, release.Environments!.Select(x => x.Status));
        Assert.Equal("notDeployed", release.Environments![1].LatestDeployment!.Status);
        Assert.Equal("undefined", release.Environments![1].LatestDeployment!.OperationStatus);
        Assert.Equal(88, Assert.Single(release.Environments![2].PendingApprovals).Id);
        Assert.Empty(release.Environments![1].PreDeployApprovals!);
        Assert.Equal(88, Assert.Single(release.Environments![2].PreDeployApprovals!).Id);
        Assert.Equal(89, Assert.Single(release.Environments![2].PostDeployApprovals!).Id);
        Assert.Equal(5, calls.Count);
        Assert.Contains("/_apis/release/deployments?api-version=4.1-preview.2", calls[4]);
    }

    [Fact]
    public void ApprovalShapesAreSeparateForDefinitionsAndReleaseInstances()
    {
        var definition = JsonSerializer.Deserialize<TfsReleaseDefinitionViewModel>(Definition, JsonOptions)!;
        var definitionEnvironment = definition.Environments![2];
        Assert.Equal(188, Assert.Single(definitionEnvironment.PreDeployApprovals!.Approvals!).Id);
        Assert.Equal(1, definitionEnvironment.PreDeployApprovals.ApprovalOptions!.RequiredApproverCount);
        Assert.False(definitionEnvironment.PreDeployApprovals.ApprovalOptions.ReleaseCreatorCanBeApprover);

        var release = JsonSerializer.Deserialize<TfsReleaseViewModel>(Release, JsonOptions)!;
        var releaseEnvironment = release.Environments![2];
        Assert.Equal(88, Assert.Single(releaseEnvironment.PreDeployApprovals!).Id);
        Assert.Equal(89, Assert.Single(releaseEnvironment.PostDeployApprovals!).Id);
        Assert.Equal(88, Assert.Single(releaseEnvironment.PendingApprovals).Id);
    }

    [Theory]
    [InlineData("notStarted", "notDeployed", "undefined", false, TfsReleaseEnvironmentAction.Deploy)]
    [InlineData("failed", "failed", "canceled", false, TfsReleaseEnvironmentAction.Redeploy)]
    [InlineData("canceled", "canceled", "canceled", false, TfsReleaseEnvironmentAction.Redeploy)]
    [InlineData("rejected", "rejected", "rejected", false, TfsReleaseEnvironmentAction.Redeploy)]
    [InlineData("succeeded", "succeeded", "phaseSucceeded", false, TfsReleaseEnvironmentAction.Redeploy)]
    [InlineData("inProgress", "inProgress", "phaseInProgress", false, TfsReleaseEnvironmentAction.None)]
    [InlineData("notStarted", "notDeployed", "undefined", true, TfsReleaseEnvironmentAction.None)]
    public void EnvironmentActionsMatchTfsDeploymentSemantics(string environmentStatus, string deploymentStatus,
        string operationStatus, bool pendingApproval, TfsReleaseEnvironmentAction expected)
    {
        var environment = Environment(environmentStatus, deploymentStatus, operationStatus, pendingApproval);
        Assert.Equal(expected, environment.Action);
        Assert.Equal(expected is not TfsReleaseEnvironmentAction.None, environment.CanDeploy);
    }

    [Fact]
    public void CanceledOperationDoesNotBlockFailedOrCanceledRedeploy()
    {
        Assert.Equal(TfsReleaseEnvironmentAction.Redeploy, Environment("failed", "failed", "canceled").Action);
        Assert.Equal(TfsReleaseEnvironmentAction.Redeploy, Environment("canceled", "canceled", "canceled").Action);
    }

    [Fact]
    public void EnvironmentCanceledOverridesLatestNotDeployedForRedeploy()
    {
        var environment = Environment("canceled", "notDeployed", "Canceled");

        Assert.Equal("canceled", environment.EffectiveDeploymentStatus);
        Assert.Equal(TfsReleaseEnvironmentAction.Redeploy, environment.Action);
        Assert.Equal("Redeploy", environment.ActionLabel);
    }

    [Fact]
    public void SucceededEnvironmentCanBeRedeployed()
    {
        var environment = Environment("succeeded", "succeeded", "phaseSucceeded");

        Assert.Equal("succeeded", environment.EffectiveDeploymentStatus);
        Assert.Equal(TfsReleaseEnvironmentAction.Redeploy, environment.Action);
        Assert.Equal("Redeploy", environment.ActionLabel);
    }

    [Fact]
    public void QueuedForAgentDisplaysQueued()
    {
        var environment = Environment("inProgress", "inProgress", "queuedForAgent");

        Assert.Equal("queued", environment.EffectiveDeploymentStatus);
        Assert.Equal(TfsReleaseEnvironmentAction.None, environment.Action);
    }

    [Fact]
    public void PhaseInProgressDisplaysInProgress()
    {
        var environment = Environment("inProgress", "inProgress", "phaseInProgress");

        Assert.Equal("inProgress", environment.EffectiveDeploymentStatus);
        Assert.Equal(TfsReleaseEnvironmentAction.None, environment.Action);
    }

    [Theory]
    [InlineData("canceled", null, "canceled")]
    [InlineData("failed", null, "failed")]
    [InlineData("rejected", null, "rejected")]
    [InlineData("notStarted", null, "notDeployed")]
    [InlineData("succeeded", null, "succeeded")]
    [InlineData("inProgress", null, "inProgress")]
    public void EffectiveDeploymentStatusMatchesTfsSemantics(string environmentStatus, string? deploymentStatus, string expected)
    {
        var environment = Environment(environmentStatus, deploymentStatus ?? "notDeployed", "undefined");
        Assert.Equal(expected, environment.EffectiveDeploymentStatus);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 502)]
    [InlineData(HttpStatusCode.Forbidden, 502)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    public async Task ErrorsAreSafeAndDoNotLookLikeEmptySuccess(HttpStatusCode status, int expected)
    {
        using var handler = new Handler(_ => new HttpResponseMessage(status) { Content = new StringContent("private TFS error") });
        using var client = Client(handler);
        var controller = Controller(Service(client));
        var model = Model(await controller.Details(900));
        Assert.Equal(expected, controller.Response.StatusCode);
        Assert.NotNull(model.Error);
        Assert.DoesNotContain("private TFS error", model.Error);
        Assert.Null(model.Release);
    }

    [Fact]
    public async Task HistoryFailurePreservesDefinition()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/275")
            ? Json(Definition) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = Client(handler);
        var model = Model(await Controller(Service(client)).Definition(275));
        Assert.NotNull(model.Definition);
        Assert.Null(model.Error);
        Assert.NotNull(model.ReleasesError);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidIdsMakeNoHttpRequests(int id)
    {
        using var handler = new Handler(_ => throw new Exception("No request expected"));
        using var client = Client(handler);
        var service = Service(client);
        var controller = Controller(service);
        Assert.NotNull(Model(await controller.Definition(id)).Error);
        Assert.NotNull(Model(await controller.Details(id)).Error);
        Assert.Equal(400, controller.Response.StatusCode);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetDefinitionAsync(id));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetReleaseAsync(id));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetReleasesAsync(id));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":[null]}")]
    [InlineData("{\"value\":[{\"id\":0,\"name\":\"bad\"}]}")]
    public async Task MalformedListsAreRejected(string payload)
    {
        using var handler = new Handler(_ => Json(payload));
        using var client = Client(handler);
        await Assert.ThrowsAsync<JsonException>(() => Service(client).GetDefinitionsAsync());
        await Assert.ThrowsAsync<JsonException>(() => Service(client).GetReleasesAsync(275));
    }

    [Fact]
    public async Task EmptyListsAndMissingOptionalMetadataAreSupported()
    {
        using var handler = new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("/900")
            ? "{\"id\":900,\"name\":\"Release-900\"}" : "{\"value\":[]}"));
        using var client = Client(handler);
        var service = Service(client);
        Assert.Empty(await service.GetDefinitionsAsync());
        Assert.Empty(await service.GetReleasesAsync(275));
        Assert.Null((await service.GetReleaseAsync(900)).Artifacts);
    }

    [Fact]
    public async Task MismatchedResourceIdsAreRejected()
    {
        using var handler = new Handler(_ => Json(Release));
        using var client = Client(handler);
        await Assert.ThrowsAsync<JsonException>(() => Service(client).GetReleaseAsync(901));
    }

    [Fact]
    public void ReleaseActionsUseExpectedHttpVerbs()
    {
        foreach (var name in new[] { "Index", "Definition", "Details", "Deploy" })
            Assert.Single(typeof(TfsReleasesController).GetMethod(name)!.GetCustomAttributes(typeof(HttpGetAttribute), true));
        foreach (var name in new[] { "ReviewDeploy", "ConfirmDeploy" })
            Assert.Single(typeof(TfsReleasesController).GetMethod(name)!.GetCustomAttributes(typeof(HttpPostAttribute), true));
        Assert.Equal(6, typeof(TfsReleasesController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Length);
    }

    [Fact]
    public async Task ReviewAndConfirmDeployRevalidateStateAndPatchEnvironmentInProgress()
    {
        var calls = new List<(HttpMethod Method, string Path, string Body)>();
        using var handler = new Handler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            calls.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            if (request.Method == HttpMethod.Patch)
            {
                Assert.Contains("/_apis/release/releases/900/environments/11?api-version=4.1-preview.5", request.RequestUri.PathAndQuery);
                Assert.Contains("\"status\":\"inProgress\"", body);
                return Json("""{"id":11,"status":"queued"}""");
            }
            return Json(request.RequestUri!.AbsolutePath.Split('/').Last() switch
            {
                "900" => Release,
                "deployments" => Deployments,
                _ => throw new Exception("Unexpected request")
            });
        }, assertGetOnly: false);
        using var client = Client(handler);
        var service = Service(client);
        var review = await service.ReviewDeployAsync(900, 11);
        Assert.NotNull(review.ConfirmationToken);

        var result = await service.DeployEnvironmentAsync(review.ConfirmationToken!);
        Assert.Equal(900, result.Id);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Get, HttpMethod.Get, HttpMethod.Get, HttpMethod.Patch, HttpMethod.Get, HttpMethod.Get }, calls.Select(x => x.Method));
        Assert.Equal(1, calls.Count(x => x.Method == HttpMethod.Patch));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeployEnvironmentAsync(review.ConfirmationToken!));
    }

    [Fact]
    public async Task DefinitionDeployResolverUsesLightweightReleaseListBeforeLoadingCandidateDetails()
    {
        var calls = new List<string>();
        using var handler = new Handler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            calls.Add(path);
            return Json(request.RequestUri!.AbsolutePath.Split('/').Last() switch
            {
                "releases" => """{"value":[{"id":900,"name":"Release-900","status":"active","releaseDefinition":{"id":275,"name":"DEVPMSV2-CD"}}]}""",
                "900" => Release,
                "deployments" => Deployments,
                _ => throw new Exception("Unexpected request")
            });
        });
        using var client = Client(handler);

        var result = await Service(client).GetLatestDeployableReleaseAsync(275);

        Assert.Equal(900, result.Id);
        Assert.Contains("definitionId=275&$top=20&queryOrder=descending", calls[0]);
        Assert.DoesNotContain("$expand=artifacts,environments", calls[0]);
        Assert.EndsWith("/releases/900?api-version=4.1-preview.6", calls[1]);
        Assert.Contains("/deployments?api-version=4.1-preview.2", calls[2]);
    }

    [Fact]
    public async Task ConfirmDeployRejectsStaleStateBeforePatch()
    {
        var afterReview = false;
        using var handler = new Handler(request =>
        {
            var release = afterReview
                ? Release.Replace("\"status\":\"notStarted\"", "\"status\":\"queued\"")
                : Release;
            if (request.RequestUri!.AbsolutePath.EndsWith("/deployments")) return Json(Deployments);
            return Json(release);
        });
        using var client = Client(handler);
        var service = Service(client);
        var review = await service.ReviewDeployAsync(900, 11);
        afterReview = true;
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeployEnvironmentAsync(review.ConfirmationToken!));
    }

    private static TfsReleasePageViewModel Model(IActionResult result) => Assert.IsType<TfsReleasePageViewModel>(Assert.IsType<ViewResult>(result).Model);
    private static TfsReleasesController Controller(ITfsReleaseService service) => new(service, NullLogger<TfsReleasesController>.Instance)
    { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    private static TfsReleaseService Service(HttpClient client) => new(client,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Tfs:Project"] = "Project & One" }).Build(),
        new TfsReleaseConfirmationStore(),
        NullLogger<TfsReleaseService>.Instance);
    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://tfs.example/tfs/Collection/") };
    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static TfsReleaseEnvironmentViewModel Environment(string environmentStatus, string deploymentStatus, string operationStatus,
        bool pendingApproval = false) => new()
        {
            Id = 10,
            DefinitionEnvironmentId = 1,
            Name = "Dev",
            Status = environmentStatus,
            DeploySteps =
            [
                new TfsReleaseDeploymentAttemptViewModel
                {
                    Id = 100,
                    Attempt = 1,
                    Status = deploymentStatus,
                    OperationStatus = operationStatus
                }
            ],
            PreDeployApprovals = pendingApproval ?
            [
                new TfsReleaseApprovalViewModel { Id = 200, Status = "pending", ApprovalType = "preDeploy" }
            ] : []
        };
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> respond;
        private readonly bool assertGetOnly;

        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond, bool assertGetOnly = true)
        {
            this.respond = request => Task.FromResult(respond(request));
            this.assertGetOnly = assertGetOnly;
        }

        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, bool assertGetOnly = true)
        {
            this.respond = respond;
            this.assertGetOnly = assertGetOnly;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (assertGetOnly) Assert.Equal(HttpMethod.Get, request.Method);
            return respond(request);
        }
    }
}
