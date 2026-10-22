using System.ComponentModel.DataAnnotations;
using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace DeploymentPortal.Services;

public sealed record ConfiguredSource(string Name, string Destination);
public interface IInfrastructureSourceProbe
{
    Task<bool> TestAsync(InfrastructureSource source, CancellationToken cancellationToken);
}
public interface IInfrastructureSourceService
{
    IReadOnlyList<ConfiguredSource> Configured();
    IReadOnlyList<SourceCredentialMetadata> Credentials();
    Task<SourcesPage> ListAsync();
    Task<InfrastructureSource?> FindAsync(int id);
    Task SaveAsync(SourceForm form);
    Task<bool> SetEnabledAsync(int id, bool enabled);
    Task<bool> DeleteAsync(int id);
    Task<bool> TestAsync(int id, CancellationToken cancellationToken);
}

public sealed class InfrastructureSourceService(ApplicationDbContext db, IConfiguration configuration,
    ISourceCredentialMetadata metadata, IInfrastructureSourceProbe probe) : IInfrastructureSourceService
{
    public IReadOnlyList<ConfiguredSource> Configured()
    {
        var sources = new List<ConfiguredSource>();
        foreach (var key in new[] { "Tfs", "TfsGit" }) {
            var url = configuration[key + ":BaseUrl"];
            if (url is null || !TfsGitClientConfiguration.IsAllowedBaseUrl(url) || url.Contains('%') || url.Contains('\\') || url.Split('/').Any(p => p is "." or "..")) continue;
            var canonical = TfsGitClientConfiguration.GetBaseAddress(url).AbsoluteUri;
            if (!sources.Any(s => s.Destination == canonical)) sources.Add(new(key + " (configured)", canonical));
        }
        return sources;
    }
    public IReadOnlyList<SourceCredentialMetadata> Credentials() => metadata.List();
    public async Task<SourcesPage> ListAsync()
    {
        var sources = await db.InfrastructureSources.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync();
        return new(sources, Configured().Where(c => !sources.Any(s => s.Provider == InfrastructureSourceProvider.Tfs2018 && s.Destination == c.Destination)).ToList());
    }
    public Task<InfrastructureSource?> FindAsync(int id) => db.InfrastructureSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);

    private string Validate(SourceForm form)
    {
        Validator.ValidateObject(form, new(form), true);
        if (string.IsNullOrWhiteSpace(form.DisplayName) || form.DisplayName.Any(char.IsControl)) throw new ArgumentException("Enter a display name without control characters.");
        if (!Enum.IsDefined(form.Provider)) throw new ArgumentException("Select a supported provider.");
        if (form.Provider == InfrastructureSourceProvider.Tfs2018) {
            if (!TfsGitClientConfiguration.IsAllowedBaseUrl(form.Destination) || form.Destination.Contains('%') || form.Destination.Contains('\\') || form.Destination.Split('/').Any(p => p is "." or "..")) throw new ArgumentException("Select an approved configured TFS collection URL.");
            var destination = TfsGitClientConfiguration.GetBaseAddress(form.Destination).AbsoluteUri;
            if (!Configured().Any(c => c.Destination == destination) || form.CredentialReference is not ("" or null)) throw new ArgumentException("TFS requires a configured collection and its existing protected Windows credentials; leave credential reference empty.");
            return destination;
        }
        try {
            var provider = TokenProvider(form.Provider);
            var destination = SourceCredentialProvider.CanonicalDestination(provider, form.Destination);
            if (!metadata.List().Any(c => c.Reference == form.CredentialReference && c.Provider == provider && c.Destination == destination))
                throw new ArgumentException("Select a provisioned credential reference matching this provider and organization URL.");
            return destination;
        }
        catch (InvalidOperationException) { throw new ArgumentException("The provider URL or secure credential store is unavailable. Verify the organization URL and ask a host administrator to provision its credential."); }
    }
    public static SourceTokenProvider TokenProvider(InfrastructureSourceProvider provider) => provider switch {
        InfrastructureSourceProvider.AzureDevOps => SourceTokenProvider.AzureDevOps,
        InfrastructureSourceProvider.GitHub => SourceTokenProvider.GitHub,
        _ => throw new ArgumentException("Select a token provider.")
    };
    public async Task SaveAsync(SourceForm form)
    {
        var destination = Validate(form);
        if (await db.InfrastructureSources.AnyAsync(s => s.Id != form.Id && s.Provider == form.Provider && s.Destination == destination)) throw new ArgumentException("This provider and organization/collection are already registered.");
        var source = form.Id == 0 ? new InfrastructureSource() : await db.InfrastructureSources.FindAsync(form.Id) ?? throw new ArgumentException("Source no longer exists.");
        if (form.Id == 0) db.InfrastructureSources.Add(source);
        source.DisplayName = form.DisplayName.Trim(); source.Provider = form.Provider; source.Destination = destination;
        source.CredentialReference = form.CredentialReference ?? ""; source.Enabled = form.Enabled;
        source.ConnectionStatus = "Unknown"; source.ConnectionMessage = "Not tested."; source.LastCheckedAt = null;
        await db.SaveChangesAsync();
    }
    public async Task<bool> SetEnabledAsync(int id, bool enabled)
    {
        var source = await db.InfrastructureSources.FindAsync(id);
        if (source is null) return false;
        source.Enabled = enabled; source.ConnectionStatus = "Unknown"; source.ConnectionMessage = "Not tested."; source.LastCheckedAt = null;
        await db.SaveChangesAsync(); return true;
    }
    public async Task<bool> DeleteAsync(int id)
    {
        var source = await db.InfrastructureSources.FindAsync(id);
        if (source is null) return false;
        db.InfrastructureSources.Remove(source); await db.SaveChangesAsync(); return true;
    }
    public async Task<bool> TestAsync(int id, CancellationToken cancellationToken)
    {
        var source = await db.InfrastructureSources.FindAsync(id);
        if (source is null) return false;
        if (!source.Enabled) throw new ArgumentException("Enable the source before testing its connection.");
        var success = false;
        try {
            Validate(SourceForm.From(source)); // Recheck configuration/credential metadata even for existing database records.
            success = await probe.TestAsync(source, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ArgumentException or ValidationException or InvalidOperationException or HttpRequestException or OperationCanceledException or IOException) { }
        source.ConnectionStatus = success ? "Connected" : "Failed";
        source.ConnectionMessage = success ? "Read-only API request succeeded." : "Connection could not be verified. Check provider access, approved URL and host-provisioned credentials.";
        source.LastCheckedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken); return true;
    }
}

