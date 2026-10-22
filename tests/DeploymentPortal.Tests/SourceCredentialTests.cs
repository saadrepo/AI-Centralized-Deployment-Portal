using System.Security.Cryptography;
using System.Text;
using DeploymentPortal.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DeploymentPortal.Tests;

public class SourceCredentialTests
{
    private const string Reference = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Destination = "https://dev.azure.com/example/";

    [Theory]
    [InlineData("https://evil.example/example/")]
    [InlineData("http://dev.azure.com/example/")]
    [InlineData("https://user:secret@dev.azure.com/example/")]
    [InlineData("https://dev.azure.com:444/example/")]
    [InlineData("https://dev.azure.com/example/?token=secret")]
    [InlineData("https://dev.azure.com/example/#secret")]
    [InlineData("https://dev.azure.com/example/../other/")]
    public void InvalidDestinationsFailClosed(string url) => Assert.Equal(SourceCredentialProvider.UnavailableMessage,
        Assert.Throws<InvalidOperationException>(() => SourceCredentialProvider.Binding(Reference, SourceTokenProvider.AzureDevOps, url)).Message);

    [Theory]
    [InlineData("https://dev.azure.com/other/_apis/projects")]
    [InlineData("https://dev.azure.com/exampleExtra/_apis/projects")]
    [InlineData("https://evil.example/example/_apis/projects")]
    [InlineData("https://dev.azure.com/example/%252e%252e/private")]
    [InlineData("https://dev.azure.com/example/../other/")]
    [InlineData("https://dev.azure.com/example/%2fother")]
    [InlineData("https://user:secret@dev.azure.com/example/_apis/projects")]
    public void RequestsCannotEscapeOrganization(string url) => Assert.False(SourceCredentialProvider.AllowsRequest(SourceTokenProvider.AzureDevOps, Destination, new Uri(url)));

    [Fact]
    public void AllowedRequestsAndHandlerStayReadOnlyAndDoNotRedirect()
    {
        Assert.True(SourceCredentialProvider.AllowsRequest(SourceTokenProvider.AzureDevOps, Destination, new Uri(Destination + "_apis/projects?api-version=7.1")));
        Assert.True(SourceCredentialProvider.AllowsRequest(SourceTokenProvider.GitHub, "https://api.github.com/orgs/example/", new Uri("https://api.github.com/orgs/example")));
        Assert.False(SourceCredentialProvider.AllowsRequest(SourceTokenProvider.GitHub, "https://api.github.com/orgs/example/", new Uri("https://api.github.com/user")));
        using var handler = SourceCredentialProvider.CreateHandler();
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseProxy); Assert.False(handler.UseDefaultCredentials);
        Assert.Null(handler.Credentials); Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void BindingMatchesScriptAndCanonicalizes()
    {
        Assert.Equal($"DeploymentPortal.SourceToken.v1\n{Reference}\nAzureDevOps\n{Destination}", Encoding.UTF8.GetString(SourceCredentialProvider.Binding(Reference, SourceTokenProvider.AzureDevOps, Destination)));
        Assert.Equal(SourceCredentialProvider.Binding(Reference, SourceTokenProvider.AzureDevOps, Destination), SourceCredentialProvider.Binding(Reference, SourceTokenProvider.AzureDevOps, "https://DEV.AZURE.COM/EXAMPLE"));
        Assert.Throws<InvalidOperationException>(() => SourceCredentialProvider.Binding("../secret", SourceTokenProvider.AzureDevOps, Destination));
        Assert.Throws<InvalidOperationException>(() => SourceCredentialProvider.Binding(Reference, SourceTokenProvider.GitHub, Destination));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void DpapiIsolatesReferencesOrganizationsAndProviders()
    {
        if (!OperatingSystem.IsWindows()) return;
        var binding = SourceCredentialProvider.Binding(Reference, SourceTokenProvider.AzureDevOps, Destination);
        var encrypted = ProtectedData.Protect(Encoding.ASCII.GetBytes("synthetic-only"), binding, DataProtectionScope.LocalMachine);
        foreach (var other in new[] {
            SourceCredentialProvider.Binding(new string('a',64), SourceTokenProvider.AzureDevOps, Destination),
            SourceCredentialProvider.Binding(Reference, SourceTokenProvider.AzureDevOps, "https://dev.azure.com/other/"),
            SourceCredentialProvider.Binding(Reference, SourceTokenProvider.GitHub, "https://api.github.com/orgs/example/") })
            Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(encrypted, other, DataProtectionScope.LocalMachine));
    }

    [Fact]
    public async Task BackendFailsClosedWithoutExposingInputs()
    {
        var provider = new SourceCredentialProvider(new TestEnvironment());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync("synthetic-secret", SourceTokenProvider.AzureDevOps, Destination, new Uri(Destination)));
        Assert.Equal(SourceCredentialProvider.UnavailableMessage, error.Message);
        Assert.Null(error.InnerException);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = SourceCredentialProvider.StoreRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
