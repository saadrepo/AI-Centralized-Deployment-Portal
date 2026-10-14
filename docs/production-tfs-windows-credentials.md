# Production TFS Git Windows authentication

The TFS Git HTTP handler previously answered Negotiate/NTLM challenges using
`CredentialCache.DefaultNetworkCredentials`, i.e. the portal process identity.
An administrator's successful explicit-credential Build API test does not prove
that `IIS AppPool\DeploymentPortal` can authenticate. Production now answers those
challenges with an administrator-provisioned Windows account. Development retains
default process credentials. There is no Production fallback to the process account.

This applies to the existing TfsGit collection client used by repository/branch,
CI build, artifact and CD preparation reads. The separate legacy `Tfs`/PMSNZ
client retains its existing authentication. No CI definition, branch, project,
API version, endpoint configuration, migration, WinRM credential, approval,
history/audit or CD flag was changed. The client remains GET-only; this change
does not enable CI writes or CD execution.

## Store and security boundary

`%ProgramData%\DeploymentPortal\TfsGitCredentials` is a dedicated store outside
`C:\Sites\DeploymentPortal`, separate from `WinRmCredentials`. A collection's
canonical URL determines its SHA-256 filename and DPAPI additional entropy using
the purpose `DeploymentPortal.TfsGit.Windows.v1`. The entire UTF-8 username and
password payload is DPAPI LocalMachine ciphertext on disk. No username/password
configuration is required in appsettings, SQL, environment variables or Git.
The script prompts for the password with Read-Host AsSecureString and never accepts
a password argument. It clears temporary character and byte arrays. Decrypted
credentials necessarily exist briefly in memory as NetworkCredential values.

Machine-scoped DPAPI supports administrator provisioning for ApplicationPoolIdentity
without a user profile or impersonation. Any local account that obtains a blob may
be able to decrypt it on that host; NTFS permissions provide the identity boundary.
The script assigns Administrators ownership, protects inheritance and allows only
SYSTEM/Administrators FullControl and the named IIS pool ReadAndExecute. The provider
reuses the existing WinRM ACL validator to reject broader access, pool write access,
unprotected inheritance or an unsafe owner. It checks both directory and file before
decrypting, rejects reparse points and refuses a store inside application content.
Administrators, SYSTEM and the portal process itself remain trusted.

Credentials are returned only for the existing collection's host, scheme, port and
path boundary and only for Negotiate or NTLM. Basic/Digest, foreign hosts/collections,
proxies, redirects, traversal, explicit Authorization headers and Host overrides
remain rejected. Normal TLS certificate validation remains enabled. The existing
approved HTTP PoC endpoint remains exactly `http://netserver:8080/tfs/DefaultCollection`;
no new HTTP endpoint is permitted. Windows challenge-response does not send a
plaintext password, but this HTTP endpoint has no TLS confidentiality/integrity for
API data. Continue using its isolated trusted network; this change does not weaken
or replace the existing endpoint policy. HTTPS credentials must be provisioned
separately if the existing configuration is changed later through its normal process.

## Exact SOURCE VM setup commands

On `windowservertest2` (`192.168.4.11`), use elevated **Windows PowerShell 5.1**.
First copy `tools\Provision-TfsGitCredential.ps1` from the reviewed source checkout
to `C:\AdminTools\DeploymentPortal`. The first block assumes that checkout is at
`C:\Projects\Deployment-Portal-VS`; if SOURCE has no checkout, transfer just that
script to the same administrator tools path. Keep it outside the published web folder.
The updated application must be released separately through the existing authorized
deployment process. Build #6 lacks this change. No push or deployment was performed.

```powershell
New-Item -ItemType Directory -Force -Path 'C:\AdminTools\DeploymentPortal' | Out-Null
Copy-Item -LiteralPath 'C:\Projects\Deployment-Portal-VS\tools\Provision-TfsGitCredential.ps1' `
    -Destination 'C:\AdminTools\DeploymentPortal\Provision-TfsGitCredential.ps1'

Import-Module WebAdministration
Get-Item 'IIS:\AppPools\DeploymentPortal' |
    Select-Object Name, @{Name='IdentityType';Expression={$_.processModel.identityType}}
