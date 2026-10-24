using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace DeploymentPortal.Services;

public sealed class CloudPackageStager(IOptions<CloudExecutionOptions> options, IPrivateCdStagingRoot privateRoot,
    IHostEnvironment environment, ILogger<CloudPackageStager>? logger = null) : ICloudPackageStager
{
    public async Task<IReadOnlyList<CdTransferFile>> StageAsync(Guid id, byte[] archive, CloudExecutionMapping mapping, CancellationToken ct)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.LocalStagingRoot));
        CloudSecurity.Require(Path.IsPathFullyQualified(options.Value.LocalStagingRoot) && !Path.GetPathRoot(root)!.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !root.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath)), StringComparison.OrdinalIgnoreCase), "LOCAL_STAGING_ROOT");
        NoLinks(root); privateRoot.Validate(root);
        CloudSecurity.Require(Directory.EnumerateDirectories(root).Take(10).Count() < 10, "STAGING_CAPACITY");
        CloudSecurity.Require(archive.Length is > 0 and <= 67108864, "ARCHIVE_SIZE");
        var directory = Path.Combine(root, id.ToString("N"));
        CloudSecurity.Require(!Directory.Exists(directory), "PREPARATION_EXISTS");
        Directory.CreateDirectory(directory); NoLinks(directory);
        try {
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        CloudSecurity.Require(zip.Entries.Count is > 0 and <= 2000, "ZIP_ENTRY_COUNT");
        var prefix = mapping.ArchiveRoot.Length == 0 ? "" : mapping.ArchiveRoot.TrimEnd('/') + "/";
        if (prefix.Length != 0) TfsArtifactExplorer.ValidatePath(prefix.TrimEnd('/'));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var files = new List<CdTransferFile>(); long total = 0;
        foreach (var entry in zip.Entries) {
            ct.ThrowIfCancellationRequested();
            CloudSecurity.Require(!entry.FullName.Contains('\\') && ((entry.ExternalAttributes >> 16) & 0xF000) is not (0xA000 or 0x6000 or 0x2000) &&
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) == 0, "ZIP_LINK");
            var name = entry.FullName.TrimEnd('/'); TfsArtifactExplorer.ValidatePath(name);
            CloudSecurity.Require(paths.Add(name), "ZIP_DUPLICATE");
            if (prefix.Length != 0 && name == prefix.TrimEnd('/') && entry.FullName.EndsWith('/')) continue;
            CloudSecurity.Require(entry.FullName.StartsWith(prefix, StringComparison.Ordinal), "ARCHIVE_ROOT");
            var relative = entry.FullName[prefix.Length..].TrimEnd('/'); TfsArtifactExplorer.ValidatePath(relative);
            if (entry.FullName.EndsWith('/')) continue;
            CloudSecurity.Require(entry.Length is >= 0 and <= 67108864 && files.Count < 1000, "ZIP_FILE_LIMIT");
            total += entry.Length; CloudSecurity.Require(total <= 268435456, "ZIP_TOTAL_LIMIT");
            var target = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
            CloudSecurity.Require(target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "ZIP_ESCAPE");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); NoLinks(Path.GetDirectoryName(target)!);
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                await using var input = entry.Open(); var buffer = new byte[65536]; int read; long written = 0;
                while ((read = await input.ReadAsync(buffer, ct)) > 0) {
                    written += read; CloudSecurity.Require(written <= entry.Length, "ZIP_LENGTH");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                CloudSecurity.Require(written == entry.Length, "ZIP_LENGTH");
            }
            await using var content = File.OpenRead(target);
            files.Add(new(relative.Replace('/', '\\'), target, entry.Length, Convert.ToHexString(await SHA256.HashDataAsync(content, ct))));
        }
        CloudSecurity.Require(files.Any(f => f.Path == "web.config") && files.Any(f => f.Path == "appsettings.json") && files.Any(f => f.Path.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)), "PUBLISH_LAYOUT");
        return files;
        } catch {
            try { RemoveFailedPreparation(root, directory); }
            catch { logger?.LogWarning("Failed cloud extraction requires manual private staging cleanup. PreparationId={PreparationId}.", id); }
            throw;
        }
    }
    private void RemoveFailedPreparation(string root, string directory)
    {
        NoLinks(directory); privateRoot.Validate(root);
        CloudSecurity.Require(Path.GetDirectoryName(directory) == root && Guid.TryParseExact(Path.GetFileName(directory), "N", out _), "CLEANUP_SCOPE");
        var entries = new List<FileSystemInfo>(); var pending = new Stack<DirectoryInfo>(); pending.Push(new(directory));
        while (pending.Count > 0) {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos()) {
                CloudSecurity.Require((entry.Attributes & FileAttributes.ReparsePoint) == 0 && entries.Count < 4000, "CLEANUP_UNSAFE_TREE");
                entries.Add(entry); if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
        // Delete known files, then empty directories. Never recurse or follow links.
        foreach (var file in entries.OfType<FileInfo>()) { NoLinks(file.DirectoryName!); file.Delete(); }
        foreach (var child in entries.OfType<DirectoryInfo>().OrderByDescending(d => d.FullName.Length)) { NoLinks(child.FullName); child.Delete(); }
        NoLinks(directory); Directory.Delete(directory);
    }
    private static void NoLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new CloudExecutionException("STAGING_LINK");
    }
}
