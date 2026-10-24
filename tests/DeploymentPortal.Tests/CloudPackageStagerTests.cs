using System.IO.Compression;
using System.Security.Cryptography;
using DeploymentPortal.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeploymentPortal.Tests;

public class CloudPackageStagerTests
{
    private sealed class PrivateRoot : IPrivateCdStagingRoot { public int Calls; public void Validate(string root) { Calls++; } }
    private sealed class Host : IHostEnvironment {
        public string ApplicationName { get; set; } = "test"; public string EnvironmentName { get; set; } = "test";
        public string ContentRootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "web"); public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private static byte[] Zip(params (string Name, string Content, int Attributes)[] files)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) foreach (var f in files) {
            var entry = zip.CreateEntry(f.Name); entry.ExternalAttributes = f.Attributes; using var writer = new StreamWriter(entry.Open()); writer.Write(f.Content);
        }
        return bytes.ToArray();
    }
    private static byte[] ValidZip(string prefix = "") => Zip((prefix + "web.config", "web", 0), (prefix + "appsettings.json", "{}", 0), (prefix + "app.runtimeconfig.json", "{}", 0), (prefix + "app.dll", "binary", 0));
    [Fact]
    public async Task FailedExtractionCleansOnlyNewPreparationAndNeverReusedOrRetainedPackages()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "cloud-package-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var stager = new CloudPackageStager(Options.Create(new CloudExecutionOptions { LocalStagingRoot = root }), new PrivateRoot(), new Host());
            var retained = Guid.NewGuid(); var failed = Guid.NewGuid();
            var files = await stager.StageAsync(retained, ValidZip(), new(), default);
            var original = await File.ReadAllBytesAsync(files.Single(f => f.Path == "app.dll").LocalPath);
            await Assert.ThrowsAnyAsync<Exception>(() => stager.StageAsync(failed, Zip(("partial.txt", "partial", 0), ("../escape", "unsafe", 0)), new(), default));
            Assert.False(Directory.Exists(Path.Combine(root, failed.ToString("N"))));
            await Assert.ThrowsAnyAsync<Exception>(() => stager.StageAsync(retained, ValidZip(), new(), default));
            Assert.Equal(original, await File.ReadAllBytesAsync(files.Single(f => f.Path == "app.dll").LocalPath));
            Assert.Single(Directory.EnumerateDirectories(root));
        } finally { Directory.Delete(root, true); }
    }
    [Theory] [InlineData("")] [InlineData("drop")]
    public async Task ApprovedRootProducesBoundedSha256TransferManifest(string prefix)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "cloud-package-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var acl = new PrivateRoot(); var stager = new CloudPackageStager(Options.Create(new CloudExecutionOptions { LocalStagingRoot = root }), acl, new Host());
            var files = await stager.StageAsync(Guid.NewGuid(), ValidZip(prefix.Length == 0 ? "" : prefix + "/"), new() { ArchiveRoot = prefix }, default);
            Assert.Equal(4, files.Count); Assert.Equal(1, acl.Calls);
            foreach (var file in files) {
                Assert.StartsWith(root, file.LocalPath); Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file.LocalPath))), file.Sha256);
            }
        } finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("../escape", 0)] [InlineData("C:/escape", 0)] [InlineData("app.dll:stream", 0)]
    [InlineData("CON.json", 0)] [InlineData("dir\\escape", 0)] [InlineData("link", unchecked((int)0xA1FF0000))]
    public async Task UnsafePathsAndLinksAreRejected(string path, int attributes)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "cloud-package-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var stager = new CloudPackageStager(Options.Create(new CloudExecutionOptions { LocalStagingRoot = root }), new PrivateRoot(), new Host());
            await Assert.ThrowsAnyAsync<Exception>(() => stager.StageAsync(Guid.NewGuid(), Zip((path, "unsafe", attributes)), new(), default));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        } finally { Directory.Delete(root, true); }
    }
    [Theory] [InlineData("duplicate")] [InlineData("wrong-root")] [InlineData("layout")]
    public async Task DuplicateNamesAndUnapprovedArchiveLayoutsFailClosed(string failure)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "cloud-package-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var stager = new CloudPackageStager(Options.Create(new CloudExecutionOptions { LocalStagingRoot = root }), new PrivateRoot(), new Host());
            var zip = failure == "duplicate" ? Zip(("app.dll", "one", 0), ("APP.DLL", "two", 0)) : failure == "layout" ? Zip(("readme.txt", "unpublished", 0)) : ValidZip("wrong/");
            await Assert.ThrowsAnyAsync<Exception>(() => stager.StageAsync(Guid.NewGuid(), zip, new() { ArchiveRoot = failure == "wrong-root" ? "drop" : "" }, default));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        } finally { Directory.Delete(root, true); }
    }
}
