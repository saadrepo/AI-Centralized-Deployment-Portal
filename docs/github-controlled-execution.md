# Controlled GitHub execution

GitHub execution is implemented but **disabled by default**. No activation settings,
credentials or production configuration are shipped. Azure DevOps execution is
deferred: its page and explorer remain read-only, and server-side execution gates
reject it even with enabled flags. TFS keeps its existing execution routes and
pipeline configuration. The shared worker receives separately identified
`CloudUpdate` deployments; its existing TFS path remains intact.

## Execution and approval

The existing three buttons are reused. Run CI Build validates the selected source,
repository, branch and workflow against an explicit administrator mapping, then
persists a dispatch intent before sending one workflow dispatch. It requests the
exact run ID using `return_run_details`; it never guesses from the latest run.
The transport uses GitHub REST API version `2026-03-10`. A response without the
required run ID, a timeout or an uncertain outcome leaves an unconfirmed intent
blocking another dispatch. An operator must investigate before recovery; do not
blindly replay an uncertain operation. A successful build never queues deployment.

Approve & Deploy first validates a successful exact run, attempt, commit and named
artifact, downloads and hashes its ZIP, stages it privately, preserves protected
configuration using the existing CD safeguards, and previews the registered IIS
target. The confirmation displays provider, repository, branch, run, commit,
attempt, artifact ID, archive hash and environment/site. Declining never queues a
deployment. Confirmation consumes a protected approval bound to this evidence,
target, current native Windows approver SID and configuration fingerprint.
Approval expires after ten minutes, including worker queue time. The worker
revalidates authorization, evidence, target and expected state before deployment.
Replayed approval returns the same deployment, rather than creating another.

Refresh Status performs provider reads and reads the tracked deployment; it does
not dispatch, download, approve or deploy. Readiness recovers the actor's active
operation after navigation. Changing selections clears client execution state.

## Safeguards

POST endpoints require existing Windows Sources Management authorization,
antiforgery protection and HTTPS. Mapped BuilderSids and ApproverSids are checked
independently using a real Windows identity; claims alone are insufficient.
Existing DPAPI credential encryption, ACL validation and revocation remain in use.
Credentials and signed download URLs never reach the browser. Dispatch is limited
to the selected registered repository/workflow. Artifact redirects use a separate
unauthenticated request to an explicitly approved exact HTTPS storage hostname;
provider authorization is never forwarded. Wildcards and IP download hosts are
rejected, as are further redirects.

Only successful runs and nonexpired, uniquely named artifacts with SHA-256 digest
metadata are accepted. The downloaded archive must match that digest. Extraction
rejects traversal, alternate streams, links, duplicate paths and invalid publish
layouts. Limits are 64 MiB compressed/per file, 256 MiB expanded, 2,000 entries and
1,000 files. ArchiveRoot must explicitly match the approved layout. Private local
staging permits fewer than ten existing preparation directories; exceeding that
capacity blocks preparation. Failed preparation is not automatically retried.

The existing deployment engine performs backup, protected configuration and file
integrity validation, controlled IIS application-pool changes and rollback. Cloud
deployment additionally requires an HTTPS health URL on port 443 matching the
mapped IIS hostname (or server hostname). Queries, fragments, URL credentials,
redirects and certificate bypass are disallowed. Only HTTP 200 succeeds. Health
failure enters the existing file/application-pool rollback path and retains the
backup. TFS deployments do not acquire a new health-check requirement.

SQL serializable transactions, transaction-owned application locks, filtered
unique indexes and rowversion enforce operation ownership and duplicate guards.
Protected records bind preparation data and approvals; audit records retain actor,
time and transitions. Unconfirmed or interrupted operations require inspection,
not automatic replay. Audit and staging retention are operational responsibilities.

## Prerequisites for separately authorized activation

1. Review and apply `20261024174124_AddCloudExecution` in a separately authorized
   maintenance operation. It adds only CloudExecutions and its two filtered unique
   indexes. This implementation session does not apply migrations.
