# Explicit single-account Pipeline CI/CD mode

Implemented on Dev, 2026-10-17. This mode supersedes the earlier prohibition on using the protected CI account for CD **only when explicitly enabled as described below**. No SOURCE settings, credentials, IIS, SQL, Netserver, deployment scripts or production gates were modified.

## Behavior and exact changes

- `PipelineWorkgroupOptions` adds `ServiceCdEnabled` (default false) and `ServiceCdUsers` (empty by default). CI allowlist membership grants no CD authority.
- `PipelineWorkgroupTransport` selects single-account CD only when that switch is true. A real authenticated WindowsIdentity and explicitly allowlisted Portal Windows SID are required. Anonymous, claims-only, unlisted and invalid-identity configurations fail closed. No personal/delegation fallback occurs while this mode is selected.
- Before every eligibility/decision callback, connectionData must verify the configured `ServiceIdentityId` GUID and the exact `authenticatedUser.descriptor` Windows identity type/SID against `ServiceCdIdentitySid`. Missing/mismatched/malformed stable identity evidence denies the operation. Display names and Account labels are never authorization evidence. The GUID is shared with existing CI configuration, ensuring the configured CI/CD actor is the same account. CI implementation is unchanged.
- `TfsGitClientConfiguration` adds a separate `PipelineServiceCd` client using the existing `ITfsWindowsCredentialProvider`, collection-bound Windows Negotiate/NTLM authentication, disabled redirects/proxies and unchanged certificate validation. Basic/Digest authentication are not offered. Passwords/PATs are never sent as HTTP Basic credentials. No credential is supplied to a browser or application log.
- `PipelineActionGuard` supports an explicit service-CD scope: GET plus PATCH of a positive release approval ID within the configured collection. It denies build writes, release creation, deployment writes, explicit Authorization/Host/Proxy-Authorization headers and collection escapes. CI's guard remains unchanged.
- `PipelineService` retains release/build/environment correlation, successful CI/published artifact requirements for approval, current-attempt/rank/approver/requester restrictions, revision-bearing buffered PATCH and same-actor confirmation GET. TFS remains the final permission authority. Existing and future releases are selected individually by their actual IDs; a queued release without an eligible current approval remains disabled.
- Decision request/confirmation logs include Portal Windows SID and verified TFS GUID; transport logs identify single-account mode and the verified account name with request reference. TFS decision comments also identify the Portal SID and actual TFS actor. No delegated personal identity is claimed.
- `PipelineIdentityController` hides/rejects personal token connection while single-account CD is selected, avoiding collecting an unused personal PAT. Setting ServiceCdEnabled=false restores the separate existing personal/delegated authentication paths.
- Existing per-release Approve & Deploy / Reject confirmation dialogs, antiforgery protection, duplicate-click suppression and no automatic retry/decision behavior remain in place. Dashboard, existing CI and IIS authentication are unchanged.

Tests extend `PipelineWorkgroupTests.cs` for default-off/separate allowlist rules, anonymous/forged/unlisted callers, wrong GUID/account identity, client selection, guarded writes and personal-mode separation. `PipelineTests.cs` exercises the actual service through the new transport for approval and rejection, duplicate build releases, self-approval restrictions, revision preservation, SID/actor comments and disabled execution gates. No live TFS calls are used by these tests.

## Exact SOURCE configuration required (not applied)

Keep the existing protected Windows credential store and `TfsGit:BaseUrl` unchanged. Its actual account must already be `fff\saad.bilal`. If it is not, stop: changing the protected credential requires separate authorization. The Portal verifies the account from TFS rather than trusting a browser username. No personal PAT is needed for this mode.

Use an authorized application configuration channel with these non-secret settings. Angle-bracket values are placeholders and must be resolved from real authenticated identities:

| Setting | Value |
|---|---|
| Pipeline:Workgroup:ServiceIdentityId | 332a7e40-5225-4a57-bfd1-a282d6585357 |
| Pipeline:Workgroup:ServiceCdIdentitySid | S-1-5-21-2360516362-3028474152-1589833627-10171 |
| Pipeline:Workgroup:ServiceCdEnabled | true, only after explicit owner authorization |
| Pipeline:Workgroup:ServiceCdUsers:0 | Actual authenticated Portal Windows SID authorized to decide as this shared actor |
| Pipeline:Workgroup:ServiceCdUsers:1, etc. | Each additional explicitly authorized Portal SID, if any |
| TfsCd:Enabled | true, through separately authorized production configuration |
| TfsCd:ExecutionEnabled | true, through separately authorized production configuration |

Existing CI settings remain in effect: `Pipeline:Ci:QueueEnabled`, `Pipeline:Workgroup:ServiceCiEnabled`, and the separate `Pipeline:Workgroup:ServiceCiUsers` allowlist. For single-account CI, ServiceCiEnabled must already be true and ServiceIdentityId must identify fff\saad.bilal. Do not change a working CI allowlist merely to enable CD.

Environment-variable equivalents for new CD settings:

```text
Pipeline__Workgroup__ServiceCdEnabled=true
Pipeline__Workgroup__ServiceCdUsers__0=<ACTUAL-PORTAL-WINDOWS-SID>
Pipeline__Workgroup__ServiceIdentityId=332a7e40-5225-4a57-bfd1-a282d6585357
Pipeline__Workgroup__ServiceCdIdentitySid=S-1-5-21-2360516362-3028474152-1589833627-10171
TfsCd__Enabled=true
TfsCd__ExecutionEnabled=true
```

