#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([ValidateNotNullOrEmpty()][string]$AppPoolName = 'DeploymentPortal')
$ErrorActionPreference = 'Stop'
$root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'DeploymentPortal\Diagnostics'
$probe = $root
while ($probe) {
    if (Test-Path -LiteralPath $probe) {
        if ((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Diagnostics path cannot contain reparse points.' }
    }
    $probe = Split-Path -Path $probe -Parent
}
$poolSid = ([Security.Principal.NTAccount]::new("IIS AppPool\$AppPoolName")).Translate([Security.Principal.SecurityIdentifier])
$admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
function New-DiagnosticsAcl([bool]$Directory) {
    if ($Directory) { $acl = [Security.AccessControl.DirectorySecurity]::new() }
    else { $acl = [Security.AccessControl.FileSecurity]::new() }
    $acl.SetOwner($admins)
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]::None
    if ($Directory) { $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' }
    foreach ($sid in @($admins, $system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, 'None', 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($poolSid, 'Modify', $inherit, 'None', 'Allow'))
    return $acl
}
$file = Join-Path $root 'demo-release-discovery.jsonl'
if ((Test-Path -LiteralPath $file) -and ((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Diagnostic file cannot be a link.' }
if (!(Test-Path -LiteralPath $root)) { [IO.Directory]::CreateDirectory($root, (New-DiagnosticsAcl $true)) | Out-Null }
Set-Acl -LiteralPath $root -AclObject (New-DiagnosticsAcl $true)
if (Test-Path -LiteralPath $file) { Set-Acl -LiteralPath $file -AclObject (New-DiagnosticsAcl $false) }
Write-Host 'Private diagnostics directory provisioned with Modify access for the specified IIS app pool.'
