param([string]$Root)
# Filesystem integration tests: real copies/backups/rollback, with IIS commands mocked.
. (Join-Path $PSScriptRoot 'PowerShell\IisWorkflowFixture.ps1') -FixtureRoot $Root
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
