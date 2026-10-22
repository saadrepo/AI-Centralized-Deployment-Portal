#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Provision','Rotate','List','Revoke')][string]$Action,
    [ValidateSet('AzureDevOps','GitHub')][string]$Provider,
    [string]$Destination,
    [string]$Reference,
    [ValidateNotNullOrEmpty()][string]$AppPoolName = 'DeploymentPortal'
)
# No secret parameter. Never run this script from a web request or as the pool identity.
$ErrorActionPreference = 'Stop'
$secure = $null; $pointer = [IntPtr]::Zero; $chars = $null; $plain = $null; $temporary = $null
try {
    Add-Type -AssemblyName System.Security
    $root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'DeploymentPortal\SourceCredentials'
    $poolSid = ([Security.Principal.NTAccount]::new("IIS AppPool\$AppPoolName")).Translate([Security.Principal.SecurityIdentifier])
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    function Assert-Path([string]$Path) {
        $probe = $Path
        while ($probe) {
            if (Test-Path -LiteralPath $probe) {
                if ((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe path.' }
            }
            $probe = Split-Path -Path $probe -Parent
        }
    }
    function New-PrivateAcl([bool]$Directory) {
        if ($Directory) { $acl = [Security.AccessControl.DirectorySecurity]::new() }
        else { $acl = [Security.AccessControl.FileSecurity]::new() }
        $acl.SetOwner($admins); $acl.SetAccessRuleProtection($true, $false)
        $inherit = [Security.AccessControl.InheritanceFlags]::None
        if ($Directory) { $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' }
        foreach ($sid in @($admins,$system)) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl',$inherit,'None','Allow')) }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($poolSid,'ReadAndExecute',$inherit,'None','Allow'))
        return $acl
    }
    function Assert-Acl([string]$Path) {
        Assert-Path $Path
        $acl = Get-Acl -LiteralPath $Path
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
        if (!$acl.AreAccessRulesProtected -or (!$owner.Equals($admins) -and !$owner.Equals($system))) { throw 'Unsafe owner or inheritance.' }
        $readable = $false
        foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -ne 'Allow') { continue }
            if ($rule.IdentityReference.Equals($admins) -or $rule.IdentityReference.Equals($system)) { continue }
            if (!$rule.IdentityReference.Equals($poolSid) -or ($rule.FileSystemRights -band (-bnot [int][Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize')) -ne 0) { throw 'Unsafe access.' }
            if (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq [Security.AccessControl.FileSystemRights]::ReadAndExecute -and !($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly)) { $readable = $true }
        }
        if (!$readable) { throw 'Pool read access missing.' }
    }
    function Canonical([string]$Kind,[string]$Url) {
        if ($Kind -ceq 'AzureDevOps') { $pattern = '\Ahttps://dev\.azure\.com/([A-Za-z0-9][A-Za-z0-9-]{0,99})/?\z' }
        elseif ($Kind -ceq 'GitHub') { $pattern = '\Ahttps://api\.github\.com/orgs/([A-Za-z0-9][A-Za-z0-9-]{0,38})/?\z' }
        else { throw 'Invalid provider.' }
        if ($Url -notmatch $pattern) { throw 'Invalid organization URL.' }
        return $Url.TrimEnd('/').ToLowerInvariant() + '/'
    }
    function Read-Record([string]$Id) {
        if ($Id -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Invalid reference.' }
        $path = Join-Path $root ($Id + '.json')
        Assert-Acl $path
        $file = Get-Item -LiteralPath $path
        if ($file.PSIsContainer -or $file.Length -lt 1 -or $file.Length -gt 65536) { throw 'Invalid record.' }
        $record = [IO.File]::ReadAllText($path) | ConvertFrom-Json
        if ($record.Reference -cne $Id -or $record.Destination -cne (Canonical $record.Provider $record.Destination)) { throw 'Invalid binding.' }
        $binding = [Text.Encoding]::UTF8.GetBytes("DeploymentPortal.SourceToken.v1`n$Id`n$($record.Provider)`n$($record.Destination)")
        $probe = $null
        try {
            $probe = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($record.Ciphertext), $binding, [Security.Cryptography.DataProtectionScope]::LocalMachine)
            if ($probe.Length -lt 1 -or $probe.Length -gt 4096) { throw 'Invalid credential.' }
            foreach ($b in $probe) { if ($b -lt 33 -or $b -gt 126) { throw 'Invalid credential.' } }
        } finally { if ($probe) { [Array]::Clear($probe,0,$probe.Length) } }
        return $record
    }
    Assert-Path $root
    if (!(Test-Path -LiteralPath $root)) {
        if ($Action -ne 'Provision') { throw 'Store unavailable.' }
        # Create ancestors first, then create the store with a private ACL before writing anything.
        [IO.Directory]::CreateDirectory((Split-Path $root -Parent)) | Out-Null
        [IO.Directory]::CreateDirectory($root,(New-PrivateAcl $true)) | Out-Null
    }
    Assert-Acl $root # Never repair or trust a previously insecure store.
    if ($Action -eq 'List') {
        foreach ($file in Get-ChildItem -LiteralPath $root -Filter '*.json' -Force) {
            $record = Read-Record $file.BaseName
            [pscustomobject]@{ Reference=$record.Reference; Provider=$record.Provider; Destination=$record.Destination; UpdatedUtc=$record.UpdatedUtc }
        }
        return
    }
    if ($Action -eq 'Revoke') {
        $null = Read-Record $Reference
        $target = [IO.Path]::GetFullPath((Join-Path $root ($Reference + '.json')))
        if ([IO.Path]::GetDirectoryName($target) -cne [IO.Path]::GetFullPath($root)) { throw 'Unsafe target.' }
        Remove-Item -LiteralPath $target -Force
        Write-Output 'Credential revoked.'
        return
    }
    $Destination = Canonical $Provider $Destination
    if ($Action -eq 'Provision') {
        if ($Reference) { throw 'Provision generates references.' }
        $random = New-Object byte[] 32
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($random) } finally { $rng.Dispose() }
        $Reference = ([BitConverter]::ToString($random)).Replace('-','').ToLowerInvariant()
    } else {
        $existing = Read-Record $Reference
        if ($existing.Provider -cne $Provider -or $existing.Destination -cne $Destination) { throw 'Rotation cannot change binding.' }
    }
    $binding = [Text.Encoding]::UTF8.GetBytes("DeploymentPortal.SourceToken.v1`n$Reference`n$Provider`n$Destination")
    $secure = Read-Host 'Source PAT/token (input hidden)' -AsSecureString
    if ($secure.Length -lt 1 -or $secure.Length -gt 4096) { throw 'Invalid token length.' }
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secure)
    $chars = New-Object char[] $secure.Length
    [Runtime.InteropServices.Marshal]::Copy($pointer,$chars,0,$chars.Length)
    foreach ($c in $chars) { if ([int]$c -lt 33 -or [int]$c -gt 126) { throw 'Invalid token characters.' } }
    $plain = [Text.Encoding]::ASCII.GetBytes($chars)
    $encrypted = [Security.Cryptography.ProtectedData]::Protect($plain,$binding,[Security.Cryptography.DataProtectionScope]::LocalMachine)
    $record = [ordered]@{ Reference=$Reference; Provider=$Provider; Destination=$Destination; UpdatedUtc=[DateTime]::UtcNow.ToString('o'); Ciphertext=[Convert]::ToBase64String($encrypted) }
    $target = Join-Path $root ($Reference + '.json')
    $temporary = Join-Path $root ([guid]::NewGuid().ToString('N') + '.tmp')
    # Inherits only the verified private directory ACL; ciphertext is never written elsewhere.
    [IO.File]::WriteAllText($temporary,($record | ConvertTo-Json -Compress))
    Set-Acl -LiteralPath $temporary -AclObject (New-PrivateAcl $false)
    Assert-Acl $temporary
    if ($Action -eq 'Rotate') { Assert-Acl $target; [IO.File]::Replace($temporary,$target,[System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary,$target) }
    $temporary = $null
    Write-Output ([pscustomobject]@{ Reference=$Reference; Provider=$Provider; Destination=$Destination; UpdatedUtc=$record.UpdatedUtc })
} catch {
    throw 'Source credential operation failed. Verify elevation, provider, organization binding, reference and private store permissions. Secret details are suppressed.'
} finally {
    if ($chars) { [Array]::Clear($chars,0,$chars.Length) }
    if ($plain) { [Array]::Clear($plain,0,$plain.Length) }
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer) }
    if ($secure) { $secure.Dispose() }
    if ($temporary -and (Test-Path -LiteralPath $temporary)) { Remove-Item -LiteralPath $temporary -Force }
}
