# CD decision and queue-state fixes

## Confirmed code issues fixed

- Approval decisions used streaming `PatchAsJsonAsync`; they now use buffered UTF-8 JSON with Content-Length for classic TFS/IIS, as the CI queue action already does. HTTP failure handling records only the status, allow-listed upstream typeKey, TF numeric error code, stage and request reference. No raw upstream message, credentials or payload is exposed. The client never retries a decision automatically.
- Preflight errors identify their read/identity/eligibility stage and whether any decision was submitted. A successful PATCH is still followed by a same-caller approval read. A missing/mismatched confirmation is explicitly unconfirmed, not successful.
- Expanded release approval data can lag the approval-list endpoint. Status, pending-release listing and decisions now merge real pending approvals correlated by release ID AND instance environment ID. Current/unknown-attempt handling is consistent between display and eligibility; known older attempts remain excluded. Approver eligibility, requester restriction, rank, active release, rejected state and TFS revision/permission enforcement remain required.
- Reject resolves the explicitly selected release ID and validates its CI artifact/build/definition/environment mapping. It does not depend on a unique build-to-release search, successful CI outcome or retained artifact files. Approve still requires successful CI and the expected published artifact. Neither action creates or redeploys a release.
- Confirmed rejection refreshes the Pending Releases panel independently and concurrently with selected-build status, so slow/failing status no longer prevents that refresh. Safe decision errors survive subsequent polling and disappear when the selection changes or a new action starts.
- Queued releases cannot be approved without an actual eligible pending pre-deployment approval. The UI now explains missing approval/queue state and individual denial reasons. Pending-release rows display actual operation status and deployment queue ID when available, plus execution policy and unresolved release IDs. These IDs are possible predecessors, not a fabricated assertion that any one release is the blocker.

## Live evidence and remaining limitation

Read-only Windows-authenticated GETs to the configured TFS project for release IDs 10 and 12 both returned HTTP **401 Unauthorized** under this execution environment's Windows credentials. No PATCH was sent. This is not proof of the browser visitor's permission outcome, nor proof of the original rejection PATCH error. Therefore the actual Release-10 rejection response and the exact Release-12 queue predecessor remain unverified. The next authorized user operation can now display its safe HTTP/type/TF-code diagnostics rather than the previous generic message.

Inspect `Portal-CD > SOURCE-Portal > Pre-deployment conditions > Deployment queue settings` alongside the observed environment operation statuses and current approvals. Sequential queue policy, earlier unresolved deployment/post-deployment approval, gates and unavailable agents remain possibilities until TFS provides evidence. No policy was changed, and no pending predecessor was automatically rejected.

## Validation

- Focused Pipeline/configuration/Windows authentication tests: 101 passed, 1 interactive SSPI test skipped, 0 failures.
- Actual page JavaScript with mocked DOM/HTTP: 10 passed, including immediate rejection-panel refresh despite stalled selected status and preservation of a failure reason through polling.
- New backend cases cover buffered decisions, rejection without retained artifacts, direct release targeting with duplicate build releases, safe HTTP 400/403/409 diagnostics, preflight known-not-submitted errors, queued live approvals without attempt metadata, and rejection of another environment's approval.
- Existing Pkcs dependency advisory warnings remain. No live browser mutation or TFS mutation was used for validation.

Changed files: PipelineService.cs, PipelinePendingReleases.cs, new PipelineApprovalException.cs, PipelineController.cs, pipeline.js, PipelineTests.cs, pipeline-client.test.cjs and this report. Dashboard, production settings, credentials, SQL, IIS and release scripts were untouched. No real approval/rejection/deployment, commit or push was executed.