2. Persist and protect the ASP.NET Core Data Protection key ring on the single
   Windows portal host. Keys are encrypted with machine DPAPI: copying the key
   directory to another machine does not support shared approvals. Multiple-host
   cloud execution is unsupported by this implementation. Any alternative key
   protection or hosting architecture requires separate approval and testing.
3. Configure the `CloudExecution` section through the approved administration
   process. Both `BuildExecutionEnabled` and `DeploymentExecutionEnabled` default
   to false. Enabling an operation also requires a nonempty
   `LiveAuthorizationReference` and future `LiveAuthorizationExpires`. Keep flags
   false until separate operational authorization and staging acceptance exist.
   Deployment preparation/approval/worker execution additionally require
   `WorkerCompatibilityConfirmed=true` and `WorkerHostName` equal to the actual
   Windows `Environment.MachineName`. Defaults are false/empty. This attests to
   the reviewed single-host rollout; it does not discover or authenticate other
   installations. Values are included in approval binding. Environment/config
   changes require process restart; do not assume instant flag revocation in an
   already running operation.
4. Provide separate private `LocalStagingRoot` and `RemoteStagingRoot`, outside web
   roots and existing TFS staging/target/backup directories. Provision and validate
   restrictive ACLs beforehand. Define a reviewed cleanup/retention procedure;
   this implementation does not recursively purge staging data.
5. Configure exactly one mapping for each allowed selection: ApplicationKey,
   Provider=`GitHub`, SourceId, Project=null, Repository, Branch, DefinitionId,
   ArtifactName, ArchiveRoot, ServerId, Environment, HealthUrl, BuilderSids,
   ApproverSids and ArtifactHosts. Each maps to one registered application
   destination and SSL-enabled server in the matching environment. No browser
   input can authorize an arbitrary target or artifact URL.
6. Review the selected workflow's `workflow_dispatch` support and ensure the
   workflow itself cannot automatically deploy. Use repository-restricted GitHub
   credentials with Actions read/write and only other permissions the reviewed
   workflow needs. Confirm REST run-ID responses and upload-artifact SHA-256
   metadata are supported. Older installations returning an empty dispatch
   response fail closed. Approve the actual exact signed storage hostnames through
   ArtifactHosts; no broad CDN wildcard is permitted.
7. Review the separate existing IIS/WinRM credentials and target permissions for
   least privilege. Verify backups, rollback, protected configuration handling,
   health endpoint and firewall connectivity in a controlled staging environment.
8. Perform separately authorized live staging acceptance before production use.
   Neither build success nor this test report is authorization for live execution.

## Verification and limits

Run `tests/pipeline-client.test.cjs` and every `tests/JavaScript/*.cjs` file with Node, the full .NET test project in
Release, and a Release solution build. The .NET suite includes PowerShell security
fixtures and the new IIS health/rollback fixture. Adapter tests use mocked provider
responses; flow tests use in-memory EF and synthetic credentials/remote operations;
ZIP tests use controlled local files. Endpoint tests cover authorization, HTTPS
and antiforgery protection. Migration tests compare the snapshot with the runtime
model and generate SQL without opening a database connection.

These checks do not establish real SQL Server concurrency/locking behavior,
GitHub dispatch/download interoperability or production IIS/WinRM behavior. Those
require separately authorized staging acceptance. No tests trigger live provider
builds or contact production deployment infrastructure. Azure execution remains
unimplemented and cannot be activated through these settings.

## Worker rollout, shutdown and rollback

Cloud deployment records use the reserved `CloudQueued` and `CloudRunning`
statuses in the existing string status column. Legacy workers select `Queued`
and recover `Running`; those queries cannot select new cloud work. A new worker
requires CloudUpdate mode, protected approval and the host compatibility gate.
Pre-hardening CloudUpdate records with legacy Queued/Running statuses are
quarantined as Failed at recovery, never deployed automatically. Older binaries
may fail to display history containing unknown status strings; this safety guard
is not a promise of old UI compatibility.

