# Prompt 3 safety hardening and readiness

Code deployment: **conditional GO with GitHub execution disabled**, subject to
the existing production release review and the shutdown/preflight procedure
below. This is code readiness evidence, not authorization or certification of the
live environment. Live GitHub execution: **NO-GO** until separately authorized
staging acceptance, migration review/application and production configuration.
Azure execution remains blocked. Multi-host cloud execution is unsupported.

## Resolved code findings

- Cloud deployments use `CloudQueued`/`CloudRunning` status strings. Legacy
  Queued/Running worker predicates exclude them. New workers require exact mode,
  native authorization, protected approval, current run/artifact, registered
  target and explicit single-host compatibility attestation. Legacy draft
  CloudUpdate work is quarantined on recovery. Completed cloud work cannot be
  executed again by calling the service a second time.
- A dedicated lifecycle saves cloud and deployment state/audit together before
  remote connection and at completion. Missing approvals and damaged audit data
  result in Failed/inspection-required outcomes, preserving existing evidence.
  Missing-record recovery creates no authorizing preparation and blocks further
  cloud deployment preparation/approval for the affected server pending review.
- Failed final SQL saves propagate. The last durable CloudRunning intent remains
  unconfirmed until reconciliation; no automatic remote retry occurs. An external
  logging sink failure cannot prevent durable failure finalization. This cannot
  guarantee immediate writes while SQL itself is unavailable.
- Startup recovery runs under the existing exclusive worker session lock and
  repairs cloud terminal mismatches, missing records, orphaned executions,
  interrupted work and incompatible statuses. Old web operations become explicitly
  unconfirmed after a conservative two-hour grace. Reservations and staging are
  retained. Each category is bounded to 200 records/pass; a larger backlog holds
  the worker closed until subsequent passes complete recovery.
- Denial auditing runs before endpoint authorization and covers authorization,
  antiforgery, selection, HTTPS and cloud gate failures. Event 4103 contains status,
  native actor SID when available and a trace reference, never request credentials
  or token bodies. A protected persistent logging sink remains an operator task.
- In-process operation locks use 64 fixed semaphores. SQL locks, rowversion and
  filtered unique indexes retain exact operation/duplicate protection.
- Failed extraction cleans only its newly created private GUID directory using
  bounded, nonrecursive deletion after link and root validation. Retained,
  successful and remote staging/backup cleanup remains a reviewed operator task.
- The ordinary TFS execution path retains its previous verification decision;
  cloud health and lifecycle logic are separated. TFS build/approval/release
  clients, pipeline configuration, PowerShell rollback and credentials remain.

## Required rollout and rollback procedure

