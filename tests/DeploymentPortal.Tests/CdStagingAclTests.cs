using System.Security.AccessControl;
using System.Security.Principal;
using DeploymentPortal.Services;
using Xunit;

namespace DeploymentPortal.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class CdStagingAclTests
{
    [Fact]
    public void AcceptsProtectedRuntimeModifyGrant()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runtime = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var security = Security(runtime);
        WindowsPrivateCdStagingRoot.ValidateSecurity(security, runtime);
    }
    [Theory]
    [InlineData(WellKnownSidType.WorldSid)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid)]
    [InlineData(WellKnownSidType.BuiltinUsersSid)]
    public void RejectsBroadReadOrWriteGrants(WellKnownSidType type)
    {
        if (!OperatingSystem.IsWindows()) return;
        var runtime = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var security = Security(runtime);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(type, null), FileSystemRights.Read, AccessControlType.Allow));
        Assert.ThrowsAny<ArgumentException>(() => WindowsPrivateCdStagingRoot.ValidateSecurity(security, runtime));
    }
    [Fact]
    public void RejectsUnprotectedInheritance()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runtime = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var security = Security(runtime); security.SetAccessRuleProtection(false, true);
        Assert.ThrowsAny<ArgumentException>(() => WindowsPrivateCdStagingRoot.ValidateSecurity(security, runtime));
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static DirectorySecurity Security(SecurityIdentifier runtime)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(runtime, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
