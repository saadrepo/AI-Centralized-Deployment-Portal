using System.ComponentModel.DataAnnotations;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DeploymentPortal.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace DeploymentPortal.Services;

public sealed class CdPreparationException(string message) : ArgumentException(message);

public sealed class TfsCdOptions
{
    public bool ExplorerEnabled { get; set; }
    public bool Enabled { get; set; } // Default-off even if the section is absent.
    public bool ExecutionEnabled { get; set; }
    public string RemoteStagingRoot { get; set; } = "";
    public string LocalStagingRoot { get; set; } = "";
    public long MaximumPackageBytes { get; set; } = 256 * 1024 * 1024;
    public long MaximumFileBytes { get; set; } = 64 * 1024 * 1024;
    public int MaximumFiles { get; set; } = 1000;
    public int MaximumPreparations { get; set; } = 10;
}

public sealed record CdFile(string Path, long Size, string Sha256);
public sealed record CdPreparation(Guid Id, DateTimeOffset Expires, string ApplicationKey, string ServerHost,
    string Environment, string SiteName, string ApplicationPath, string TargetPath, int BuildId, string BuildNumber,
    string Branch, string Commit, string RepositoryId, string Artifact, long ContainerId, string ContainerRoot,
    string PublishRoot, string ManifestSha256, IReadOnlyList<CdFile> Files, string Status);
public sealed record CdSelection(RegistryApplication Application, Server Server, RegistryDestination Destination, TfsArtifactListing Listing);

public interface ITfsCdPreparation
{
    bool Enabled { get; }
    Task<CdSelection> SelectAsync(string applicationKey, int buildId, string serverHost, CancellationToken cancellationToken = default);
    Task<CdPreparation> StageAsync(string applicationKey, int buildId, string serverHost, CancellationToken cancellationToken = default);
    Task<CdPreparation> FindAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CdTransferFile>> TransferFilesAsync(Guid id, CancellationToken cancellationToken = default) => throw new CdPreparationException("Verified transfer files are unavailable.");
}

public interface IPrivateCdStagingRoot { void Validate(string root); }

public sealed class WindowsPrivateCdStagingRoot : IPrivateCdStagingRoot
{
    public void Validate(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Private CD staging requires the Windows portal host.");
        if (!Directory.Exists(root)) throw new CdPreparationException("An administrator must provision the approved private staging directory first.");
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Cannot identify the portal runtime account.");
        ValidateSecurity(new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access), sid);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void ValidateSecurity(DirectorySecurity security, SecurityIdentifier runtimeSid)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        if (!security.AreAccessRulesProtected) throw new CdPreparationException("Private staging must have protected ACL inheritance.");
        if (rules.Any(x => x.AccessControlType == AccessControlType.Allow && !x.IdentityReference.Equals(runtimeSid) && !x.IdentityReference.Equals(system) && !x.IdentityReference.Equals(administrators)))
            throw new CdPreparationException("Staging ACL must permit access only to the portal runtime account, SYSTEM and administrators.");
        if (!rules.Any(x => x.AccessControlType == AccessControlType.Allow && x.IdentityReference.Equals(runtimeSid) && (x.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify && (x.PropagationFlags & PropagationFlags.InheritOnly) == 0 && (x.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)))
            throw new CdPreparationException("Private staging requires an explicit inheritable Modify grant for the portal runtime account.");
    }
}

