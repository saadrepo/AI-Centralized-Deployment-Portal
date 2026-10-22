# Pipeline tracker, timeline and log synchronization

The browser no longer reorders task events by `Date.parse(started) || 0`, which placed missing timestamps at the beginning. The backend now traverses real CI parent/job/task records in TFS order, using actual order/rank and valid timestamps as fallbacks. Missing timestamps sort last when no sequence is available. CD phases/jobs/tasks use the same stable metadata ordering. No timestamps or progress are synthesized.

The status response includes `Phase` and `TimelineMessage`. Active/failed/cancelled CI retains CI tasks and CI logs. Successful CI with a selected CD environment switches both panels to CD, even while waiting for the existing trigger's release. Finished CI tasks/logs are not appended to CD tasks/logs. No release is created by viewing or refreshing the page.

Pending approval display now includes approvals whose attempt number is missing/zero. The strict existing approval authorization helper is unchanged. An additional read-only TFS pending-approval query checks the exact release/environment; pending results override stale expanded environment data. Approval pending keeps deployment/health grey and displays only the actual approval wait. Missing/unverified approval data remains pending. It never grants approval permissions or bypasses TFS rules.

Deploy to IIS turns blue only after approval evidence is complete, the environment reports `inProgress`, and real tasks/phases/operation status substantiate a started deployment. Queued/scheduled environments remain grey and do not display a previous attempt's tasks/logs. Verify Health runs only after the selected deployment reports success. Pending tasks do not trigger speculative log fetches. The UI shows CI/CD context and uses a blue CD running badge only when deployment is actually running.

Files changed for this correction:
- `src/DeploymentPortal/Services/PipelineTimeline.cs` (new ordering, display-approval and deployment-evidence helpers)
- `src/DeploymentPortal/Services/PipelineService.cs`
- `src/DeploymentPortal/Services/PipelineModels.cs`
- `src/DeploymentPortal/wwwroot/js/pipeline.js`
- `tests/DeploymentPortal.Tests/PipelineTests.cs`
- `docs/pipeline-timeline-sync.md`

Regression tests cover task sequence, missing timestamps, parent job order, pending approval without an attempt number, stale expanded environment status overridden by live pending approval, CI-to-CD switching, ordered CD tasks with CD-only logs, queued/scheduled deployment waits and missing start evidence.

No Dashboard/sidebar, approval mutation/authorization rules, production configuration, credentials, database, IIS settings, deployment scripts or TFS definitions were changed. No live build/deployment, cancellation, approval, commit or push was performed. Live TFS response compatibility and browser rendering were not exercised; automated tests use HTTP fixtures.

Final validation: solution compilation/build passed; existing Pkcs dependency-audit warnings remain. Targeted pipeline/authentication tests: 78 passed, 1 interactive-SSPI skip. Full suite: 517 passed, 3 existing private-ACL sandbox failures, 1 skipped, 521 total. JavaScript syntax and Git whitespace checks passed. Tests also confirm that a previous completed deployment attempt cannot turn a newly initializing deployment blue.
