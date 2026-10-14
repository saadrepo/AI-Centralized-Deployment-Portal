using System.Net;
using System.Security.Cryptography;
using System.Text;
using DeploymentPortal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Xunit;

namespace DeploymentPortal.Tests;

public class TfsWindowsCredentialTests
{
    private static readonly Uri Collection = new("http://netserver:8080/tfs/DefaultCollection/");

    [Fact]
    public void BindingMatchesProvisionerAndIsSeparateFromWinRm()
    {
        Assert.Equal("DeploymentPortal.TfsGit.Windows.v1\nhttp://netserver:8080/tfs/DefaultCollection/", Encoding.UTF8.GetString(TfsWindowsCredentialProvider.Binding(Collection)));
        Assert.Equal(TfsWindowsCredentialProvider.FileName(Collection), TfsWindowsCredentialProvider.FileName(new Uri("http://NETSERVER:8080/tfs/DefaultCollection")));
        Assert.Matches("^[a-f0-9]{64}\\.dpapi$", TfsWindowsCredentialProvider.FileName(Collection));
        Assert.NotEqual(ProductionCredentialProvider.StoreRoot, TfsWindowsCredentialProvider.StoreRoot);
    }

    [Theory]
    [InlineData("DOMAIN\\reader", "reader", "DOMAIN")]
    [InlineData("reader@example.test", "reader@example.test", "")]
    [InlineData("reader", "reader", "")]
    public void EncryptedPayloadSupportsWindowsAccountForms(string username, string expectedUser, string expectedDomain)
    {
        var credential = TfsWindowsCredentialProvider.DecodePayload(Encoding.UTF8.GetBytes(username + "\0synthetic-only-\uF123"));
        Assert.Equal(expectedUser, credential.UserName);
        Assert.Equal(expectedDomain, credential.Domain);
        Assert.Equal("synthetic-only-\uF123", credential.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-delimiter")]
    [InlineData("\0synthetic-only")]
    [InlineData("reader\0")]
    [InlineData("DOMAIN\\\0synthetic-only")]
    [InlineData("\\reader\0synthetic-only")]
    [InlineData("DOMAIN\\reader\\extra\0synthetic-only")]
    [InlineData("reader\n\0synthetic-only")]
    public void MalformedPayloadFailsWithoutSecretDetails(string payload)
    {
        var error = Assert.Throws<InvalidOperationException>(() => TfsWindowsCredentialProvider.DecodePayload(Encoding.UTF8.GetBytes(payload)));
        Assert.Equal(TfsWindowsCredentialProvider.UnavailableMessage, error.Message);
    }

    [Fact]
    public void InvalidUtf8FailsSafely() => Assert.Throws<InvalidOperationException>(() => TfsWindowsCredentialProvider.DecodePayload([0xff, 0, 0xff]));

    [Fact]
    public void DevelopmentKeepsDefaultProcessCredentialsWithoutAStore()
    {
        var provider = new TfsWindowsCredentialProvider(new TestEnvironment { EnvironmentName = "Development" });
        Assert.Same(CredentialCache.DefaultNetworkCredentials, provider.GetCredential(Collection));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void NonDevelopmentFailsClosedAndNeverFallsBackToProcessCredentials(string environment)
    {
        var provider = new TfsWindowsCredentialProvider(new TestEnvironment { EnvironmentName = environment, ContentRootPath = TfsWindowsCredentialProvider.StoreRoot });
        using var handler = TfsGitClientConfiguration.CreatePrimaryHandler(Collection.AbsoluteUri, () => provider.GetCredential(Collection));
        var error = Assert.Throws<InvalidOperationException>(() => handler.Credentials!.GetCredential(new Uri(Collection, "deployment-portal-poc/_apis/build/builds"), "Negotiate"));
        Assert.Equal(TfsWindowsCredentialProvider.UnavailableMessage, error.Message);
    }

    [Theory]
    [InlineData("http://netserver:8080/tfs/DefaultCollection/project/_apis/build/builds", "Basic")]
    [InlineData("http://netserver:8080/tfs/DefaultCollection/project/_apis/build/builds", "Digest")]
    [InlineData("http://external.example/tfs/DefaultCollection/project", "NTLM")]
    [InlineData("http://netserver:80/tfs/DefaultCollection/project", "NTLM")]
    [InlineData("https://netserver:8080/tfs/DefaultCollection/project", "NTLM")]
    [InlineData("http://netserver:8080/tfs/OtherCollection/project", "NTLM")]
    [InlineData("http://netserver:8080/tfs/DefaultCollectionExtra/project", "Negotiate")]
    [InlineData("http://netserver:8080/tfs/DefaultCollection/%252e%252e/private", "Negotiate")]
    public void OutOfScopeOrUnsupportedChallengesNeverReadCredentials(string endpoint, string authType)
    {
        var reads = 0;
        using var handler = TfsGitClientConfiguration.CreatePrimaryHandler(Collection.AbsoluteUri, () => { reads++; throw new Exception("Secret store must not be accessed."); });
        Assert.Null(handler.Credentials!.GetCredential(new Uri(endpoint), authType));
        Assert.Equal(0, reads);
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseProxy); Assert.False(handler.PreAuthenticate);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Theory]
    [InlineData("Negotiate")]
    [InlineData("NTLM")]
    public void AllowedChallengesReadFreshExplicitCredentials(string authType)
    {
        var reads = 0;
        using var handler = TfsGitClientConfiguration.CreatePrimaryHandler(Collection.AbsoluteUri, () => new NetworkCredential("reader", "synthetic-" + ++reads, "DOMAIN"));
        var uri = new Uri(Collection, "deployment-portal-poc/_apis/build/builds");
        Assert.Equal("synthetic-1", handler.Credentials!.GetCredential(uri, authType)!.Password);
        Assert.Equal("synthetic-2", handler.Credentials.GetCredential(uri, authType)!.Password);
        Assert.False(handler.UseDefaultCredentials);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [Theory]
    [InlineData("collection")]
    [InlineData("purpose")]
    [InlineData("corrupt")]
    public void DpapiRejectsWrongCollectionPurposeOrCorruptedCiphertext(string change)
    {
        if (!OperatingSystem.IsWindows()) return;
        var binding = TfsWindowsCredentialProvider.Binding(Collection);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes("DOMAIN\\reader\0synthetic-only"), binding, DataProtectionScope.LocalMachine);
        Assert.Equal("reader", TfsWindowsCredentialProvider.DecodePayload(ProtectedData.Unprotect(encrypted, binding, DataProtectionScope.LocalMachine)).UserName);
        if (change == "collection") binding = TfsWindowsCredentialProvider.Binding(new Uri("https://tfs.example.test/tfs/OtherCollection/"));
        if (change == "purpose") binding = Encoding.UTF8.GetBytes("DeploymentPortal.WinRm.v1");
        if (change == "corrupt") encrypted[encrypted.Length / 2] ^= 0xff;
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(encrypted, binding, DataProtectionScope.LocalMachine));
    }

    [Fact]
    public void AllGitTypedClientsShareProvisionedCredentialAndExistingGuard()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TfsGit:BaseUrl"] = Collection.AbsoluteUri }).Build());
        var credentials = new FakeCredentials();
        var filter = new InspectHandlerFilter();
        services.AddSingleton<ITfsWindowsCredentialProvider>(credentials);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(filter);
        services.AddTfsGitClient();
        using var provider = services.BuildServiceProvider();
        Assert.IsType<TfsGitService>(provider.GetRequiredService<ITfsGitService>());
        Assert.IsType<TfsGitBuildService>(provider.GetRequiredService<ITfsGitBuildService>());
        Assert.IsType<TfsArtifactExplorer>(provider.GetRequiredService<ITfsArtifactExplorer>());
        // Preparation additionally requires registry/DB services; its client registration uses this same handler.
        Assert.True(filter.Checked);
        Assert.Equal(1, credentials.Reads);
    }

    private sealed class FakeCredentials : ITfsWindowsCredentialProvider
    {
        public int Reads;
        public NetworkCredential GetCredential(Uri collection) { Assert.Equal(Collection, collection); Reads++; return new("reader", "synthetic-only", "DOMAIN"); }
    }
    private sealed class InspectHandlerFilter : IHttpMessageHandlerBuilderFilter
    {
        public bool Checked;
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder => {
            next(builder);
            var handler = Assert.IsType<HttpClientHandler>(builder.PrimaryHandler);
            Assert.Contains(builder.AdditionalHandlers, h => h is TfsGitRequestGuard);
            var credential = handler.Credentials!.GetCredential(new Uri(Collection, "deployment-portal-poc/_apis/build/builds"), "NTLM");
            Assert.Equal("reader", credential!.UserName); Assert.Equal("DOMAIN", credential.Domain);
            Checked = true;
        };
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
