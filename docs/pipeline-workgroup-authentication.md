# Workgroup SOURCE: CI service actor and personal CD actor

## Implemented behavior

- Existing LOCAL delegation/same-interactive-user behavior remains the default. Workgroup capabilities are explicitly configured and default off.
- Optional CI service transport reuses **ITfsWindowsCredentialProvider** and its existing DPAPI/NTFS-protected collection credential. It requires a real authenticated Portal Windows SID in an explicit CI allowlist, validates the actual TFS connectionData user GUID against an approved service identity GUID, and records Portal SID, actual TFS actor and request reference in server logs. Existing application/build/branch validation, queue enablement, duplicate prevention and TFS permissions still apply. TFS records the real service actor; no requestedFor impersonation is sent.
- A dedicated HTTP guard permits CI build queue/cancellation writes only, within the configured collection. The original shared explorer/read client stays read-only. CI service credentials cannot PATCH a release approval or create a release. The service CI allowlist also controls service-backed Stop builds actions, which still need the service actor's effective TFS Stop builds permission.
- Workgroup CD uses the **individual approver's own TFS PAT**, entered through a separate personal-identity panel. The administrator must map the real Portal Windows SID to that person's actual TFS identity GUID. Connect verifies connectionData; every subsequent CD/eligibility operation revalidates the token's actual TFS user. A token for another person or the configured CI service actor is refused. TFS continues to enforce approver membership, rank, revision and requester restrictions. Approval decisions never use CI service credentials.
- The personal session is server memory only, fixed at ten minutes, bounded to 100 sessions, bound to Portal SID, TFS ID and exact HTTPS collection URL. The browser receives only a random Secure/HttpOnly/SameSite=Strict session cookie; PAT input is cleared immediately after reading it for the POST, never echoed or stored in browser storage, and excluded from session serialization/ToString. Disconnect, expiry or application recycle removes access. No existing secret store or Data Protection keys are modified.
- Both Portal and personal TFS endpoint must use trusted HTTPS. Redirects are blocked, certificate validation is not weakened, and tokens cannot be sent to HTTP or another collection/host. A separate HTTPS binding for the same TFS host/path can be specified while keeping the existing service/read HTTP endpoint and its credential binding unchanged.
- Pending releases, status, CI history, CD logs and decision checks retain their existing services. New personal login only enables identity verification; it does not grant approver rights.

## Manual administrator setup — not applied by this change

1. Keep SOURCE in Production. Keep its existing Windows login setup for Pipeline; workgroup users may authenticate to IIS as their approved SOURCE Windows accounts. Identify the **actual Portal account SID**, not a browser-submitted username or a display name. Use `whoami /user` in that account's Windows session, or an administrator's approved account/SID lookup.
2. Reuse the already provisioned TFS collection credential. Do not rotate/rewrite it merely for this feature. Verify that its actual connectionData `authenticatedUser.id` is the approved **CI service TFS GUID**, and give that TFS account only required View/Queue build permissions for configured definitions. Stop builds is optional. No release approval authority is required for this service actor. The existing provisioning tool is `tools/Provision-TfsGitCredential.ps1`; do not run it against a working store without a separate credential-provisioning authorization.
3. Through an administrator-approved application configuration channel, supply these **non-secret** settings (examples are placeholders, not production values). Environment-variable names use `__` in place of `:`; array index `0` supplies the first SID:

   | Setting | Required value |
   |---|---|
   | `Pipeline:Ci:QueueEnabled` | `true` for authorized CI operation |
   | `Pipeline:Workgroup:ServiceCiEnabled` | `true` to opt into service-backed CI |
   | `Pipeline:Workgroup:ServiceIdentityId` | Actual approved TFS service-user GUID |
   | `Pipeline:Workgroup:ServiceCiUsers:0` | Actual allowed Portal Windows SID; add indexed entries per authorized CI user |
   | `Pipeline:Workgroup:PerUserAuthenticationEnabled` | `true` for personal CD login |
   | `Pipeline:Workgroup:PersonalCollectionUrl` | Trusted HTTPS URL for the **same** host/path as `TfsGit:BaseUrl`, e.g. `https://netserver/tfs/DefaultCollection/` only after that binding exists |
   | `Pipeline:Workgroup:PerUserTfsIdentities:<PORTAL-SID>` | That person's actual personal TFS-user GUID |

   CI allowlist membership does not grant CD authority. CD still requires existing effective `TfsCd:Enabled=true` and `TfsCd:ExecutionEnabled=true`. Do not remove an intentional execution gate. No production JSON/environment/IIS settings have been altered by this code change; the owner must approve and apply the needed configuration separately.

