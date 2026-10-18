using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using DeploymentPortal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class DemoReleaseDiscoveryTests
{
    private const string Project = "ad323489-e93b-4b84-8003-857a6ca4406f";
    private const string Artifact = """{"type":"Build","definitionReference":{"project":{"id":"PROJECT"},"definition":{"id":"2"},"version":{"id":"9"}}}""";

    [Theory]
    [InlineData("succeeded", "approved", false)]
    [InlineData("notStarted", "pending", true)]
    [InlineData("succeeded", "approved", false, "http", "release/releases", "release candidate retrieval", "HttpRequestException", 403)]
    [InlineData("succeeded", "approved", false, "json", "release/releases/27", "release detail retrieval", "JsonReaderException", null)]
    [InlineData("succeeded", "approved", false, "duplicate", "release/releases", "release artifact matching", "InvalidOperationException", null)]
    public async Task VerifiedDemoMappingDistinguishesCompletedReleaseFromPendingApproval(
        string environmentStatus, string approvalStatus, bool canApprove, string? failure = null,
        string? failingPath = null, string? expectedStage = null, string? expectedType = null, int? expectedHttp = null)
    {
        var artifact = Artifact.Replace("PROJECT", Project);
        var definition = JsonNode.Parse("""{"id":2,"artifacts":[],"environments":[{"id":2,"preDeployApprovals":{"approvalOptions":{"releaseCreatorCanBeApprover":false}}}]}""")!;
        definition["artifacts"]!.AsArray().Add(JsonNode.Parse(artifact));
        var release = JsonNode.Parse("""
            {"id":27,"name":"Release-3","status":"active","releaseDefinition":{"id":2},
             "createdBy":{"id":"creator"},"artifacts":[],"environments":[
              {"id":27,"definitionEnvironmentId":2,"name":"TARGET-CdDemo","status":"succeeded",
               "preDeployApprovals":[{"id":31,"revision":1,"attempt":1,"rank":1,"approvalType":"preDeploy","status":"approved","approver":{"id":"actor"}}],
               "deploySteps":[]}]}
            """)!;
        release["artifacts"]!.AsArray().Add(JsonNode.Parse(artifact));
        release["environments"]![0]!["status"] = environmentStatus;
        release["environments"]![0]!["preDeployApprovals"]![0]!["status"] = approvalStatus;
        var paths = new List<string>();
        using var http = new HttpClient(new ReadHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.AbsolutePath.Split("/_apis/")[1];
            paths.Add(path);
            if (path == failingPath && failure == "http")
                throw new HttpRequestException("password=SECRET token=SECRET https://SECRET", null, HttpStatusCode.Forbidden);
            if (path == failingPath && failure == "json")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("SECRET invalid json") };
            if (path == failingPath && failure == "duplicate")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    new JsonObject { ["value"] = new JsonArray(release.DeepClone(), release.DeepClone()) }.ToJsonString()) };
            var json = path switch
            {
                "build/definitions/2" => """{"id":2,"project":{"id":"PROJECT","name":"deployment-portal-poc"},"repository":{"id":"demo-repo","name":"Demo-App-Repo","type":"TfsGit"}}""".Replace("PROJECT", Project),
                "build/builds/9" => """{"id":9,"buildNumber":"9","status":"completed","result":"succeeded","definition":{"id":2},"project":{"id":"PROJECT"},"repository":{"id":"demo-repo"}}""".Replace("PROJECT", Project),
                "build/builds/9/timeline" => """{"records":[{"id":"task","type":"Task","name":"Get sources","state":"completed","result":"succeeded"}]}""",
                "build/builds/9/artifacts" => """{"value":[{"name":"drop"}]}""",
                "build/builds/9/logs" => """{"value":[]}""",
                "release/definitions" => new JsonObject { ["value"] = new JsonArray(definition.DeepClone()) }.ToJsonString(),
                "release/definitions/2" => definition.ToJsonString(),
                "release/releases" => new JsonObject { ["value"] = new JsonArray(release.DeepClone()) }.ToJsonString(),
                "release/releases/27" => release.ToJsonString(),
                "release/approvals" => """{"value":[]}""",
                _ => throw new InvalidOperationException("Unexpected request: " + path)
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        })) { BaseAddress = new Uri("https://tfs.example/collection/") };
        var registry = new ConfigurationSiteRegistry(new Snapshot<SiteRegistryOptions>(new()
        {
            Applications = [new() { Key = "Demo", TfsGitProject = "deployment-portal-poc", TfsGitRepository = "Demo-App-Repo", TfsGitCiDefinitionId = 2 }]
        }));
        var log = new CaptureLogger();
        var fileLog = new CaptureFile();
        var service = new PipelineService(http, registry, Options.Create(new TfsCdOptions { Enabled = true, ExecutionEnabled = true }),
            new Users(http), log, new Health(), Options.Create(new PipelineCiOptions()), fileLog);

        var state = await service.Status("Demo", 9, 2, 2,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "Test")), default);

        if (failure is not null)
        {
            Assert.Null(state.ReleaseId);
            Assert.Null(state.EnvironmentId);
            Assert.False(state.CanApprove);
            Assert.Contains(state.Notices, n => n.Contains(failure == "http" ? "HTTP 403" : "unavailable or ambiguous"));
            var entry = Assert.Single(log.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Equal(expectedStage, entry.Fields["Stage"]);
            Assert.Equal(expectedType, entry.Fields["ExceptionType"]);
            Assert.Equal(expectedHttp, entry.Fields["HttpStatus"]);
            var fileEntry = Assert.Single(fileLog.Entries);
            Assert.Equal(expectedStage, fileEntry.Stage);
            Assert.Equal(expectedHttp, fileEntry.HttpStatus);
            Assert.Equal(expectedType, fileEntry.ExceptionType);
            Assert.Equal(entry.Fields["Endpoint"], fileEntry.Endpoint);
            Assert.Equal(failingPath == "release/releases/27" ? "release/releases/{id}" : failingPath, entry.Fields["Endpoint"]);
            Assert.DoesNotContain("SECRET", string.Join(" ", entry.Fields.Values));
            return;
        }
        Assert.Empty(log.Entries);
        Assert.Empty(fileLog.Entries);
        Assert.Equal(27, state.ReleaseId);
        Assert.Equal(27, state.EnvironmentId);
        Assert.Equal(canApprove, state.CanApprove);
        Assert.DoesNotContain(state.Notices, n => n.Contains("ambiguous"));
        if (canApprove)
        {
            Assert.Equal(31, state.ApprovalId);
            Assert.Equal("Approval Pending", state.Status);
            Assert.Equal("pending", state.Stages[4].State);
        }
        else
        {
            Assert.Null(state.ApprovalId);
            Assert.Equal("completed", state.Stages[3].State);
            Assert.Equal("completed", state.Stages[4].State);
            Assert.Contains("succeeded", state.Status, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("release/releases/27", paths);
    }

    private sealed class ReadHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
    private sealed class CaptureFile : IPipelineReleaseDiagnosticFile
    {
        public List<(string Stage, string Endpoint, int? HttpStatus, string ExceptionType)> Entries { get; } = [];
        public void Write(string stage, string endpoint, int? httpStatus, string exceptionType) => Entries.Add((stage, endpoint, httpStatus, exceptionType));
    }
    private sealed class CaptureLogger : ILogger<PipelineService>
    {
        public List<(LogLevel Level, Exception? Exception, Dictionary<string, object?> Fields)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, exception, ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(p => p.Key, p => p.Value)));
    }
    private sealed class Snapshot<T>(T value) : IOptionsSnapshot<T> where T : class
    {
        public T Value => value;
        public T Get(string? name) => value;
    }
    private sealed class Users(HttpClient http) : IPipelineUserTransport
    {
        public bool Available(ClaimsPrincipal user) => true;
        public Task<T> RunAsync<T>(ClaimsPrincipal user, Func<HttpClient, string, Task<T>> action) => action(http, "actor");
    }
    private sealed class Health : IPipelineHealthCheck
    {
        public Task<PipelineHealthResult> Check(string applicationKey, CancellationToken ct) =>
            Task.FromResult(new PipelineHealthResult(null, "pending", "Health endpoint not configured"));
    }
}
