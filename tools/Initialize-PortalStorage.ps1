#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string]$AppPoolName = 'DeploymentPortal',
    [switch]$ProvisionCdStaging
)
$ErrorActionPreference = 'Stop'
$pool = ([Security.Principal.NTAccount]::new("IIS AppPool\$AppPoolName")).Translate([Security.Principal.SecurityIdentifier])
$admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
function New-PrivateAcl([bool]$Directory) {
    if ($Directory) { $acl = [Security.AccessControl.DirectorySecurity]::new() }
    else { $acl = [Security.AccessControl.FileSecurity]::new() }
    $acl.SetOwner($admins)
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]::None
    if ($Directory) { $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' }
    foreach ($sid in @($admins,$system)) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl',$inherit,'None','Allow')) }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($pool,'Modify',$inherit,'None','Allow'))
    return $acl
}
function Assert-NoLinks([string]$Path) {
    $probe = $Path
    while ($probe) {
        if (Test-Path -LiteralPath $probe) {
            if ((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Storage paths must not contain reparse points.' }
        }
        $probe = Split-Path -Path $probe -Parent
    }
}
$keyRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'DeploymentPortal\DataProtectionKeys'
$roots = @($keyRoot)
if ($ProvisionCdStaging) { $roots += 'C:\DeploymentPortalData\CdStaging' }
foreach ($root in $roots) {
    Assert-NoLinks $root
    if (Test-Path -LiteralPath $root) {
        foreach ($entry in Get-ChildItem -LiteralPath $root -Force) {
            Assert-NoLinks $entry.FullName
            if ($root -eq $keyRoot -and ($entry.PSIsContainer -or $entry.Extension -ne '.xml')) { throw 'Key directory contains unexpected content; administrator inspection is required.' }
            if ($root -ne $keyRoot -and (!$entry.PSIsContainer -or $entry.Name -notmatch '^[0-9a-fA-F]{32}$')) { throw 'Staging directory contains unrelated content; administrator inspection is required.' }
        }
    } else { [IO.Directory]::CreateDirectory($root,(New-PrivateAcl $true)) | Out-Null }
    Set-Acl -LiteralPath $root -AclObject (New-PrivateAcl $true)
    if ($root -eq $keyRoot) {
        # Preserve all keys and revocations; restrict existing XML files without reading their contents.
        foreach ($file in Get-ChildItem -LiteralPath $root -File -Filter '*.xml' -Force) {
            Set-Acl -LiteralPath $file.FullName -AclObject (New-PrivateAcl $false)
        }
    }
}
Write-Host 'Private portal storage provisioned. No application settings, credentials, CD flags or IIS identity were changed.'
