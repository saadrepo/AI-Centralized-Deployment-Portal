using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeploymentPortal.ViewModels.Tfs;

namespace DeploymentPortal.Services;

public interface ITfsArtifactExplorer
{
    Task<TfsArtifactListing> InspectAsync(int buildId, CancellationToken cancellationToken = default);
}

public sealed record TfsArtifactEntry(string Path, bool IsFolder, long? Size);
public sealed record TfsArtifactListing(TfsGitCiBuildViewModel Build, string Artifact, IReadOnlyList<TfsArtifactEntry> Entries)
{
    [System.Text.Json.Serialization.JsonIgnore] public long ContainerId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public string ContainerRoot { get; init; } = "";
}

// Metadata only. No URL, container identity or download ticket crosses the service boundary.
public sealed class TfsArtifactExplorer(HttpClient client, ITfsGitBuildService builds, IConfiguration configuration) : ITfsArtifactExplorer
{
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    public const int MaximumEntries = 10000;
    private sealed class Artifact { public string? Name { get; set; } public Resource? Resource { get; set; } }
    private sealed class Resource { public string? Type { get; set; } public string? Data { get; set; } }
    private sealed class Item { public string? Path { get; set; } public JsonElement ItemType { get; set; } public JsonElement Status { get; set; } public long? FileLength { get; set; } }
    private sealed class Page<T> { public List<T>? Value { get; set; } }

    public async Task<TfsArtifactListing> InspectAsync(int buildId, CancellationToken cancellationToken = default)
    {
        var build = await builds.GetBuildAsync(buildId) ?? throw new ArgumentException("Select a build from the configured Dev CI pipeline.");
        if (build.Status != "completed" || build.Result != "succeeded")
            throw new ArgumentException("Artifact inspection requires a completed, successful Dev build.");
        if (!Regex.IsMatch(build.SourceVersion ?? "", "^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("The build has no valid Git commit identity.");
        var project = configuration["TfsGit:Project"];
        var version = configuration["TfsGit:ApiVersion"];
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("TFS Git configuration is incomplete.");
        var artifacts = await ReadAsync<Artifact>($"{Uri.EscapeDataString(project)}/_apis/build/builds/{buildId}/artifacts?api-version={Uri.EscapeDataString(version)}", cancellationToken);
        var drops = artifacts.Where(x => x.Name == "drop").ToList();
        if (drops.Count != 1) throw new ArgumentException("This build must have exactly one drop artifact.");
        var resource = drops[0].Resource;
        if (!string.Equals(resource?.Type, "Container", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only TFS Container artifacts can be explored. No file-share access is performed.");
        var match = Regex.Match(resource!.Data ?? "", @"^#/([1-9][0-9]*)/(.+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var containerId))
            throw new InvalidOperationException("Invalid artifact container identity.");
        var root = match.Groups[2].Value;
        ValidatePath(root);
        // FileContainer is a separate preview API on TFS 2018; build APIs remain 4.1.
        var scope = build.Project?.Id is { } projectId && projectId != Guid.Empty ? "&scope=" + projectId.ToString("D") : "";
        var items = await ReadAsync<Item>($"_apis/resources/Containers/{containerId}?api-version=4.1-preview.4&itemPath={Uri.EscapeDataString(root)}&$format=json&isShallow=false&includeDownloadTickets=false{scope}", cancellationToken);
        var entries = new List<TfsArtifactEntry>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var path = item.Path ?? "";
            ValidatePath(path);
            if (path != root && !path.StartsWith(root + "/", StringComparison.Ordinal)) throw new InvalidOperationException("Artifact listing escaped its root.");
            var isFile = EnumIs(item.ItemType, "file", 2);
            var isFolder = EnumIs(item.ItemType, "folder", 1);
            if ((!isFile && !isFolder) || (item.Status.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && !EnumIs(item.Status, "created", 1))) throw new InvalidOperationException("Artifact contains an unsupported or incomplete item.");
            if (path == root) continue;
            var relative = path[(root.Length + 1)..];
            if (!paths.Add(relative) || (isFile && (item.FileLength is null or < 0))) throw new InvalidOperationException("Invalid artifact file metadata.");
            entries.Add(new(relative, isFolder, isFile ? item.FileLength : null));
        }
        foreach (var file in entries.Where(x => !x.IsFolder))
            if (entries.Any(x => x.Path.StartsWith(file.Path + "/", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Artifact contains a file/directory collision.");
        return new(build, "drop", entries.OrderBy(x => x.Path, StringComparer.Ordinal).ToList()) { ContainerId = containerId, ContainerRoot = root };
    }

    private async Task<List<T>> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidOperationException("Artifact metadata exceeds the inspection limit.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, deadline.Token)) != 0)
        {
            if (buffer.Length + read > MaximumResponseBytes) throw new InvalidOperationException("Artifact metadata exceeds the inspection limit.");
            buffer.Write(chunk, 0, read);
        }
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var document = JsonDocument.Parse(buffer.ToArray());
        var values = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.Deserialize<List<T>>(jsonOptions)
            : document.RootElement.Deserialize<Page<T>>(jsonOptions)?.Value;
        if (values is null || values.Count > MaximumEntries || values.Any(x => x is null)) throw new JsonException("Invalid artifact metadata collection.");
        return values;
    }

    private static bool EnumIs(JsonElement element, string name, int value) => element.ValueKind == JsonValueKind.String
        ? string.Equals(element.GetString(), name, StringComparison.OrdinalIgnoreCase)
        : element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) && number == value;

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1000 || path.Any(char.IsControl) || path.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|', '%']) >= 0 ||
            path.Split('/').Any(x => x.Length == 0 || x is "." or ".." || x.EndsWith('.') || x.EndsWith(' ') || Regex.IsMatch(x, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidOperationException("Artifact contains an unsafe path.");
    }
}
