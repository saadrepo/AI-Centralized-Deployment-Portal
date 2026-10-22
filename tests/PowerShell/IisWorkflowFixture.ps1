param([string]$FixtureRoot)
# Shared test-only IIS mocks; production scripts and security checks are unchanged.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixture = if ($FixtureRoot) { [IO.Path]::GetFullPath($FixtureRoot) } else { Join-Path $repo ('.tools\ps-tests\' + [guid]::NewGuid().ToString('N')) }
if (!$fixture.StartsWith([IO.Path]::GetFullPath($repo) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture must remain inside the repository.' }
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
