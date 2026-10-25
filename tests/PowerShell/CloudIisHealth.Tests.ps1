param([string]$Root, [string]$Scripts)
$ErrorActionPreference = 'Stop'
# Reuse the established IIS mocks. Manual scenarios run as a separate test case.
# No WinRM session, credentials or real IIS provider is used anywhere in this test.
. (Join-Path $PSScriptRoot 'IisWorkflowFixture.ps1') -FixtureRoot $Root
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
$global:CloudHealthCalls = 0
function Invoke-WebRequest {
    param($Uri, [switch]$UseBasicParsing, $MaximumRedirection, $TimeoutSec, $ErrorAction)
    $global:CloudHealthCalls++
    Assert-True ($Uri -ceq 'https://app.example/health' -and $MaximumRedirection -eq 0 -and $TimeoutSec -eq 30) 'Unsafe health request'
    if ($global:CloudScenario -eq 'cloud-health-fail') { throw 'Synthetic health failure' }
    [pscustomobject]@{ StatusCode = $(if ($global:CloudScenario -eq 'cloud-health-redirect') { 302 } else { 200 }) }
}
foreach ($scenario in @('cloud-health-ok', 'cloud-health-fail', 'cloud-health-redirect', 'cloud-health-invalid')) {
    $global:CloudScenario = $scenario
    $global:CloudHealthCalls = 0
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
    $global:SitePresent = $true
    $global:AppPresent = $true
    $global:PoolPresent = $true
    $global:PoolState='Started'; $global:CdCorruptCopy=$false
    $parameters = @{ SiteName='TestSite'; ApplicationName='portal'; AppPoolName='PortalPool'; SourcePath=$global:ActiveSource;
        TargetPath=$global:ActiveTarget; BackupPath=$global:ActiveBackup; BackupRoot=$backupRoot; RemoteInvocation=$true; CdPackage=$snapshot; CdSecurityFunctions=$helpers }
    $preview = & (Join-Path $Scripts 'Deploy-IISApplication.ps1') @parameters -PreviewOnly $true
    Assert-True ($preview.Success -and $preview.StateFingerprint) $preview.Message
    Assert-True (([IO.File]::ReadAllText((Join-Path $global:ActiveTarget 'app.dll'))) -ceq 'old binary') 'CD preview changed the target'
    Assert-True ($global:CloudHealthCalls -eq 0) 'Preview triggered a health request'
    $url = if ($scenario -eq 'cloud-health-invalid') { 'http://app.example/health' } else { 'https://app.example/health' }
    $result = & (Join-Path $Scripts 'Deploy-IISApplication.ps1') @parameters -ExpectedState $preview.StateFingerprint -CloudHealthUrl $url
    $expectedCalls = if ($scenario -eq 'cloud-health-invalid') { 0 } else { 1 }
    Assert-True ($global:CloudHealthCalls -eq $expectedCalls) 'Unexpected health request count'
    if ($scenario -eq 'cloud-health-ok') {
        Assert-True ($result.Success -and $result.Verified -and $result.Verification -and $result.HealthHttpStatus -eq 200) $result.Message
        Assert-True ((Get-CdConfigurationJson $global:ActiveTarget) -ceq $configuration) 'CD settings were overwritten'
        Assert-True ((Get-CdManifestJson $global:ActiveTarget) -ceq $snapshot.EffectiveManifestJson) 'CD deployed hash verification failed'
    } else {
        Assert-True (!$result.Success -and !$result.Verified) 'Unsafe CD unexpectedly succeeded'
        Assert-True (([IO.File]::ReadAllText((Join-Path $global:ActiveTarget 'app.dll'))) -ceq 'old binary') 'Rejected CD did not preserve/restore the original target'
        Assert-True ($result.Message -match 'Rollback: Succeeded') $result.Message
        Assert-True (Test-Path -LiteralPath $global:ActiveBackup) 'Backup was not retained'
    }
    Assert-True ($global:PoolState -eq 'Started') 'CD did not restore/start the pool'
    Write-Output "PASS: $scenario (local filesystem; IIS mocked)"
}
