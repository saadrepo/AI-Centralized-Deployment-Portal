using DeploymentPortal.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeploymentPortal.Tests;

public class CloudDenialAuditTests
{
    private sealed class Capture : ILogger<CloudDenialAuditMiddleware>
    {
        public List<string> Events = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            Assert.Equal(4103, id.Id); Events.Add(formatter(state, exception));
        }
    }
    [Fact]
    public async Task MiddlewareAuditsAuthorizationAndAntiforgeryShortCircuitsWithoutRequestSecrets()
    {
        var capture = new Capture();
        foreach (var status in new[] { 401, 403, 400, 409 }) {
            var http = new DefaultHttpContext(); http.TraceIdentifier = "safe-reference";
            http.Request.Path = "/Pipeline/Cloud/Approve";
            http.Request.QueryString = new("?ticket=synthetic-secret&token=synthetic-secret");
            http.Request.Headers.Authorization = "Bearer synthetic-secret";
            var middleware = new CloudDenialAuditMiddleware(context => { context.Response.StatusCode = status; return Task.CompletedTask; }, capture);
            await middleware.InvokeAsync(http);
        }
        Assert.Equal(4, capture.Events.Count);
        Assert.All(capture.Events, e => { Assert.Contains("safe-reference", e); Assert.DoesNotContain("synthetic-secret", e); });
    }
}
