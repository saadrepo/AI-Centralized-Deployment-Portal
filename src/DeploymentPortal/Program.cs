using DeploymentPortal.Data;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
// Registry defaults are isolated from the existing portal configuration.
var registryConfiguration = new ConfigurationBuilder().SetBasePath(builder.Environment.ContentRootPath)
    .AddJsonFile("sites-registry.json", optional: true, reloadOnChange: true)
    .AddJsonFile("tfs-cd.json", optional: true, reloadOnChange: true)
    .AddConfiguration(builder.Configuration).Build();
builder.Services.AddOptions<SiteRegistryOptions>().Bind(registryConfiguration.GetSection("SitesRegistry"));
builder.Services.AddScoped<ISiteRegistry, ConfigurationSiteRegistry>();
builder.Services.AddScoped<IRegistrySourceProvider, PreStagedSourceProvider>();
builder.Services.AddScoped<SiteRegistryDraftService>();
builder.Services.AddScoped<IAssistantCommandParser, AssistantCommandParser>();
builder.Services.AddScoped<AssistantDeploymentResolver>();
builder.Services.AddScoped<AssistantQueries>();
builder.Services.AddScoped<AssistantArtifactResolver>();
builder.Services.AddScoped<ICdDeploymentFlow, CdDeploymentFlow>();
builder.Services.AddSingleton<IPrivateCdStagingRoot, WindowsPrivateCdStagingRoot>();
builder.Services.AddOptions<TfsCdOptions>().Bind(registryConfiguration.GetSection("TfsCd"));
builder.Services.AddOptions<PipelineCiOptions>().BindConfiguration("Pipeline:Ci");

// Add services to the container.
builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddLocalWindowsAuthentication(builder.Environment);
builder.Services.AddSourcesManagementAuthorization();
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddOptions<AutomationOptions>().BindConfiguration("Automation")
    .Validate(x => x.ConnectionTimeoutSeconds is >= 5 and <= 120 && x.DeploymentTimeoutSeconds is >= 60 and <= 3600, "Invalid automation timeouts.").ValidateOnStart();
builder.Services.AddPortalDataProtection(builder.Environment);
if (builder.Environment.IsDevelopment())
    builder.Services.AddScoped<ICredentialProvider, DevelopmentCredentialProvider>();
else
    builder.Services.AddScoped<ICredentialProvider, ProductionCredentialProvider>();
builder.Services.AddScoped<IPowerShellService, PowerShellService>();
builder.Services.AddScoped<IWinRmService, WinRmService>();
builder.Services.AddScoped<IServerService, ServerService>();
builder.Services.AddScoped<ISourceCredentialProvider, SourceCredentialProvider>();
builder.Services.AddScoped<ISourceCredentialMetadata, SourceCredentialProvider>();
builder.Services.AddScoped<ISourceApiReader, SourceCredentialProvider>();
builder.Services.AddScoped<ICloudBuildExplorer, CloudBuildExplorer>();
// Default-off cloud execution; separate live authorization and explicit mappings are required.
builder.Services.AddOptions<CloudExecutionOptions>().BindConfiguration("CloudExecution");
builder.Services.AddScoped<ICloudProviderTransport, SourceCredentialProvider>();
builder.Services.AddScoped<ICloudProvider, CloudExecutionProvider>();
builder.Services.AddScoped<ICloudPackageStager, CloudPackageStager>();
builder.Services.AddScoped<CloudExecutionFlow>();
builder.Services.AddScoped<ICloudDeploymentGuard>(s => s.GetRequiredService<CloudExecutionFlow>());

builder.Services.AddSingleton<IGitHubAccountDetector>(_ => new GitHubAccountDetector(
    new HttpClient(SourceCredentialProvider.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) }));
builder.Services.AddScoped<IInfrastructureSourceProbe, InfrastructureSourceProbe>();
builder.Services.AddScoped<IInfrastructureSourceService, InfrastructureSourceService>();
builder.Services.AddScoped<IDeploymentService, DeploymentService>();
builder.Services.AddScoped<SiteDeletionService>();
builder.Services.AddScoped<IDockerContainerService, DockerContainerService>();
builder.Services.AddScoped<IDockerCommandRunner, DockerCommandRunner>();
builder.Services.AddScoped<IDockerFleetService, DockerFleetService>();
builder.Services.AddSingleton<TfsBuildConfirmationStore>();
builder.Services.AddSingleton<TfsReleaseConfirmationStore>();
builder.Services.AddHttpClient<ITfsService, TfsService>((services, client) =>
{
    var baseUrl = services.GetRequiredService<IConfiguration>()["Tfs:BaseUrl"];
    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        throw new InvalidOperationException("TFS BaseUrl must be an absolute HTTPS URL without credentials.");
    client.BaseAddress = new Uri(baseUrl!.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseDefaultCredentials = true,
    AllowAutoRedirect = false
}).AddTypedClient<ITfsReleaseService, TfsReleaseService>();
// Isolated, read-only Git client; HTTP is limited to the temporary internal PoC endpoint.
builder.Services.AddTfsGitClient();
builder.Services.AddHostedService<DeploymentWorker>();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue<int>("Automation:DeploymentTimeoutSeconds", 900) + 120));

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    var effectiveCd = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TfsCdOptions>>().Value;
    app.Logger.LogInformation("Local Pipeline CD switches: Enabled={Enabled}, EnabledSource={EnabledSource}, ExecutionEnabled={ExecutionEnabled}, ExecutionSource={ExecutionSource}. Windows identity and TFS approval checks remain required.",
        effectiveCd.Enabled, PipelineConfigurationDiagnostics.Source(registryConfiguration, "TfsCd:Enabled"),
        effectiveCd.ExecutionEnabled, PipelineConfigurationDiagnostics.Source(registryConfiguration, "TfsCd:ExecutionEnabled"));
}

// Configure the HTTP request pipeline.
app.UseExceptionHandler("/Home/Error");
if (!app.Environment.IsDevelopment())
{
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});
app.UseRouting();
app.UseMiddleware<CloudDenialAuditMiddleware>();

app.UseLocalWindowsAuthentication(app.Environment);
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