See [the detailed operator procedure](github-controlled-execution.md#worker-rollout-shutdown-and-rollback).
Stop new mutation requests, let active operations finish, and stop every old app
pool/worker sharing the database before rollout or activation. Deploy matching
binaries and scripts together with flags false. The compatibility attestation is
an operator assertion, not automatic discovery of other installations. Downgrade
requires reconciling all cloud work with the current version first; old UIs may
not parse reserved status values. Preserve the additive table and audit records;
do not apply migration Down as a routine binary rollback.

`BuildExecutionEnabled` and `DeploymentExecutionEnabled` remain false by default;
`WorkerCompatibilityConfirmed` defaults false and `WorkerHostName` is empty.
No shipped environment configuration enables these settings. Live operation
still requires an explicit mapping, valid credentials, native actor permissions,
unexpired authorization reference and approval for deployment. Neither this
reference nor the host confirmation contacts an external approval system.

The migration remains additive and unchanged. Offline tests compare the EF
snapshot/model, generate SQL, verify status column capacity and show that legacy
worker SQL excludes CloudQueued. No migration was applied. Before production,
review actual migration history/schema drift and SQL permissions. If the new table
exists, the worker needs its SELECT/INSERT/UPDATE permissions even with execution
disabled, because existing cloud work must be reconciled. Do not bypass recovery
to accommodate missing permissions. No new DDL privilege is required at runtime.

Production startup does not apply migrations. With the new migration absent and
no historic cloud work, cloud reconciliation exits and existing TFS operations
use their existing tables. Enabled cloud operations fail closed on missing schema.
Startup still requires the existing secure Windows Data Protection storage.

Data Protection encryption architecture was not changed. Machine DPAPI protects
keys on the designated Windows host. Copying those keys across machines is not a
supported shared key-ring design. Any multi-host support requires separate
approval, architecture review and testing; do not enable it using configuration
alone.

## Verification and remaining acceptance

Final validation on Dev (Release):

| Check | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Complete .NET suite | 976 | 0 | 1 |
| Recovery regressions (included above) | 17 | 0 | 0 |
| Dedicated PowerShell security suite | 8 | 0 | 0 |
| All JavaScript test files, including 22 TFS client cases | 4 files | 0 | 0 |

Release solution build succeeded with zero errors and one existing xUnit2031
warning at PipelineTests.cs:858. The existing skip requires interactive Windows
SSPI credentials; no failing test was disabled. The offline EF migration test
passed. `git diff --check` passed. Final local TRX files are
`tests/DeploymentPortal.Tests/TestResults/safety-final.trx` and
`safety-powershell.trx` in the same directory. These ignored artifacts and logs
are not committed. Full and dedicated PowerShell runs were sequential, avoiding
test contention on the production IIS mutex.

Primary hardening changes:

| Files | Purpose |
| --- | --- |
| `Services/CloudExecutionLifecycle.cs` (new), `Services/DeploymentService.cs`, `Services/DeploymentWorker.cs` | Atomic lifecycle saves, fail-closed execution and bounded restart reconciliation |
| `Models/Deployment.cs`, `Services/CloudExecutionModels.cs`, `Services/CloudExecutionFlow.cs` | Reserved queue protocol, explicit host gate, missing-approval quarantine and bounded semaphores |
| `Services/CloudDenialAuditMiddleware.cs` (new), `Controllers/PipelineCloudController.cs`, `Program.cs` | Denial audit coverage before authorization |
| `Services/CloudPackageStager.cs` | Scoped failed-extraction cleanup |
| `CloudRecoveryTests.cs`, `CloudDenialAuditTests.cs` (new) | Recovery, persistence failure, queue compatibility and denial regression coverage |
| Existing cloud execution, endpoint, migration and package tests | Compatibility fixtures, real middleware denials, offline SQL and cleanup checks |
| `github-controlled-execution.md`, `prompt3-completion.md`, this report | Rollout, retention, Data Protection and readiness documentation |

Production source paths above are relative to `src/DeploymentPortal`; test paths
are relative to `tests/DeploymentPortal.Tests`. Existing Prompt 3 workspace
changes remain preserved. No PowerShell or TFS configuration changes were needed
for this hardening pass; their existing uncommitted Prompt 3 changes remain.

Regression tests cover missing records, corrupt audit evidence, a throwing logging
sink, final-save failure followed by a fresh-context restart, terminal mismatch,
legacy and reserved queue states, wrong deployment mode, host mismatch, orphaned
records, bounded recovery backlog, abandoned/recent preparations and actual
authorization/CSRF middleware denials. Archive rejection tests also assert failed
preparation directories are removed. No failing test was skipped or removed.

All tests use mocks or controlled local fixtures. No live provider POST, IIS/WinRM
deployment or production database access occurred. Real SQL Server locking and
transaction failure behavior, effective production settings/ACLs, external TFS
release definitions, target-specific health routing and live GitHub artifact
interoperability require separately authorized acceptance. Existing TFS production
release definitions are outside this checkout and have not been certified here.

No commit, push, migration application, production setting change, live build or
deployment was performed. Local ignored test/build logs are verification artifacts.