4. For personal CD authentication, the TFS administrator must provide a trusted HTTPS binding to the same collection host/path. The current `http://netserver:8080/tfs/DefaultCollection` is permitted for the existing Windows service credential path, **not for PAT transmission**. If no trusted HTTPS TFS endpoint exists, personal CD login remains disabled. This change does not install certificates, change TFS/IIS bindings, or reprovision the existing HTTP credential store.
5. Each approver creates their **own** short-lived TFS PAT using the TFS user security/PAT UI, with minimum release read/manage scope available in that TFS version. PAT scope does not override the user's release permissions. Have the TFS administrator verify the individual's pre-deployment approver/group membership and requester restrictions. Do not use a service-account PAT or map every Portal user to a shared TFS account. Determine the actual personal TFS GUID with a read-only authenticated `_apis/connectionData?api-version=4.1` request under that person; never paste the PAT into a URL, config file, logs or chat.
6. TFS must accept PAT authentication; IIS **Basic Authentication must not be enabled as a workaround**, because it interferes with server PAT authentication. The administrator should inspect the existing TFS authentication policy before any change. If that TFS installation/policy does not support personal PATs, use an approved domain/Kerberos delegation deployment instead; the Portal will not fall back to shared-account approvals.
7. After a separately authorized application release/configuration restart, open SOURCE `/Pipeline` over HTTPS. The personal identity panel appears for configured mapped users. Connect the user's PAT, verify the selected build/release/environment, and view current eligibility. No build or approval is executed by Connect. Test actual build/approval/rejection only with separate operational permission. LOCAL can keep its current working Windows path; the same personal path is available on LOCAL HTTPS when configured.

Microsoft references: [Personal tokens and Azure DevOps Server authentication caveats](https://learn.microsoft.com/en-us/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate), [REST authentication and server collection endpoints](https://learn.microsoft.com/rest/api/azure/devops/).

## Limits and changed files

No live SOURCE identity, TFS permissions, service account or PAT was verified by executing a real operation in this task. Configuration, trusted HTTPS and actual user mappings remain environmental prerequisites. No live build/approval/rejection/deployment, commit or push was performed. Production JSON, credentials, SQL, IIS, Dashboard and release scripts were not modified.

Changed files:

- `Services/PipelineWorkgroupTransport.cs` (new)
- `Controllers/PipelineIdentityController.cs` (new)
- `Controllers/PipelineController.cs` (denied-action audit)
- `Services/TfsGitClientConfiguration.cs`
- `Services/PipelineService.cs` (identity availability messages)
- `Views/Pipeline/Index.cshtml`
- `wwwroot/js/pipeline.js`
- `tests/DeploymentPortal.Tests/PipelineWorkgroupTests.cs` (new)
- `tests/pipeline-client.test.cjs`
- This report.

## Validation

Solution build succeeded (0 errors; existing Pkcs dependency advisory warnings remain). Focused tests: 128 passed, 1 interactive SSPI test skipped, 0 failures. Client-script tests: 12 passed. Coverage includes default-deny service policy, real Portal SID versus forged claims, expected service actor verification, no service-to-CD fallback, per-user identity revalidation/mismatch, TLS/collection restrictions, CI/CD HTTP guard separation, secure session cookies, token non-disclosure, CSRF attributes and personal-login UI behavior. HTTP responses and tokens in tests are synthetic; no live TFS action or PAT was used. JavaScript syntax and Git whitespace checks passed.
