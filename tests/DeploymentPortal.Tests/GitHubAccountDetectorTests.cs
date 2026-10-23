using System.Net;
using System.Text;
using System.Text.Json;
using DeploymentPortal.Services;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class GitHubAccountDetectorTests
{
    // Exercise the real detector with synthetic GitHub responses; never make live provider requests.
    internal static GitHubAccountDetector Detector() => new(new HttpClient(new Handler(request => {
        var login = request.RequestUri!.Segments.Last();
        return Json(new { login, type = login == "saadrepo" ? "User" : "Organization" });
    })));
    internal static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Calls++; return Task.FromResult(reply(request));
        }
    }

    [Theory]
    [InlineData("saadrepo", "User", "users")]
    [InlineData("https://github.com/SaadRepo", "User", "users")]
    [InlineData("https://api.github.com/users/saadrepo/", "User", "users")]
    [InlineData("https://api.github.com/orgs/saadrepo/", "User", "users")]
    [InlineData("example", "Organization", "orgs")]
    [InlineData("https://github.com/example/", "Organization", "orgs")]
    [InlineData("https://api.github.com/orgs/example/", "Organization", "orgs")]
    [InlineData("https://api.github.com/users/example", "Organization", "orgs")]
    public async Task AccountTypeAlwaysComesFromApiRegardlessOfInputPath(string input, string type, string segment)
    {
        var login = GitHubAccountDetector.ParseLogin(input);
        var handler = new Handler(request => {
            Assert.Equal($"https://api.github.com/users/{login}", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.Contains("DeploymentPortal/1.0", request.Headers.UserAgent.ToString());
            Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
            Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            return Json(new { login = login.ToUpperInvariant(), type });
        });
        using var detector = new GitHubAccountDetector(new HttpClient(handler));
        var result = await detector.DetectAsync(input);
        Assert.Equal(type, result.Type);
        Assert.Equal($"https://api.github.com/{segment}/{login}/", result.Destination);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://evil.example/saadrepo")]
    [InlineData("https://api.github.com.evil.example/users/saadrepo/")]
    [InlineData("https://github.com/saadrepo/repository")]
    [InlineData("https://github.com/saadrepo?x=1")]
    [InlineData("https://github.com/saadrepo#fragment")]
    [InlineData("https://github.com:443/saadrepo")]
    [InlineData("https://user:password@github.com/saadrepo")]
    [InlineData("http://github.com/saadrepo")]
    [InlineData("https://api.github.com/users/saadrepo/../example")]
    [InlineData("https://api.github.com/users/%73aadrepo")]
    [InlineData("https://api.github.com/users/saadrepo\\example")]
    [InlineData("saadrepo\n")]
    [InlineData("saadrepo';whoami")]
    public async Task UnsafeInputsNeverReachNetwork(string input)
    {
        var handler = new Handler(_ => throw new Exception("Must not send"));
        using var detector = new GitHubAccountDetector(new HttpClient(handler));
        await Assert.ThrowsAsync<ArgumentException>(() => detector.DetectAsync(input));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(429)] [InlineData(500)]
    [InlineData(301)] [InlineData(302)]
    public async Task ApiFailuresNeverGuessOrRetryOrExposeResponse(int status)
    {
        var handler = new Handler(_ => new HttpResponseMessage((HttpStatusCode)status) {
            Content = new StringContent("synthetic-secret-never-display"), Headers = { Location = new Uri("https://evil.example/") }
        });
        using var detector = new GitHubAccountDetector(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => detector.DetectAsync("saadrepo"));
        Assert.DoesNotContain("synthetic-secret", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"login\":\"other\",\"type\":\"User\"}")]
    [InlineData("{\"login\":\"saadrepo\",\"type\":\"Bot\"}")]
    [InlineData("{\"login\":\"saadrepo\",\"type\":42}")]
    public async Task InvalidResponsesFailClosed(string body)
    {
        using var detector = new GitHubAccountDetector(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) })));
        await Assert.ThrowsAsync<ArgumentException>(() => detector.DetectAsync("saadrepo"));
    }

    [Fact]
    public async Task OversizedResponseAndNetworkOrTimeoutFailuresAreSanitized()
    {
        foreach (var handler in new[] {
            new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65537)) }),
            new Handler(_ => throw new HttpRequestException("synthetic-secret")),
            new Handler(_ => throw new TaskCanceledException("synthetic-secret")) }) {
            using var detector = new GitHubAccountDetector(new HttpClient(handler));
            var error = await Assert.ThrowsAsync<ArgumentException>(() => detector.DetectAsync("saadrepo"));
            Assert.DoesNotContain("synthetic-secret", error.Message); Assert.Null(error.InnerException);
        }
    }
}
