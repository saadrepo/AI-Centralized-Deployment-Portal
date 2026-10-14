#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidatePattern('^[a-zA-Z0-9.:-]+$')][string]$HostName = 'deployment-test.vitonta.com',
    [ValidateNotNullOrEmpty()][string]$Username = 'WINDOWSERVERTES\saad.bilal',
    [ValidateRange(1,65535)][int]$Port = 5985,
    [switch]$UseSsl,
    [ValidateNotNullOrEmpty()][string]$AppPoolName = 'DeploymentPortal'
)
$ErrorActionPreference = 'Stop'
$secure = $null; $pointer = [IntPtr]::Zero; $plainBytes = $null
try {
    if ($Username -match '[\x00-\x1f\x7f]') { throw 'Invalid username.' }
    Add-Type -AssemblyName System.Security
    $root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'DeploymentPortal\WinRmCredentials'
    # Do not follow junctions/symlinks, including any existing ancestor.
    $probe = $root
    while ($probe) {
        if (Test-Path -LiteralPath $probe) {
            if ((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Credential store cannot contain reparse points.' }
        }
        $probe = Split-Path -Path $probe -Parent
    }
    $poolSid = ([Security.Principal.NTAccount]::new("IIS AppPool\$AppPoolName")).Translate([Security.Principal.SecurityIdentifier])
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    function New-PrivateAcl([bool]$Directory) {
        if ($Directory) { $acl = [Security.AccessControl.DirectorySecurity]::new() }
        else { $acl = [Security.AccessControl.FileSecurity]::new() }
        $acl.SetOwner($admins)
        $acl.SetAccessRuleProtection($true, $false)
        $inherit = [Security.AccessControl.InheritanceFlags]::None
        if ($Directory) { $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' }
        foreach ($sid in @($admins, $system)) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, 'None', 'Allow'))
        }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($poolSid, 'ReadAndExecute', $inherit, 'None', 'Allow'))
        return $acl
    }
    if (!(Test-Path -LiteralPath $root)) {
        # Create with the private DACL, before any encrypted material is written.
        [IO.Directory]::CreateDirectory($root, (New-PrivateAcl $true)) | Out-Null
    }
    Set-Acl -LiteralPath $root -AclObject (New-PrivateAcl $true)
    $scheme = if ($UseSsl) { 'https' } else { 'http' }
    $binding = [Text.Encoding]::UTF8.GetBytes("DeploymentPortal.WinRm.v1`n$($HostName.ToLowerInvariant())`n$($Username.ToLowerInvariant())`n$Port`n$scheme")
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $name = ([BitConverter]::ToString($hash.ComputeHash($binding))).Replace('-', '').ToLowerInvariant() + '.dpapi' }
    finally { $hash.Dispose() }
    $destination = Join-Path $root $name
    if (Test-Path -LiteralPath $destination) {
        if ((Get-Item -LiteralPath $destination -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Credential file cannot be a link.' }
        Set-Acl -LiteralPath $destination -AclObject (New-PrivateAcl $false)
    }
    $secure = Read-Host "Password for $Username on $HostName" -AsSecureString
    if ($secure.Length -eq 0) { throw 'Password cannot be empty.' }
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secure)
    # Copy directly into chars; no plaintext password string, file, log or argument.
    $chars = New-Object char[] $secure.Length
    [Runtime.InteropServices.Marshal]::Copy($pointer, $chars, 0, $chars.Length)
    try { $plainBytes = [Text.Encoding]::UTF8.GetBytes($chars) }
    finally { [Array]::Clear($chars, 0, $chars.Length) }
    $encrypted = [Security.Cryptography.ProtectedData]::Protect($plainBytes, $binding, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    # Same-directory atomic replacement; temporary file holds only ciphertext.
    $temporary = Join-Path $root (([guid]::NewGuid().ToString('N')) + '.tmp')
    try {
        [IO.File]::WriteAllBytes($temporary, $encrypted)
        Set-Acl -LiteralPath $temporary -AclObject (New-PrivateAcl $false)
        if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($temporary, $destination, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $destination) }
        Set-Acl -LiteralPath $destination -AclObject (New-PrivateAcl $false)
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    Write-Host 'Encrypted credential provisioned. Verify through the portal Test Connection under the IIS identity.'
} catch {
    throw 'Credential provisioning failed. Verify administrator rights, the IIS pool identity and private store permissions. No password details are reported.'
} finally {
    if ($plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer) }
    if ($secure) { $secure.Dispose() }
}