The Portal SID belongs to the actual Windows login accepted by SOURCE IIS; it may be a SOURCE account and must not be guessed from the TFS name. Each listed Portal user is explicitly authorized to act **as the same TFS account**. TFS history will record fff\saad.bilal; Portal audit distinguishes who initiated the decision. No per-user TFS mapping is required in this mode.

Open https://deployment-portal.vitonta.com/Pipeline from an authorized browser, select the application/environment and inspect the exact release row. An eligible user explicitly confirms each Approve & Deploy or Reject. Login, listing, polling, refresh and eligibility checks never decide anything. The TFS actor must have actual approver/group membership and appropriate release permissions; requester/self-approval restrictions still apply to fff\saad.bilal even when another Portal user clicks.

SOURCE's tfs-cd.json switches are reported false. Configuration overlays may override those files; inspect the effective application configuration through existing administrative procedures. This implementation does not turn either gate on automatically and requires no localhost Portal, IIS or Netserver changes.

To use personal authentication separately, set ServiceCdEnabled=false and retain the existing personal-authentication settings and HTTPS requirements. Existing sessions do not bypass the single-account allowlist while that mode is enabled.

## Operational limits

No actual SOURCE identity, permission, credential-store contents, release 16/17 state or live browser decision was verified in this task. Enabling the mode grants no new TFS permissions, and incorrect or missing connectionData account evidence fails closed. Deploy/configuration changes and real release decisions require separate authorization. No commit, push, deployment or real approval/rejection was performed.

## Validation commands

```powershell
dotnet test tests/DeploymentPortal.Tests/DeploymentPortal.Tests.csproj --no-restore --logger 'console;verbosity=normal'
& 'C:/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Microsoft/VisualStudio/NodeJs/node.exe' tests/pipeline-client.test.cjs
& 'C:/Program Files/Git/cmd/git.exe' diff --check
```

The JavaScript tests execute the actual page script with synthetic DOM/HTTP; all 14 pass, including exact release approval/rejection, explicit confirmation cancellation, duplicate-click prevention and personal-auth behavior. Backend tests also use synthetic HTTP and credentials, without real release decisions. Existing Pkcs 9.0.4 dependency advisories NU1902/NU1903 remain.

Full backend run before the final clean rebuild: **587 passed, 3 failed, 1 skipped (591 total)**. All single-account transport/service and Pipeline tests passed. The three failures are the existing `PowerShellCredentialProvisioningTests` cases for PortalStorage.Tests.ps1, WinRmCredentialProvisioning.Tests.ps1 and TfsCredentialProvisioning.Tests.ps1: UnauthorizedAccessException creating synthetic credential directories with private ACLs in this execution environment. The CD IIS workflow test passed in the final run. No security/deployment scripts or ACL rules were weakened to make these tests pass. Git whitespace check passed.

A final `dotnet build --no-restore --no-incremental` succeeded (0 errors). The rebuilt Pipeline suite passed **146/146**, including the new personal-mode separation test. The final broad run using that rebuilt assembly and excluding only the three known credential-provisioning ACL cases passed **588 tests, 1 skipped, 0 failures (589 total)**; both PowerShell CD-security cases passed. Command: `dotnet test tests/DeploymentPortal.Tests/DeploymentPortal.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~PowerShellCredentialProvisioningTests' --verbosity quiet`. No application code changed after that rebuild.
## TFS 2018 identity verification correction

The supplied real response identifies fff\saad.bilal by GUID `332a7e40-5225-4a57-bfd1-a282d6585357` and Windows SID `S-1-5-21-2360516362-3028474152-1589833627-10171`. Its providerDisplayName is `Saad Bilal` and its properties.Account.$value is `saad.bilal`; neither label is used for authentication. CD now requires the expected configured GUID and an exact descriptor of `System.Security.Principal.WindowsIdentity;` followed by the independently configured ServiceCdIdentitySid. It inspects authenticatedUser only, never authorizedUser, browser claims, display names or account-name lookalikes. Missing SID configuration denies CD; it never derives its own expected SID from the response. The TFS actor SID is independent of every Portal SID in ServiceCdUsers. The settings above are documentation only, based on the user-supplied evidence; no SOURCE settings or protected credentials were changed. CI identity verification remains unchanged.
Identity-correction validation: clean non-incremental build succeeded; **275 relevant backend tests passed, 0 failed**, covering Pipeline, TFS release, protected Windows authentication and TFS Git security. **14 client tests passed**. Regression coverage includes the exact supplied TFS 2018 response, display-label changes, correct account/display strings with wrong GUID/SID, wrong descriptor authority, prefix/suffix spoofing, missing/malformed descriptors, invalid/missing expected SID, malformed authenticatedUser objects, and ensuring the decision callback is never reached after a mismatch. Existing Portal SID authorization, default-off mode, execution gates, CI transport, approval/rejection policies and personal-mode tests also pass. Existing dependency/analyzer warnings remain. Git whitespace validation passed. No live TFS requests, approvals, rejections, production configuration changes, commit or push were performed for this correction.
