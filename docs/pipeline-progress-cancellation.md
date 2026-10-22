# CI progress and cancellation

The six stages remain Source → Build → Publish Artifact → Approval → Deploy to IIS → Verify Health. Source checkout records are aggregated from the real build timeline. Missing/late timeline data remains grey with “Waiting for status”; source work in progress keeps the Build stage pending with “Waiting for source”. An overall “CI running” badge is shown separately, so active build-container state does not incorrectly mark the compile/build stage running before source checkout.

Published artifact evidence is independent of overall build completion. A real published artifact can be green while CI remains active. Approval and deployment are not advanced while CI is incomplete/failed/cancelled. A pending pre-deployment approval always keeps Deploy to IIS and Verify Health pending; a previous environment status cannot override that wait. Existing approval authorization is retained.

## Cancel Build

- A conditional Cancel Build button appears for the selected queued/running build. It is disabled while state/permission is unknown, during an action, or after cancellation has already been requested. Run CI Build is disabled while that selected build is active. Queue submission also revalidates the supplied selected build ID on the server.
- Clicking Cancel Build opens the browser confirmation dialog with the actual build number/ID. Dismissing it sends no cancellation request.
- Authenticated, antiforgery-protected POST `/Pipeline/Cancel` accepts only an application key and a selected CI build ID. The server validates project, repository and definition associations against real TFS data as the current verified Windows user.
- TFS's Build security namespace metadata supplies the `StopBuilds` bit. The Portal queries effective permissions for the calling Windows user and the selected definition token (`projectId/definitionId`), with `alwaysAllowAdministrators=false`. Group inheritance/deny evaluation belongs to TFS. A generic Portal role cannot substitute for permission. Missing/unsupported/malformed permission metadata fails closed.
- The existing CI execution gate and verified-user transport are reused. Stored TFS/service credentials are not used to cancel builds. Existing approval delegation requirements and CD flags are not relaxed.
- Build state is read again after permission checking. A completed build, wrong-application build or already-cancelling build is rejected before mutation.
- The only mutation is buffered JSON `{"status":"cancelling"}` sent to the selected project's `/_apis/build/builds/{buildId}?api-version=4.1` via PATCH. TFS enforces authorization again. No release, deployment, IIS, pipeline configuration or agent endpoint is mutated.
- A subsequent GET must report `status=completed` and `result=canceled` before the Portal displays “Cancelled”. Accepted requests otherwise say they are waiting for TFS confirmation. A build that wins the completion race retains TFS's actual final result.
- Polling and real CI logs continue after cancellation, including uncertain responses. The client suppresses repeated cancellation submissions while reconciling the selected build. Cancellation never automatically retries a mutation.

TFS users need the existing **Stop builds** permission on the definition/project. No permission or pipeline setting was changed automatically. If TFS 2018 does not return verifiable effective-permission data, the button remains disabled with an explanation; live compatibility must be checked using read-only access before enabling operations.

## Changed files

- `src/DeploymentPortal/Services/PipelineProgress.cs` (new evidence-based stage mapper)
- `src/DeploymentPortal/Services/PipelineCancellation.cs` (new cancellation and effective-permission checks)
- `src/DeploymentPortal/Services/PipelineService.cs`
- `src/DeploymentPortal/Services/PipelineModels.cs`
- `src/DeploymentPortal/Controllers/PipelineController.cs`
- `src/DeploymentPortal/Views/Pipeline/Index.cshtml`
- `src/DeploymentPortal/wwwroot/js/pipeline.js`
- `src/DeploymentPortal/wwwroot/css/pipeline.css`
- `tests/DeploymentPortal.Tests/PipelineTests.cs`
- `docs/pipeline-progress-cancellation.md`

Tests use synthetic HTTP fixtures: late/empty timelines, active checkout, artifact publication during active CI, pending approval, selected-build queue blocking, cancellation confirmation, denied Stop builds, completed/cross-application targets, state changes during permission checking, and missing antiforgery tokens. No real build was queued or cancelled, and no live approval/deployment, commit or push was performed. Dashboard, sidebar, production settings, credentials, SQL, IIS, agent configuration, pipeline definitions and release scripts were not edited for this change.

Restart the local Development process and hard-refresh `/Pipeline` to load the updated backend and assets. Tests/builds use `.tools/pipeline-validation` so the running local process is not stopped for validation. Browser confirmation and live TFS cancellation were not exercised against a real build.

Validation: solution build passed with zero compiler errors and four existing Pkcs dependency-audit warnings. Targeted pipeline/authentication tests: 67 passed, 1 interactive-SSPI skip. Final full suite: 506 passed, 3 existing sandbox private-ACL failures, 1 skipped, 510 total. JavaScript syntax and Git whitespace checks passed. No additional package or configuration changes were made for this feature.

References: [TFS 4.1 security namespace discovery](https://learn.microsoft.com/th-th/rest/api/azure/devops/security/security-namespaces/query?view=vsts-rest-tfs-4.1), [Microsoft Build permission token reference](https://learn.microsoft.com/en-us/azure/devops/organizations/security/namespace-reference?view=azure-devops).
