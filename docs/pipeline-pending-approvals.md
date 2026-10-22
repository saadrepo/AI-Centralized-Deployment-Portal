# Pending release approvals

## Implementation

- New authenticated read-only PendingReleases endpoint validates the registered CI pipeline, release definition, project, artifact build ID and definition environment, then reads release details and current-attempt approval/environment statuses. Releases are sorted newest first, without assuming release names or build numbers equal TFS IDs.
- A scoped Pending Releases / Approval Status card refreshes with live status polling. Reject buttons require backend eligibility and confirmation naming the release, build and approval. Stale selection/read responses are discarded. Only the selected approval is submitted, without mutation retries.
- Reject and Approve share the existing execution gate, delegated current Windows identity, caller eligibility/group check, requester restriction, approval rank and current-attempt rules. Both use the existing TFS 2018 `PATCH release/approvals/{id}?api-version=4.1-preview.2` route, carry the revision, and re-read confirmation as the same caller. Reject sends status `rejected`; it does not cancel or redeploy an environment.
- The existing Approve & Deploy action continues to select only the release correlated with the selected successful build/artifact and environment. It does not create releases, approve every release, or start deployment through an environment-status bypass.
- Any rejected approval in the current attempt blocks further approval and leaves deployment/health pending in the Portal. No automatic reapproval or redeployment was added. CI history and live CD logs remain available.

Microsoft's legacy approval API documentation describes the list/filter and decision API: https://learn.microsoft.com/en-us/previous-versions/azure/devops/integrate/previous-apis/rm/approvals?view=tfs-2017

## Environment verification still required

A read-only request to the specified TFS collection failed with WebException from this execution environment. Release-7 through Release-12, the Build #23 association, and the actual reason for their waiting state could not be independently verified. No live approval/rejection/deployment was attempted.

Inspect in TFS: `Portal-CD > SOURCE-Portal > Pre-deployment conditions > Deployment queue settings`. Check the maximum concurrent deployments and subsequent-release policy (including a sequential deployment policy). The new card displays the definition's actual `executionPolicy.concurrencyCount` and `executionPolicy.queueDepthCount`, when returned, rather than asserting that older approvals are definitely the blocker. Also check pending post-deployment approvals, deployment gates/conditions and agent availability if the latest release remains queued. No queue-policy change is required by this implementation; do not change policies automatically or remove approvals to resolve a queue.

To operate Approve/Reject, effective `TfsCd:Enabled` and `TfsCd:ExecutionEnabled` must already be true in the authorized environment. The Portal also requires a delegated Windows login accepted by TFS and an eligible approver; local Development also accepts an identical real browser/process Windows SID, verified against the remote TFS SID before permission checks; it grants no approval authority by itself. If these conditions are absent, actions remain disabled. No configuration, IIS delegation, credentials or permissions were changed. Any required production change must be reviewed by the environment owner separately.

Select the real build corresponding to #23 and the discovered SOURCE-Portal environment, check the displayed matching Release ID, then use Approve & Deploy only when authorized. Old pending approvals can be individually rejected after confirming their actual IDs; the Portal never rejects them automatically.

## Changed files

- `src/DeploymentPortal/Services/PipelinePendingReleases.cs` (new)
- `src/DeploymentPortal/Services/PipelineService.cs`
- `src/DeploymentPortal/Controllers/PipelineController.cs`
- `src/DeploymentPortal/Views/Pipeline/Index.cshtml`
- `src/DeploymentPortal/wwwroot/js/pipeline.js`
- `tests/DeploymentPortal.Tests/PipelineTests.cs`
- `tests/pipeline-client.test.cjs`
- This document.

Dashboard, production JSON, sites registry, credentials, Data Protection keys, SQL, IIS, deployment scripts, staging and backup folders were not modified. No commit or push was performed.

## Validation

- Solution build succeeded with 0 errors. Existing Pkcs 9.0.4 vulnerability advisory warnings remain.
- Pipeline tests: 79 passed, 0 failed. Includes read-only release correlation, releases without approval records, exact rejection PATCH/confirmation, wrong-release rejection, requester restrictions, rejected-state deployment blocking, anonymous endpoint denial and antiforgery checks.
- Browser-script fixture tests: 8 passed, 0 failed. Includes rejection confirmation, duplicate-click prevention and declining confirmation without sending a mutation. These are mocked client tests, not live browser/TFS verification.
- JavaScript syntax and Git whitespace checks passed.