// Preparation only. Has no confirmation, queue, worker or PowerShell dependency.
public sealed class TfsCdPreparation(HttpClient client, ITfsArtifactExplorer explorer, ISiteRegistry registry,
    IServerService servers, IOptions<TfsCdOptions> options, IDataProtectionProvider protection,
    IHostEnvironment environment, IOptions<AutomationOptions> automation, IPrivateCdStagingRoot privateRoot) : ITfsCdPreparation
{
    private static readonly SemaphoreSlim stagingLock = new(1, 1);
    private readonly IDataProtector protector = protection.CreateProtector("TfsCdPreparation.v1");
    public bool Enabled => options.Value.Enabled;

    public async Task<CdSelection> SelectAsync(string applicationKey, int buildId, string serverHost, CancellationToken cancellationToken = default)
    {
        if (!Enabled) throw new CdPreparationException("TFS Git CD preparation is disabled. No artifact selection or deployment was started.");
        var app = registry.Find(applicationKey) ?? throw new CdPreparationException("Select a registered application.");
        if (app.TfsGitCiDefinitionId != 1 || app.TfsGitCiBranch != TfsGitBuildService.CiBranch || app.TfsGitArtifactName != "drop")
            throw new CdPreparationException("Application is not mapped to definition 1, Dev and drop.");
        var candidates = (await servers.ListAsync()).Where(x => string.Equals(x.HostNameOrIp, serverHost, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count != 1) throw new CdPreparationException("Select a unique registered server hostname.");
        var server = candidates[0];
        var destinations = app.Destinations.Where(x => x.Matches(server)).ToList();
        if (destinations.Count != 1) throw new CdPreparationException("Select a server with exactly one registered IIS destination for this application.");
        var destination = destinations[0];
        // Use the existing form constraints without allowing posted paths or executable parameters.
        var form = new ViewModels.DeploymentForm { ServerId = server.Id, Environment = server.Environment,
            SiteName = destination.SiteName, ApplicationName = destination.ApplicationName, AppPoolName = destination.ApplicationPool,
            SourcePath = @"C:\CdValidation\package", TargetPath = destination.TargetPath, SitePort = destination.SitePort, SiteHostName = destination.SiteHostName };
        Validator.ValidateObject(form, new(form), true);
        DeploymentPaths.Validate(form.SourcePath, form.TargetPath, automation.Value);
        var listing = await explorer.InspectAsync(buildId, cancellationToken);
        return new(app, server, destination, listing);
    }

    public async Task<CdPreparation> StageAsync(string applicationKey, int buildId, string serverHost, CancellationToken cancellationToken = default)
    {
        if (!Enabled) throw new CdPreparationException("TFS Git CD is disabled. An administrator must approve and enable private preparation staging first.");
        var root = ApprovedRoot(); // Before network I/O or filesystem mutation.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        await stagingLock.WaitAsync(deadline.Token);
        string? packageDirectory = null;
        try
        {
            EnsureNoLinks(root);
            privateRoot.Validate(root);
            if (Directory.EnumerateFileSystemEntries(root).Any(x => !Directory.Exists(x) || !Guid.TryParseExact(Path.GetFileName(x), "N", out _)))
                throw new CdPreparationException("CD staging root must be dedicated to portal preparations and contain no unrelated files.");
            if (Directory.GetDirectories(root).Length >= options.Value.MaximumPreparations) throw new CdPreparationException("Preparation capacity reached. Ask an administrator to remove expired, unreferenced preparations.");
            var selection = await SelectAsync(applicationKey, buildId, serverHost, deadline.Token);
            var listing = selection.Listing;
            var build = listing.Build;
            var identitySnapshot = Identity(listing);
            var targetSnapshot = TargetIdentity(selection);
            if (build.Id != buildId || build.Definition?.Id != 1 || build.SourceBranch != TfsGitBuildService.CiBranch || build.Status != "completed" || build.Result != "succeeded" || !System.Text.RegularExpressions.Regex.IsMatch(build.SourceVersion ?? "", "^[a-fA-F0-9]{40}$") || build.Repository?.Type != "TfsGit" || !Guid.TryParse(build.Repository.Id, out var repositoryId) || repositoryId == Guid.Empty || build.Repository.Name != "deployment-portal-poc" || build.Project?.Name != "deployment-portal-poc" || build.Project.Id == Guid.Empty)
                throw new CdPreparationException("Build repository/project provenance is missing or differs from deployment-portal-poc.");
            if (listing.ContainerId <= 0 || string.IsNullOrWhiteSpace(listing.ContainerRoot)) throw new InvalidOperationException("Artifact container provenance is missing.");
            var files = listing.Entries.Where(x => !x.IsFolder).ToList();
            if (files.Count == 0 || files.Count > options.Value.MaximumFiles) throw new CdPreparationException("Artifact file count exceeds the preparation policy or is empty.");
            long total = 0;
            foreach (var file in files)
            {
                TfsArtifactExplorer.ValidatePath(file.Path);
                if (file.Size is null or < 0 || file.Size > options.Value.MaximumFileBytes || file.Size > options.Value.MaximumPackageBytes - total) throw new CdPreparationException("Artifact size exceeds the preparation policy.");
                total += file.Size.Value;
            }
            var roots = files.Where(x => x.Path.Equals("web.config", StringComparison.OrdinalIgnoreCase) || x.Path.EndsWith("/web.config", StringComparison.OrdinalIgnoreCase)).Select(x => x.Path.Contains('/') ? x.Path[..x.Path.LastIndexOf('/')] : "").ToList();
            if (roots.Count != 1) throw new CdPreparationException("Artifact must contain exactly one IIS publish root (web.config). Archive-only/source-tree artifacts are not supported.");
            var publishRoot = roots[0];
            var id = Guid.NewGuid();
            packageDirectory = Child(root, id.ToString("N"));
            Directory.CreateDirectory(packageDirectory);
            var content = Child(packageDirectory, "content");
            Directory.CreateDirectory(content);
            var manifest = new List<CdFile>();
            foreach (var file in files.OrderBy(x => x.Path, StringComparer.Ordinal))
            {
                var destination = Child(content, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                EnsureNoLinks(destination);
                var scope = build.Project!.Id != Guid.Empty ? "&scope=" + build.Project.Id.ToString("D") : "";
                var path = $"_apis/resources/Containers/{listing.ContainerId}?api-version=4.1-preview.4&itemPath={Uri.EscapeDataString(listing.ContainerRoot + "/" + file.Path)}&includeDownloadTickets=false{scope}";
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Accept.ParseAdd("application/octet-stream");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != file.Size) throw new InvalidOperationException("Artifact file length changed.");
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                // JSON/HTML can be legitimate published files; never infer metadata solely from their MIME type.
                if ((mediaType == "application/json" && !file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ||
                    (mediaType == "text/html" && !file.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !file.Path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("TFS returned metadata or a login page instead of artifact bytes.");
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    if (read > file.Size!.Value - written) throw new InvalidOperationException("Artifact file exceeded its declared size.");
                    written += read;
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                }
                if (written != file.Size) throw new InvalidOperationException("Artifact transfer was incomplete.");
                manifest.Add(new(file.Path, written, Convert.ToHexString(hash.GetHashAndReset())));
            }
            // Re-resolve the same pinned build/artifact, never latest, after transfer.
            var refreshed = await SelectAsync(applicationKey, buildId, serverHost, deadline.Token);
            if (Identity(refreshed.Listing) != identitySnapshot || TargetIdentity(refreshed) != targetSnapshot) throw new InvalidOperationException("Artifact identity, metadata or target selection changed during retrieval.");
            var prepared = new CdPreparation(id, DateTimeOffset.UtcNow.AddHours(1), selection.Application.Key,
                selection.Server.HostNameOrIp, selection.Server.Environment, selection.Destination.SiteName, selection.Destination.ApplicationName,
                selection.Destination.TargetPath, build.Id, build.BuildNumber ?? "", build.SourceBranch!, build.SourceVersion!, build.Repository!.Id!,
                listing.Artifact, listing.ContainerId, listing.ContainerRoot, publishRoot, Digest(manifest), manifest,
                "PortalStagedBlocked: authenticated remote staging, configuration preservation and protected Review are not yet enabled.");
            await File.WriteAllTextAsync(Child(packageDirectory, "preparation.dat"), protector.Protect(JsonSerializer.Serialize(prepared)), deadline.Token);
            return prepared;
        }
        catch
        {
            // Only remove the unique owned preparation child; never a posted or configured arbitrary directory.
            if (packageDirectory is not null && Path.GetDirectoryName(packageDirectory) == root && Directory.Exists(packageDirectory))
            {
                EnsureTreeNoLinks(packageDirectory);
                Directory.Delete(packageDirectory, true);
            }
            throw;
        }
        finally { stagingLock.Release(); }
    }

    public async Task<CdPreparation> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!Enabled) throw new CdPreparationException("TFS Git CD is disabled.");
        var directory = Child(ApprovedRoot(), id.ToString("N"));
        privateRoot.Validate(ApprovedRoot());
        EnsureTreeNoLinks(directory);
        CdPreparation result;
        try { result = JsonSerializer.Deserialize<CdPreparation>(protector.Unprotect(await File.ReadAllTextAsync(Child(directory, "preparation.dat"), cancellationToken))) ?? throw new InvalidOperationException(); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException) { throw new CdPreparationException("Preparation is missing or invalid. Prepare the artifact again."); }
        if (result.Id != id || result.Expires < DateTimeOffset.UtcNow || result.ManifestSha256 != Digest(result.Files)) throw new CdPreparationException("Preparation expired or changed. Prepare again.");
        var content = Child(directory, "content");
        if (Directory.GetFiles(content, "*", SearchOption.AllDirectories).Length != result.Files.Count) throw new CdPreparationException("Prepared content changed.");
        foreach (var file in result.Files)
        {
            TfsArtifactExplorer.ValidatePath(file.Path);
            var path = Child(content, file.Path.Replace('/', Path.DirectorySeparatorChar));
            await using var stream = File.OpenRead(path);
            if (stream.Length != file.Size || Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)) != file.Sha256) throw new CdPreparationException("Prepared content changed. Prepare again.");
        }
        return result;
    }

    public async Task<IReadOnlyList<CdTransferFile>> TransferFilesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var preparation = await FindAsync(id, cancellationToken);
        var prefix = preparation.PublishRoot == "" ? "" : preparation.PublishRoot + "/";
        var root = Child(Child(ApprovedRoot(), id.ToString("N")), "content");
        return preparation.Files.Where(x => x.Path.StartsWith(prefix, StringComparison.Ordinal)).Select(x => new CdTransferFile(x.Path[prefix.Length..], Child(root, x.Path.Replace('/', Path.DirectorySeparatorChar)), x.Size, x.Sha256)).ToList();
    }

    private string ApprovedRoot()
    {
        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.LocalStagingRoot) || !Path.IsPathFullyQualified(value.LocalStagingRoot) || value.MaximumPackageBytes is <= 0 or > 1024L * 1024 * 1024 || value.MaximumFileBytes <= 0 || value.MaximumFileBytes > value.MaximumPackageBytes || value.MaximumFiles is < 1 or > 10000 || value.MaximumPreparations is < 1 or > 100)
            throw new CdPreparationException("Configure an approved private local staging root and bounded CD limits.");
        var root = Path.GetFullPath(value.LocalStagingRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) DeploymentPaths.Validate(root, @"C:\CdValidation\target", automation.Value);
        var web = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "wwwroot"));
        if (root.Equals(Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || root.Equals(web, StringComparison.OrdinalIgnoreCase) || root.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || web.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || root.StartsWith(@"\\", StringComparison.Ordinal)) throw new CdPreparationException("CD staging must be a private local directory outside web content and drive roots.");
        return root;
    }

    private static string Child(string root, string relative)
    {
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Staging path escaped its approved root.");
        return result;
    }
    private static string Digest(IReadOnlyList<CdFile> files) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(files))));
    private static string Identity(TfsArtifactListing listing) => JsonSerializer.Serialize(new { listing.Build.Id, listing.Build.SourceVersion, listing.Build.SourceBranch, listing.Build.Status, listing.Build.Result, listing.Build.Repository, listing.Build.Project, listing.Artifact, listing.ContainerId, listing.ContainerRoot, listing.Entries });
    private static string TargetIdentity(CdSelection selection) => JsonSerializer.Serialize(new { selection.Application.Key, selection.Application.TfsGitCiDefinitionId, selection.Application.TfsGitCiBranch, selection.Application.TfsGitArtifactName,
        selection.Destination, selection.Server.Id, selection.Server.HostNameOrIp, selection.Server.Environment, selection.Server.WinRmPort, selection.Server.UseSsl, selection.Server.Username });
    private static void EnsureNoLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if ((Directory.Exists(current.FullName) || File.Exists(current.FullName)) && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Staging paths cannot contain links or junctions.");
    }
    private static void EnsureTreeNoLinks(string root)
    {
        EnsureNoLinks(root);
        if (!Directory.Exists(root)) throw new CdPreparationException("Preparation was not found.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            EnsureNoLinks(entry);
            if (Directory.Exists(entry)) EnsureTreeNoLinks(entry);
        }
    }
}
