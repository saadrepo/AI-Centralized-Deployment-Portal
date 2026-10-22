using System.Net;
using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class InfrastructureSourceTests
{
    internal const string Reference = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Destination = "https://dev.azure.com/example/";
    private static ApplicationDbContext Database() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["Tfs:BaseUrl"] = "http://netserver:8080/tfs/DefaultCollection", ["TfsGit:BaseUrl"] = "http://NETSERVER:8080/tfs/DefaultCollection/"
    }).Build();
    private static SourceForm Form() => new() { DisplayName = "Example", Provider = InfrastructureSourceProvider.AzureDevOps, Destination = Destination, CredentialReference = Reference };
    private static InfrastructureSourceService Service(ApplicationDbContext db, Metadata? metadata = null, Probe? probe = null) => new(db, Configuration(), metadata ?? new(), probe ?? new());

    [Fact]
    public async Task CrudEnableDisableAndDuplicateProtection()
    {
        using var db = Database(); var service = Service(db);
        await service.SaveAsync(Form());
        var source = Assert.Single((await service.ListAsync()).Sources);
        Assert.Equal(Destination, source.Destination);
        Assert.NotNull(await service.FindAsync(source.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(Form()));
        var edit = SourceForm.From(source); edit.DisplayName = "Renamed";
        await service.SaveAsync(edit);
        Assert.Equal("Renamed", (await service.FindAsync(source.Id))!.DisplayName);
        Assert.True(await service.SetEnabledAsync(source.Id, false));
        Assert.False((await service.FindAsync(source.Id))!.Enabled);
        await Assert.ThrowsAsync<ArgumentException>(() => service.TestAsync(source.Id, default));
        Assert.True(await service.SetEnabledAsync(source.Id, true));
        Assert.True(await service.DeleteAsync(source.Id));
        Assert.Empty((await service.ListAsync()).Sources);
        Assert.False(await service.DeleteAsync(source.Id));
        Assert.False(await service.SetEnabledAsync(source.Id, true));
        Assert.False(await service.TestAsync(source.Id, default));
    }

    [Theory]
    [InlineData("http://dev.azure.com/example/")]
    [InlineData("https://evil.example/example/")]
    [InlineData("https://user:secret@dev.azure.com/example/")]
    [InlineData("https://dev.azure.com/example/?token=secret")]
    [InlineData("https://dev.azure.com/example/%2e%2e/other/")]
    public async Task UnsafeUrlsNeverReachProbeOrDatabase(string destination)
    {
        using var db = Database(); var probe = new Probe(); var service = Service(db, probe: probe);
        var form = Form(); form.Destination = destination;
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(form));
        Assert.Empty(db.InfrastructureSources); Assert.Equal(0, probe.Calls);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("reference")]
    [InlineData("organization")]
    [InlineData("enum")]
    [InlineData("revoked")]
    [InlineData("missing")]
    public async Task ProviderCredentialAndDestinationMustMatch(string mismatch)
    {
        using var db = Database(); var metadata = new Metadata(); var service = Service(db, metadata);
        var form = Form();
        if (mismatch == "provider") { form.Provider = InfrastructureSourceProvider.GitHub; form.Destination = "https://api.github.com/orgs/example/"; }
        if (mismatch == "reference") form.CredentialReference = new string('f',64);
        if (mismatch == "organization") form.Destination = "https://dev.azure.com/other/";
        if (mismatch == "enum") form.Provider = (InfrastructureSourceProvider)99;
        if (mismatch == "revoked") metadata.Values = [];
        if (mismatch == "missing") form.CredentialReference = null;
        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveAsync(form));
        Assert.Empty(db.InfrastructureSources);
    }

    [Fact]
    public async Task ConfiguredTfsIsDeduplicatedAndNeverImportedOrOverwritten()
    {
        using var db = Database(); var service = Service(db);
        var configured = Assert.Single((await service.ListAsync()).Configured);
        Assert.Empty(db.InfrastructureSources);
        var form = new SourceForm { DisplayName = "TFS", Destination = configured.Destination };
        await service.SaveAsync(form);
        Assert.Empty((await service.ListAsync()).Configured);
        var source = Assert.Single((await service.ListAsync()).Sources);
        await service.DeleteAsync(source.Id);
        Assert.Single((await service.ListAsync()).Configured);
        form.Destination = "https://unconfigured.example/tfs/collection/";
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(form));
        form.Destination = configured.Destination; form.CredentialReference = Reference;
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(form));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task TestsPersistOnlySafeStatusAndResetAfterEdit(bool success, bool throws)
    {
        using var db = Database(); var probe = new Probe { Success = success, Throws = throws }; var service = Service(db, probe: probe);
        await service.SaveAsync(Form()); var source = Assert.Single((await service.ListAsync()).Sources);
        Assert.True(await service.TestAsync(source.Id, default));
        var tested = (await service.FindAsync(source.Id))!;
        Assert.Equal(success ? "Connected" : "Failed", tested.ConnectionStatus);
        Assert.NotNull(tested.LastCheckedAt); Assert.DoesNotContain("secret", tested.ConnectionMessage);
        await service.SaveAsync(SourceForm.From(tested));
        Assert.Null((await service.FindAsync(source.Id))!.LastCheckedAt);
    }

    [Fact]
    public async Task RevokedMetadataBlocksProbeEvenForExistingRegistration()
    {
        using var db = Database(); var metadata = new Metadata(); var probe = new Probe(); var service = Service(db, metadata, probe);
        await service.SaveAsync(Form()); var id = (await db.InfrastructureSources.SingleAsync()).Id;
        metadata.Values = [];
        await service.TestAsync(id, default);
        Assert.Equal(0, probe.Calls); Assert.Equal("Failed", (await service.FindAsync(id))!.ConnectionStatus);
    }

    [Theory]
    [InlineData(InfrastructureSourceProvider.AzureDevOps, HttpStatusCode.OK, true)]
    [InlineData(InfrastructureSourceProvider.GitHub, HttpStatusCode.Unauthorized, false)]
    [InlineData(InfrastructureSourceProvider.GitHub, HttpStatusCode.Redirect, false)]
    public async Task CloudProbeUsesBoundCredentialResolverAndFixedReadEndpoint(InfrastructureSourceProvider provider, HttpStatusCode status, bool success)
    {
        var transport = new Tokens(status); var probe = new InfrastructureSourceProbe(transport, new NoWindows());
        var source = new InfrastructureSource { Provider = provider, Destination = provider == InfrastructureSourceProvider.GitHub ? "https://api.github.com/orgs/example/" : Destination, CredentialReference = Reference };
        Assert.Equal(success, await probe.TestAsync(source, default));
        Assert.Equal(Reference, transport.Reference); Assert.Equal(source.Destination, transport.Destination);
        Assert.True(SourceCredentialProvider.AllowsRequest(InfrastructureSourceService.TokenProvider(provider), source.Destination, transport.Uri!));
    }

    [Fact]
    public void MetadataFailsClosedWithoutExposingFilesystemOrCredentials()
    {
        var provider = new SourceCredentialProvider(new EnvironmentStub());
        Assert.Equal(SourceCredentialProvider.UnavailableMessage, Assert.Throws<InvalidOperationException>(() => provider.List()).Message);
    }
    private sealed class Metadata : ISourceCredentialMetadata
    {
        public IReadOnlyList<SourceCredentialMetadata> Values = [new(Reference, SourceTokenProvider.AzureDevOps, Destination)];
        public IReadOnlyList<SourceCredentialMetadata> List() => Values;
    }
    private sealed class Probe : IInfrastructureSourceProbe
    {
        public int Calls; public bool Success = true; public bool Throws;
        public Task<bool> TestAsync(InfrastructureSource source, CancellationToken cancellationToken) {
            Calls++; if (Throws) throw new HttpRequestException("synthetic-secret-never-display"); return Task.FromResult(Success);
        }
    }
    private sealed class Tokens(HttpStatusCode status) : ISourceCredentialProvider
    {
        public string? Reference, Destination; public Uri? Uri;
        public Task<HttpResponseMessage> GetAsync(string reference, SourceTokenProvider provider, string destination, Uri requestUri, CancellationToken cancellationToken) {
            Reference = reference; Destination = destination; Uri = requestUri; return Task.FromResult(new HttpResponseMessage(status));
        }
    }
    private sealed class NoWindows : ITfsWindowsCredentialProvider { public NetworkCredential GetCredential(Uri collection) => throw new Exception("Cloud probe must not use Windows credentials."); }
    private sealed class EnvironmentStub : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = SourceCredentialProvider.StoreRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