public sealed class InfrastructureSourceProbe(ISourceCredentialProvider tokens, ITfsWindowsCredentialProvider windows) : IInfrastructureSourceProbe
{
    public async Task<bool> TestAsync(InfrastructureSource source, CancellationToken cancellationToken)
    {
        if (source.Provider != InfrastructureSourceProvider.Tfs2018) {
            var provider = InfrastructureSourceService.TokenProvider(source.Provider);
            var destination = SourceCredentialProvider.CanonicalDestination(provider, source.Destination);
            var url = provider == SourceTokenProvider.AzureDevOps ? destination + "_apis/projects?$top=1&api-version=7.1" : destination.TrimEnd('/');
            using var response = await tokens.GetAsync(source.CredentialReference, provider, destination, new Uri(url), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        var collection = TfsGitClientConfiguration.GetBaseAddress(source.Destination);
        using var handler = TfsGitClientConfiguration.CreatePrimaryHandler(collection.AbsoluteUri, () => windows.GetCredential(collection));
        using var guard = new TfsGitRequestGuard(collection) { InnerHandler = handler };
        using var client = new HttpClient(guard) { BaseAddress = collection, Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "_apis/projects?$top=1&api-version=4.1");
        request.Headers.Accept.ParseAdd("application/json");
        using var responseTfs = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return responseTfs.IsSuccessStatusCode && responseTfs.Content.Headers.ContentType?.MediaType == "application/json";
    }
}
