using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class PipelineIisAuthenticationTests
{
    [Theory]
    [InlineData("/Pipeline")]
    [InlineData("/Pipeline/Reject")]
    [InlineData("/Pipeline/Status")]
    public async Task ProductionPipelineChallengesNativeIisScheme(string path)
    {
        await using var app = CreateHost(true, false);
        await app.StartAsync();
        using var client = Client(app);
        client.DefaultRequestHeaders.Add("X-Windows-User", "DOMAIN\\forged");
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate");
        Assert.DoesNotContain("protected endpoint", await response.Content.ReadAsStringAsync());
        await app.StopAsync();
    }

    [Fact]
    public async Task NativeIisAuthenticatedPrincipalReachesPipeline()
    {
        await using var app = CreateHost(true, true);
        await app.StartAsync();
        using var client = Client(app);
        Assert.Equal("protected endpoint: office-user", await client.GetStringAsync("/Pipeline"));
        await app.StopAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublicPagesRemainAnonymousAndUnchallenged(bool nativeScheme)
    {
        await using var app = CreateHost(nativeScheme, false);
        await app.StartAsync();
        using var client = Client(app);
        var response = await client.GetAsync("/Home/Index");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        await app.StopAsync();
    }

    [Fact]
    public async Task MissingIisHandlerFailsClosedWithConfigurationReason()
    {
        await using var app = CreateHost(false, false);
        await app.StartAsync();
        using var client = Client(app);
        var response = await client.GetAsync("/Pipeline");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Windows Authentication is unavailable", await response.Content.ReadAsStringAsync());
        await app.StopAsync();
    }

    private static WebApplication CreateHost(bool nativeScheme, bool authenticated)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLocalWindowsAuthentication(builder.Environment);
        if (nativeScheme)
            builder.Services.AddAuthentication().AddScheme<NativeOptions, FakeNativeHandler>(IISDefaults.AuthenticationScheme, o => o.Authenticated = authenticated);
        var app = builder.Build();
        app.UseRouting();
        app.UseLocalWindowsAuthentication(app.Environment);
        app.MapGet("/Home/Index", () => "Existing Dashboard");
        app.MapGet("/Pipeline/{**action}", (HttpContext context) => "protected endpoint: " + context.User.Identity?.Name);
        return app;
    }

    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler { UseDefaultCredentials = false, UseProxy = false })
    { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };

    public sealed class NativeOptions : AuthenticationSchemeOptions { public bool Authenticated { get; set; } }
    public sealed class FakeNativeHandler(IOptionsMonitor<NativeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<NativeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Options.Authenticated
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "office-user")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.NoResult());
        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = 401;
            Response.Headers.WWWAuthenticate = "Negotiate";
            return Task.CompletedTask;
        }
    }
}
