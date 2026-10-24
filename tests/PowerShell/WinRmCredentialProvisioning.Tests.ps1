# Run with Windows PowerShell 5.1, without elevation or real credentials.
# Adapt fixture paths/identities and the owner/DACL persistence API. The actual
# provisioning algorithm, secure prompt, DPAPI and file rotation still execute.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $PSScriptRoot 'ProvisioningFixture.ps1')
$fixtureContext = New-ProvisioningFixture $repo
$fixture = Join-Path $fixtureContext.Root 'store'
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$source = [IO.File]::ReadAllText((Join-Path $repo 'tools\Provision-WinRmCredential.ps1'))
$source = $source -replace '(?m)^#Requires.*\r?\n', ''
$source = $source.Replace("`$root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'DeploymentPortal\WinRmCredentials'", "`$root = '$fixture'")
$source = $source.Replace('([Security.Principal.NTAccount]::new("IIS AppPool\$AppPoolName")).Translate([Security.Principal.SecurityIdentifier])', "[Security.Principal.SecurityIdentifier]::new('S-1-5-21-1-2-3-1001')")
# The test user stands in for the provisioning administrator, without elevating.
$source = $source.Replace("[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')", "[Security.Principal.SecurityIdentifier]::new('$currentSid')")
$source = $source.Replace("throw 'Credential provisioning failed. Verify administrator rights, the IIS pool identity and private store permissions. No password details are reported.'", 'throw $_') # Synthetic test diagnostics only.
# Test-only equivalent owner/DACL persistence and restricted-token identity.
$source = $source.Replace('Set-Acl -LiteralPath', 'Set-ProvisioningFixtureAcl -LiteralPath')
$source = $source.Replace("[Security.Principal.SecurityIdentifier]::new('S-1-5-18')", "[Security.Principal.SecurityIdentifier]::new('$($fixtureContext.SystemSid)')")
$provision = [scriptblock]::Create($source)
$global:PromptCount = 0
$global:TestPassword = 'synthetic-provisioning-test-only'
function Read-Host { param($Prompt, [switch]$AsSecureString)
    if (!$AsSecureString) { throw 'Prompt must be secure.' }
    $global:PromptCount++
    ConvertTo-SecureString $global:TestPassword -AsPlainText -Force
}
function Assert-True($Condition, $Message) { if (!$Condition) { throw $Message } }
try {
    & $provision
    $binding = [Text.Encoding]::UTF8.GetBytes("DeploymentPortal.WinRm.v1`ndeployment-test.vitonta.com`nwindowservertes\saad.bilal`n5985`nhttp")
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $name = ([BitConverter]::ToString($hash.ComputeHash($binding))).Replace('-', '').ToLowerInvariant() + '.dpapi' } finally { $hash.Dispose() }
    $file = Join-Path $fixture $name
    $first = [IO.File]::ReadAllBytes($file)
    $decoded = [Security.Cryptography.ProtectedData]::Unprotect($first, $binding, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    Assert-True ([Text.Encoding]::UTF8.GetString($decoded) -ceq $global:TestPassword) 'DPAPI round trip failed.'
    Assert-True ((Get-Acl -LiteralPath $fixture).AreAccessRulesProtected) 'Directory ACL is not protected.'
    Assert-True ((Get-Acl -LiteralPath $file).AreAccessRulesProtected) 'File ACL is not protected.'
    $poolRule = @((Get-Acl -LiteralPath $file).Access | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-21-1-2-3-1001' })
    Assert-True ($poolRule.Count -eq 1 -and ($poolRule[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -eq 0) 'Pool must only have read access.'
    $global:TestPassword = 'synthetic-rotated-test-only' + [char]0xD83D + [char]0xDD10 + [char]0xF123
    & $provision
    $second = [IO.File]::ReadAllBytes($file)
    $decoded = [Security.Cryptography.ProtectedData]::Unprotect($second, $binding, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    Assert-True ([Text.Encoding]::UTF8.GetString($decoded) -ceq $global:TestPassword) 'Rotation failed.'
    Assert-True (@(Get-ChildItem -LiteralPath $fixture -Filter '*.tmp').Count -eq 0) 'Temporary ciphertext was left behind.'
    & $provision -Port 5986 -UseSsl
    Assert-True (@(Get-ChildItem -LiteralPath $fixture -Filter '*.dpapi').Count -eq 2) 'HTTPS must be provisioned separately.'
    $global:TestPassword = ''
    $rejected = $false
    try { & $provision } catch { $rejected = $true }
    Assert-True $rejected 'Empty password was accepted.'
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($file)) -ceq [Convert]::ToBase64String($second)) 'Failed provisioning replaced the existing credential.'
    Assert-True ($global:PromptCount -eq 4) 'Secure prompt was not used for every provisioning.'
    Write-Host 'PASS: secure prompt, DPAPI binding, protected ACLs, read-only pool grant, rotation, HTTPS separation, empty-password rejection and preservation.'
} finally {
    Remove-ProvisioningFixture $fixtureContext
    Remove-Variable TestPassword,PromptCount -Scope Global
}
