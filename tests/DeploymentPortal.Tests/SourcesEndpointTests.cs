using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using DeploymentPortal.Controllers;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeploymentPortal.Tests;
public sealed class SourcesEndpointTests
{
    [Theory]
    [InlineData("GET", "/Servers/Sources")]
    [InlineData("GET", "/Sources/Create")]
    [InlineData("GET", "/Sources/Edit/1")]
    [InlineData("GET", "/Sources/Credentials")]
    [InlineData("POST", "/Sources/Create")]
    [InlineData("POST", "/Sources/Edit/1")]
    [InlineData("POST", "/Sources/SetEnabled/1")]
    [InlineData("POST", "/Sources/Delete/1")]
    [InlineData("POST", "/Sources/TestConnection/1")]
    public async Task EverySourceEndpointRejectsAnonymousBeforeServiceAccess(string method, string path)
    {
        var service = new FakeSources();
        await using var app = Host(service);
        await app.StartAsync(); using var client = Client(app);
        client.DefaultRequestHeaders.Add("X-User-Sid", InfrastructureSourceTests.Reference);
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path + "?sid=forged"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal(0, service.Calls);
        await app.StopAsync();
    }

    [Fact]
    public async Task AuthenticatedGenericIdentityCannotAccessMetadata()
    {
        var service = new FakeSources();
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001")], "Windows"));
        await using var app = Host(service, forged);
        await app.StartAsync(); using var client = Client(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Sources/Credentials")).StatusCode);
        Assert.Equal(0, service.Calls); await app.StopAsync();
    }

    [Fact]
    public async Task AuthorizedMvcReadsRenderAndMutationsRequireAntiforgery()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent(); var service = new FakeSources();
        await using var app = Host(service, new WindowsPrincipal(identity));
        await app.StartAsync(); using var client = Client(app);
        var list = await client.GetStringAsync("/Servers/Sources");
        Assert.Contains("Registered sources", list); Assert.Contains("/Servers", list);
        var form = await client.GetStringAsync("/Sources/Create");
        Assert.Contains("CredentialReference", form); Assert.Contains("__RequestVerificationToken", form);
        var metadata = await client.GetStringAsync("/Sources/Credentials");
        Assert.Contains(InfrastructureSourceTests.Reference, metadata); Assert.DoesNotContain("Ciphertext", metadata);
        var calls = service.Calls;
        using var rejected = await client.PostAsync("/Sources/Delete/1", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); Assert.Equal(calls, service.Calls);
        var token = await client.GetStringAsync("/test-token");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Sources/Delete/1") { Content = new FormUrlEncodedContent([]) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var accepted = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode); Assert.Equal(calls + 1, service.Calls);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/Sources/Create") {
            Content = new FormUrlEncodedContent(new Dictionary<string,string> {
                ["DisplayName"] = "Configured TFS", ["Provider"] = "Tfs2018",
                ["Destination"] = "http://netserver:8080/tfs/DefaultCollection/", ["CredentialReference"] = "", ["Enabled"] = "true"
            })
        };
        create.Headers.Add("X-CSRF-TOKEN", token);
        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal(calls + 2, service.Calls); // Blank Windows-credential selection must reach service validation.
        await app.StopAsync();
    }

    [Fact]
    public void ControllerPolicyCoversAllActionsAndMutationsArePostOnly()
    {
        var controller = typeof(SourcesController);
        Assert.Equal(SourcesManagementAuthorization.Policy, Assert.Single(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()).Policy);
        Assert.Empty(controller.GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
        foreach (var method in controller.GetMethods().Where(m => m.DeclaringType == controller)) {
            Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
            if (method.Name is "Delete" or "SetEnabled" or "TestConnection" || (method.Name is "Edit" or "Create" && method.GetParameters().Any(p => p.ParameterType == typeof(SourceForm))))
                Assert.Single(method.GetCustomAttributes(typeof(HttpPostAttribute), true));
        }
    }

    private static WebApplication Host(FakeSources sources, ClaimsPrincipal? principal = null)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName,"src","DeploymentPortal","Views"))) repo = repo.Parent;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            EnvironmentName = Microsoft.Extensions.Hosting.Environments.Production, ApplicationName = typeof(SourcesController).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(repo!.FullName, "src", "DeploymentPortal")
        });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute())).AddApplicationPart(typeof(SourcesController).Assembly);
        builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddSourcesManagementAuthorization();
        builder.Services.Configure<SourcesManagementOptions>(o => o.AllowedUserSids = OperatingSystem.IsWindows() && principal?.Identity is WindowsIdentity windows ? [windows.User!.Value] : ["S-1-5-21-1-2-3-1001"]);
        builder.Services.AddAuthentication().AddScheme<SourcesManagementAuthorizationTests.NativeOptions, SourcesManagementAuthorizationTests.NativeHandler>(IISDefaults.AuthenticationScheme, o => o.Principal = principal);
        builder.Services.AddSingleton<IInfrastructureSourceService>(sources);
        var app = builder.Build(); app.UseRouting(); app.UseAuthorization();
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        app.MapGet("/test-token", (HttpContext context, IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(context).RequestToken!).RequireAuthorization(SourcesManagementAuthorization.Policy);
        return app;
    }
    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseDefaultCredentials = false }) {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
    };
    private sealed class FakeSources : IInfrastructureSourceService
    {
        public int Calls;
        public IReadOnlyList<ConfiguredSource> Configured() { Calls++; return []; }
        public IReadOnlyList<SourceCredentialMetadata> Credentials() { Calls++; return [new(InfrastructureSourceTests.Reference, SourceTokenProvider.AzureDevOps, "https://dev.azure.com/example/")]; }
        public Task<SourcesPage> ListAsync() { Calls++; return Task.FromResult(new SourcesPage([], [])); }
        public Task<InfrastructureSource?> FindAsync(int id) { Calls++; return Task.FromResult<InfrastructureSource?>(null); }
        public Task SaveAsync(SourceForm form) { Calls++; return Task.CompletedTask; }
        public Task<bool> SetEnabledAsync(int id, bool enabled) { Calls++; return Task.FromResult(true); }
        public Task<bool> DeleteAsync(int id) { Calls++; return Task.FromResult(true); }
        public Task<bool> TestAsync(int id, CancellationToken cancellationToken) { Calls++; return Task.FromResult(true); }
    }
}
