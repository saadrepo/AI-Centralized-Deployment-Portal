using System.Net;
using System.Text;
using System.Text.Json;
using DeploymentPortal.Services;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class SourceApiResponseTests
{
    private const string Token = "synthetic-only-PAT";
    private static HttpResponseMessage Response(string body = "{\"value\":[]}", HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(429)] [InlineData(500)] [InlineData(301)] [InlineData(302)]
    public async Task ErrorsAndRedirectsNeverExposeProviderBodyOrLocation(int status)
    {
        using var response = Response(Token + " https://private.example", (HttpStatusCode)status);
        response.Headers.Location = new Uri("https://evil.example/" + Token);
        var error = await Assert.ThrowsAsync<SourceExplorerException>(() => SourceApiResponse.ParseAsync(response, Token, default));
        Assert.DoesNotContain(Token, error.Message); Assert.DoesNotContain("private.example", error.Message); Assert.DoesNotContain("evil.example", error.Message); Assert.Null(error.InnerException);
    }
    [Fact]
    public async Task RateLimitIsSanitizedAndNeitherHeaderValuesNorRetryUrlsAreExposed()
    {
        using var response = Response(Token, HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0"); response.Headers.TryAddWithoutValidation("Retry-After", Token);
        var error = await Assert.ThrowsAsync<SourceExplorerException>(() => SourceApiResponse.ParseAsync(response, Token, default));
        Assert.Contains("rate limit", error.Message); Assert.DoesNotContain(Token, error.Message);
    }
    [Fact]
    public async Task ReflectedPlainEncodedAndJsonEscapedSecretsFailClosed()
    {
        foreach (var body in new[] { JsonSerializer.Serialize(new { name = Token }), "{\"name\":\"synthetic-only-\\u0050AT\"}",
            JsonSerializer.Serialize(new Dictionary<string, string> { [Token] = "value" }),
            JsonSerializer.Serialize(new { name = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + Token)) }) }) {
            using var response = Response(body);
            var error = await Assert.ThrowsAsync<SourceExplorerException>(() => SourceApiResponse.ParseAsync(response, Token, default));
            Assert.Equal(SourceExplorerException.Unavailable, error.Message);
        }
    }
    [Fact]
    public async Task ContinuationIsBoundedAndCannotContainCredential()
    {
        foreach (var value in new[] { Token, new string('x', 513), "unsafe value" }) {
            using var response = Response(); response.Headers.TryAddWithoutValidation("x-ms-continuationtoken", value);
            await Assert.ThrowsAsync<SourceExplorerException>(() => SourceApiResponse.ParseAsync(response, Token, default));
        }
    }
    [Theory]
    [InlineData("not json")] [InlineData("<html>login</html>")]
    public async Task InvalidProviderJsonIsSanitized(string body)
    {
        using var response = Response(body);
        var error = await Assert.ThrowsAsync<SourceExplorerException>(() => SourceApiResponse.ParseAsync(response, Token, default));
        Assert.Equal(SourceExplorerException.Unavailable, error.Message);
    }
    [Fact]
    public async Task NonJsonAndOversizedResponseFailsClosed()
    {
        using var html = Response(); html.Content.Headers.ContentType = new("text/html");
        await Assert.ThrowsAsync<SourceExplorerException>(() => SourceApiResponse.ParseAsync(html, Token, default));
        using var oversized = Response(new string('x', 2097153));
        await Assert.ThrowsAsync<SourceExplorerException>(() => SourceApiResponse.ParseAsync(oversized, Token, default));
    }
    [Fact]
    public async Task SafeMetadataAndOpaqueContinuationAreReturnedButArbitraryNextUrlIsNot()
    {
        using var response = Response(); response.Headers.Add("x-ms-continuationtoken", "opaque+token");
        response.Headers.Add("Link", "<https://evil.example/private>; rel=\"next\"");
        using var page = await SourceApiResponse.ParseAsync(response, Token, default);
        Assert.True(page.HasMore); Assert.Equal("opaque+token", page.Continuation);
        Assert.Empty(page.Json.RootElement.GetProperty("value").EnumerateArray());
    }
}
