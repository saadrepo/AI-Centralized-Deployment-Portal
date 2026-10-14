using System.Text.Json;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

public sealed record ArtifactExplorerPage(TfsArtifactListing? Listing, string Folder, IReadOnlyList<TfsArtifactEntry> Entries, string? Error)
{
    public string ApplicationKey { get; init; } = "Portal";
    public IReadOnlyList<RegistryApplication> Applications { get; init; } = [];
}

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TfsArtifactsController(ITfsArtifactExplorer explorer, ILogger<TfsArtifactsController> logger, Microsoft.Extensions.Options.IOptions<TfsCdOptions>? options = null, ISiteRegistry? registry = null) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(int id, string? folder, CancellationToken cancellationToken, string applicationKey = "Portal")
    {
        if (!ModelState.IsValid || id <= 0) return BadRequest("Select a valid build ID.");
        var app = registry?.Find(applicationKey);
        if (registry is not null && (app is null || !TfsApplicationScope.IsConfigured(app))) return NotFound();
        if (registry is null && applicationKey != "Portal") return NotFound();
        ArtifactExplorerPage Page(TfsArtifactListing? listing, string path, IReadOnlyList<TfsArtifactEntry> entries, string? error) => new(listing, path, entries, error) { ApplicationKey = app?.Key ?? "Portal", Applications = registry?.List().Where(TfsApplicationScope.IsConfigured).ToList() ?? [] };
        if (options?.Value.ExplorerEnabled != true) return View(Page(null, "", [], "Artifact Explorer is disabled pending live TFS 2018 verification. An administrator can enable TfsCd:ExplorerEnabled for read-only testing."));
        try
        {
            var listing = app is null ? await explorer.InspectAsync(id, cancellationToken) : await explorer.InspectAsync(id, app, cancellationToken);
            folder ??= "";
            // Folder navigation is restricted to the validated artifact tree, never sent to TFS.
            var folders = listing.Entries.SelectMany(x => ParentFolders(x.Path)).Concat(listing.Entries.Where(x => x.IsFolder).Select(x => x.Path)).ToHashSet(StringComparer.Ordinal);
            if (folder != "" && !folders.Contains(folder)) return NotFound();
            var prefix = folder == "" ? "" : folder + "/";
            var children = listing.Entries.Where(x => x.Path.StartsWith(prefix, StringComparison.Ordinal)).Select(x =>
            {
                var relative = x.Path[prefix.Length..];
                var separator = relative.IndexOf('/');
                return separator < 0 ? x : new TfsArtifactEntry(prefix + relative[..separator], true, null);
            }).Where(x => x.Path != folder).DistinctBy(x => x.Path).OrderByDescending(x => x.IsFolder).ThenBy(x => x.Path, StringComparer.Ordinal).ToList();
            return View(Page(listing, folder, children, null));
        }
        catch (ArgumentException ex) { Response.StatusCode = 400; return View(Page(null, "", [], ex.Message)); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning("TFS artifact inspection failed ({FailureType}).", ex.GetType().Name);
            Response.StatusCode = 502;
            return View(Page(null, "", [], "Unable to inspect this artifact safely. Check TFS access, artifact type and metadata limits."));
        }
    }

    private static IEnumerable<string> ParentFolders(string path)
    {
        for (var i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1)) yield return path[..i];
    }
}
