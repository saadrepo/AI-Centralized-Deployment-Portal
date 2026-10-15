# IIS persistent keys and local CD staging diagnostics

## Confirmed root causes

`Program.cs` previously registered only `AddDataProtection()`. Under an IIS identity
without a usable default key persistence location, framework discovery selects
ephemeral keys. A recycle then loses access to previously issued antiforgery tokens,
protected confirmations and preparation records. Explicit persistence and explicit
at-rest encryption are now configured for non-Development Windows hosting.

The old `CD preparation failed (ArgumentException)` log discarded the validation
reason. The operator confirmed SOURCE's local and remote staging roots are both
`C:\ProgramData\DeploymentPortal\CdStaging`, with backup root `C:\DeploymentBackups`,
ExplorerEnabled/Enabled true and ExecutionEnabled false. Build #9 has a Container
drop with one root web.config. These facts identify the local root policy as the
cause, rather than the definition-2 mapping or artifact layout.

The exact reproducible failure is the local staging root policy:
`StageAsync -> ApprovedRoot -> DeploymentPaths.Validate`. Any root beneath
`C:\ProgramData` or other protected Windows paths, or overlapping the backup root,
throws ArgumentException before artifact requests. Its message is
`Use an application directory, not a protected Windows or user-profile directory.`
This policy is unchanged. Move SOURCE's configured local staging location to the
provisioned private application directory below. Neither the target path nor the
artifact layout needs changing. A regression reproduces the original exception
with the confirmed root and proves the replacement root passes the same policy.
Other existing rejections include failed/incomplete or out-of-scope builds, a missing
or duplicate drop artifact and FilePath rather than Container artifacts. None are
relaxed. No CI definition or artifact publishing configuration was changed.

## Production behavior

The fixed key directory is `%ProgramData%\DeploymentPortal\DataProtectionKeys`,
outside the web application and independent of both credential stores and staging.
The key ring uses `SetApplicationName("DeploymentPortal")`, filesystem persistence
and Windows DPAPI LocalMachine encryption. It needs no loaded IIS user profile.
Key metadata is XML; cryptographic key material is encrypted. Local machine DPAPI
requires restrictive NTFS ACLs because another local account that acquires a blob
may decrypt it on the same host. Administrators, SYSTEM and the portal process are
trusted. This is single-SOURCE storage, not a multi-machine key-sharing design.

An early hosted startup check verifies the directory is outside application content,
rejects reparse points, requires protected directory inheritance and Administrators/
SYSTEM ownership, and checks that only the pool identity, Administrators and SYSTEM
can access the key directory/files. The pool requires inheritable Modify to rotate
keys. Generated key files can be pool-owned. A protect/unprotect probe exercises key
creation/loading; storage failure stops startup instead of silently using memory.
Development continues using the framework's existing Development defaults.

Local staging logs now contain integer BuildId, a fixed Stage, a fixed Reason and
the exception type. They do not log exception objects/messages, submitted hosts,
paths, URLs, package contents, credentials or keys. Request errors also include the
server-generated request trace ID and a fixed reason. Example:

```text
CD local preparation rejected. BuildId=9 Stage=Configuration Reason=StagingRootPolicy FailureType=CdPreparationException
```

Stages: Configuration, PrivateDirectory, Selection, PackageValidation, Download,
Revalidation and Persistence. Specific reasons include StagingRootPolicy,
BuildScope, BuildState, ArtifactCount and ArtifactType. Unknown failures retain the
safe OperationRejected reason. Known policy failures return fixed actionable text;
other failures remain generic. Existing cleanup, quotas, provenance, hashes,
approval, expiry, audit, update-only rules and all CD flags are unchanged.

## Exact SOURCE setup

Use elevated **Windows PowerShell 5.1** on `windowservertest2` (`192.168.4.11`).
Transfer the reviewed `tools\Initialize-PortalStorage.ps1` to
`C:\AdminTools\DeploymentPortal\Initialize-PortalStorage.ps1`. If a checkout exists
on SOURCE at the repository path, the copy commands are:

```powershell
New-Item -ItemType Directory -Force 'C:\AdminTools\DeploymentPortal' | Out-Null
Copy-Item -LiteralPath 'C:\Projects\Deployment-Portal-VS\tools\Initialize-PortalStorage.ps1' `
    -Destination 'C:\AdminTools\DeploymentPortal\Initialize-PortalStorage.ps1'
Import-Module WebAdministration
Get-Item 'IIS:\AppPools\DeploymentPortal' |
    Select-Object Name,@{Name='IdentityType';Expression={$_.processModel.identityType}}
