using System.Security.AccessControl;
using System.Security.Principal;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeploymentPortal.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class PortalDataProtectionTests
{
    [Fact]
    public async Task EncryptedPersistentKeysPreserveAntiforgeryAcrossIndependentHosts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(AppContext.BaseDirectory,"key-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            AntiforgeryTokenSet tokens;
            string payload;
            using (var first = Host(root))
            {
                payload = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("test-confirmation").Protect("synthetic-confirmation");
                tokens = first.GetRequiredService<IAntiforgery>().GetTokens(new DefaultHttpContext { RequestServices=first });
            }
            var xml = string.Join("\n",Directory.GetFiles(root,"*.xml").Select(File.ReadAllText));
            Assert.Contains("encryptedSecret",xml); Assert.Contains("DpapiXmlDecryptor",xml); Assert.DoesNotContain("<masterKey",xml);
            using var second = Host(root);
            Assert.Equal("synthetic-confirmation",second.GetRequiredService<IDataProtectionProvider>().CreateProtector("test-confirmation").Unprotect(payload));
            var context = new DefaultHttpContext { RequestServices=second };
            context.Request.Method="POST";
            context.Request.Headers.Cookie="PortalTest.Antiforgery="+tokens.CookieToken;
            context.Request.Headers["X-CSRF-TEST"]=tokens.RequestToken;
            await second.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            var tampered = new DefaultHttpContext { RequestServices=second };
            tampered.Request.Method="POST";
            tampered.Request.Headers.Cookie="PortalTest.Antiforgery="+tokens.CookieToken;
            tampered.Request.Headers["X-CSRF-TEST"]="tampered-token";
            await Assert.ThrowsAsync<AntiforgeryValidationException>(()=>second.GetRequiredService<IAntiforgery>().ValidateRequestAsync(tampered));
        }
        finally { Directory.Delete(root,true); }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("broad")]
    [InlineData("inheritance")]
    [InlineData("owner")]
    [InlineData("read-only")]
    public void KeyDirectoryRequiresPrivateInheritedModifyGrant(string variant)
    {
        if (!OperatingSystem.IsWindows()) return;
        var runtime = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var acl = new DirectorySecurity(); acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)); acl.SetAccessRuleProtection(true,false);
        acl.AddAccessRule(new FileSystemAccessRule(runtime,variant == "read-only" ? FileSystemRights.ReadAndExecute : FileSystemRights.Modify,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        if (variant == "broad") acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.Read,AccessControlType.Allow));
        if (variant == "inheritance") acl.SetAccessRuleProtection(false,true);
        if (variant == "owner") acl.SetOwner(runtime);
        if (variant == "valid") PortalDataProtection.ValidateSecurity(acl,runtime,true);
        else Assert.ThrowsAny<Exception>(()=>PortalDataProtection.ValidateSecurity(acl,runtime,true));
    }

    [Fact]
    public void RuntimeOwnedGeneratedKeysAreAllowedButBroadFileReadsAreRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runtime = new SecurityIdentifier("S-1-5-21-1-2-3-1001"); var acl = new FileSecurity(); acl.SetOwner(runtime);
        acl.AddAccessRule(new FileSystemAccessRule(runtime,FileSystemRights.Modify,AccessControlType.Allow));
        PortalDataProtection.ValidateSecurity(acl,runtime,false);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),FileSystemRights.Read,AccessControlType.Allow));
        Assert.Throws<InvalidOperationException>(()=>PortalDataProtection.ValidateSecurity(acl,runtime,false));
    }

    [Fact]
    public void RejectsKeyDirectoryInsideWebApplicationBeforeAccess()
    {
        var error=Assert.Throws<InvalidOperationException>(()=>PortalDataProtection.ValidateDirectory(PortalDataProtection.KeyRoot,PortalDataProtection.KeyRoot));
        Assert.Equal(PortalDataProtection.UnavailableMessage,error.Message);
    }

    private static ServiceProvider Host(string root)
    {
        var services=new ServiceCollection(); services.AddLogging(); PortalDataProtection.ConfigurePersistentKeyRing(services,new DirectoryInfo(root));
        services.AddAntiforgery(options=> { options.Cookie.Name="PortalTest.Antiforgery"; options.HeaderName="X-CSRF-TEST"; });
        return services.BuildServiceProvider();
    }
}
