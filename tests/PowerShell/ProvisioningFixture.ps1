# Test-only support for unprivileged and restricted Windows tokens.
# Production scripts never load this file.
function Set-ProvisioningFixtureAcl([string]$LiteralPath, $AclObject) {
    $root = [IO.Path]::GetFullPath($fixtureContext.Root).TrimEnd('\') + '\'
    $path = [IO.Path]::GetFullPath($LiteralPath)
    if (!$path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'ACL target is outside this test fixture.' }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture ACL operation refuses links.' }
    if ($item.PSIsContainer) { $acl = [Security.AccessControl.DirectorySecurity]::new() }
    else { $acl = [Security.AccessControl.FileSecurity]::new() }
    # Set-Acl in Windows PowerShell requests SACL privileges. These tests only
    # change owner/DACL; persist exactly those sections without audit privileges.
    $sections = [Security.AccessControl.AccessControlSections]'Owner, Access'
    $acl.SetSecurityDescriptorBinaryForm($AclObject.GetSecurityDescriptorBinaryForm(), $sections)
    $item.SetAccessControl($acl)
    $actual = Get-Acl -LiteralPath $path
    # Ignore only Windows' auto-inheritance bookkeeping flag on protected DACLs.
    $actualSddl = $actual.GetSecurityDescriptorSddlForm($sections) -replace '(?<=D:P)AI', ''
    $expectedSddl = $acl.GetSecurityDescriptorSddlForm($sections) -replace '(?<=D:P)AI', ''
    if ($actualSddl -cne $expectedSddl) { throw 'Fixture owner/DACL did not persist exactly.' }
}

function New-ProvisioningFixture([string]$Repository) {
    if (!('ProvisioningFixtureToken' -as [type])) {
        Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class ProvisioningFixtureToken {
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr data, int size, out int needed);
    public static string[] RestrictedSids() {
        using (var identity = WindowsIdentity.GetCurrent()) {
            int size;
            GetTokenInformation(identity.Token, 11, IntPtr.Zero, 0, out size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr data = Marshal.AllocHGlobal(size);
            try {
                if (!GetTokenInformation(identity.Token, 11, data, size, out size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                int count = Marshal.ReadInt32(data);
                var values = new string[count];
                int offset = IntPtr.Size == 8 ? 8 : 4;
                int stride = IntPtr.Size == 8 ? 16 : 8;
                for (int i = 0; i < count; i++)
                    values[i] = new SecurityIdentifier(Marshal.ReadIntPtr(data, offset + i * stride)).Value;
                return values;
            } finally { Marshal.FreeHGlobal(data); }
        }
    }
}
'@
    }
    $restricted = @([ProvisioningFixtureToken]::RestrictedSids())
    $systemSid = 'S-1-5-18'
    if ($restricted.Count -gt 0) {
        # A restricted token must also pass the restricted-SID DACL check.
        # Stand in for SYSTEM with one existing workspace capability, not Everyone.
        $rules = (Get-Acl -LiteralPath $Repository).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
        $capability = @($rules | Where-Object {
            $_.IdentityReference.Value -match '^S-1-5-21-' -and
            $restricted -contains $_.IdentityReference.Value -and
            $_.AccessControlType -eq 'Allow' -and
            ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify
        } | Select-Object -First 1)
        if ($capability.Count -ne 1) { throw 'No private workspace SID is available for this restricted test token.' }
        $systemSid = $capability[0].IdentityReference.Value
    }
    $parent = Join-Path $Repository '.tools\provisioning-fixtures'
    $root = Join-Path $parent ([guid]::NewGuid().ToString('N'))
    # Precreate ancestors without applying the private store's ACL to shared paths.
    [IO.Directory]::CreateDirectory($root) | Out-Null
    [pscustomobject]@{ Root = $root; Parent = $parent; SystemSid = $systemSid }
}

function Remove-ProvisioningFixture($Context) {
    $parent = [IO.Path]::GetFullPath($Context.Parent).TrimEnd('\') + '\'
    $root = [IO.Path]::GetFullPath($Context.Root)
    if (!$root.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($root) -cnotmatch '\A[a-f0-9]{32}\z' -or
        [IO.Path]::GetDirectoryName($root) + '\' -ine $parent) { throw 'Unsafe fixture cleanup path.' }
    function Assert-FixtureTree([string]$Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture cleanup refuses reparse points.' }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $Path -Force) { Assert-FixtureTree $child.FullName }
        }
    }
    if (Test-Path -LiteralPath $root) {
        Assert-FixtureTree $root
        Remove-Item -LiteralPath $root -Recurse -Force
    }
    if (Test-Path -LiteralPath $root) { throw 'Fixture cleanup did not remove the isolated directory.' }
}
