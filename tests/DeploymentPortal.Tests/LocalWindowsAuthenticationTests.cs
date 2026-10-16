using System.Net;
using System.Net.Security;
using System.ComponentModel;
using System.Security.Principal;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class LocalWindowsAuthenticationTests
{
    [Fact]
    public void ProductionRegistrationIsUnchanged()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var original = services.ToArray();
        services.AddLocalWindowsAuthentication(new HostEnvironment { EnvironmentName = Environments.Production });
        Assert.Equal(original, services.ToArray());
    }

    [Fact]
    public void LocalTransportAcceptsOnlyRealSameWindowsUserAndPreservesProductionDelegation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var user = new WindowsPrincipal(identity);
        var settings = Options.Create(new TfsGitConnectionOptions { BaseUrl = "http://netserver:8080/tfs/DefaultCollection" });
        var dev = new PipelineUserTransport(settings, new HostEnvironment { EnvironmentName = Environments.Development });
        Assert.True(dev.AvailableForBuild(user));
        Assert.True(dev.Available(user));
        var production = new PipelineUserTransport(settings, new HostEnvironment { EnvironmentName = Environments.Production });
        Assert.Equal(production.Available(user), production.AvailableForBuild(user));
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, identity.Name)], "Fake"));
        Assert.False(dev.AvailableForBuild(forged));
        Assert.False(dev.Available(forged));
        Assert.Equal(identity.ImpersonationLevel == TokenImpersonationLevel.Delegation, production.Available(user));
    }

    [Fact]
    public async Task ClaimsOnlyLocalApproverCannotReachTfsCallback()
    {
        var transport = new PipelineUserTransport(Options.Create(new TfsGitConnectionOptions { BaseUrl = "http://netserver:8080/tfs/DefaultCollection" }),
            new HostEnvironment { EnvironmentName = Environments.Development });
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "local-user")], "Fake"));
        var called = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transport.RunAsync(forged, (http, id) => { called = true; return Task.FromResult(true); }));
        Assert.False(called);
    }

    [Theory]
    [InlineData("/Pipeline")]
    [InlineData("/pipeline/Status")]
    [InlineData("/Pipeline/Queue")]
    [InlineData("/Pipeline/Approve")]
    public async Task AnonymousPipelineGetsRealNegotiateChallenge(string path)
    {
        await using var app = CreateHost();
        await app.StartAsync();
        try
        {
            using var client = Client(app);
            // A forged user header must never authenticate a request.
            client.DefaultRequestHeaders.Add("X-Windows-User", "DOMAIN\\administrator");
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate");
            Assert.DoesNotContain("protected endpoint executed", await response.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task DashboardDoesNotRequireNewLogin()
    {
        await using var app = CreateHost();
        await app.StartAsync();
        try
        {
            using var client = Client(app);
            var response = await client.GetAsync("/Home/Index");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(response.Headers.WwwAuthenticate);
        }
        finally { await app.StopAsync(); }
    }

    [InteractiveWindowsFact]
    public async Task CurrentWindowsCredentialsCanAuthenticateToLocalKestrel()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var app = CreateHost();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var local = new UriBuilder(address) { Host = "localhost" }.Uri;
            using var client = new HttpClient(new HttpClientHandler { UseDefaultCredentials = true, UseProxy = false }) { BaseAddress = local };
            var response = await client.GetAsync("/Pipeline/WhoAmI");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var current = WindowsIdentity.GetCurrent();
            Assert.Equal(current.Name, await response.Content.ReadAsStringAsync(), ignoreCase: true);
        }
        finally { await app.StopAsync(); }
    }

    private static WebApplication CreateHost()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLocalWindowsAuthentication(builder.Environment);
        var app = builder.Build();
        app.UseRouting();
        app.UseLocalWindowsAuthentication(app.Environment);
        app.MapGet("/Home/Index", () => "Existing Dashboard access");
        app.MapGet("/Pipeline/WhoAmI", (Microsoft.AspNetCore.Http.HttpContext context) => context.User.Identity?.Name ?? "anonymous");
        app.MapGet("/Pipeline/{**action}", () => "protected endpoint executed");
        return app;
    }

    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler { UseDefaultCredentials = false })
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
    };

    private sealed class HostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

// Restricted CI/sandbox processes may have a Windows identity but no SSPI logon credentials.
public sealed class InteractiveWindowsFactAttribute : FactAttribute
{
    public InteractiveWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) { Skip = "Requires an interactive Windows logon."; return; }
        try
        {
            using var probe = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
            { Credential = CredentialCache.DefaultNetworkCredentials, TargetName = "HTTP/localhost" });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == unchecked((int)0x8009030E))
        { Skip = "Current process has no SSPI credentials; run from an interactive Windows session."; }
    }
}