# Expect ApplicationPoolIdentity. Keep ASPNETCORE_ENVIRONMENT=Production.
& 'C:\AdminTools\DeploymentPortal\Initialize-PortalStorage.ps1' `
    -AppPoolName 'DeploymentPortal'
Get-Acl "$env:ProgramData\DeploymentPortal\DataProtectionKeys" |
    Format-List Owner,AreAccessRulesProtected,AccessToString
```

Expected: owner Administrators, protected inheritance, SYSTEM/Administrators
FullControl and `IIS AppPool\DeploymentPortal` inheritable Modify. No Users,
Authenticated Users or Everyone grants. Provision **before** separately releasing
the updated application, because Production startup now requires this directory.
The script preserves existing key XML contents; never delete old keys to repair an
error. It does not modify appsettings, IIS identity, credentials or any CD flag.

At a quiet time with no queued/running deployment (IIS configuration changes can
also recycle the pool), provision the replacement local staging directory:

```powershell
& 'C:\AdminTools\DeploymentPortal\Initialize-PortalStorage.ps1' `
    -AppPoolName 'DeploymentPortal' -ProvisionCdStaging
Get-Acl 'C:\DeploymentPortalData\CdStaging' |
    Format-List Owner,AreAccessRulesProtected,AccessToString
```

This creates only the private SOURCE staging directory in addition to key storage;
it does not move old preparations, configure CD or contact TARGET. Set only SOURCE's
local root override (not Enabled or ExecutionEnabled). The following idempotent IIS
pool override is the exact command
for this approved directory:

```powershell
$poolEnvironment = "system.applicationHost/applicationPools/add[@name='DeploymentPortal']/environmentVariables"
$rootVariable = "$poolEnvironment/add[@name='TfsCd__LocalStagingRoot']"
$existing = @(Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $rootVariable)
if ($existing.Count -eq 0) {
    Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
        -Filter $poolEnvironment -Name '.' `
        -Value @{name='TfsCd__LocalStagingRoot';value='C:\DeploymentPortalData\CdStaging'}
} else {
    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
        -Filter $rootVariable -Name 'value' -Value 'C:\DeploymentPortalData\CdStaging'
}
```

Do not change `Automation:BackupRoot` blindly: it must remain a valid, distinct
application path and must not contain the staging or target paths. No staging
flag is enabled by these commands. All tracked CD flags remain false; keep
ExecutionEnabled false in SOURCE's operator configuration too.

The existing RemoteStagingRoot also lies beneath ProgramData and would conflict
with the remote deployment path policy if execution were enabled later. Leave it
and ExecutionEnabled unchanged in this task. A separately authorized remote-staging
setup must provision a valid private TARGET application directory before any future
execution. Do not reuse or grant the remoting account access to the SOURCE key ring.
Data Protection keys may reside under ProgramData because their dedicated storage
policy and DPAPI encryption are separate from deployment/staging path policy.

After the separate authorized release/configuration change, recycle only at a
quiet time with no queued/running deployment:

```powershell
Restart-WebAppPool -Name 'DeploymentPortal'
Get-ChildItem "$env:ProgramData\DeploymentPortal\DataProtectionKeys" -Filter '*.xml' |
    Select-Object Name,Length,LastWriteTime
```

The application should report persistent Windows DPAPI storage ready. Do not print
key XML or enable verbose request/body logging. On the initial switch from ephemeral
keys, old browser tokens and unrecoverable old protected preparations cannot be
restored: reload forms and re-prepare/review as appropriate; never bypass approval.
Thereafter retain the key directory and application name across releases/recycles.
Test antiforgery restart survival with a read-only form/cookie-token pair in an
isolated acceptance environment; never use a real deployment POST as a token test.
Cookies, identity changes, token expiry policies and normal antiforgery checks still
apply. Keep restricted, machine-aware backups of the encrypted ring if required;
DPAPI files alone do not provide portable recovery to a different machine.

For Build #9, inspect a fresh sanitized Stage/Reason log after the authorized release.
If local preparation is already authorized/enabled, retry only local Stage and verify
the protected details show Demo / definition 2 / drop / CdDemo / C:\Sites\CdDemo.
Do not enable CD or run StageRemote, Review, Confirm or any deployment for this check.

References: [Microsoft key storage defaults](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings?view=aspnetcore-10.0),
[DPAPI key encryption](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-encryption-at-rest?view=aspnetcore-10.0).