Required operator procedure (not executed by this implementation):

1. Put portal mutation routes into maintenance and stop new approvals/build
   submissions. Inventory every portal host, app pool, background service and
   scheduled worker sharing the database, including draft Prompt 3 versions.
2. Allow active deployments to finish; inspect uncertain outcomes and retained
   backups. Stop all old workers/app pools before replacing binaries/scripts.
   Do not kill an active remote deployment and assume rollback completed.
3. Back up the database, key directory and previous complete publish output.
   Review migration history/schema separately. Deploy the matching binaries and
   PowerShell files together with both cloud execution flags still false. This
   code session does not authorize these operations or migration application.
4. Start only the reviewed current-version worker on the designated single host.
   Verify existing TFS behavior and missing-migration/default-off behavior. Verify
   database permissions and protected logging delivery. Reconcile/inspect any
   quarantined work; never restore its status to Queued to retry it.
5. Only after separate staging acceptance and live authorization, set the host
   compatibility attestation and approved mappings/credentials, then enable the
   desired GitHub operation. Azure execution remains blocked.
6. Before downgrading, stop new requests and quiesce active work. Use the current
   version with execution disabled to reconcile/quarantine remaining cloud work.
   Verify no CloudQueued, CloudRunning, or legacy queued/running CloudUpdate
   records remain before restarting old binaries. Preserve the additive table,
   protected records and keys. Migration Down drops evidence and is not the
   normal binary rollback procedure. Any manual data repair requires separate
   authorization, backup and recorded inspection of target state.

## Failure reconciliation and retention

Before remote connection, cloud worker start state and audit are saved with the
deployment in one SaveChanges transaction. Final result, completion time and
cloud state/audit are also saved together. Missing approval records create a
non-authorizing recovery record and Failed deployment. Missing approval recovery
also blocks new cloud deployment preparation/approval
for that server until separately authorized operator inspection and data repair.
Recovery evidence must not be deleted merely to bypass that gate. Invalid audit JSON or
capacity failure preserves prior audit verbatim inside recovery evidence, marks
the outcome Failed and requires inspection. Logging sink exceptions cannot block
that durable failure save. SQL failure propagates; an already persisted
CloudRunning intent is recovered as Failed/unconfirmed on the next worker start.
Neither failure nor restart retries remote execution.

Reconciliation runs under the existing exclusive SQL worker session lock before
normal TFS restart recovery. It repairs terminal state mismatches, quarantines
interrupted/legacy cloud work and records orphaned executions. Processing is
bounded to 200 records per category per pass. Larger backlogs hold the worker
closed and continue on subsequent recovery passes. Abandoned Dispatching,
Preparing, Staging or Prepared records older than two hours become explicitly
unconfirmed, retain duplicate reservations and require operator inspection. This
grace exceeds the supported one-hour remote-operation deadline; recent web
preparations are left alone. No remote operation or provider POST is performed by
reconciliation. No background timer deletes evidence or staging data.

Denials produce secret-safe structured events, including authorization and CSRF
short circuits (event 4103). Route/controller denial codes include correlation
references; request bodies, headers, tokens and provider response bodies are not
logged. Production must route these events to a protected persistent sink and
alert on critical recovery events; no new external logging service is configured
here. The operation lock pool is fixed at 64 semaphores; exact SQL application
locks and unique indexes still enforce cross-process scope.

Failed local extraction cleans only its newly created, link-free GUID directory
after revalidating the private root, with bounded enumeration and nonrecursive
deletion. It does not touch reused directories, siblings, links, successful
packages, remote staging or backups. Unsafe cleanup is refused and logged. An
operator must inventory database references and uncertain remote outcomes before
removing retained/expired local or remote preparations. Keep anything referenced
by queued/running work, preserve forensic evidence and obey backup retention.
The ten-directory staging capacity continues to fail closed.
