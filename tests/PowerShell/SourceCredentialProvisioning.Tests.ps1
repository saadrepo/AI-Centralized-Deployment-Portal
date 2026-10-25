# Unprivileged synthetic fixture: never invokes the elevated production script.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $PSScriptRoot 'ProvisioningFixture.ps1')
$fixtureContext = New-ProvisioningFixture $repo
$fixture = Join-Path $fixtureContext.Root 'store'
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$source = [IO.File]::ReadAllText((Join-Path $repo 'tools\Manage-SourceCredential.ps1'))
if (!$source.Contains('#Requires -RunAsAdministrator')) { throw 'Elevation gate missing.' }
$source = $source -replace '(?m)^#Requires.*\r?\n',''
$source = $source.Replace("`$root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'DeploymentPortal\SourceCredentials'", "`$root = '$fixture'")
$source = $source.Replace('([Security.Principal.NTAccount]::new("IIS AppPool\$AppPoolName")).Translate([Security.Principal.SecurityIdentifier])', "[Security.Principal.SecurityIdentifier]::new('S-1-5-21-1-2-3-1001')")
$source = $source.Replace("[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')", "[Security.Principal.SecurityIdentifier]::new('$currentSid')")
# Synthetic-only diagnostics: retain redaction and report location/type, never exception text or payloads.
$source = $source.Replace("throw 'Source credential operation failed. Verify elevation, provider, organization binding, reference and private store permissions. Secret details are suppressed.'", 'throw ("Synthetic credential fixture failed. Line={0}; Type={1}; HResult={2}. Secret details are suppressed." -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.GetType().Name, $_.Exception.GetBaseException().HResult)')
# Use the equivalent owner/DACL API without requesting audit privileges.
$source = $source.Replace('Set-Acl -LiteralPath', 'Set-ProvisioningFixtureAcl -LiteralPath')
# Adapt the synthetic SYSTEM role for restricted Windows test tokens only.
$source = $source.Replace("[Security.Principal.SecurityIdentifier]::new('S-1-5-18')", "[Security.Principal.SecurityIdentifier]::new('$($fixtureContext.SystemSid)')")
$manage = [scriptblock]::Create($source)
$global:SourceTestToken = 'synthetic-only-first'
function Read-Host { param($Prompt,[switch]$AsSecureString)
    if (!$AsSecureString) { throw 'Insecure prompt.' }
    ConvertTo-SecureString $global:SourceTestToken -AsPlainText -Force
}
function Assert-True($Condition,$Message) { if (!$Condition) { throw $Message } }
function Assert-Rejected([scriptblock]$Operation) {
    $failed = $false
    try { & $Operation | Out-Null } catch {
        $failed = $true
        Assert-True (!$_.ToString().Contains($global:SourceTestToken)) 'Secret leaked in error.'
        Assert-True ($_.Exception.Message.Contains('Secret details are suppressed.')) 'Unexpected failure.'
    }
    Assert-True $failed 'Operation should fail closed.'
}
try {
    foreach ($kind in @('AzureDevOps','GitHubOrganization','GitHubUser')) {
        $provider = 'GitHub'
        if ($kind -eq 'AzureDevOps') { $provider = 'AzureDevOps' }
        $url = 'https://dev.azure.com/example/'
        if ($provider -eq 'GitHub') { $url = 'https://api.github.com/orgs/example/' }
        if ($kind -eq 'GitHubUser') { $url = 'https://api.github.com/users/example/' }
        $result = & $manage -Action Provision -Provider $provider -Destination $url
        Assert-True ($result.Reference -cmatch '\A[a-f0-9]{64}\z') 'Opaque reference missing.'
        Assert-True (!(($result | ConvertTo-Json).Contains($global:SourceTestToken))) 'Secret exposed in metadata.'
        $file = Join-Path $fixture ($result.Reference + '.json')
        $before = [IO.File]::ReadAllText($file)
        Assert-True (!$before.Contains($global:SourceTestToken)) 'Plaintext persisted.'
        $binding = [Text.Encoding]::UTF8.GetBytes("DeploymentPortal.SourceToken.v1`n$($result.Reference)`n$provider`n$url")
        $decoded = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String(($before | ConvertFrom-Json).Ciphertext),$binding,[Security.Cryptography.DataProtectionScope]::LocalMachine)
        Assert-True ([Text.Encoding]::ASCII.GetString($decoded) -ceq $global:SourceTestToken) 'Backend binding mismatch.'
        [Array]::Clear($decoded,0,$decoded.Length)
        Assert-Rejected { & $manage -Action Rotate -Reference $result.Reference -Provider $provider -Destination ($url.Replace('example','other')) }
        if ($provider -eq 'GitHub') {
            $differentType = $url.Replace('/orgs/','/users/')
            if ($kind -eq 'GitHubUser') { $differentType = $url.Replace('/users/','/orgs/') }
            Assert-Rejected { & $manage -Action Rotate -Reference $result.Reference -Provider $provider -Destination $differentType }
        }
        $global:SourceTestToken = 'synthetic-only-rotated'
        $rotated = & $manage -Action Rotate -Reference $result.Reference -Provider $provider -Destination $url
        Assert-True ($rotated.Reference -ceq $result.Reference) 'Rotation changed reference.'
        $after = [IO.File]::ReadAllText($file)
        Assert-True ($before -cne $after -and !$after.Contains($global:SourceTestToken)) 'Rotation failed.'
        $decoded = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String(($after | ConvertFrom-Json).Ciphertext),$binding,[Security.Cryptography.DataProtectionScope]::LocalMachine)
        Assert-True ([Text.Encoding]::ASCII.GetString($decoded) -ceq $global:SourceTestToken) 'Rotated token mismatch.'
        [Array]::Clear($decoded,0,$decoded.Length)
        $metadata = @(& $manage -Action List)
        Assert-True ($metadata.Count -eq 1 -and !(($metadata | ConvertTo-Json).Contains('Ciphertext'))) 'List exposed ciphertext.'
        # Tampered metadata must not validate against the encrypted binding.
        [IO.File]::WriteAllText($file,$after.Replace('example','other'))
        Assert-Rejected { & $manage -Action List }
        [IO.File]::WriteAllText($file,$after)
        # A foreign allowed SID must make the store unusable, rather than be silently repaired.
        $originalAcl = Get-Acl -LiteralPath $file
        $badAcl = Get-Acl -LiteralPath $file
        $badAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'),'Read','Allow'))
        Set-ProvisioningFixtureAcl -LiteralPath $file -AclObject $badAcl
        Assert-Rejected { & $manage -Action List }
        Set-ProvisioningFixtureAcl -LiteralPath $file -AclObject $originalAcl
        & $manage -Action Revoke -Reference $result.Reference | Out-Null
        Assert-True (!(Test-Path -LiteralPath $file)) 'Revocation failed.'
        Assert-Rejected { & $manage -Action Rotate -Reference $result.Reference -Provider $provider -Destination $url }
    }
    Assert-Rejected { & $manage -Action Provision -Provider GitHub -Destination 'https://attacker.example/orgs/example/' }
    Assert-Rejected { & $manage -Action Revoke -Reference '../escape' }
    Assert-True (@(Get-ChildItem -LiteralPath $fixture -Filter '*.tmp').Count -eq 0) 'Temporary file left behind.'
    Write-Output 'PASS: Source credential binding, rotation, revocation, ACL rejection and redaction.'
} finally {
    Remove-ProvisioningFixture $fixtureContext
    Remove-Item Function:\Read-Host -ErrorAction SilentlyContinue
    Remove-Variable SourceTestToken -Scope Global
}
