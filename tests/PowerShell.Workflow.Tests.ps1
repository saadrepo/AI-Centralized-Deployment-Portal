# Filesystem integration tests: real copies/backups/rollback, with IIS commands mocked.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $repo ('.tools\ps-tests\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$global:PoolState = 'Started'
$global:FailDeployCopy = $false
$global:FailRestoreCopy = $false
$global:FailBackupCopy = $false
$global:FailIisCreate = $false
$global:SitePresent = $true
$global:AppPresent = $true
$global:PoolPresent = $true
$global:PreviewPass = $false
$global:ActiveSource = ''
$global:ActiveTarget = ''
$global:ActiveBackup = ''
Add-Type @'
using System;
using System.Collections.Generic;
public class FakeDirectory { public string Path = "/"; public string PhysicalPath; }
public class FakeDirectories : List<FakeDirectory> { public FakeDirectory this[string path] { get { return Find(x => x.Path == path); } } }
public class FakeApplication { public string Path = "/portal"; public string ApplicationPoolName = "PortalPool"; public FakeDirectories VirtualDirectories = new FakeDirectories(); }
public class FakeApplications : List<FakeApplication> { public FakeApplication this[string path] { get { return Find(x => x.Path == path); } } }
public class FakePool { public string Name = "PortalPool"; }
public class FakePools : List<FakePool> { public FakePool this[string name] { get { return Find(x => x.Name == name); } } }
public class FakeSite { public int Id = 1; public string[] Bindings = new string[0]; public string Name = "TestSite"; public FakeApplications Applications = new FakeApplications(); }
public class FakeSites : List<FakeSite> { public FakeSite this[string name] { get { return Find(x => x.Name == name); } } }
public class FakeManager : IDisposable { public FakePools ApplicationPools = new FakePools(); public FakeSites Sites = new FakeSites(); public void Dispose() {} }
'@
function Import-Module { param($Name, $ErrorAction) if ($Name -ne 'WebAdministration') { throw 'Unexpected module' } }
function Add-Type { param($Path) if (!$Path.EndsWith('Microsoft.Web.Administration.dll')) { throw 'Unexpected assembly' } }
function New-Object {
    param([Parameter(Position=0)]$TypeName, [Parameter(Position=1)]$ArgumentList)
    if ($TypeName -eq 'Microsoft.Web.Administration.ServerManager') {
        $manager = [FakeManager]::new(); $site = [FakeSite]::new(); $application = [FakeApplication]::new()
        $directory = [FakeDirectory]::new(); $directory.PhysicalPath = $global:ActiveTarget
        $application.VirtualDirectories.Add($directory)
        if ($global:AppPresent) { $site.Applications.Add($application) }
        if ($global:SitePresent) { $manager.Sites.Add($site) }
        if ($global:PoolPresent) { $manager.ApplicationPools.Add([FakePool]::new()) }
        return $manager
    }
    if ($null -ne $ArgumentList) { return Microsoft.PowerShell.Utility\New-Object -TypeName $TypeName -ArgumentList $ArgumentList }
    return Microsoft.PowerShell.Utility\New-Object -TypeName $TypeName
}
function Get-Website { if ($global:SitePresent) { [pscustomobject]@{ Name = 'TestSite'; State = 'Started' } } }
function New-WebAppPool { param($Name) $global:PoolPresent = $true }
function Set-ItemProperty { param($LiteralPath,$Name,$Value) }
function New-Website { param($Name,$Port,$HostHeader,$PhysicalPath,$ApplicationPool) $global:SitePresent = $true }
function New-WebApplication { param($Site,$Name,$PhysicalPath,$ApplicationPool) if ($global:FailIisCreate) { throw 'Injected IIS failure' }; $global:AppPresent = $true }
function Remove-Website { param($Name) $global:SitePresent = $false; $global:AppPresent = $false }
function Remove-WebApplication { param($Site,$Name) $global:AppPresent = $false }
function Remove-WebAppPool { param($Name) $global:PoolPresent = $false }
function Start-Website { param($Name) }
function Get-WebAppPoolState { param($Name) [pscustomobject]@{ Value = $global:PoolState } }
function Stop-WebAppPool { param($Name) $global:PoolState = 'Stopped' }
function Start-WebAppPool { param($Name) $global:PoolState = 'Started' }
function Copy-Item {
    param($LiteralPath, $Destination, [switch]$Recurse, [switch]$Force)
    if ($global:FailDeployCopy -and $LiteralPath.StartsWith($global:ActiveSource + '\')) { throw 'Injected package copy failure' }
    if ($global:FailBackupCopy -and $Destination -eq $global:ActiveBackup) { throw 'Injected backup failure' }
    if ($global:FailRestoreCopy -and $LiteralPath.StartsWith($global:ActiveBackup + '\') -and $Destination -eq $global:ActiveTarget) { throw 'Injected rollback failure' }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Recurse:$Recurse -Force:$Force
}
function Assert-True($Value, [string]$Message) { if (!$Value) { throw "FAIL: $Message" } }
foreach ($scenario in @('success', 'rollback', 'rollback-failed', 'overlapping-paths', 'backup-failed', 'source-junction', 'create-site', 'create-application', 'stale-approval', 'missing-webconfig', 'create-config-failed')) {
    $global:SitePresent = $scenario -notin @('create-site','create-config-failed'); $global:AppPresent = $scenario -notin @('create-site','create-application','create-config-failed'); $global:PoolPresent = $global:AppPresent
    $global:FailIisCreate = $scenario -eq 'create-config-failed'
    $caseRoot = Join-Path $fixture $scenario
    $sourceRoot = Join-Path $caseRoot 'packages'; $targetRoot = Join-Path $caseRoot 'apps'; $backupRoot = Join-Path $caseRoot 'backups'
    $global:ActiveSource = Join-Path $sourceRoot 'release'; $global:ActiveTarget = Join-Path $targetRoot 'portal'; $global:ActiveBackup = Join-Path $backupRoot 'retained'
    New-Item -ItemType Directory -Path $global:ActiveSource, $global:ActiveTarget, $backupRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $global:ActiveSource 'web.config') -Value 'new release'
    Set-Content -LiteralPath (Join-Path $global:ActiveTarget 'web.config') -Value 'old release'
    Set-Content -LiteralPath (Join-Path $global:ActiveTarget 'old-only.txt') -Value 'retained in backup'
    $global:FailDeployCopy = $scenario -in @('rollback','rollback-failed'); $global:FailRestoreCopy = $scenario -eq 'rollback-failed'; $global:PoolState = 'Started'
    $global:FailBackupCopy = $scenario -eq 'backup-failed'
    if ($scenario -eq 'source-junction') {
        $outside = Join-Path $caseRoot 'outside'
        New-Item -ItemType Directory -Path $outside | Out-Null
        Set-Content -LiteralPath (Join-Path $outside 'sentinel.txt') -Value 'untouched'
        New-Item -ItemType Junction -Path (Join-Path $global:ActiveSource 'linked') -Target $outside | Out-Null
    }
    $parameters = @{ SiteName = 'TestSite'; ApplicationName = 'portal'; AppPoolName = 'PortalPool'; SourcePath = $global:ActiveSource;
        TargetPath = $global:ActiveTarget; BackupPath = $global:ActiveBackup; BackupRoot = $backupRoot; RemoteInvocation = $true }
    if ($scenario -eq 'overlapping-paths') { $parameters.SourcePath = $global:ActiveTarget }
    if ($scenario -eq 'missing-webconfig') { Remove-Item -LiteralPath (Join-Path $global:ActiveSource 'web.config') }
    $preview = & (Join-Path $repo 'src\DeploymentPortal\PowerShell\Deploy-IISApplication.ps1') @parameters -PreviewOnly $true
    Assert-True ((Get-Content (Join-Path $global:ActiveTarget 'web.config')) -eq 'old release') 'Preview must not change files'
    $parameters.ExpectedState = $preview.StateFingerprint
    if ($scenario -eq 'stale-approval') { $parameters.ExpectedState = 'not-approved' }
    $result = & (Join-Path $repo 'src\DeploymentPortal\PowerShell\Deploy-IISApplication.ps1') @parameters
    if ($scenario -in @('success','create-site','create-application')) {
        Assert-True $result.Success $result.Message
        Assert-True ((Get-Content (Join-Path $global:ActiveTarget 'web.config')) -eq 'new release') 'New files deployed'
        Assert-True (!(Test-Path (Join-Path $global:ActiveTarget 'old-only.txt'))) 'Obsolete files removed'
    } else {
        Assert-True (!$result.Success) 'Failure must be reported'
        if ($scenario -in @('rollback','create-config-failed')) {
            Assert-True ($result.Message -match 'Rollback: Succeeded') $result.Message
            Assert-True ((Get-Content (Join-Path $global:ActiveTarget 'web.config')) -eq 'old release') 'Previous files restored'
        }
        if ($scenario -eq 'rollback-failed') { Assert-True ($result.Message -match 'Rollback: Failed') $result.Message }
        if ($scenario -eq 'overlapping-paths') {
            Assert-True ((Get-Content (Join-Path $global:ActiveTarget 'web.config')) -eq 'old release') 'Invalid target untouched'
            Assert-True (!(Test-Path $global:ActiveBackup)) 'No backup created for rejected input'
        }
        if ($scenario -in @('backup-failed','source-junction','stale-approval','missing-webconfig')) {
            Assert-True ((Get-Content (Join-Path $global:ActiveTarget 'web.config')) -eq 'old release') 'Existing target preserved'
        }
        if ($scenario -eq 'source-junction') { Assert-True ((Get-Content (Join-Path $outside 'sentinel.txt')) -eq 'untouched') 'Junction destination untouched' }
    }
    if ($scenario -in @('success','rollback','rollback-failed','create-site','create-application')) { Assert-True ((Get-Content (Join-Path $global:ActiveBackup 'web.config')) -eq 'old release') 'Backup retained' }
    if ($scenario -eq 'create-config-failed') { Assert-True (!$global:SitePresent -and !$global:PoolPresent) 'New IIS resources removed after failure' }
    if ($scenario -notin @('rollback-failed','create-config-failed')) { Assert-True ($global:PoolState -eq 'Started') 'Pool restarted' }
    Write-Output "PASS: $scenario"
}
