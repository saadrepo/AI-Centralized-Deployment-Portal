#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://netserver:8080/tfs/DefaultCollection',
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Username,
    [ValidateNotNullOrEmpty()][string]$AppPoolName = 'DeploymentPortal'
)
$ErrorActionPreference = 'Stop'
$secure = $null; $pointer = [IntPtr]::Zero; $plainBytes = $null; $passwordBytes = $null; $chars = $null
try {
    $uri = $null
    if (![Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'Invalid collection.' }
    $approvedHttp = $uri.Scheme -eq 'http' -and $uri.Host -eq 'netserver' -and $uri.Port -eq 8080 -and
        ($uri.AbsolutePath -ceq '/tfs/DefaultCollection' -or $uri.AbsolutePath -ceq '/tfs/DefaultCollection/')
    if ($uri.Scheme -ne 'https' -and !$approvedHttp) { throw 'Unapproved collection.' }
    if ([string]::IsNullOrWhiteSpace($Username) -or $Username -match '[\x00-\x1f\x7f-\x9f]' -or
        ($Username.Contains('\') -and $Username -notmatch '^[^\\]+\\[^\\]+$')) { throw 'Invalid Windows username.' }
    $collection = [Uri]::new($BaseUrl.TrimEnd('/') + '/')
    $binding = [Text.Encoding]::UTF8.GetBytes("DeploymentPortal.TfsGit.Windows.v1`n$($collection.AbsoluteUri)")
    Add-Type -AssemblyName System.Security
    $root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'DeploymentPortal\TfsGitCredentials'
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
    if (!(Test-Path -LiteralPath $root)) { [IO.Directory]::CreateDirectory($root, (New-PrivateAcl $true)) | Out-Null }
    Set-Acl -LiteralPath $root -AclObject (New-PrivateAcl $true)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $name = ([BitConverter]::ToString($hash.ComputeHash($binding))).Replace('-', '').ToLowerInvariant() + '.dpapi' }
    finally { $hash.Dispose() }
    $destination = Join-Path $root $name
    if (Test-Path -LiteralPath $destination) {
        if ((Get-Item -LiteralPath $destination -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Credential file cannot be a link.' }
        Set-Acl -LiteralPath $destination -AclObject (New-PrivateAcl $false)
    }
    $secure = Read-Host 'TFS Windows account password' -AsSecureString
    if ($secure.Length -eq 0) { throw 'Password cannot be empty.' }
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secure)
    $chars = New-Object char[] $secure.Length
    [Runtime.InteropServices.Marshal]::Copy($pointer, $chars, 0, $chars.Length)
    $passwordBytes = [Text.Encoding]::UTF8.GetBytes($chars)
    $usernameBytes = [Text.Encoding]::UTF8.GetBytes($Username)
    # Username + NUL + password, encrypted together. No plaintext JSON or password strings.
    $plainBytes = New-Object byte[] ($usernameBytes.Length + 1 + $passwordBytes.Length)
    [Array]::Copy($usernameBytes, 0, $plainBytes, 0, $usernameBytes.Length)
    [Array]::Copy($passwordBytes, 0, $plainBytes, $usernameBytes.Length + 1, $passwordBytes.Length)
    $encrypted = [Security.Cryptography.ProtectedData]::Protect($plainBytes, $binding, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    if ($encrypted.Length -gt 65536) { throw 'Credential is too large.' }
    $temporary = Join-Path $root (([guid]::NewGuid().ToString('N')) + '.tmp')
    try {
        [IO.File]::WriteAllBytes($temporary, $encrypted)
        Set-Acl -LiteralPath $temporary -AclObject (New-PrivateAcl $false)
        if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($temporary, $destination, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $destination) }
        Set-Acl -LiteralPath $destination -AclObject (New-PrivateAcl $false)
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    Write-Host 'Encrypted TFS collection credential provisioned. Recycle the portal pool and verify CI builds under IIS.'
} catch {
    throw 'TFS credential provisioning failed. Verify the approved collection, Windows username, administrator rights and IIS pool identity. No credential details are reported.'
} finally {
    if ($chars) { [Array]::Clear($chars, 0, $chars.Length) }
    if ($passwordBytes) { [Array]::Clear($passwordBytes, 0, $passwordBytes.Length) }
    if ($plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer) }
    if ($secure) { $secure.Dispose() }
}
