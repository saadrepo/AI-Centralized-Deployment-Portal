param([string]$Root, [string]$Scripts)
$ErrorActionPreference = 'Stop'
# Reuse the established IIS mocks and run all original manual filesystem scenarios first.
# No WinRM session, credentials or real IIS provider is used anywhere in this test.
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'PowerShell.Workflow.Tests.ps1')
. (Join-Path $Scripts 'CdPackageSecurity.ps1')
$helpers = [IO.File]::ReadAllText((Join-Path $Scripts 'CdPackageSecurity.ps1'))
# ACL enforcement is independently checked by security policy tests; these fixtures inherit
# workspace ACLs. This test-only override is never included in production scripts/configuration.
$helpers += "`nfunction Assert-CdPrivateRoot([string]`$Root) { Assert-CdNoLinks `$Root }`n"
function Copy-Item {
    param($LiteralPath, $Destination, [switch]$Recurse, [switch]$Force)
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Recurse:$Recurse -Force:$Force
    if ($global:CdCorruptCopy -and $LiteralPath -eq (Join-Path $global:ActiveSource 'app.dll') -and $Destination -eq $global:ActiveTarget) {
        [IO.File]::AppendAllText((Join-Path $Destination 'app.dll'),'corruption')
    }
}
foreach ($scenario in @('cd-success','cd-source-tamper','cd-settings-tamper','cd-target-only-change','cd-runtime-change','cd-create-site','cd-create-app','cd-missing-pool','cd-verification-rollback')) {
    $caseRoot = Join-Path $fixture $scenario
    $remoteRoot = Join-Path $caseRoot 'private'
    $prepId = [guid]::NewGuid()
    $global:ActiveSource = Join-Path (Join-Path $remoteRoot $prepId.ToString('N')) 'publish'
    $global:ActiveTarget = Join-Path $caseRoot 'target'
    $backupRoot = Join-Path $caseRoot 'backups'
    $global:ActiveBackup = Join-Path $backupRoot 'retained'
    New-Item -ItemType Directory -Path $global:ActiveSource,$global:ActiveTarget,$backupRoot | Out-Null
    $web='<configuration><system.webServer><handlers><add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" /></handlers><aspNetCore processPath="dotnet" arguments=".\app.dll" hostingModel="inprocess" /></system.webServer></configuration>'
    foreach ($directory in @($global:ActiveSource,$global:ActiveTarget)) {
        [IO.File]::WriteAllText((Join-Path $directory 'web.config'),$web)
        [IO.File]::WriteAllText((Join-Path $directory 'appsettings.json'),'{"Environment":"Production"}')
        [IO.File]::WriteAllText((Join-Path $directory 'app.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net10.0"}}')
    }
    [IO.File]::WriteAllText((Join-Path $global:ActiveSource 'app.dll'),'new binary')
    [IO.File]::WriteAllText((Join-Path $global:ActiveTarget 'app.dll'),'old binary')
    $configuration = Preserve-CdTargetConfiguration $global:ActiveSource $global:ActiveTarget @()
    $snapshot = [pscustomobject]@{ PreparationId=$prepId; Policy='PreserveExistingRequired.v1'; RemoteRoot=$remoteRoot; SourcePath=$global:ActiveSource;
        Provenance=[pscustomobject]@{ TargetPath=$global:ActiveTarget }; EffectiveManifestJson=(Get-CdManifestJson $global:ActiveSource);
        ConfigurationManifestJson=$configuration; ConfigurationDigest=(Get-CdHash $configuration); PreservedConfigurationFiles=@() }
    $global:SitePresent = $scenario -ne 'cd-create-site'
    $global:AppPresent = $scenario -notin @('cd-create-site','cd-create-app')
    $global:PoolPresent = $scenario -notin @('cd-create-site','cd-create-app','cd-missing-pool')
    $global:PoolState='Started'; $global:CdCorruptCopy=$scenario -eq 'cd-verification-rollback'
    $parameters = @{ SiteName='TestSite'; ApplicationName='portal'; AppPoolName='PortalPool'; SourcePath=$global:ActiveSource;
        TargetPath=$global:ActiveTarget; BackupPath=$global:ActiveBackup; BackupRoot=$backupRoot; RemoteInvocation=$true; CdPackage=$snapshot; CdSecurityFunctions=$helpers }
    $preview = & (Join-Path $Scripts 'Deploy-IISApplication.ps1') @parameters -PreviewOnly $true
    Assert-True (([IO.File]::ReadAllText((Join-Path $global:ActiveTarget 'app.dll'))) -ceq 'old binary') 'CD preview changed the target'
    if ($scenario -eq 'cd-source-tamper') { [IO.File]::AppendAllText((Join-Path $global:ActiveSource 'app.dll'),'tamper') }
    if ($scenario -eq 'cd-settings-tamper') { [IO.File]::WriteAllText((Join-Path $global:ActiveTarget 'appsettings.json'),'changed settings') }
    if ($scenario -eq 'cd-target-only-change') { [IO.File]::WriteAllText((Join-Path $global:ActiveTarget 'new-data.bin'),'do not delete') }
    if ($scenario -eq 'cd-runtime-change') { [IO.File]::WriteAllText((Join-Path $global:ActiveTarget 'app.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net11.0"}}') }
    $result = & (Join-Path $Scripts 'Deploy-IISApplication.ps1') @parameters -ExpectedState $preview.StateFingerprint
    if ($scenario -eq 'cd-success') {
        Assert-True ($result.Success -and $result.Verified -and $result.Verification) $result.Message
        Assert-True ((Get-CdConfigurationJson $global:ActiveTarget) -ceq $configuration) 'CD settings were overwritten'
        Assert-True ((Get-CdManifestJson $global:ActiveTarget) -ceq $snapshot.EffectiveManifestJson) 'CD deployed hash verification failed'
    } else {
        Assert-True (!$result.Success -and !$result.Verified) 'Unsafe CD unexpectedly succeeded'
        Assert-True (([IO.File]::ReadAllText((Join-Path $global:ActiveTarget 'app.dll'))) -ceq 'old binary') 'Rejected CD did not preserve/restore the original target'
        if ($scenario -eq 'cd-verification-rollback') { Assert-True ($result.Message -match 'Rollback: Succeeded') $result.Message }
        else { Assert-True (!(Test-Path -LiteralPath $global:ActiveBackup)) 'Rejected CD changed pool/files before validation' }
    }
    Assert-True ($global:PoolState -eq 'Started') 'CD did not restore/start the pool'
    Write-Output "PASS: $scenario (local filesystem; IIS mocked)"
}
