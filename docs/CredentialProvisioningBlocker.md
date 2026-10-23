# Browser credential provisioning: blocked

Historical assessment of the earlier request to accept PATs and provision from the browser. The subsequent manual-command help and metadata-refresh workflow is implemented as described in [SourcesManagement.md](SourcesManagement.md). That workflow does not accept PATs or require a provisioning service; the blocked service design below is not a prerequisite for using the manual helper.

Reviewed locally on 2026-10-23. Browser provisioning is not implemented. The Add/Edit Source forms, credential endpoints, configuration, SQL schema and existing TFS behavior remain unchanged. No SOURCE deployment, service installation, push or CI/CD action was performed.

## Existing boundary

- `tools/Manage-SourceCredential.ps1` is an interactive, elevated host administration tool. It explicitly forbids execution from a web request or as the application pool identity.
- Source credentials use machine-scoped DPAPI and the existing `DeploymentPortal.SourceToken.v1` reference/provider/canonical-destination binding. The protected store is `%ProgramData%\DeploymentPortal\SourceCredentials`.
- The script and `SourceCredentialProvider` require administrator/SYSTEM ownership, administrator/SYSTEM write access, and application pool read access. The reader calls the shared `ProductionCredentialProvider.ValidateSecurity`, which rejects an additional service writer. A nonadministrative service creating files would also fail the existing owner check. Simply granting a service account write access would make the store fail validation.
- `SourcesManagement` authorizes approved Windows user SIDs. Membership in that allowlist does not establish administrator authorization. Reusing it alone for provisioning would not meet the requested boundary.
- There is no existing provisioning service, authenticated service channel, independent provisioning administrator policy, or provisioning audit sink in the project.

This is not a safe drop-in modal change. A separate least-privilege writer needs a source-specific security contract and integration verification. Running the existing script under IIS, using an elevated generic command runner, granting IIS write access, or treating Source Management access as administrator approval would violate the requested design. No such fallback was added. The missing service can be designed, but its security boundary has not been implemented or validated by this change.

## Required implementation and eventual SOURCE setup

These are prerequisites for a future implementation, not instructions to deploy a service that already exists:

1. Implement a narrowly scoped Windows service exposing only GitHub credential provisioning, with bounded inputs and no arbitrary commands, paths, provider URLs or PowerShell execution. Run under a dedicated nonadministrative service identity. Protect its binaries, configuration and service control permissions from IIS and unprivileged users.
2. Define and test a source-specific ACL validator and compatible host management tool. Allow the dedicated writer only the required source-store access, preserve IIS read-only access, reject reparse points and insecure owners/ACLs, and keep existing TFS/WinRM ACL validation unchanged. Preserve the existing DPAPI payload and binding. Do not alter real store ACLs before the new contract and migration are reviewed.
3. Provision a mutually authenticated, confidential local service channel, for example certificate-authenticated TLS with protected nonexportable private keys and pinned peer identities. Define certificate issuance, renewal and revocation. Do not use a shared secret in configuration. Reject wrong clients, service impersonation, redirects, plaintext transport and oversized requests. The service must bind only to its intended local endpoint.
4. Configure an independent, deny-by-default provisioning administrator allowlist. The portal must derive the caller from native Windows authentication, never a posted SID/header. Define how the service verifies the portal's authenticated caller assertion, independently enforces the provisioning allowlist, and prevents replay. Source managers must not implicitly receive provisioning permission.
5. Preconfigure a protected audit destination. Record authenticated caller, operation ID, canonical destination, generated reference, time and sanitized outcome. Record authorization failures as well as successful writes. Define audit failure handling and atomic-write/retry behavior. Never record PATs, request bodies, authorization material or secret-bearing exception details. Disable body tracing/capture and automatic retries on both sides.
6. After that boundary passes local integration testing, add the administrator-only **Credentials Provisioning** text link beside Credential Reference in the shared Add/Edit form. Use a separate modal form with a password input and **Provision Credential** submit button. Require HTTPS, administrator authorization, antiforgery and no-store responses on the portal POST. Do not place the PAT in source models, SQL, ModelState, TempData, URLs, browser storage or logs. Clear the field on submission/close and return only reference metadata. Refresh the protected metadata endpoint after success and select the returned reference only after matching provider and canonical destination.
7. Preserve GitHub organization validation: `https://api.github.com/orgs/{organization}/`. Personal accounts, `/users/`, GitHub Enterprise and account-type conversion remain unsupported. Preserve TFS connection selection and existing credentials.
8. Before any SOURCE installation, verify the service with synthetic credentials locally: unauthorized/nonadministrator requests, forged caller assertions, CSRF, incorrect certificates, replay, audit failure, malformed/oversized PATs, destination mismatch, insecure ACLs, atomic-write failure, concurrent provisioning, secret redaction, dropdown refresh/selection, and TFS regressions. SOURCE setup would then require independently approved administrator SIDs, service installation, identity/ACL provisioning, channel certificates, audit storage and production acceptance checks. None of these host changes was performed.

## LOCAL verification

Command:

```powershell
dotnet test tests/DeploymentPortal.Tests/DeploymentPortal.Tests.csproj --no-restore --filter 'FullyQualifiedName~SourceCredentialTests|FullyQualifiedName~SourcesManagementAuthorizationTests|FullyQualifiedName~SourcesEndpointTests|FullyQualifiedName~InfrastructureSourceTests|FullyQualifiedName~TfsGitSecurityTests|FullyQualifiedName~TfsWindowsCredentialTests' --verbosity minimal
```

Result: **150 passed, 0 failed, 0 skipped**. Build emitted an existing xUnit2031 warning in `PipelineTests.cs:858`. These verify existing behavior; there is no new provisioning implementation to test.

The existing synthetic `tests/PowerShell/SourceCredentialProvisioning.Tests.ps1` was also executed using Windows PowerShell's absolute path. It exited with an access-denied error deleting its temporary fixture, so it is **not a pass**, and the cleanup exception prevents establishing the original operation's result. It did not run the production management script against the real store. Its temporary fixture path was `.tools/source-credential-tests/a4001242970e4b6fa758a430ae7439cf`; cleanup may require the fixture owner's permissions. No real PAT was supplied.
