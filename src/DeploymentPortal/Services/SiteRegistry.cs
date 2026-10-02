using System.ComponentModel.DataAnnotations;
using DeploymentPortal.Models;
using DeploymentPortal.ViewModels;
using Microsoft.Extensions.Options;

namespace DeploymentPortal.Services;

public sealed class SiteRegistryOptions
{
    public List<RegistryApplication> Applications { get; set; } = [];
}
public sealed class RegistryApplication
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SourceProvider { get; set; } = "PreStaged";
    public string SourceReference { get; set; } = "";
    public List<RegistryDestination> Destinations { get; set; } = [];
}
public sealed class RegistryDestination
{
    public int ServerId { get; set; }
    public string SiteName { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string ApplicationName { get; set; } = "/";
    public string? ApplicationPool { get; set; }
    public int SitePort { get; set; } = 8080;
    public string? SiteHostName { get; set; }
}
public interface ISiteRegistry
{
    IReadOnlyList<RegistryApplication> List();
    RegistryApplication? Find(string key);
}
public sealed class ConfigurationSiteRegistry(IOptionsSnapshot<SiteRegistryOptions> options) : ISiteRegistry
{
    public IReadOnlyList<RegistryApplication> List() => options.Value.Applications;
    public RegistryApplication? Find(string key)
    {
        var matches = List().Where(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1) throw new ArgumentException("Registry application keys must be unique (case-insensitive).");
        return matches.SingleOrDefault();
    }
}
// Providers resolve a source on the selected server; they do not deploy or approve requests.
public interface IRegistrySourceProvider
{
    string Name { get; }
    string Resolve(RegistryApplication application, Server server);
}
public sealed class PreStagedSourceProvider : IRegistrySourceProvider
{
    public string Name => "PreStaged";
    public string Resolve(RegistryApplication application, Server server) => DeploymentPaths.Normalize(application.SourceReference);
}
public sealed record RegistryMapping(RegistryApplication Application, RegistryDestination? Destination,
    Server? Server, DeploymentForm? Form, string Readiness);

public sealed class SiteRegistryDraftService(ISiteRegistry registry, IServerService servers,
    IEnumerable<IRegistrySourceProvider> providers, IOptions<AutomationOptions> automation)
{
    public async Task<RegistryMapping> PrepareAsync(string key, int serverId)
    {
        var app = registry.Find(key) ?? throw new ArgumentException("Registry application was not found.");
        if (string.IsNullOrWhiteSpace(app.Key) || string.IsNullOrWhiteSpace(app.DisplayName))
            throw new ArgumentException("Application key and display name are required.");
        var destinations = app.Destinations.Where(x => x.ServerId == serverId).ToList();
        if (destinations.Count != 1) throw new ArgumentException("Configure exactly one destination per application and server.");
        var destination = destinations[0];
        var server = serverId > 0 ? await servers.FindAsync(serverId) : null;
        if (server is null) throw new ArgumentException("Configure an existing registered ServerId.");
        var provider = providers.SingleOrDefault(x => string.Equals(x.Name, app.SourceProvider, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Unsupported source provider. Use PreStaged.");
        var form = new DeploymentForm
        {
            ServerId = server.Id, Environment = server.Environment, SiteName = destination.SiteName,
            ApplicationName = destination.ApplicationName, AppPoolName = destination.ApplicationPool,
            SourcePath = provider.Resolve(app, server), TargetPath = destination.TargetPath,
            SitePort = destination.SitePort, SiteHostName = destination.SiteHostName
        };
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(form, new ValidationContext(form), errors, true))
            throw new ArgumentException(string.Join(" ", errors.Select(x => x.ErrorMessage)));
        DeploymentPaths.Validate(form.SourcePath, form.TargetPath, automation.Value);
        return new(app, destination, server, form, "Ready for review (remote files and IIS not checked)");
    }
    public async Task<IReadOnlyList<RegistryMapping>> ListAsync(string? key = null)
    {
        var rows = new List<RegistryMapping>();
        foreach (var app in registry.List().Where(x => key is null || string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            if (app.Destinations.Count == 0)
                rows.Add(new(app, null, null, null, "Not configured: add a server destination."));
            foreach (var destination in app.Destinations)
            {
                try { rows.Add(await PrepareAsync(app.Key, destination.ServerId)); }
                catch (ArgumentException ex)
                {
                    var server = destination.ServerId > 0 ? await servers.FindAsync(destination.ServerId) : null;
                    rows.Add(new(app, destination, server, null, "Not ready: " + ex.Message));
                }
            }
        }
        return rows;
    }
}
