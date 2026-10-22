# CI queue failure correction

## What is confirmed

The old controller mapped errors from definition lookup, Git branch validation, Windows/TFS identity lookup, the queue POST, and accepted-response parsing to the same “TFS action outcome is unconfirmed” message. It did not retain an upstream status or the failing stage. Thus that message did **not** establish that a queue POST had been sent.

The new Git queue action also used `PostAsJsonAsync` streaming JSON, unlike the existing TFS client's buffered JSON pattern. It now sends a fixed-length UTF-8 JSON body with the actual definition ID, current definition's agent queue ID, project ID and selected full branch ref. The endpoint remains the configured collection/project `/_apis/build/builds?api-version=4.1`. No guessed definition/queue IDs or requested-for impersonation are used.

These are confirmed code defects/compatibility corrections. The original live server's exact rejection cannot be reconstructed from the old generic message. It would be incorrect to claim that Windows permissions, HTTP 411, or API version rejection caused that particular click without its response. No real queue POST was used to reproduce it.

## Safe live observations

Read-only probes of the configured internal collection's `/_apis/connectionData` route with `api-version=4.1`, `api-version=4.1-preview.1`, and no API version all returned HTTP 401 for the agent's sandbox default credentials. No response bodies or credentials were printed. This demonstrates an authentication limitation of that test identity; it does not prove the normal browser user's identity has the same limitation.

No runtime console capture of the user's original failing request was available. The new read-only readiness check diagnoses the active browser identity without sending a build request.

## Implemented behavior

- Authenticated `GET /Pipeline/QueueReadiness?applicationKey=...` validates CI eligibility, the actual TFS-authenticated Windows SID, and access to the application's configured definition/queue. It never queues, approves or creates a release. Page metadata loading calls this check before enabling Run CI Build.
- TFS Queue builds authorization is still enforced by TFS at POST time. A successful readiness check is not presented as proof of that mutation permission.
- The build-only connection-data read can fall back to the unversioned legacy GET route on HTTP 400/404. There is no fallback for HTTP 401/403, no credential fallback and no mutation retry. The approval identity path does not use this compatibility fallback.
- Before POST, the definition is re-read as the verified visiting user; its repository/project association, enabled queue status and agent queue ID are checked.
- Buffered JSON declares `application/json; charset=utf-8` and a computable Content-Length. The request does not set `requestedFor` or reuse the read-only/stored service credential for queueing.
- Queue and readiness operations have a bounded deadline; identity reading also has a deadline that covers response-body reading.
- Errors identify definition lookup, branch validation, TFS identity verification, visiting-user definition access, sending POST, or reading its accepted response.
- Confirmed pre-POST errors state that no queue POST was sent. Explicit rejection reports its HTTP status. A timeout/transport loss after POST, a server error, or a successful response without a valid build ID remains conservatively ambiguous and is never automatically retried.
- Browser code retains the diagnostic error and respects `mayHaveQueued` instead of labeling every rejection as ambiguous.

## Diagnostics and next local verification

Restart the existing local Development session, then open `/Pipeline` and use Refresh Status. Loading the page performs only the read-only readiness check; there is no need to click Run CI Build to test identity/definition access.

The Portal displays a safe reason, stage, code and request reference. Search the local VS Code terminal for that reference and `CI queue failed`. Structured logs include stage, status, whitelisted TFS `typeKey`, whether queueing might have occurred, application/definition/queue IDs, the sanitized endpoint path and payload byte count. Raw exception messages, HTTP bodies, JSON payloads, passwords, cookies, tokens, authentication headers and SSPI blobs are not logged.

Examples:
- `CI_TFS_HTTP_401` at identity verification: TFS Windows authentication failed before POST.
- `CI_TFS_IDENTITY_MISMATCH`: TFS did not confirm the signed-in Windows SID; no service-account substitution is permitted.
- `CI_TFS_HTTP_403`: verify the user's project/repository access and Queue builds permission on the definition.
- `CI_TFS_HTTP_404`: verify configured collection/project/definition and API route.
- `CI_DEFINITION_NOT_ENABLED` or `CI_AGENT_QUEUE_MISSING`: review the selected definition's existing queue configuration manually.
- `CI_TIMEOUT` after `sending queue POST`: refresh real recent builds before any manual retry.

The existing Development CI flag, production configuration and all approval gates/identity restrictions are unchanged by this correction. No production settings, credentials, database, IIS settings, deployment scripts, Dashboard, sidebar or design assets were changed.

## Changed files

- `src/DeploymentPortal/Services/PipelineQueueException.cs` (new safe error contract)
- `src/DeploymentPortal/Services/PipelineService.cs`
- `src/DeploymentPortal/Services/PipelineUserTransport.cs`
- `src/DeploymentPortal/Controllers/PipelineController.cs`
- `src/DeploymentPortal/wwwroot/js/pipeline.js`
- `tests/DeploymentPortal.Tests/PipelineTests.cs`
- `docs/pipeline-queue-diagnostics.md`

Automated checks use synthetic HTTP responses. They cover fixed-length queue payloads, no-mutation readiness, preflight rejection, HTTP 400/401/403/411, timeout ambiguity, malformed acceptance, safe controller diagnostics, read-only legacy compatibility and unchanged approval fallback rules. No live build, approval, deployment, commit or push was performed.

Validation: isolated solution build succeeded with zero compiler errors; existing Pkcs dependency advisories remain. Targeted pipeline/authentication run: 55 passed, 1 interactive-SSPI skip. Latest full run: 494 passed, 3 existing private-ACL sandbox failures, 1 skipped, 498 total. JavaScript syntax and Git whitespace checks passed. Build/test output was isolated under `.tools/pipeline-validation`; the running local Portal was not stopped.

[Microsoft TFS 4.1 build queue API](https://learn.microsoft.com/th-th/rest/api/azure/devops/build/builds/queue?view=vsts-rest-tfs-4.1).
