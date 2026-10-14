[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SiteName,
    [Parameter(Mandatory)][string]$ApplicationName,
    [AllowEmptyString()][string]$AppPoolName,
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$TargetPath,
    [Parameter(Mandatory)][string]$BackupPath,
    [Parameter(Mandatory)][string]$BackupRoot,
    [bool]$RemoteInvocation = $false,
    [bool]$PreviewOnly = $false,
    [AllowEmptyString()][string]$ExpectedState = "",
    [int]$SitePort = 8080,
    [AllowEmptyString()][string]$SiteHostName = "",
    $CdPackage = $null,
    [AllowEmptyString()][string]$CdSecurityFunctions = ""
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$stage = 'validation'
$backupReady = $false
$modified = $false
$poolStopped = $false
$poolWasStarted = $false
$rollback = 'Not required'
$mutex = $null
$ownsMutex = $false
$success = $false
$message = ''
$cdVerified = $false
$verification = ''
if ($CdPackage) { . ([scriptblock]::Create($CdSecurityFunctions)) }

function Normalize-LocalPath([string]$Value) {
    if ($Value -notmatch '^[A-Za-z]:\\' -or $Value -match '[*?"<>|/]' -or $Value.Substring(2).Contains(':')) { Stop-Deployment 'Invalid local path.' }
    $clean = $Value.TrimEnd('\')
    foreach ($segment in $clean.Substring(3).Split('\')) {
        if (!$segment -or $segment -in @('.', '..') -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match '[\x00-\x1F]' -or $segment -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { Stop-Deployment 'Invalid path segment.' }
    }
    return [IO.Path]::GetFullPath($clean).TrimEnd('\')
}
function Assert-Child([string]$Path, [string]$Root) {
    if (!$Path.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { Stop-Deployment 'Path outside allowed root.' }
}
function Assert-NoLinks([string]$Path) {
    $current = $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { Stop-Deployment 'Reparse points are not permitted.' }
        }
        $current = Split-Path -Path $current -Parent
    }
    if (Test-Path -LiteralPath $Path) {
        # Inspect each directory before descending; never traverse a junction.
        $pending = New-Object 'System.Collections.Generic.Stack[string]'
        $pending.Push($Path)
        while ($pending.Count -gt 0) {
            foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Stop-Deployment 'Reparse points are not permitted.' }
                if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            }
        }
    }
}
function Copy-Contents([string]$From, [string]$To) {
    foreach ($item in Get-ChildItem -LiteralPath $From -Force) { Copy-Item -LiteralPath $item.FullName -Destination $To -Recurse -Force }
}
function Clear-Target {
    if ($TargetPath -ne $approvedTarget) { Stop-Deployment 'Target changed unexpectedly.' }
    Assert-SafePaths
    Assert-NoLinks $TargetPath
    foreach ($item in Get-ChildItem -LiteralPath $TargetPath -Force) {
        Assert-Child ([IO.Path]::GetFullPath($item.FullName)) $TargetPath
        Remove-Item -LiteralPath $item.FullName -Recurse -Force
    }
}
function Wait-Pool([string]$Expected) {
    for ($i = 0; $i -lt 60; $i++) {
        if ((Get-WebAppPoolState -Name $AppPoolName).Value -eq $Expected) { return }
        Start-Sleep -Milliseconds 500
    }
    Stop-Deployment 'Application pool state timeout.'
}

function Stop-Deployment([string]$Reason) {
    $error = New-Object System.InvalidOperationException($Reason)
    $error.Data['PortalReason'] = $Reason
    throw $error
}
function Get-Hash([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Assert-SafePaths {
    $paths = @($SourcePath, $TargetPath, $BackupRoot)
    for ($i = 0; $i -lt 3; $i++) {
        # Protect OS directories on any drive; also check the actual OS installation path.
        if ($paths[$i].Substring(3) -match '^(Windows|Program Files|Program Files \(x86\)|ProgramData|\$Recycle.Bin|System Volume Information)(\\|$)' -or
            $paths[$i].Substring(3) -match '^Users(\\[^\\]+)?$' -or
            $paths[$i].Equals($env:windir, [StringComparison]::OrdinalIgnoreCase) -or
            $paths[$i].StartsWith($env:windir + '\', [StringComparison]::OrdinalIgnoreCase)) { Stop-Deployment 'Use an application directory, not a protected Windows or user-profile directory.' }
        for ($j = $i + 1; $j -lt 3; $j++) {
            if ($paths[$i].Equals($paths[$j], [StringComparison]::OrdinalIgnoreCase) -or $paths[$i].StartsWith($paths[$j] + '\', [StringComparison]::OrdinalIgnoreCase) -or $paths[$j].StartsWith($paths[$i] + '\', [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Deployment 'Source, target and backup directories must not be equal or nested inside one another.'
            }
        }
    }
    Assert-Child $BackupPath $BackupRoot
}
function Read-IISPlan {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$SiteName]
        $app = if ($null -ne $site) { $site.Applications[$applicationPath] } else { $null }
        $mode = if ($null -eq $site) { 'CreateSite' } elseif ($null -eq $app) { 'CreateApplication' } else { 'Update' }
        $pool = if ($app) { $app.ApplicationPoolName } elseif ($AppPoolName) { $AppPoolName } else { 'DeployHQ-' + (Get-Hash ($SiteName.ToLowerInvariant() + $applicationPath.ToLowerInvariant())).Substring(0, 16) }
        if ($pool -notmatch '^[a-zA-Z0-9][a-zA-Z0-9 _.-]{0,79}$') { Stop-Deployment 'Unsupported pool name.' }
        if ($app) {
            $actual = Normalize-LocalPath ([Environment]::ExpandEnvironmentVariables($app.VirtualDirectories['/'].PhysicalPath))
            if (!$TargetPath.Equals($actual, [StringComparison]::OrdinalIgnoreCase)) { Stop-Deployment 'The application already exists. Enter its current IIS physical path as the target; moving an existing application is not supported.' }
            if ($AppPoolName -and $AppPoolName -ne $pool) { Stop-Deployment 'Application pool does not match the existing IIS application.' }
        }
        $references = @($manager.Sites | ForEach-Object { $_.Applications } | Where-Object ApplicationPoolName -EQ $pool)
        if (($app -and $references.Count -ne 1) -or (!$app -and $references.Count -gt 0)) { Stop-Deployment 'Use a dedicated application pool. The selected pool is shared with another application.' }
        foreach ($otherSite in $manager.Sites) {
            foreach ($otherApp in $otherSite.Applications) {
                foreach ($directory in $otherApp.VirtualDirectories) {
                    if ($otherSite.Name -eq $SiteName -and $otherApp.Path -eq $applicationPath -and $directory.Path -eq '/') { continue }
                    $other = [Environment]::ExpandEnvironmentVariables($directory.PhysicalPath).TrimEnd('\')
                    if ($other.Equals($TargetPath, [StringComparison]::OrdinalIgnoreCase) -or $other.StartsWith($TargetPath + '\', [StringComparison]::OrdinalIgnoreCase)) { Stop-Deployment 'Target contains or is shared by another IIS binding.' }
                }
            }
            if (!$site) {
                foreach ($binding in $otherSite.Bindings) {
                    # New sites bind all addresses. Reject overlapping HTTP host/port bindings.
                    if ($binding.Protocol -eq 'http' -and $binding.EndPoint.Port -eq $SitePort -and
                        (!$SiteHostName -or !$binding.Host -or $binding.Host -eq $SiteHostName)) { Stop-Deployment 'The new site HTTP port/hostname conflicts with an existing IIS binding. Choose another port or hostname.' }
                }
            }
        }
        $siteRoot = if (!$site -and $applicationPath -ne '/') { Join-Path $BackupRoot ('SiteRoots\' + (Get-Hash $SiteName.ToLowerInvariant()).Substring(0, 24)) } else { $TargetPath }
        if (!$site -and $applicationPath -ne '/') {
            Assert-NoLinks $siteRoot
            if (Test-Path -LiteralPath $siteRoot) { Stop-Deployment 'The reserved empty site-root folder already exists. Inspect it before retrying.' }
        }
        $rootPool = $pool + '-Root'
        if (!$site -and $applicationPath -ne '/' -and $null -ne $manager.ApplicationPools[$rootPool]) { Stop-Deployment 'The new site root pool name already exists. Choose a different application pool name.' }
        $targetExists = Test-Path -LiteralPath $TargetPath -PathType Container
        $hasFiles = $targetExists -and (@(Get-ChildItem -LiteralPath $TargetPath -Force).Count -gt 0)
        $bindings = if ($site) { @($site.Bindings | ForEach-Object { $_.Protocol + ':' + $_.BindingInformation } | Sort-Object) } else { @() }
        $state = [ordered]@{ Mode = $mode; SiteId = $(if ($site) { [string]$site.Id } else { '' }); Bindings = $bindings;
            Pool = $pool; PoolExists = ($null -ne $manager.ApplicationPools[$pool]); Target = $TargetPath; TargetExists = $targetExists; HasFiles = $hasFiles; SiteRoot = $siteRoot }
        $message = switch ($mode) {
            'CreateSite' { "Site does not exist. Create the IIS site, application and required pools, then deploy. New HTTP binding: *:$($SitePort):$SiteHostName." }
            'CreateApplication' { 'Site already exists. Approval will add the missing application and deploy it; existing site bindings will be retained.' }
            'Update' { 'Site and application already exist. Approve update to back up and replace this application''s files.' }
        }
        if ($hasFiles -and $mode -ne 'Update') { $message += ' The target folder contains files; approval also authorizes backing up and replacing them.' }
        if (!$site -and $applicationPath -ne '/') { $message += " An empty site root will be created at $siteRoot with a separate pool." }
        return [pscustomobject]@{ Mode = $mode; Fingerprint = (Get-Hash ($state | ConvertTo-Json -Compress -Depth 4)); Pool = $pool; PoolExists = $state.PoolExists;
            SiteRoot = $siteRoot; RootPool = $rootPool; TargetExists = $targetExists; Message = $message }
    } finally { if ($manager) { $manager.Dispose() } }
}

$plan = $null
$createdSite = $false
$createdApplication = $false
$createdPool = $false
$createdRootPool = $false
$createdSiteRoot = $false
$siteWasStarted = $false
$approvedTarget = ''
try {
    if ($SiteName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9 _.-]{0,79}$' -or $ApplicationName -notmatch '^([a-zA-Z0-9][a-zA-Z0-9_-]{0,79}|/)$' -or ($AppPoolName -and $AppPoolName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9 _.-]{0,79}$')) { Stop-Deployment 'Invalid IIS name.' }
    if ($SitePort -lt 1 -or $SitePort -gt 65535 -or ($SiteHostName -and $SiteHostName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.\-]{0,252}$')) { Stop-Deployment 'Invalid new-site port or hostname.' }
    $SourcePath = Normalize-LocalPath $SourcePath
    $TargetPath = Normalize-LocalPath $TargetPath
    $BackupRoot = Normalize-LocalPath $BackupRoot
    $BackupPath = Normalize-LocalPath $BackupPath
    $approvedTarget = $TargetPath
    Assert-SafePaths
    foreach ($path in @($SourcePath, $TargetPath, $BackupPath)) { Assert-NoLinks $path }
    if (!(Test-Path -LiteralPath $SourcePath -PathType Container)) { Stop-Deployment 'The package folder does not exist on the target server.' }
    if (!(Test-Path -LiteralPath (Join-Path $SourcePath 'web.config') -PathType Leaf)) { Stop-Deployment 'The package folder does not contain web.config. Select the publish output folder itself, not its parent or source-code folder.' }
    if ((Test-Path -LiteralPath $TargetPath) -and !(Test-Path -LiteralPath $TargetPath -PathType Container)) { Stop-Deployment 'The target must be a directory, not a file.' }
    if (Test-Path -LiteralPath $BackupPath) { Stop-Deployment 'Backup path already exists.' }
    $stage = 'IIS preflight'
    Import-Module WebAdministration -ErrorAction Stop
    Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
    $applicationPath = if ($ApplicationName -eq '/') { '/' } else { '/' + $ApplicationName }
    # Serializes the final state comparison, configuration changes and filesystem deployment.
    $mutex = New-Object Threading.Mutex($false, 'Global\DeploymentPortal-IIS')
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (!$ownsMutex) { Stop-Deployment 'Another deployment is active on this server.' }
    $plan = Read-IISPlan
    if ($CdPackage) {
        if ($plan.Mode -ne 'Update' -or !$plan.PoolExists -or !$plan.TargetExists) { Stop-Deployment 'CD requires an existing application, pool and target directory; creation is not supported.' }
        try { Assert-CdApprovedPackage $CdPackage $SourcePath $TargetPath }
        catch { Stop-Deployment 'CD package or preserved configuration changed. Prepare and Review again.' }
    }
    if ($PreviewOnly) {
        $success = $true; $message = $plan.Message
    } else {
        if (!$ExpectedState -or $ExpectedState -cne $plan.Fingerprint) { Stop-Deployment 'IIS or target-folder state changed, or approval is missing. Review and confirm a new deployment before any changes can run.' }
        $AppPoolName = $plan.Pool
        $siteWasStarted = $plan.Mode -ne 'CreateSite' -and (Get-Website | Where-Object Name -EQ $SiteName).State -eq 'Started'
        $poolWasStarted = $plan.PoolExists -and (Get-WebAppPoolState -Name $AppPoolName).Value -eq 'Started'
        if ($poolWasStarted) {
            $stage = 'stopping application pool'; $poolStopped = $true
            Stop-WebAppPool -Name $AppPoolName; Wait-Pool 'Stopped'
        }
        if ($plan.TargetExists) {
            $stage = 'backup'
            Assert-NoLinks $TargetPath; Assert-NoLinks $BackupPath
            New-Item -ItemType Directory -Path $BackupPath | Out-Null
            Copy-Contents $TargetPath $BackupPath
            $backupReady = $true
        } else { New-Item -ItemType Directory -Path $TargetPath | Out-Null }
        $stage = 'file deployment'
        if ($CdPackage) {
            try { Assert-CdApprovedPackage $CdPackage $SourcePath $TargetPath }
            catch { Stop-Deployment 'CD integrity changed before copying. Prepare and Review again.' }
        }
        Assert-NoLinks $SourcePath
        $modified = $true
        Clear-Target
        Copy-Contents $SourcePath $TargetPath
        $stage = 'IIS configuration'
        if (!$plan.PoolExists) {
            New-WebAppPool -Name $AppPoolName | Out-Null; $createdPool = $true
            Set-ItemProperty -LiteralPath ("IIS:\AppPools\" + $AppPoolName) -Name managedRuntimeVersion -Value ''
        }
        if ($plan.Mode -eq 'CreateSite') {
            if ($applicationPath -ne '/') {
                Assert-NoLinks $plan.SiteRoot
                New-Item -ItemType Directory -Path $plan.SiteRoot | Out-Null
                $createdSiteRoot = $true
                New-WebAppPool -Name $plan.RootPool | Out-Null; $createdRootPool = $true
                Set-ItemProperty -LiteralPath ("IIS:\AppPools\" + $plan.RootPool) -Name managedRuntimeVersion -Value ''
            }
            $sitePool = if ($applicationPath -eq '/') { $AppPoolName } else { $plan.RootPool }
            New-Website -Name $SiteName -Port $SitePort -HostHeader $SiteHostName -PhysicalPath $plan.SiteRoot -ApplicationPool $sitePool | Out-Null
            $createdSite = $true
        }
        if ($plan.Mode -ne 'Update' -and $applicationPath -ne '/') {
            New-WebApplication -Site $SiteName -Name $ApplicationName -PhysicalPath $TargetPath -ApplicationPool $AppPoolName | Out-Null
            $createdApplication = $true
        }
        $stage = 'verification'
        if ((Get-WebAppPoolState -Name $AppPoolName).Value -ne 'Started') { Start-WebAppPool -Name $AppPoolName }
        Wait-Pool 'Started'
        if ((Get-Website | Where-Object Name -EQ $SiteName).State -ne 'Started') { Start-Website -Name $SiteName }
        if ((Get-Website | Where-Object Name -EQ $SiteName).State -ne 'Started' -or !(Test-Path -LiteralPath (Join-Path $TargetPath 'web.config'))) { Stop-Deployment 'Final verification failed.' }
        if ($CdPackage) {
            if ((Get-CdManifestJson $TargetPath) -cne $CdPackage.EffectiveManifestJson -or (Get-CdConfigurationJson $TargetPath $CdPackage.PreservedConfigurationFiles) -cne $CdPackage.ConfigurationManifestJson) { Stop-Deployment 'CD deployed files or preserved configuration failed verification.' }
            $verifiedPlan = Read-IISPlan
            if ($verifiedPlan.Mode -ne 'Update' -or $verifiedPlan.Pool -ne $AppPoolName) { Stop-Deployment 'CD IIS application verification failed.' }
            $cdVerified = $true
            $verification = 'Selected IIS application, physical path, dedicated pool, started site/pool and deployed file/configuration hashes verified. HTTP health was not checked.'
        }
        $poolStopped = $false
        $success = $true
        $message = "Deployment successful ($($plan.Mode)). Files deployed and IIS started."
        if ($backupReady) { $message += ' Previous files retained in backup.' }
        $message += ' Application HTTP health was not checked.'
    }
} catch {
    $reason = 'Check IIS permissions, filesystem access, package contents and the selected HTTP binding.'
    if ($_.Exception.Data.Contains('PortalReason')) { $reason = [string]$_.Exception.Data['PortalReason'] }
    if ($modified) {
        try {
            if ($plan.PoolExists -or $createdPool) {
                if ((Get-WebAppPoolState -Name $AppPoolName).Value -ne 'Stopped') { Stop-WebAppPool -Name $AppPoolName }
                Wait-Pool 'Stopped'
            }
            if ($createdSite) { Remove-Website -Name $SiteName }
            elseif ($createdApplication) { Remove-WebApplication -Site $SiteName -Name $ApplicationName }
            if ($createdRootPool) { Remove-WebAppPool -Name $plan.RootPool }
            if ($createdPool) { Remove-WebAppPool -Name $AppPoolName }
            if ($createdSiteRoot) {
                Assert-Child $plan.SiteRoot $BackupRoot
                Assert-NoLinks $plan.SiteRoot
                if (@(Get-ChildItem -LiteralPath $plan.SiteRoot -Force).Count -eq 0) { Remove-Item -LiteralPath $plan.SiteRoot }
            }
            Clear-Target
            if ($backupReady) { Assert-NoLinks $BackupPath; Copy-Contents $BackupPath $TargetPath }
            if ($poolWasStarted) { Start-WebAppPool -Name $AppPoolName; Wait-Pool 'Started' }
            if (!$createdSite -and !$siteWasStarted -and (Get-Website | Where-Object Name -EQ $SiteName).State -eq 'Started') { Stop-Website -Name $SiteName }
            $poolStopped = $false
            $rollback = 'Succeeded; previous files and IIS pool state restored, newly created IIS objects removed. Empty created directories are retained.'
        } catch { $rollback = 'Failed; inspect IIS and restore the retained backup manually' }
    }
    $message = "Deployment failed during $stage. $reason Rollback: $rollback."
} finally {
    if ($poolStopped -and $poolWasStarted -and !$modified) {
        try { Start-WebAppPool -Name $AppPoolName; Wait-Pool 'Started' }
        catch { $message += ' Application pool could not be restarted; manual intervention is required.' }
    }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
$result = [pscustomobject]@{ Success = $success; Message = $message; ServerName = $env:COMPUTERNAME; OperatingSystem = ''; BackupPath = $(if ($backupReady) { $BackupPath } else { '' });
    StateFingerprint = $(if ($plan) { $plan.Fingerprint } else { '' }); DeploymentMode = $(if ($plan) { $plan.Mode } else { '' }); Verified = ($success -and $cdVerified); Verification = $verification }
if ($RemoteInvocation) { return $result }
$result | ConvertTo-Json -Compress
if (!$success) { exit 1 }
exit 0