# Expect ApplicationPoolIdentity; keep the application environment Production.

# Enter the exact TFS Windows account that returned HTTP 200 in your explicit test.
# This username is not a password. Use DOMAIN\user, user@domain, or a TFS-local user.
$tfsUsername = Read-Host 'TFS Windows username from the successful HTTP 200 test'
& 'C:\AdminTools\DeploymentPortal\Provision-TfsGitCredential.ps1' `
    -BaseUrl 'http://netserver:8080/tfs/DefaultCollection' `
    -Username $tfsUsername -AppPoolName 'DeploymentPortal'
# Enter the password only at the secure password prompt.
Remove-Variable tfsUsername

Get-Acl "$env:ProgramData\DeploymentPortal\TfsGitCredentials" |
    Format-List Owner,AreAccessRulesProtected,AccessToString
Get-ChildItem "$env:ProgramData\DeploymentPortal\TfsGitCredentials" -Filter '*.dpapi' |
    Get-Acl | Format-List Owner,AreAccessRulesProtected,AccessToString
```

Expected owner: BUILTIN\Administrators. Protected inheritance: True. Allowed
identities: SYSTEM and Administrators (FullControl), IIS AppPool\DeploymentPortal
(ReadAndExecute). This script changes only its dedicated TfsGit credential store;
it does not change app pool identity, WinRM files, IIS authentication or TFS settings.
Use an account with the necessary collection/project/build/artifact read permissions;
do not grant deployment or pipeline administration rights merely for these reads.

After the updated application is deployed, recycle the pool at an approved quiet
time with **no queued/running deployment**. Existing authenticated HTTP connections
can retain old credentials after rotation. Recycling also affects the worker, so do
not perform it during active work:

```powershell
Restart-WebAppPool -Name 'DeploymentPortal'
Start-Process 'https://deployment-portal.vitonta.com/TfsGitBuilds'
```

Verify the CI Builds page returns builds for the existing Dev definition and that
an existing build's artifact listing loads. These are read-only acceptance checks;
do not click preparation, confirmation or deployment actions. An admin HTTP 200 test
alone is insufficient: loading this page verifies authentication through the actual
IIS identity. Do not add a portal authentication bypass. CD remains disabled.

## Rotation, troubleshooting and recovery

Run the same provisioning command again to replace both username and password
atomically for this collection, then recycle at a safe time. No plaintext backup
is created. Lookup reads fresh ciphertext on each authentication challenge, but
pooled authenticated connections may persist until recycled. To revoke, remove the
matching encrypted file and recycle at a safe time. Do not copy blobs to another
host or include them in source control, web content, release artifacts or ordinary
backups; reprovision after migration or recovery. Do not loosen ACLs to debug.

Missing, misbound, corrupted or insecure credentials fail with a sanitized provider
error and no default-account fallback. The existing controllers display a generic
error and log only the failure type; no password or credential-bearing exception is
logged. Inspect directory/file ACLs as an administrator. If they are correct,
verify the configured collection URL matches provisioning, the account/password is
valid and the account has TFS read permissions. Do not print decrypted payloads,
add Basic authentication, disable certificate validation, enable redirects, alter
the CI pipeline or enable CD to resolve authentication.

## Verification

`dotnet test DeploymentPortal.sln` runs the complete .NET suite, existing PowerShell
CD fixtures and both synthetic credential provisioning fixtures. The latter perform
real DPAPI, NTFS ACL and atomic file operations in isolated repository directories;
they substitute the test user for the administrator and a synthetic SID for IIS,
and never modify ProgramData, IIS, either VM or TFS. A restricted execution sandbox
may need permission for these local fixture ACL operations.

TFS tests cover payload account forms, Unicode, malformed payloads, DPAPI corruption,
collection/purpose isolation, Development behavior, Production fail-closed behavior,
credential challenge scoping, fresh lookups, DI integration, endpoint rejection,
secure prompting, ACLs and rotation. Actual SOURCE-to-TFS authentication remains a
separate acceptance check after deployment/provisioning.

References: [Windows DPAPI scopes](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection),
[NTLM and Kerberos authentication](https://learn.microsoft.com/en-us/dotnet/framework/network-programming/ntlm-and-kerberos-authentication).
