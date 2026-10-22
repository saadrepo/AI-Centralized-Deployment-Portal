# SOURCE Pipeline CD review — 2026-10-17

Implemented on Dev; no commit, push, CI trigger, approval, rejection or deployment performed.

## Exact changes

- `Services/PipelinePendingReleases.cs`: each discovered release/approval now returns independent CanApprove/approveReason and CanReject/rejectReason. Approval requires the existing personal TFS eligibility checks plus a successful correlated CI build and expected published artifact. Missing/unreadable CI metadata disables approval while retaining an otherwise eligible rejection. Existing current-attempt filtering, pagination, operation status, deployment queue ID, execution policy and unresolved-release evidence remain in use. No release IDs/build IDs are hardcoded. Bounded metadata pagination fails explicitly rather than silently returning partial results beyond its limit.
- `Services/PipelineService.cs`: both decisions require positive explicit release/approval IDs and resolve that exact release, validating its application/build/definition/environment mapping. Multiple releases for one build no longer prevent an explicitly selected approval. The summary for an ambiguous selected build remains disabled; choose its individual release row. Confirmed-decision audit now records decision, application, build, release, instance environment, approval, Portal user and verified TFS actor. Existing request timing/transport audit supplies request references. TFS remains the final permission/revision authority, and success still requires a same-user confirmation GET.
- `wwwroot/js/pipeline.js`: pending-release rows offer Approve & Deploy and Reject with separate eligibility/tooltips. Confirmation names exact release, build, environment and approval. The main approval action now also requires confirmation. Actions submit the row's build/release/approval, disable duplicate submission, and refresh release rows independently of selected-build status. No automatic decision retries.
- `tests/DeploymentPortal.Tests/PipelineTests.cs`: adds exact approval targeting with duplicate build releases and independent approval/rejection eligibility without retained artifacts; extends row eligibility assertions and adjusts stale-release fixture for direct lookup.
- `tests/pipeline-client.test.cjs`: adds approval targeting across selected builds and declined main approval confirmation tests.

## Read-only live findings and limits

User-provided evidence: Release 16 / Build 28 pending, Release 17 / Build 30 queued; SOURCE tfs-cd.json Enabled=false and ExecutionEnabled=false. These release states were not independently verified here.

The repository also defaults both CD switches to false. Program.cs loads tfs-cd.json and then overlays builder.Configuration, so a JSON file alone does not prove effective deployed values. SOURCE administrative-share access was denied; effective SOURCE application/environment overrides could not be inspected. No SOURCE settings were changed.

Windows-authenticated GET to http://netserver:8080/tfs/DefaultCollection/_apis/connectionData?api-version=4.1 returned HTTP 401 under this process identity. SOURCE HTTPS /Pipeline/Identity and /Pipeline/Options could not authenticate: underlying SSPI reported "No credentials are available in the security package". This does not establish the outcome for an authorized browser or fff\saad.bilal.

HTTPS netserver:443 refused the connection at 192.168.0.234. This establishes only that the probed binding was unavailable; another existing HTTPS port/binding has not been ruled out. No password/PAT was supplied, no certificate validation bypassed, and all live requests were GETs. No live release eligibility, approver membership, actual TFS GUID, delegation, effective SOURCE overrides or Release 17 queue predecessor was verified. Release 16 must not be called the confirmed queue blocker without TFS evidence.

## Required configuration — documented only, not applied

A separately authorized owner must deploy the code and provide effective `TfsCd:Enabled=true` and `TfsCd:ExecutionEnabled=true` on SOURCE. This task does not authorize removing intentional execution gates. Keep SOURCE Production.

For the existing personal-authentication path:

| Setting | Required value |
|---|---|
| Pipeline:Workgroup:PerUserAuthenticationEnabled | true |
| Pipeline:Workgroup:PersonalCollectionUrl | An already existing trusted HTTPS collection URL with the same host/path as TfsGit:BaseUrl; port may differ |
| Pipeline:Workgroup:PerUserTfsIdentities:<actual-Portal-Windows-SID> | Actual personal TFS GUID for fff\saad.bilal, verified by authenticated TFS connectionData |

The Portal Windows SID may represent a SOURCE account and must be obtained from the real authenticated Portal identity. Do not infer a SID/GUID from the text username. The mapped TFS GUID must differ from the configured CI service identity. Each other authorized Portal user needs their own mapping and personal TFS approver identity.

From https://deployment-portal.vitonta.com/Pipeline, that user connects their own personal TFS PAT over HTTPS. Both legs must use trusted TLS. The existing ten-minute server-memory session is bound to the Portal Windows SID and verified TFS GUID; each operation rechecks the TFS identity. The user's actual release approver/group membership, requester restrictions, approval rank and permissions still apply. CI service credentials are never used for CD. No localhost Development Portal is needed.

Alternative: an already available domain/Kerberos deployment providing a genuinely delegated Windows token and a TFS-confirmed matching SID can use the existing Windows transport. Changing IIS/SPNs/delegation/domain configuration is outside this task and requires separate infrastructure authorization.

If neither an existing trusted HTTPS TFS endpoint accepting the personal user's PAT nor verified Windows delegation is available with Netserver unchanged, secure SOURCE approval cannot work under the current constraints. Viable choices are to provide an existing secure endpoint/delegation through separately authorized configuration, obtain separate authorization for required infrastructure work, or perform decisions directly in the existing TFS UI using the user's own authenticated account. Sending a PAT/password over HTTP or approving with the CI actor is not an option.

A queued release with no current eligible pre-deployment approval stays disabled. Inspect TFS deployment queue policy, latest operation status, agents, gates and unresolved approvals; never approve/reject an unrelated release merely to unblock a queue.

## Validation

- Focused backend Pipeline/TFS release/Windows identity tests: 192 passed.
- Broader backend run excluding the two environment-sensitive PowerShell test classes: 566 passed, 1 skipped, 0 failed.
- Those PowerShell classes separately: 1 passed, 4 failed. Three failures are UnauthorizedAccessException while creating synthetic credential test directories with private ACLs; CdIisWorkflow.Tests.ps1 exceeds its 30-second test timeout. No deployment scripts were changed to mask these failures.
- Client tests executing the actual JavaScript: 14 passed. Used the Visual Studio Node executable directly because node --test child-process spawning is denied in this sandbox.
- Git whitespace check passed. Build emits existing Pkcs 9.0.4 NU1902/NU1903 advisory warnings.

SOURCE configuration, IIS, SQL, secrets, deployment scripts and Netserver were untouched. Existing untracked workspace material was preserved. Live browser end-to-end operation remains unverified for the authentication/access reasons above.
