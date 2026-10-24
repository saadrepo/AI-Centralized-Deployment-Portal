# Synthetic, unprivileged fixture with isolated paths, test identities and an
# owner/DACL API without audit privileges. Actual provisioning, DPAPI, secure
# prompting and ACL validation still run.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $PSScriptRoot 'ProvisioningFixture.ps1')
$fixtureContext = New-ProvisioningFixture $repo
$fixture = Join-Path $fixtureContext.Root 'store'
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$source = [IO.File]::ReadAllText((Join-Path $repo 'tools\Provision-TfsGitCredential.ps1'))
$source = $source -replace '(?m)^#Requires.*\r?\n', ''
$source = $source.Replace("`$root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'DeploymentPortal\TfsGitCredentials'", "`$root = '$fixture'")
$source = $source.Replace('([Security.Principal.NTAccount]::new("IIS AppPool\$AppPoolName")).Translate([Security.Principal.SecurityIdentifier])', "[Security.Principal.SecurityIdentifier]::new('S-1-5-21-1-2-3-1001')")
$source = $source.Replace("[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')", "[Security.Principal.SecurityIdentifier]::new('$currentSid')")
$source = $source.Replace("throw 'TFS credential provisioning failed. Verify the approved collection, Windows username, administrator rights and IIS pool identity. No credential details are reported.'", 'throw $_')
# Test-only equivalent owner/DACL persistence and restricted-token identity.
$source = $source.Replace('Set-Acl -LiteralPath', 'Set-ProvisioningFixtureAcl -LiteralPath')
$source = $source.Replace("[Security.Principal.SecurityIdentifier]::new('S-1-5-18')", "[Security.Principal.SecurityIdentifier]::new('$($fixtureContext.SystemSid)')")
$provision = [scriptblock]::Create($source)
$global:TfsPromptCount = 0
$global:TfsTestPassword = 'synthetic-tfs-only'
function Read-Host { param($Prompt, [switch]$AsSecureString)
    if (!$AsSecureString) { throw 'Password prompt must be secure.' }
    $global:TfsPromptCount++
    ConvertTo-SecureString $global:TfsTestPassword -AsPlainText -Force
}
function Assert-True($Condition, $Message) { if (!$Condition) { throw $Message } }
try {
    & $provision -Username 'DOMAIN\reader'
    $binding = [Text.Encoding]::UTF8.GetBytes("DeploymentPortal.TfsGit.Windows.v1`nhttp://netserver:8080/tfs/DefaultCollection/")
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $name = ([BitConverter]::ToString($hash.ComputeHash($binding))).Replace('-', '').ToLowerInvariant() + '.dpapi' } finally { $hash.Dispose() }
    $file = Join-Path $fixture $name
    $first = [IO.File]::ReadAllBytes($file)
    $decoded = [Security.Cryptography.ProtectedData]::Unprotect($first, $binding, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    Assert-True ([Text.Encoding]::UTF8.GetString($decoded) -ceq "DOMAIN\reader`0$global:TfsTestPassword") 'Username/password DPAPI round trip failed.'
    Assert-True (![Text.Encoding]::UTF8.GetString($first).Contains($global:TfsTestPassword)) 'Password persisted as plaintext.'
    Assert-True ((Get-Acl -LiteralPath $fixture).AreAccessRulesProtected) 'Directory ACL is not protected.'
    Assert-True ((Get-Acl -LiteralPath $file).AreAccessRulesProtected) 'File ACL is not protected.'
    $poolRule = @((Get-Acl -LiteralPath $file).Access | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-21-1-2-3-1001' })
    Assert-True ($poolRule.Count -eq 1 -and ($poolRule[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -eq 0) 'Pool must have read-only access.'
    $global:TfsTestPassword = 'synthetic-rotation-' + [char]0xD83D + [char]0xDD10 + [char]0xF123
    & $provision -BaseUrl 'http://NETSERVER:8080/tfs/DefaultCollection/' -Username 'reader@example.test'
    $second = [IO.File]::ReadAllBytes($file)
    $decoded = [Security.Cryptography.ProtectedData]::Unprotect($second, $binding, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    Assert-True ([Text.Encoding]::UTF8.GetString($decoded) -ceq "reader@example.test`0$global:TfsTestPassword") 'Username/password rotation failed.'
    Assert-True (@(Get-ChildItem -LiteralPath $fixture -Filter '*.tmp').Count -eq 0) 'Temporary ciphertext left behind.'
    & $provision -BaseUrl 'https://tfs.example.test/tfs/DefaultCollection' -Username 'DOMAIN\reader'
    Assert-True (@(Get-ChildItem -LiteralPath $fixture -Filter '*.dpapi').Count -eq 2) 'Different collection/transport must provision separately.'
    foreach ($endpoint in @('http://external.example/tfs/DefaultCollection', 'http://netserver:80/tfs/DefaultCollection', 'http://netserver:8080/tfs/OtherCollection', 'http://user:synthetic-only@netserver:8080/tfs/DefaultCollection', 'https://tfs.example.test/tfs/DefaultCollection?token=synthetic-only')) {
        $rejected = $false
        try { & $provision -BaseUrl $endpoint -Username 'DOMAIN\reader' } catch { $rejected = $true }
        Assert-True $rejected 'Unsafe endpoint was accepted.'
    }
    Assert-True ($global:TfsPromptCount -eq 3) 'Unsafe endpoint reached the secure prompt.'
    $global:TfsTestPassword = ''
    $rejected = $false
    try { & $provision -Username 'DOMAIN\reader' } catch { $rejected = $true }
    Assert-True $rejected 'Empty password was accepted.'
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($file)) -ceq [Convert]::ToBase64String($second)) 'Failed provisioning replaced the existing credential.'
    Write-Host 'PASS: TFS secure prompt, DPAPI payload/binding, protected ACLs, read-only IIS grant, Unicode/UPN rotation, collection separation, endpoint validation and failed-rotation preservation.'
} finally {
    Remove-ProvisioningFixture $fixtureContext
    Remove-Variable TfsTestPassword,TfsPromptCount -Scope Global
}
