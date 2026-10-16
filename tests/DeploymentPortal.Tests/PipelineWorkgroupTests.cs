using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using DeploymentPortal.Controllers;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class PipelineWorkgroupTests
{
    private const string Actor = "11111111-1111-1111-1111-111111111111";
    private const string Other = "22222222-2222-2222-2222-222222222222";
    private const string Url = "https://netserver/tfs/DefaultCollection/";

    [Fact]
    public void ServiceCiRequiresExplicitRealPortalSidAndExpectedTfsIdentity()
    {
        var options = new PipelineWorkgroupOptions { ServiceCiEnabled = true, ServiceIdentityId = Actor, ServiceCiUsers = ["S-1-5-21-1"] };
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "Fake"));
        Assert.False(PipelineWorkgroupTransport.ServiceCiAllowed(options, forged));
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var user = new WindowsPrincipal(identity);
        options.ServiceCiUsers = [identity.User!.Value];
        Assert.True(PipelineWorkgroupTransport.ServiceCiAllowed(options, user));
        options.ServiceCiEnabled = false;
        Assert.False(PipelineWorkgroupTransport.ServiceCiAllowed(options, user));
    }

    [Fact]
    public void PersonalSessionIsBoundToPortalSidAndFixedExpiration()
    {
        using var sessions = new PipelinePersonalSessions();
        var key = sessions.Store(new("sid-a", Actor, "synthetic-token", DateTimeOffset.UtcNow.AddMinutes(10), Url));
        Assert.NotNull(sessions.Find(key, "sid-a"));
        Assert.Null(sessions.Find(key, "sid-b"));
        sessions.Remove(key, "sid-b");
        Assert.NotNull(sessions.Find(key, "sid-a"));
        sessions.Remove(key, "sid-a");
        Assert.Null(sessions.Find(key, "sid-a"));
    }

    [Theory]
    [InlineData("http://netserver:8080/tfs/DefaultCollection")]
    [InlineData("https://attacker/tfs/DefaultCollection")]
    [InlineData("https://netserver/tfs/OtherCollection")]
    public void PersonalEndpointCannotUseHttpOrDifferentCollection(string endpoint)
    {
        Assert.Throws<InvalidOperationException>(() => PipelineWorkgroupTransport.PersonalCollection(
            new() { PersonalCollectionUrl = endpoint }, new() { BaseUrl = "http://netserver:8080/tfs/DefaultCollection" }));
    }

    [Fact]
    public void PersonalHttpsBindingDoesNotChangeExistingServiceCollection()
    {
        var connection = new TfsGitConnectionOptions { BaseUrl = "http://netserver:8080/tfs/DefaultCollection" };
        Assert.Equal(Url, PipelineWorkgroupTransport.PersonalCollection(new() { PersonalCollectionUrl = Url }, connection));
        Assert.StartsWith("http:", connection.BaseUrl);
    }

    [Theory]
    [InlineData(false, "PATCH", "project/_apis/release/approvals/21")]
    [InlineData(true, "POST", "project/_apis/build/builds")]
    [InlineData(false, "POST", "project/_apis/release/releases")]
    [InlineData(true, "PATCH", "https://attacker/project/_apis/release/approvals/21")]
    public async Task ActionGuardsCannotCrossCiCdOrCollectionBoundaries(bool personal, string method, string path)
    {
        using var http = new HttpClient(new PipelineActionGuard(new(Url), personal) { InnerHandler = new Reply(Actor) }) { BaseAddress = new(Url) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.SendAsync(new(new HttpMethod(method), path)));
    }

    [Fact]
    public async Task ServiceIdentityMismatchNeverInvokesAction()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var user = new WindowsPrincipal(identity);
        var options = new PipelineWorkgroupOptions { ServiceCiEnabled = true, ServiceIdentityId = Actor, ServiceCiUsers = [identity.User!.Value] };
        using var sessions = new PipelinePersonalSessions();
        var transport = Transport(options, sessions, new DefaultHttpContext { User = user }, Other);
        var invoked = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transport.RunBuildAsync(user, (http, id) => { invoked = true; return Task.FromResult(true); }));
        Assert.False(invoked);
    }

    [Fact]
    public async Task AuthorizedServiceCiUsesVerifiedActorAndCannotAuthorizeCd()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var user = new WindowsPrincipal(identity);
        var options = new PipelineWorkgroupOptions { ServiceCiEnabled = true, ServiceIdentityId = Actor, ServiceCiUsers = [identity.User!.Value] };
        using var sessions = new PipelinePersonalSessions();
        var transport = Transport(options, sessions, new DefaultHttpContext { User = user }, Actor);
        Assert.Equal(Actor, await transport.RunBuildAsync(user, (http, id) => Task.FromResult(id)));
        if (identity.ImpersonationLevel != TokenImpersonationLevel.Delegation)
        {
            Assert.False(transport.Available(user));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transport.RunAsync(user, (http, id) => Task.FromResult(id)));
        }
    }

    [Fact]
    public async Task PersonalSessionRevalidatesActualTfsIdentityBeforeCallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var user = new WindowsPrincipal(identity); var sid = identity.User!.Value;
        using var sessions = new PipelinePersonalSessions();
        var key = sessions.Store(new(sid, Actor, "synthetic-token", DateTimeOffset.UtcNow.AddMinutes(10), Url));
        var context = new DefaultHttpContext { User = user };
        context.Request.Scheme = "https";
        context.Request.Headers.Cookie = PipelineWorkgroupTransport.Cookie + "=" + key;
        var options = new PipelineWorkgroupOptions { PerUserAuthenticationEnabled = true, PerUserTfsIdentities = { [sid] = Actor } };
        var transport = Transport(options, sessions, context, Other);
        Assert.True(transport.Available(user));
        var invoked = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transport.RunAsync(user, (http, id) => { invoked = true; return Task.FromResult(id); }));
        Assert.False(invoked);
        Assert.Equal(Actor, await Transport(options, sessions, context, Actor).RunAsync(user, (http, id) => Task.FromResult(id)));
    }

    [Fact]
    public async Task PersonalConnectOverHttpNeverCallsTfs()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        using var sessions = new PipelinePersonalSessions();
        var factory = new Factory(Actor);
        var controller = new PipelineIdentityController(Options.Create(new PipelineWorkgroupOptions {
            PerUserAuthenticationEnabled = true, PerUserTfsIdentities = { [identity.User!.Value] = Actor }
        }), Options.Create(new TfsGitConnectionOptions { BaseUrl = Url }), sessions, NullLogger<PipelineIdentityController>.Instance, factory)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new WindowsPrincipal(identity) } } };
        Assert.IsType<BadRequestObjectResult>(await controller.Connect("synthetic-token", default));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void PersonalConnectAndDisconnectRequireAntiforgery()
    {
        foreach (var method in new[] { "Connect", "Disconnect" })
            Assert.NotEmpty(typeof(PipelineIdentityController).GetMethod(method)!.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
    }

    [Fact]
    public void ServiceIdentityCannotBeMappedAsPersonalApproverAndTokenIsNeverSerialized()
    {
        var options = new PipelineWorkgroupOptions { PerUserAuthenticationEnabled = true, ServiceIdentityId = Actor, PerUserTfsIdentities = { ["sid"] = Actor } };
        Assert.False(PipelineWorkgroupTransport.PersonalIdentityAllowed(options, "sid", Actor));
        options.PerUserTfsIdentities["sid"] = Other;
        Assert.True(PipelineWorkgroupTransport.PersonalIdentityAllowed(options, "sid", Other));
        var session = new PipelinePersonalSession("sid", Other, "never-expose-this-token", DateTimeOffset.UtcNow.AddMinutes(10), Url);
        Assert.DoesNotContain("never-expose-this-token", System.Text.Json.JsonSerializer.Serialize(session));
        Assert.DoesNotContain("never-expose-this-token", session.ToString());
    }

    [Theory]
    [InlineData(false, "POST", "project/_apis/build/builds?api-version=4.1")]
    [InlineData(true, "PATCH", "project/_apis/release/approvals/21?api-version=4.1-preview.2")]
    public async Task ScopedActionGuardsAllowTheirOwnSupportedWrite(bool personal, string method, string path)
    {
        var header = personal ? "Basic synthetic" : null;
        using var http = new HttpClient(new PipelineActionGuard(new(Url), personal, header) { InnerHandler = new Reply(Actor) }) { BaseAddress = new(Url) };
        if (personal) http.DefaultRequestHeaders.Authorization = new("Basic", "synthetic");
        using var response = await http.SendAsync(new(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersonalConnectChecksTfsMappingAndKeepsTokenOutOfResponse(bool correctIdentity)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        using var sessions = new PipelinePersonalSessions();
        var factory = new Factory(correctIdentity ? Actor : Other);
        var context = new DefaultHttpContext { User = new WindowsPrincipal(identity) }; context.Request.Scheme = "https";
        var controller = new PipelineIdentityController(Options.Create(new PipelineWorkgroupOptions {
            PerUserAuthenticationEnabled = true, PerUserTfsIdentities = { [identity.User!.Value] = Actor }
        }), Options.Create(new TfsGitConnectionOptions { BaseUrl = Url }), sessions, NullLogger<PipelineIdentityController>.Instance, factory)
        { ControllerContext = new() { HttpContext = context } };
        var result = await controller.Connect("synthetic-secret-pat", default);
        Assert.Equal(1, factory.Calls);
        if (correctIdentity)
        {
            Assert.DoesNotContain("synthetic-secret-pat", System.Text.Json.JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value));
            var cookie = context.Response.Headers.SetCookie.ToString();
            Assert.Contains("secure", cookie); Assert.Contains("httponly", cookie); Assert.Contains("samesite=strict", cookie);
            Assert.DoesNotContain("synthetic-secret-pat", cookie);
        }
        else
        {
            Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal(0, context.Response.Headers.SetCookie.Count);
        }
    }

    private static PipelineWorkgroupTransport Transport(PipelineWorkgroupOptions options, PipelinePersonalSessions sessions, HttpContext context, string actual)
    {
        var connection = Options.Create(new TfsGitConnectionOptions { BaseUrl = Url });
        var windows = new PipelineUserTransport(connection, new Env());
        var factory = new Factory(actual);
        return new(windows, factory, Options.Create(options), connection, sessions,
            new HttpContextAccessor { HttpContext = context }, NullLogger<PipelineWorkgroupTransport>.Instance, factory);
    }
    private sealed class Factory(string id) : IHttpClientFactory, IPipelinePersonalClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) { Calls++; return new(new Reply(id)) { BaseAddress = new(Url) }; }
        public HttpClient Create(string collection, string token) => CreateClient("personal");
    }
    private sealed class Reply(string id) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"authenticatedUser\":{{\"id\":\"{id}\"}}}}") });
    }
    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
