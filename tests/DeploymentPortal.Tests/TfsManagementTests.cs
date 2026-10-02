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

public class TfsManagementTests
{
    private const string Definition = """{"id":42,"name":"Example CI","revision":7,"queueStatus":"enabled","queue":{"id":8,"name":"Windows"},"repository":{"type":"TfsVersionControl","defaultBranch":"$/Example/Main"}}""";
    private const string Build = """{"id":101,"buildNumber":"20260925.1","status":"inProgress","definition":{"id":42,"name":"Example CI"},"queue":{"id":8,"name":"Windows"},"sourceBranch":"$/Example/Main","sourceVersion":"12345","requestedBy":{"displayName":"Operator"},"startTime":"2026-09-25T05:00:00Z"}""";

    [Fact]
    public async Task ReviewIsReadOnlyAndConfirmationPostsAuthoritativeValuesOnce()
    {
        var gets = 0;
        var posts = 0;
        using var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                gets++;
                Assert.Equal("/tfs/DefaultCollection/PMSNZ/_apis/build/definitions/42?api-version=4.1", request.RequestUri!.PathAndQuery);
                return Json(Definition);
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/tfs/DefaultCollection/PMSNZ/_apis/build/builds?api-version=4.1", request.RequestUri!.PathAndQuery);
            posts++;
            // Check length before reading/buffering the content in the test handler.
            Assert.NotNull(request.Content!.Headers.ContentLength);
            Assert.True(request.Content.Headers.ContentLength > 0);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", request.Content.Headers.ContentType?.CharSet);
            Assert.NotEqual(true, request.Headers.TransferEncodingChunked);
            var bytes = await request.Content.ReadAsByteArrayAsync();
            Assert.Equal((long)bytes.Length, request.Content.Headers.ContentLength);
            Assert.Equal((byte)'{', bytes[0]);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal(3, root.EnumerateObject().Count());
            Assert.Equal(42, root.GetProperty("definition").GetProperty("id").GetInt32());
            Assert.Equal("$/Example/Main", root.GetProperty("sourceBranch").GetString());
            Assert.Equal("12345", root.GetProperty("sourceVersion").GetString());
            Assert.False(root.TryGetProperty("queue", out _));
            return Json(Build);
        });
        using var client = Client(handler);
        var service = Service(client);
        var review = await service.ReviewBuildAsync(42, "12345");
        Assert.Equal(0, posts);
        Assert.Equal("12345", review.SourceVersion);
        // Mutating the displayed model cannot alter the server-side confirmation snapshot.
        review.Definition!.Repository!.DefaultBranch = "$/Tampered";
        review.SourceVersion = "999";
        var result = await service.QueueBuildAsync(review.ConfirmationToken!);
        Assert.Equal(101, result.Id);
        Assert.Equal("20260925.1", result.BuildNumber);
        Assert.Equal("inProgress", result.Status);
        Assert.Equal("12345", result.SourceVersion);
        Assert.Equal("Windows", result.Queue?.Name);
        Assert.Equal(2, gets);
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueueBuildAsync(review.ConfirmationToken!));
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("C12345")]
    [InlineData("123&queue=8")]
    [InlineData("2147483648")]
    public async Task InvalidChangesetsNeverCallTfs(string version)
    {
        using var handler = new Handler(_ => throw new Exception("No HTTP request should occur"));
        using var client = Client(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(client).ReviewBuildAsync(42, version));
    }

    [Theory]
    [InlineData("enabled", "disabled")]
    [InlineData("enabled", "paused")]
    [InlineData("TfsVersionControl", "TfsGit")]
    public async Task NonRunnableDefinitionNeverGetsAConfirmation(string before, string after)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(Definition.Replace(before, after)));
        });
        using var client = Client(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(client).ReviewBuildAsync(42, "12345"));
    }

    [Theory]
    [InlineData("\"revision\":7", "\"revision\":8")]
    [InlineData("\"id\":8", "\"id\":9")]
    [InlineData("$/Example/Main", "$/Example/Changed")]
    [InlineData("enabled", "disabled")]
    [InlineData("TfsVersionControl", "TfsGit")]
    public async Task DefinitionChangeAfterReviewPreventsPost(string before, string after)
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(++calls == 1 ? Definition : Definition.Replace(before, after)));
        });
        using var client = Client(handler);
        var service = Service(client);
        var review = await service.ReviewBuildAsync(42, "12345");
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueueBuildAsync(review.ConfirmationToken!));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UnknownConfirmationDoesNotCallTfs()
    {
        using var handler = new Handler(_ => throw new Exception("No HTTP request should occur"));
        using var client = Client(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(client).QueueBuildAsync("tampered"));
    }

    [Fact]
    public async Task FailedPostCannotBeRetriedWithSameConfirmation()
    {
        var posts = 0;
        using var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(Definition));
            posts++;
            throw new HttpRequestException("Connection lost after request was sent");
        });
        using var client = Client(handler);
        var service = Service(client);
        var review = await service.ReviewBuildAsync(42, "12345");
        var error = await Assert.ThrowsAsync<TfsBuildRequestException>(() => service.QueueBuildAsync(review.ConfirmationToken!));
        Assert.True(error.MayHaveQueued);
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueueBuildAsync(review.ConfirmationToken!));
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task BuildDetailsLoadsMetadataOnlyAndKeepsArtifactsWhenLogsFail()
    {
        var paths = new List<string>();
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.RequestUri.AbsolutePath.Split('/').Last() switch
            {
                "101" => Json(Build),
                "logs" => new HttpResponseMessage(HttpStatusCode.Forbidden),
                "artifacts" => Json("""{"value":[{"name":"drop","resource":{"type":"filepath","data":"\\\\fileserver\\drop","url":"https://tfs.example/resource"}},{"name":"other","resource":null}]}"""),
                _ => throw new Exception("Log content must not be requested on Build Details")
            });
        });
        using var client = Client(handler);
        var controller = Controller(Service(client));
        var result = Assert.IsType<ViewResult>(await controller.BuildDetails(101));
        var model = Assert.IsType<TfsBuildPageViewModel>(result.Model);
        Assert.Equal(101, model.Build?.Id);
        Assert.Equal("In progress", model.Build?.Duration);
        Assert.Null(model.Build?.Result);
        Assert.Contains("denied access", model.LogsError);
        Assert.Null(model.ArtifactsError);
        Assert.Equal(2, model.Artifacts.Count);
        Assert.Null(model.Artifacts[1].Resource);
        Assert.Equal(3, paths.Count);
    }

    [Fact]
    public async Task SelectedLogUsesBoundedPlainTextRequest()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/tfs/DefaultCollection/PMSNZ/_apis/build/builds/101/logs/3?api-version=4.1&startLine=1000&endLine=1999", request.RequestUri!.PathAndQuery);
            Assert.Equal("text/plain", Assert.Single(request.Headers.Accept).MediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("line 1\r\n<script>text only</script>\n", Encoding.UTF8, "text/plain") });
        });
        using var client = Client(handler);
        Assert.Equal("line 1\r\n<script>text only</script>\n", await Service(client).GetBuildLogAsync(101, 3, 1000));
    }

    [Fact]
    public async Task LogViewerLoadsMetadataAndOnlySelectedLog()
    {
        var paths = new List<string>();
        using var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return Task.FromResult(path.Split('/').Last() switch
            {
                "101" => Json(Build),
                "logs" => Json("""{"value":[{"id":3,"type":"Container","lineCount":50000},{"id":4,"lineCount":10}]}"""),
                "3" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("selected content", Encoding.UTF8, "text/plain") },
                _ => throw new Exception("Only the selected log may be loaded")
            });
        });
        using var client = Client(handler);
        var result = Assert.IsType<ViewResult>(await Controller(Service(client)).BuildLog(101, 3));
        var model = Assert.IsType<TfsLogPageViewModel>(result.Model);
        Assert.Equal("20260925.1", model.BuildNumber);
        Assert.Equal(50000, model.LineCount);
        Assert.Equal("selected content", model.Content);
        Assert.True(model.HasNext);
        Assert.Equal(3, paths.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task BuildErrorsDoNotExposeTfsResponseBody(HttpStatusCode code)
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(code)
        {
            Content = new StringContent("private server details")
        }));
        using var client = Client(handler);
        var result = Assert.IsType<ViewResult>(await Controller(Service(client)).BuildDetails(101));
        var model = Assert.IsType<TfsBuildPageViewModel>(result.Model);
        Assert.NotNull(model.Error);
        Assert.DoesNotContain("private server details", model.Error);
        Assert.Null(model.Build);
    }

    [Fact]
    public async Task InvalidReadIdsAreRejectedBeforeHttp()
    {
        using var handler = new Handler(_ => throw new Exception("No HTTP request should occur"));
        using var client = Client(handler);
        var service = Service(client);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetBuildAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetBuildLogsAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetBuildArtifactsAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetBuildLogAsync(101, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetBuildLogAsync(101, 3, -1));
    }

    [Fact]
    public async Task ConcurrentConfirmationSubmissionsOnlySendOnePost()
    {
        var posts = 0;
        using var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(Definition));
            Interlocked.Increment(ref posts);
            return Task.FromResult(Json(Build));
        });
        using var client = Client(handler);
        var service = Service(client);
        var review = await service.ReviewBuildAsync(42, "12345");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            try { await service.QueueBuildAsync(review.ConfirmationToken!); return true; }
            catch (ArgumentException) { return false; }
        })));
        Assert.Single(attempts, x => x);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task OversizedLogIsRejected()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 1_000_001), Encoding.UTF8, "text/plain")
        }));
        using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(client).GetBuildLogAsync(101, 3));
    }

    [Fact]
    public async Task ConfirmationRedirectsToReturnedBuild()
    {
        using var handler = new Handler(request => Task.FromResult(Json(request.Method == HttpMethod.Post ? Build : Definition)));
        using var client = Client(handler);
        var service = Service(client);
        var review = await service.ReviewBuildAsync(42, "12345");
        var result = Assert.IsType<RedirectToActionResult>(await Controller(service).ConfirmBuild(review.ConfirmationToken!));
        Assert.Equal("BuildDetails", result.ActionName);
        Assert.Equal(101, result.RouteValues!["id"]);
    }

    [Fact]
    public void ReviewAndConfirmRequirePostAndAntiforgery()
    {
        foreach (var name in new[] { nameof(TfsController.ReviewBuild), nameof(TfsController.ReviewExistingBuild), nameof(TfsController.ReviewCiBuild), nameof(TfsController.ConfirmBuild) })
        {
            var method = typeof(TfsController).GetMethod(name)!;
            Assert.Single(method.GetCustomAttributes(typeof(HttpPostAttribute), true));
            Assert.Single(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        }
    }

    [Fact]
    public void DurationHandlesCompletedAndNotStartedBuilds()
    {
        var build = new TfsBuildDetailsViewModel();
        Assert.Equal("Not started", build.Duration);
        build.StartTime = DateTimeOffset.Parse("2026-09-25T05:00:00Z");
        build.FinishTime = build.StartTime.Value.AddMinutes(5);
        Assert.Equal("0.00:05:00", build.Duration);
    }

    [Fact]
    public async Task RejectionShowsSafeDiagnosticsWithoutRawServerMessage()
    {
        using var handler = new Handler(request => Task.FromResult(request.Method == HttpMethod.Get ? Json(Definition)
            : new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"typeKey":"BuildRequestValidationFailedException","errorCode":123,"message":"private server details"}""", Encoding.UTF8, "application/json")
            }));
        using var client = Client(handler);
        var service = Service(client);
        var review = await service.ReviewBuildAsync(42, "12345");
        var result = Assert.IsType<ViewResult>(await Controller(service).ConfirmBuild(review.ConfirmationToken!));
        var model = Assert.IsType<TfsRunBuildViewModel>(result.Model);
        Assert.Equal(42, model.DefinitionId);
        Assert.Contains("HTTP 400", model.Error);
        Assert.Contains("BuildRequestValidationFailedException", model.Error);
        Assert.Contains("123", model.Error);
        Assert.DoesNotContain("private server details", model.Error);
        Assert.DoesNotContain("may already have accepted", model.Error);
    }

    [Fact]
    public async Task FailedDefinitionReloadReportsThatNoPostWasSent()
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(++calls == 1 ? Json(Definition) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var client = Client(handler);
        var service = Service(client);
        var review = await service.ReviewBuildAsync(42, "12345");
        var result = Assert.IsType<ViewResult>(await Controller(service).ConfirmBuild(review.ConfirmationToken!));
        var model = Assert.IsType<TfsRunBuildViewModel>(result.Model);
        Assert.Contains("No build POST was sent", model.Error);
        Assert.Contains("HTTP 503", model.Error);
    }

    [Theory]
    [InlineData("succeeded", "Rebuild")]
    [InlineData("failed", "Rebuild")]
    [InlineData("canceled", "Build")]
    public async Task ExistingBuildReviewQueuesNewBuildWithServerValues(string result, string action)
    {
        var posts = 0;
        var oldBuild = Build.Replace("inProgress", "completed").Replace("\"status\":", $"\"result\":\"{result}\",\"status\":");
        using var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(request.RequestUri!.AbsolutePath.EndsWith("/builds/101") ? oldBuild : Definition);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/builds", request.RequestUri!.AbsolutePath);
            posts++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(42, body.RootElement.GetProperty("definition").GetProperty("id").GetInt32());
            Assert.Equal("12345", body.RootElement.GetProperty("sourceVersion").GetString());
            return Json(Build.Replace("\"id\":101", "\"id\":202"));
        });
        using var client = Client(handler);
        var service = Service(client);
        Assert.Equal(action, (await service.GetBuildAsync(101)).BuildAction);
        var controller = Controller(service);
        var view = Assert.IsType<ViewResult>(await controller.ReviewExistingBuild(101));
        Assert.Equal("ReviewBuild", view.ViewName);
        var review = Assert.IsType<TfsRunBuildViewModel>(view.Model);
        Assert.Equal(0, posts);
        review.DefinitionId = 999;
        review.SourceVersion = "999";
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.ConfirmBuild(review.ConfirmationToken!));
        Assert.Equal("BuildDetails", redirect.ActionName);
        Assert.Equal(202, redirect.RouteValues!["id"]);
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueueBuildAsync(review.ConfirmationToken!));
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData("completed", "partiallySucceeded", 42, "12345", "$/Example/Main")]
    [InlineData("inProgress", "succeeded", 42, "12345", "$/Example/Main")]
    [InlineData("completed", "succeeded", 0, "12345", "$/Example/Main")]
    [InlineData("completed", "succeeded", 42, "", "$/Example/Main")]
    [InlineData("completed", "succeeded", 42, "C12345", "$/Example/Main")]
    [InlineData("completed", "succeeded", 42, "12345", "")]
    [InlineData("completed", "succeeded", 42, "12345", "$/Other")]
    public async Task InvalidOriginalBuildCannotBeReviewed(string status, string result, int definitionId, string version, string branch)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("/builds/101")
                ? JsonSerializer.Serialize(new { id = 101, status, result, definition = new { id = definitionId }, sourceVersion = version, sourceBranch = branch })
                : Definition));
        });
        using var client = Client(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(client).ReviewExistingBuildAsync(101));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidExistingBuildIdMakesNoRequest(int id)
    {
        using var handler = new Handler(_ => throw new Exception("No request expected"));
        using var client = Client(handler);
        var controller = Controller(Service(client));
        Assert.IsType<ViewResult>(await controller.ReviewExistingBuild(id));
        Assert.Equal(400, controller.Response.StatusCode);
    }

    [Fact]
    public async Task CiBuildResolvesSourceOnClickAndQueuesReviewedSnapshotOnce()
    {
        var changesetGets = 0;
        var posts = 0;
        using var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/tfvc/changesets"))
                {
                    changesetGets++;
                    Assert.Equal("/tfs/DefaultCollection/PMSNZ/_apis/tfvc/changesets?api-version=4.1&$top=1&$orderby=id%20desc&searchCriteria.itemPath=%24%2FExample%2FMain", request.RequestUri.PathAndQuery);
                    return Json("""{"value":[{"changesetId":45678}]}""");
                }
                return Json(request.RequestUri.AbsolutePath.EndsWith("/definitions") ? "{\"value\":[" + Definition + "]}" : Definition);
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/builds", request.RequestUri!.AbsolutePath);
            posts++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(42, body.RootElement.GetProperty("definition").GetProperty("id").GetInt32());
            Assert.Equal("$/Example/Main", body.RootElement.GetProperty("sourceBranch").GetString());
            Assert.Equal("45678", body.RootElement.GetProperty("sourceVersion").GetString());
            return Json(Build);
        });
        using var client = Client(handler);
        var service = Service(client);
        Assert.True(Assert.Single(await service.GetBuildDefinitionsAsync()).CanRun);
        Assert.Equal(0, changesetGets);
        var controller = Controller(service);
        var view = Assert.IsType<ViewResult>(await controller.ReviewCiBuild(42));
        Assert.Equal("ReviewCiBuild", view.ViewName);
        var review = Assert.IsType<TfsRunBuildViewModel>(view.Model);
        Assert.Equal(1, changesetGets);
        Assert.Equal(0, posts);
        review.SourceVersion = "1";
        review.Definition!.Repository!.DefaultBranch = "$/Tampered";
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.ConfirmBuild(review.ConfirmationToken!));
        Assert.Equal("BuildDetails", redirect.ActionName);
        Assert.Equal(101, redirect.RouteValues!["id"]);
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueueBuildAsync(review.ConfirmationToken!));
        Assert.Equal(1, changesetGets);
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData("{\"value\":[]}")]
    [InlineData("{\"value\":[{\"changesetId\":0}]}")]
    [InlineData("{\"value\":[null]}")]
    [InlineData("{}")]
    public async Task CiBuildRejectsMissingOrInvalidChangesets(string payload)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("/tfvc/changesets") ? payload : Definition));
        });
        using var client = Client(handler);
        var view = Assert.IsType<ViewResult>(await Controller(Service(client)).ReviewCiBuild(42));
        var model = Assert.IsType<TfsRunBuildViewModel>(view.Model);
        Assert.NotNull(model.Error);
        Assert.Null(model.ConfirmationToken);
    }

    [Theory]
    [InlineData("enabled", "disabled")]
    [InlineData("enabled", "paused")]
    [InlineData("TfsVersionControl", "TfsGit")]
    [InlineData("$/Example/Main", "refs/heads/main")]
    public async Task CiBuildRejectsIneligibleDefinitionBeforeChangesetLookup(string before, string after)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/definitions/42", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(Definition.Replace(before, after)));
        });
        using var client = Client(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(client).ReviewLatestCiBuildAsync(42));
    }

    [Theory]
    [InlineData("\"revision\":7", "\"revision\":8")]
    [InlineData("$/Example/Main", "$/Other")]
    [InlineData("enabled", "disabled")]
    public async Task CiBuildRevalidatesDefinitionBeforeQueueing(string before, string after)
    {
        var definitions = 0;
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("/tfvc/changesets")
                ? "{\"value\":[{\"changesetId\":12345}]}"
                : ++definitions == 1 ? Definition : Definition.Replace(before, after)));
        });
        using var client = Client(handler);
        var service = Service(client);
        var review = await service.ReviewLatestCiBuildAsync(42);
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueueBuildAsync(review.ConfirmationToken!));
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueueBuildAsync(review.ConfirmationToken!));
        Assert.Equal(2, definitions);
    }

    private static TfsController Controller(ITfsService service) => new(service, NullLogger<TfsController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };
    private static TfsService Service(HttpClient client) => new(client,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tfs:Project"] = "PMSNZ", ["Tfs:ApiVersion"] = "4.1"
        }).Build(), new TfsBuildConfirmationStore());
    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://tfs.example/tfs/DefaultCollection/") };
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
