using System.Net;
using System.Text;
using System.Text.Json;
using DeploymentPortal.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeploymentPortal.Tests;

public class TfsServiceTests
{
    [Fact]
    public async Task ReadsDefinitionIncludingQueueAndRepository()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/tfs/DefaultCollection/PMSNZ/_apis/build/definitions/42?api-version=4.1", request.RequestUri!.PathAndQuery);
            return JsonResponse("""{"id":42,"name":"PMSNZ CI","path":"\\PMSNZ","revision":7,"queueStatus":"enabled","queue":{"name":"Windows"},"repository":{"type":"TfsVersionControl","defaultBranch":"$/PMSNZ/Main"}}""");
        });
        using var client = CreateClient(handler);
        var definition = await CreateService(client).GetBuildDefinitionAsync(42);
        Assert.Equal(42, definition.Id);
        Assert.Equal("PMSNZ CI", definition.Name);
        Assert.Equal("\\PMSNZ", definition.Path);
        Assert.Equal(7, definition.Revision);
        Assert.Equal("enabled", definition.QueueStatus);
        Assert.Equal("Windows", definition.Queue?.Name);
        Assert.Equal("TfsVersionControl", definition.Repository?.Type);
        Assert.Equal("$/PMSNZ/Main", definition.Repository?.DefaultBranch);
    }

    [Fact]
    public async Task ReadsSourceVersionAndNullableBuildFields()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/tfs/DefaultCollection/PMSNZ/_apis/build/builds?api-version=4.1&definitions=42&$top=20&queryOrder=queueTimeDescending", request.RequestUri!.PathAndQuery);
            return JsonResponse("""{"value":[{"id":101,"buildNumber":"20260925.1","status":"inProgress","sourceBranch":"$/PMSNZ/Main","sourceVersion":"C12345","queueTime":"2026-09-25T10:00:00+05:00","startTime":"2026-09-25T05:01:00Z"}]}""");
        });
        using var client = CreateClient(handler);
        var build = Assert.Single(await CreateService(client).GetLatestBuildsAsync(42));
        Assert.Equal(101, build.Id);
        Assert.Equal("20260925.1", build.BuildNumber);
        Assert.Equal("inProgress", build.Status);
        Assert.Equal("$/PMSNZ/Main", build.SourceBranch);
        Assert.Equal("C12345", build.SourceVersion);
        Assert.Null(build.Result);
        Assert.Null(build.FinishTime);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T05:00:00Z"), build.QueueTime);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T05:01:00Z"), build.StartTime);
    }

    [Fact]
    public async Task LimitsHistoryToTwentyNewestQueuedBuilds()
    {
        var payload = JsonSerializer.Serialize(new { value = Enumerable.Range(1, 25).Select(id => new
        {
            id, queueTime = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero).AddMinutes(id)
        }) });
        using var handler = new StubHandler(_ => JsonResponse(payload));
        using var client = CreateClient(handler);
        var builds = await CreateService(client).GetLatestBuildsAsync(42);
        Assert.Equal(Enumerable.Range(6, 20).Reverse(), builds.Select(x => x.Id));
    }

    [Fact]
    public async Task EmptyHistoryIsValid()
    {
        using var handler = new StubHandler(_ => JsonResponse("""{"value":[]}"""));
        using var client = CreateClient(handler);
        Assert.Empty(await CreateService(client).GetLatestBuildsAsync(42));
    }

    [Fact]
    public async Task InvalidDetailsAndHistoryAreRejected()
    {
        using var handler = new StubHandler(_ => JsonResponse("{}"));
        using var client = CreateClient(handler);
        var service = CreateService(client);
        await Assert.ThrowsAsync<JsonException>(() => service.GetBuildDefinitionAsync(42));
        await Assert.ThrowsAsync<JsonException>(() => service.GetLatestBuildsAsync(42));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task DetailsAndHistoryPropagateHttpFailures(HttpStatusCode status)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(status));
        using var client = CreateClient(handler);
        var service = CreateService(client);
        var detailsError = await Assert.ThrowsAsync<HttpRequestException>(() => service.GetBuildDefinitionAsync(42));
        var historyError = await Assert.ThrowsAsync<HttpRequestException>(() => service.GetLatestBuildsAsync(42));
        Assert.Equal(status, detailsError.StatusCode);
        Assert.Equal(status, historyError.StatusCode);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("  PMS & NZ  ", "&name=PMS%20%26%20NZ%2A")]
    public async Task DefinitionSearchIsLimitedAndEncoded(string? search, string suffix)
    {
        var calls = 0;
        using var handler = new StubHandler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/tfs/DefaultCollection/PMSNZ/_apis/build/definitions?api-version=4.1&$top=50&queryOrder=definitionNameAscending&includeAllProperties=true" + suffix, request.RequestUri!.PathAndQuery);
            var response = JsonResponse("""{"value":[{"id":42,"name":"PMSNZ CI"}]}""");
            response.Headers.Add("x-ms-continuationtoken", "more-records");
            return response;
        });
        using var client = CreateClient(handler);
        Assert.Equal(42, Assert.Single(await CreateService(client).GetBuildDefinitionsAsync(search)).Id);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{\"displayName\":\"Build Operator\"}", "Build Operator")]
    [InlineData("null", null)]
    public async Task ReadsActualQueuingIdentity(string identity, string? expected)
    {
        using var handler = new StubHandler(_ => JsonResponse("{\"value\":[{\"id\":1,\"requestedBy\":" + identity + ",\"requestedFor\":{\"displayName\":\"Different Person\"}}]}"));
        using var client = CreateClient(handler);
        var build = Assert.Single(await CreateService(client).GetLatestBuildsAsync(42));
        Assert.Equal(expected, build.RequestedBy?.DisplayName);
    }
    [Fact]
    public async Task AuthenticationFailureIsNotReportedAsAnEmptyList()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = CreateClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => CreateService(client).GetBuildDefinitionsAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("<html>Sign in</html>")]
    public async Task InvalidPayloadIsNotReportedAsAnEmptyList(string payload)
    {
        using var handler = new StubHandler(_ => JsonResponse(payload));
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<JsonException>(() => CreateService(client).GetBuildDefinitionsAsync());
    }

    private static TfsService CreateService(HttpClient client) => new(client,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tfs:Project"] = "PMSNZ",
            ["Tfs:ApiVersion"] = "4.1"
        }).Build(), new TfsBuildConfirmationStore());

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://tfs.example/tfs/DefaultCollection/")
    };

    private static HttpResponseMessage JsonResponse(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
