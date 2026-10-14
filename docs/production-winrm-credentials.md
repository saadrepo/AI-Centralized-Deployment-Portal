# Production WinRM credentials

Production and other non-Development environments use `ProductionCredentialProvider`;
Development continues to use the unchanged Development provider. All consumers of
`ICredentialProvider`, including Test Connection, IIS discovery, deployment preview,
the worker and CD staging, resolve the same provider. No approval, expiry, queue,
history, migration, pipeline, remote or CD flag changes are required.

## Storage and identity boundary

The fixed store is `%ProgramData%\DeploymentPortal\WinRmCredentials`, outside
`C:\Sites\DeploymentPortal`. Each `.dpapi` file contains only Windows DPAPI
LocalMachine ciphertext. Its filename and DPAPI additional entropy bind it to
the lowercase hostname and username, WinRM port and transport. Passwords are never
stored in SQL or application configuration. Provisioning prompts with Read-Host
AsSecureString; there is no password parameter. The existing runner sends credentials
through a redirected stdin pipe, never arguments, and does not log raw PowerShell
output. Decrypted credentials necessarily exist briefly in process memory, including
the existing managed password string and stdin JSON. This is not a durable JSON file.

DPAPI LocalMachine lets an administrator provision for ApplicationPoolIdentity
without the pool's profile or impersonation. It is **machine scoped, not identity
scoped**: any local account that obtains the ciphertext can potentially decrypt it
on that host. NTFS permissions are the identity boundary. The script protects
inheritance, assigns ownership to Administrators, gives SYSTEM and Administrators
FullControl, and gives only the named pool ReadAndExecute. The provider checks
directory and file ACLs and ownership on each lookup, rejects broad grants and pool
write permissions, and rejects reparse points. Administrators and SYSTEM are trusted;
a compromised administrator or portal process is outside this protection boundary.
The script owns this dedicated store's ACLs; do not share it with other applications.
Changing pool identity requires reprovisioning with that actual identity design.

Do not copy blobs to another machine or put them in Git, artifacts, web content or
ordinary backups. Reprovision locally after migration/recovery. To rotate, run the
script again with the same connection fields. It atomically replaces ciphertext;
lookups read afresh, so a portal restart is unnecessary. Already executing operations
may still hold the previous credential. To revoke, remove the matching file as an
administrator; do not loosen permissions to troubleshoot. Provisioning failure after
an ACL change requires administrator inspection before retrying.

## SOURCE VM setup (administrator PowerShell 5.1)

These commands are for `windowservertest2` (`192.168.4.11`). Deploy the updated
application separately through the existing authorized process; Build #6 does not
contain this provider. Copy `tools\Provision-WinRmCredential.ps1` to an administrator
tools directory outside the published web folder, for example
`C:\AdminTools\DeploymentPortal`. No deployment or remote machine setup was performed
by implementing this change.

```powershell
Import-Module WebAdministration
Get-Item 'IIS:\AppPools\DeploymentPortal' |
    Select-Object Name, @{Name='IdentityType';Expression={$_.processModel.identityType}}
# Expect ApplicationPoolIdentity. Keep ASPNETCORE_ENVIRONMENT=Production.
& 'C:\AdminTools\DeploymentPortal\Provision-WinRmCredential.ps1' `
    -HostName 'deployment-test.vitonta.com' `
    -Username 'WINDOWSERVERTES\saad.bilal' `
    -Port 5985 -AppPoolName 'DeploymentPortal'
# Enter the TARGET password only at the secure prompt.
Get-Acl "$env:ProgramData\DeploymentPortal\WinRmCredentials" |
    Format-List Owner,AreAccessRulesProtected,AccessToString
Get-ChildItem "$env:ProgramData\DeploymentPortal\WinRmCredentials" -Filter '*.dpapi' |
    Get-Acl | Format-List Owner,AreAccessRulesProtected,AccessToString
```

Do not run with a password in an argument, transcript or environment variable.
The IIS identity must match the pool supplied to the script. The provider reports a
sanitized provisioning error for missing, corrupted, misbound or insecure credentials.
Use administrator ACL inspection to diagnose it; never log decrypted contents.

In the existing TARGET SQL record, verify hostname `deployment-test.vitonta.com`,
username `WINDOWSERVERTES\saad.bilal`, port `5985`, and TLS unchecked. Host aliases,
IP addresses, username changes and transport changes each require separate provisioning.
Click **Test Connection** in the SOURCE-hosted portal to verify decryption under IIS,
then perform read-only IIS discovery. A successful administrator remoting test alone
does not verify access under the pool identity. These actions do not approve or queue
a deployment. Leave CD execution disabled and the manual approval flow in place.

## HTTP lab and later HTTPS

The portal keeps Negotiate authentication and WinRM message encryption. It does not
change listeners, TrustedHosts, firewall, UAC, Basic, CredSSP, AllowUnencrypted or
certificate-validation settings. Inspect the previously configured lab settings:

```powershell
Resolve-DnsName 'deployment-test.vitonta.com' # expect 192.168.4.12
Test-NetConnection 'deployment-test.vitonta.com' -Port 5985
Get-Item WSMan:\localhost\Client\TrustedHosts
Get-Item WSMan:\localhost\Client\AllowUnencrypted # must be false
```

Workgroup HTTP/Negotiate may require the exact hostname already approved in
TrustedHosts. Do not use wildcards or overwrite an existing list. If it is absent,
have the administrator review the existing remoting configuration before changing it.
HTTP/NTLM encrypts messages but cannot authenticate the target server identity;
TrustedHosts does not remedy that limitation. Keep this lab isolated, restrict the
TARGET firewall to SOURCE, and prefer trusted HTTPS for broader use.

For HTTPS, first provision a TARGET 5986 listener with a certificate matching
`deployment-test.vitonta.com`, trusted by SOURCE, and restrict firewall access.
Then provision an additional credential and update the server record's port/TLS:

```powershell
& 'C:\AdminTools\DeploymentPortal\Provision-WinRmCredential.ps1' `
    -HostName 'deployment-test.vitonta.com' `
    -Username 'WINDOWSERVERTES\saad.bilal' `
    -Port 5986 -UseSsl -AppPoolName 'DeploymentPortal'
```

The existing PowerShell runner uses UseSSL with normal certificate validation.
Remove the obsolete HTTP credential file after verifying HTTPS. This provider does
not add portal authentication; retain existing access restrictions for the prototype.

References: [Windows DPAPI scopes](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection),
[WinRM remoting security](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security).

## Local verification

Run `dotnet test DeploymentPortal.sln` and, with Windows PowerShell 5.1,
`powershell.exe -NoProfile -File tests\PowerShell\WinRmCredentialProvisioning.Tests.ps1`.
The script test uses synthetic passwords, a repository fixture and substituted
administrator/pool identities; it never provisions the real ProgramData store.
It verifies protected ACLs, a read-only pool grant, DPAPI round trip, Unicode password
rotation, separate HTTPS provisioning and preservation after an empty-password failure.
The .NET tests verify connection binding, DPAPI rejection of mismatched connections,
unsafe ACL/owner rejection and sanitized connection-test errors. SOURCE-hosted
Test Connection remains the required validation under the real IIS identity.
