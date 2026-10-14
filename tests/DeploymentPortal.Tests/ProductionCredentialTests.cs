using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DeploymentPortal.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class ProductionCredentialTests
{
    private static Server Target() => new() { HostNameOrIp = "deployment-test.vitonta.com", Username = @"WINDOWSERVERTES\saad.bilal", WinRmPort = 5985, UseSsl = false };

    [Fact]
    public void BindingMatchesProvisioningFormatAndNormalizesCase()
    {
        var target = Target();
        Assert.Equal("DeploymentPortal.WinRm.v1\ndeployment-test.vitonta.com\nwindowservertes\\saad.bilal\n5985\nhttp", Encoding.UTF8.GetString(ProductionCredentialProvider.Binding(target)));
        var name = ProductionCredentialProvider.FileName(target);
        target.HostNameOrIp = target.HostNameOrIp.ToUpperInvariant(); target.Username = target.Username.ToUpperInvariant();
        Assert.Equal(name, ProductionCredentialProvider.FileName(target));
        Assert.Matches("^[a-f0-9]{64}\\.dpapi$", name);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("host/secret")]
    [InlineData("host\nsecret")]
    [InlineData("")]
    public void RejectsInvalidHostBeforeFileAccess(string host)
    {
        var target = Target(); target.HostNameOrIp = host;
        Assert.Throws<InvalidOperationException>(() => ProductionCredentialProvider.FileName(target));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("username")]
    [InlineData("port")]
    [InlineData("tls")]
    public void DpapiRejectsCredentialReuseForDifferentConnection(string change)
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Target();
        var bytes = Encoding.UTF8.GetBytes("synthetic-test-only-password");
        var encrypted = ProtectedData.Protect(bytes, ProductionCredentialProvider.Binding(target), DataProtectionScope.LocalMachine);
        Assert.Equal(bytes, ProtectedData.Unprotect(encrypted, ProductionCredentialProvider.Binding(target), DataProtectionScope.LocalMachine));
        Assert.DoesNotContain("synthetic-test-only-password", Encoding.UTF8.GetString(encrypted));
        switch (change) {
            case "host": target.HostNameOrIp = "other.internal"; break;
            case "username": target.Username = "other-user"; break;
            case "port": target.WinRmPort = 5986; break;
            case "tls": target.UseSsl = true; break;
        }
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(encrypted, ProductionCredentialProvider.Binding(target), DataProtectionScope.LocalMachine));
    }

    [Fact]
    public async Task ProviderErrorsAreSafeForConnectionTests()
    {
        var env = new TestEnvironment { EnvironmentName = "Development" };
        var provider = new ProductionCredentialProvider(env);
        var result = await new WinRmService(new NeverExecute(), provider).TestConnectionAsync(Target());
        Assert.False(result.Success);
        Assert.Equal(ProductionCredentialProvider.UnavailableMessage, result.Message);
        Assert.DoesNotContain("saad.bilal", result.Message);
    }

    [Fact]
    public async Task RejectsStoreInsideApplicationBeforeReadingSecrets()
    {
        var provider = new ProductionCredentialProvider(new TestEnvironment { ContentRootPath = ProductionCredentialProvider.StoreRoot });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync(Target()));
        Assert.Equal(ProductionCredentialProvider.UnavailableMessage, error.Message);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("broad")]
    [InlineData("write")]
    [InlineData("inheritance")]
    [InlineData("owner")]
    [InlineData("missing-read")]
    public void EnforcesPrivateReadOnlyAcl(string variant)
    {
        if (!OperatingSystem.IsWindows()) return;
        var runtime = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var acl = new FileSecurity();
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        acl.SetAccessRuleProtection(true, false);
        if (variant != "missing-read") acl.AddAccessRule(new FileSystemAccessRule(runtime, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        switch (variant) {
            case "broad": acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow)); break;
            case "write": acl.AddAccessRule(new FileSystemAccessRule(runtime, FileSystemRights.Write, AccessControlType.Allow)); break;
            case "inheritance": acl.SetAccessRuleProtection(false, true); break;
            case "owner": acl.SetOwner(runtime); break;
        }
        if (variant == "valid") ProductionCredentialProvider.ValidateSecurity(acl, runtime);
        else Assert.Throws<InvalidOperationException>(() => ProductionCredentialProvider.ValidateSecurity(acl, runtime));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class NeverExecute : IPowerShellService
    {
        public Task<RemoteResult> ExecuteAsync(RemoteOperation operation, Server server, RemoteCredential credential, Deployment? deployment = null) => throw new Exception("PowerShell must not run without credentials.");
    }
}
