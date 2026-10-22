# Pipeline history and queue feedback

The initial selection loads authenticated CI history independently of the slower execution-permission and release status checks. The History endpoint performs read-only build/timeline/artifact/log requests and retains the existing authentication and application/build validation.

Status responses include separate CI events/logs alongside the active CI or CD phase. The page displays CI history and CD progress together. Previously received CI history for the same application/build is retained with an explicit notice if a subsequent response has no history. The cache is bounded to four builds and does not retain authorization decisions.

Queueing immediately locks actions and selections, displays `Queuing Build...` and a spinner, and blocks duplicate clicks. A returned build ID selects that real build and requests its status. No progress is invented while waiting. Selection generations and request versions reject late responses, including a late partial history response after full CD status has arrived.

Files changed for this update:

- `src/DeploymentPortal/Controllers/PipelineController.cs`
- `src/DeploymentPortal/Services/PipelineModels.cs`
- `src/DeploymentPortal/Services/PipelineService.cs`
- `src/DeploymentPortal/Views/Pipeline/Index.cshtml`
- `src/DeploymentPortal/wwwroot/css/pipeline.css`
- `src/DeploymentPortal/wwwroot/js/pipeline.js`
- `tests/DeploymentPortal.Tests/PipelineTests.cs`
- `tests/pipeline-client.test.cjs`
- This document.

Validation:

- Solution test run: 518 passed, 1 skipped, 3 failed. The failures are existing PowerShell credential-provisioning tests denied private Windows ACL directory creation in the sandbox; the skip requires interactive Windows SSPI testing.
- Focused Pipeline and Windows authentication tests: 79 passed, 1 skipped, 0 failed.
- Client regression tests: 6 passed using the actual page JavaScript with mocked DOM/HTTP responses; covers initial loading, queue feedback, duplicate clicks, delayed responses, retained CI history and manual refresh.
- Existing Pkcs 9.0.4 dependency advisory warnings remain.

No real TFS build, approval or deployment was executed. Dashboard, production configuration, credentials, database, IIS and release scripts were not changed by this update. Live browser/TFS verification remains a local manual check. Restart the local application and hard-refresh `/Pipeline` to load the updated assets.
