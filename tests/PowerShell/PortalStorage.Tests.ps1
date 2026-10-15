$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixture = Join-Path $repo ('.tools\storage-tests\' + [guid]::NewGuid().ToString('N'))
$keyFixture = Join-Path $fixture 'keys'
$cdFixture = Join-Path $fixture 'staging'
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$source = [IO.File]::ReadAllText((Join-Path $repo 'tools\Initialize-PortalStorage.ps1'))
$source = $source -replace '(?m)^#Requires.*\r?\n',''
$source = $source.Replace('([Security.Principal.NTAccount]::new("IIS AppPool\$AppPoolName")).Translate([Security.Principal.SecurityIdentifier])',"[Security.Principal.SecurityIdentifier]::new('S-1-5-21-1-2-3-1001')")
$source = $source.Replace("[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')","[Security.Principal.SecurityIdentifier]::new('$currentSid')")
$source = $source.Replace("`$keyRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'DeploymentPortal\DataProtectionKeys'","`$keyRoot = '$keyFixture'")
$source = $source.Replace("'C:\DeploymentPortalData\CdStaging'","'$cdFixture'")
$initialize = [scriptblock]::Create($source)
try {
    & $initialize
    if (!(Test-Path -LiteralPath $keyFixture) -or (Test-Path -LiteralPath $cdFixture)) { throw 'Default provisioning must create only key storage.' }
    $keyFile = Join-Path $keyFixture 'key-test.xml'
    [IO.File]::WriteAllText($keyFile,'synthetic-key-content-preservation-check')
    & $initialize -ProvisionCdStaging
    & $initialize -ProvisionCdStaging
    if ([IO.File]::ReadAllText($keyFile) -cne 'synthetic-key-content-preservation-check') { throw 'Existing key content changed.' }
    foreach ($directory in @($keyFixture,$cdFixture)) {
        $acl = Get-Acl -LiteralPath $directory
        if (!$acl.AreAccessRulesProtected) { throw 'Directory inheritance was not protected.' }
        $rules = @($acl.Access | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-21-1-2-3-1001' })
        if ($rules.Count -ne 1 -or ($rules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -ne [Security.AccessControl.FileSystemRights]::Modify) { throw 'Pool needs Modify.' }
        if (($rules[0].InheritanceFlags -band [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit') -ne [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit') { throw 'Pool grant must be inheritable.' }
    }
    if (!(Get-Acl -LiteralPath $keyFile).AreAccessRulesProtected) { throw 'Existing key file ACL was not restricted.' }
    [IO.File]::WriteAllText((Join-Path $keyFixture 'unexpected.txt'),'synthetic')
    $rejected = $false
    try { & $initialize } catch { $rejected = $true }
    if (!$rejected) { throw 'Unexpected key directory content was accepted.' }
    Write-Host 'PASS: private key/staging ACLs, inheritable pool Modify, existing key preservation, idempotency and unrelated-content rejection.'
} finally {
    $allowed = [IO.Path]::GetFullPath((Join-Path $repo '.tools\storage-tests')) + '\'
    if (![IO.Path]::GetFullPath($fixture).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup.' }
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
