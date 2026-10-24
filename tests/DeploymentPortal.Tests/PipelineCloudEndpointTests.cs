using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using DeploymentPortal.Controllers;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public class PipelineCloudEndpointTests
{
    private sealed class Registry : ISiteRegistry
    {
        public IReadOnlyList<RegistryApplication> List() => [new() { Key = "Portal", DisplayName = "Portal" }];
        public RegistryApplication? Find(string key) => List().SingleOrDefault(a => a.Key == key);
    }
    private sealed class Explorer : ICloudBuildExplorer
    {
        public int Calls; public bool Fail;
        public Task<CloudExplorerPage> BrowseAsync(CloudExplorerQuery query, CancellationToken token)
        {
            Calls++;
            if (Fail) throw new IOException("PAT=synthetic-secret https://private-host/");
            return Task.FromResult(new CloudExplorerPage { Items = [new("1", "Source")] });
        }
    }
    private sealed class AuditCapture : ILoggerProvider, ILogger
    {
        public List<string> Events = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            if (id.Id == 4103) Events.Add(formatter(state, exception));
        }
    }
    private static WebApplication Host(Explorer explorer, ClaimsPrincipal? principal = null, AuditCapture? audit = null)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "src", "DeploymentPortal", "Views"))) repo = repo.Parent;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(PipelineController).Assembly.GetName().Name, EnvironmentName = Environments.Production,
            ContentRootPath = Path.Combine(repo!.FullName, "src", "DeploymentPortal")
        });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (audit is not null) builder.Logging.AddProvider(audit);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(PipelineController).Assembly);
        builder.Services.AddSourcesManagementAuthorization();
        builder.Services.Configure<SourcesManagementOptions>(o => o.AllowedUserSids = OperatingSystem.IsWindows() && principal?.Identity is WindowsIdentity identity ? [identity.User!.Value] : ["S-1-5-21-1-2-3-1001"]);
        builder.Services.AddAuthentication().AddScheme<SourcesManagementAuthorizationTests.NativeOptions, SourcesManagementAuthorizationTests.NativeHandler>(IISDefaults.AuthenticationScheme, o => o.Principal = principal);
        var registry = new Registry();
        builder.Services.AddSingleton<ISiteRegistry>(registry); builder.Services.AddSingleton<ICloudBuildExplorer>(explorer);
        // Index does not need or call any TFS transport. No external HTTP handler is installed.
        builder.Services.AddSingleton(new PipelineService(new HttpClient(), registry, Options.Create(new TfsCdOptions()), null!,
            NullLogger<PipelineService>.Instance, null!, Options.Create(new PipelineCiOptions())));
        var app = builder.Build(); app.UseRouting(); app.UseMiddleware<CloudDenialAuditMiddleware>(); app.UseAuthorization();
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}"); return app;
    }
    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
    };
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DenialAuditIncludesRealAuthorizationAndCsrfMiddlewareShortCircuits(bool authenticated)
    {
        if (!OperatingSystem.IsWindows()) return;
        var capture = new AuditCapture(); var explorer = new Explorer();
        await using var app = Host(explorer, authenticated ? new WindowsPrincipal(WindowsIdentity.GetCurrent()) : null, capture);
        await app.StartAsync(); using var client = Client(app);
        var response = await client.PostAsync("/Pipeline/Cloud/Approve", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "synthetic-secret" }));
        Assert.Equal(authenticated ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized, response.StatusCode);
        var entry = Assert.Single(capture.Events); Assert.DoesNotContain("synthetic-secret", entry); Assert.Equal(0, explorer.Calls);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("/Pipeline?provider=GitHub")]
    [InlineData("/Pipeline?provider=AzureDevOps")]
    [InlineData("/Pipeline/Cloud/Applications")]
    [InlineData("/Pipeline/Cloud/Browse?provider=GitHub&operation=sources&applicationKey=Portal")]
    public async Task AnonymousAndForgedIdentityCannotReadCloudData(string path)
    {
        foreach (var principal in new ClaimsPrincipal?[] { null, new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001")], "Windows")) }) {
            var explorer = new Explorer(); await using var app = Host(explorer, principal); await app.StartAsync(); using var client = Client(app);
            var response = await client.GetAsync(path);
            Assert.Equal(principal is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, explorer.Calls); await app.StopAsync();
        }
    }
    [Theory]
    [InlineData("GitHub")]
    [InlineData("AzureDevOps")]
    public async Task CloudPageHasExactlyThreeActionsAndNoTfsExecutionScript(string provider)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent(); var explorer = new Explorer();
        await using var app = Host(explorer, new WindowsPrincipal(identity)); await app.StartAsync(); using var client = Client(app);
        var html = await client.GetStringAsync("/Pipeline?provider=" + provider);
        var actions = System.Text.RegularExpressions.Regex.Match(html, "<div class=\"pipeline-actions\">(.*?)</div>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(actions, "<button\\b").Count);
        Assert.Contains("Run CI Build", html); Assert.Contains("Approve &amp; Deploy", html); Assert.Contains("Refresh Status", html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(actions, "<button[^>]*disabled").Count);
        Assert.Equal(provider == "AzureDevOps", html.Contains("id=\"cp-project\""));
        Assert.DoesNotContain("js/pipeline.js", html); Assert.DoesNotContain("type=\"password\"", html);
        Assert.Contains("role=\"combobox\"", html);
        Assert.Contains("aria-expanded=\"false\"", html);
        Assert.Contains("role=\"listbox\"", html);
        Assert.Contains($"id=\"provider-option-{provider}\"", html);
        Assert.Matches($"id=\"provider-option-{provider}\"[^>]*aria-selected=\"true\"", html);
        Assert.Matches("pipeline-cloud\\.js\\?v=[^\"]+", html);
        Assert.Matches("pipeline-provider\\.js\\?v=[^\"]+", html);
        Assert.Matches("icons/providers/github\\.svg\\?v=[^\"]+", html);
        Assert.Matches("icons/providers/azuredevops\\.svg\\?v=[^\"]+", html);
        Assert.Matches("icons/providers/tfs-server\\.svg\\?v=[^\"]+", html);
        var webRoot = Path.Combine(app.Services.GetRequiredService<IHostEnvironment>().ContentRootPath, "wwwroot");
        foreach (var asset in new[] { "js/pipeline-cloud.js", "js/pipeline-provider.js", "css/pipeline.css" }) {
            var hash = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(webRoot, asset))));
            Assert.Contains(asset + "?v=" + hash, html);
        }
        Assert.Equal(0, explorer.Calls); await app.StopAsync();
    }
    [Theory]
    [InlineData("provider=Tfs2018&operation=sources&applicationKey=Portal")]
    [InlineData("provider=GitHub&operation=builds&applicationKey=Portal")]
    [InlineData("provider=AzureDevOps&operation=artifacts&definitionId=1&applicationKey=Unknown")]
    [InlineData("provider=GitHub&operation=queue&applicationKey=Portal")]
    [InlineData("provider=GitHub&operation=status&definitionId=invalid&applicationKey=Portal")]
    public async Task InvalidCloudSelectionsAreRejectedBeforeExplorerAccess(string query)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent(); var explorer = new Explorer();
        await using var app = Host(explorer, new WindowsPrincipal(identity)); await app.StartAsync(); using var client = Client(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/Pipeline/Cloud/Browse?" + query)).StatusCode);
        Assert.Equal(0, explorer.Calls); await app.StopAsync();
    }
    [Fact]
    public async Task AuthorizedReadUsesSharedExplorerNoStoreAndSafeErrors()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent(); var explorer = new Explorer();
        await using var app = Host(explorer, new WindowsPrincipal(identity)); await app.StartAsync(); using var client = Client(app);
        const string url = "/Pipeline/Cloud/Browse?provider=GitHub&operation=sources&applicationKey=Portal";
        using var success = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode); Assert.True(success.Headers.CacheControl!.NoStore);
        explorer.Fail = true; var failure = await client.GetAsync(url); var body = await failure.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode); Assert.DoesNotContain("synthetic-secret", body); Assert.DoesNotContain("private-host", body);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync(url, new StringContent(""))).StatusCode);
        Assert.Equal(2, explorer.Calls); await app.StopAsync();
    }

    [Fact]
    public async Task DefaultTfsViewKeepsOriginalAuthenticationWithoutSourcesPolicy()
    {
        var controller = new PipelineController(null!, NullLogger<PipelineController>.Instance) {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };
        var anonymous = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(await controller.Index());
        Assert.Equal(401, anonymous.StatusCode);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "original-tfs-user")], "ExistingHost"));
        Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(await controller.Index());
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestResult>(await controller.Index("Unknown"));
    }
    [Theory] [InlineData("Queue")] [InlineData("Prepare")] [InlineData("Approve")]
    public async Task CloudMutationsRequireAntiforgeryEvenForApprovedWindowsUsers(string action)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent(); var explorer = new Explorer();
        await using var app = Host(explorer, new WindowsPrincipal(identity)); await app.StartAsync(); using var client = Client(app);
        var response = await client.PostAsync("/Pipeline/Cloud/" + action, new FormUrlEncodedContent(new Dictionary<string, string> { ["ticket"] = "untrusted", ["token"] = "untrusted" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, explorer.Calls); await app.StopAsync();
    }
    [Fact] public async Task CloudMutationsRequireHttpsBeforeDispatch()
    {
        if (!OperatingSystem.IsWindows()) return; using var fixture = new CloudExecutionTests.Fixture();
        var controller = new PipelineCloudController(new Explorer(), new Registry(), NullLogger<PipelineCloudController>.Instance, fixture.Flow) {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };
        controller.Request.Scheme = "http";
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(await controller.Queue("untrusted", default));
        Assert.Equal(403, result.StatusCode); Assert.Equal(0, fixture.Provider.Queues);
    }
}
