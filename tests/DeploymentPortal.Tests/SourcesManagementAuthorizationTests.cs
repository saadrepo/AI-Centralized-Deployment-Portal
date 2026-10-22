using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Encodings.Web;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class SourcesManagementAuthorizationTests
{
    private const string ExampleSid = "S-1-5-21-1-2-3-1001";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1-5-32-544")]
    [InlineData("SERVER\\operator")]
    [InlineData("S-1-5-21-1-2-3")]
    [InlineData("S-1-5-21-1-2-3-4294967296")]
    [InlineData("S-1-5-21-01-2-3-1001")]
    [InlineData("s-1-5-21-1-2-3-1001")]
    [InlineData(" S-1-5-21-1-2-3-1001")]
    [InlineData("S-1-5-21-1-2-3-1001\n")]
    public void InvalidSidConfigurationIsRejected(string? sid) => Assert.False(SourcesManagementAuthorization.IsValidUserSid(sid));

    [Fact]
    public void ValidSidSyntaxIsAccepted() => Assert.True(SourcesManagementAuthorization.IsValidUserSid(ExampleSid));

    [Fact]
    public async Task AnonymousAndForgedClaimsCannotAuthorize()
    {
        var settings = new MutableSettings(new() { AllowedUserSids = [ExampleSid] });
        Assert.False(await Authorized(new ClaimsPrincipal(new ClaimsIdentity()), settings));
        var forged = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, ExampleSid), new Claim(ClaimTypes.PrimarySid, ExampleSid),
            new Claim(ClaimTypes.Name, "SERVER\\administrator")], "Windows"));
        Assert.False(await Authorized(forged, settings));
    }

    [Fact]
    public async Task GenuineWindowsIdentityRequiresExactAllowlistAndRechecksConfiguration()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var sid = identity.User!.Value;
        Assert.True(SourcesManagementAuthorization.IsValidUserSid(sid));
        var settings = new MutableSettings(new() { AllowedUserSids = [sid] });
        Assert.True(await Authorized(principal, settings));
        settings.Value = new() { AllowedUserSids = [] };
        Assert.False(await Authorized(principal, settings));
        settings.Value = new() { AllowedUserSids = [sid, "malformed"] };
        Assert.False(await Authorized(principal, settings));
        settings.Value = new() { AllowedUserSids = [sid.EndsWith("-1001") ? "S-1-5-21-9-8-7-1002" : ExampleSid] };
        Assert.False(await Authorized(principal, settings));
        settings.Value = new() { AllowedUserSids = null! };
        Assert.False(await Authorized(principal, settings));
        settings.Throw = true;
        Assert.False(await Authorized(principal, settings));
    }

    [Theory]
    [InlineData("Production", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Development", true, false)]
    [InlineData("Production", true, true)]
    public async Task AnonymousEndpointChallengesOnlyNativeSchemeAndNeverTrustsRequestIdentity(string environment, bool nativeScheme, bool injectPrincipal)
    {
        await using var app = CreateHost(environment, nativeScheme, null, [], injectPrincipal);
        await app.StartAsync();
        using var client = Client(app);
        client.DefaultRequestHeaders.Add("X-Windows-User", "SERVER\\administrator");
        client.DefaultRequestHeaders.Add("X-User-Sid", ExampleSid);
        using var response = await client.PostAsync("/source-test?sid=" + ExampleSid, new FormUrlEncodedContent(new Dictionary<string,string> { ["sid"] = ExampleSid }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(nativeScheme, response.Headers.WwwAuthenticate.Any(h => h.Scheme == "Negotiate"));
        Assert.Equal("public", await client.GetStringAsync("/Servers"));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task AuthenticatedClaimsForgeryIsForbidden(string environment)
    {
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, ExampleSid)], "Windows"));
        await using var app = CreateHost(environment, true, forged, [ExampleSid]);
        await app.StartAsync();
        using var client = Client(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/source-test")).StatusCode);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("Production", "allowed", HttpStatusCode.OK)]
    [InlineData("Development", "allowed", HttpStatusCode.OK)]
    [InlineData("Production", "empty", HttpStatusCode.Forbidden)]
    [InlineData("Production", "denied", HttpStatusCode.Forbidden)]
    [InlineData("Production", "malformed", HttpStatusCode.Forbidden)]
    public async Task EndpointEnforcesRealWindowsSid(string environment, string mode, HttpStatusCode expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        string[] allowed = mode switch {
            "allowed" => [sid], "empty" => [], "malformed" => [sid, "bad"],
            _ => [sid.EndsWith("-1001") ? "S-1-5-21-9-8-7-1002" : ExampleSid]
        };
        await using var app = CreateHost(environment, true, new WindowsPrincipal(identity), allowed);
        await app.StartAsync();
        using var client = Client(app);
        Assert.Equal(expected, (await client.GetAsync("/source-test")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task PolicyIsOptInAndDoesNotAlterDefaultAuthenticationOrAuthorization()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddAuthentication("ExistingScheme");
        services.AddSourcesManagementAuthorization();
        using var provider = services.BuildServiceProvider();
        Assert.Equal("ExistingScheme", provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value.DefaultScheme);
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        Assert.Null(await policies.GetFallbackPolicyAsync());
        var policy = await policies.GetPolicyAsync(SourcesManagementAuthorization.Policy);
        Assert.Equal([SourcesManagementAuthorization.Scheme], policy!.AuthenticationSchemes);
    }

    private static async Task<bool> Authorized(ClaimsPrincipal principal, MutableSettings settings)
    {
        var requirement = new SourcesManagementRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, null);
        await new SourcesManagementHandler(settings).HandleAsync(context);
        return context.HasSucceeded;
    }

    private static WebApplication CreateHost(string environment, bool native, ClaimsPrincipal? principal, string[] allowed, bool injectPrincipal = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSourcesManagementAuthorization();
        builder.Services.Configure<SourcesManagementOptions>(options => options.AllowedUserSids = allowed);
        if (native) builder.Services.AddAuthentication().AddScheme<NativeOptions, NativeHandler>(
            environment == "Development" ? NegotiateDefaults.AuthenticationScheme : IISDefaults.AuthenticationScheme,
            options => options.Principal = principal);
        var app = builder.Build();
        app.UseRouting();
        if (injectPrincipal) app.Use((context, next) => {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, ExampleSid)], "forged"));
            return next(context);
        });
        app.UseAuthorization();
        app.MapMethods("/source-test", ["GET", "POST"], () => "protected").RequireAuthorization(SourcesManagementAuthorization.Policy);
        app.MapGet("/Servers", () => "public");
        return app;
    }

    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler { UseDefaultCredentials = false, UseProxy = false }) {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
    };

    public sealed class NativeOptions : AuthenticationSchemeOptions { public ClaimsPrincipal? Principal { get; set; } }
    public sealed class NativeHandler(IOptionsMonitor<NativeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<NativeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Options.Principal is { } principal
            ? AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)) : AuthenticateResult.NoResult());
        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = 401; Response.Headers.WWWAuthenticate = "Negotiate";
            return Task.CompletedTask;
        }
    }

    private sealed class MutableSettings(SourcesManagementOptions value) : IOptionsMonitor<SourcesManagementOptions>
    {
        public SourcesManagementOptions Value = value;
        public bool Throw;
        public SourcesManagementOptions CurrentValue => Throw ? throw new OptionsValidationException("", typeof(SourcesManagementOptions), ["Invalid settings"]) : Value;
        public SourcesManagementOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SourcesManagementOptions,string?> listener) => null;
    }
}
