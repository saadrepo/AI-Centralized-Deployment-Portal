# Pipeline cancellation, health verification and reconnect

Implemented on `Dev` without changing SOURCE, IIS, SQL, Netserver, production JSON, protected credentials, or deployment scripts. Existing CI queue/cancel and approval/rejection routes were not rewritten.

## Cancellation contract and safety

TFS 2018 exposes environment cancellation through `PATCH {project}/_apis/release/releases/{releaseId}/environments/{releaseEnvironmentId}?api-version=4.1-preview.5` with `{ "status": "canceled", "comment": "..." }`. This updates one release environment; it does not cancel a CI build, reject an approval, abandon a release, or create a deployment. Microsoft documents the endpoint as “Update Release Environment,” with a release-execute permission requirement.

The Portal shows the action only when all of these are true:

- the existing `TfsCd:Enabled` and `TfsCd:ExecutionEnabled` gates are effective;
- single-account CD and the new cancellation gate are enabled;
- the request has a real authenticated Windows identity whose SID is in `ServiceCdUsers`;
- TFS connection data revalidates the pinned actor GUID and Windows SID descriptor;
- the exact release/build/definition/environment mapping still matches;
- the release is active, the environment is queued/scheduled/in progress, and its latest attempt ID, attempt number and operation status are cancellable.

The browser sends the exact release ID, instance environment ID, attempt ID and attempt number after explicit confirmation. The server repeats all correlation checks, then reads the environment through the verified TFS actor immediately before the PATCH. TFS performs the final permission check. The Portal does not retry the mutation. It reports success only after a same-actor GET confirms the same attempt is cancelling/cancelled. A changed or terminal attempt remains read-only. The TFS 4.1 endpoint itself is environment-scoped and has no attempt concurrency token; therefore the gate must stay off until SOURCE testing confirms the observed TFS 2018 response behavior and permissions.

Required SOURCE setting, after separate operational authorization and testing:

```text
Pipeline__Workgroup__ServiceDeploymentCancellationEnabled=true
```

This setting defaults to false. It does not replace or imply any existing CI/CD gate, actor GUID/SID setting, or Portal SID allowlist. The protected credential for `fff\saad.bilal` and existing settings remain unchanged. The TFS actor needs its normal Release execution/update permission for the selected release environment. Do not grant broader permissions merely to make this control appear.

## Health verification

The existing sixth stage is reused. It becomes green only after TFS reports the environment deployment succeeded and every configured HTTPS check returns its exact configured successful HTTP status. Missing or unsafe configuration stays unverified/pending; connection or status failures are red. CD success alone never turns health green.

The primary URL must be the deployed Portal endpoint or dedicated Portal health endpoint:

```text
Pipeline__HealthChecks__Portal__Url=https://deployment-portal.vitonta.com/
Pipeline__HealthChecks__Portal__ExpectedStatusCode=200
```

Optional required dependencies use indexed entries:

```text
Pipeline__HealthChecks__Portal__Dependencies__0__Name=<DEPENDENCY-NAME>
Pipeline__HealthChecks__Portal__Dependencies__0__Url=https://<TRUSTED-DEPENDENCY>/health
Pipeline__HealthChecks__Portal__Dependencies__0__ExpectedStatusCode=200
```

Only administrator-configured absolute HTTPS URLs without credentials, query strings or fragments are accepted. No dependency is assumed: add only an existing safe health endpoint whose success is required for the Portal. No production health settings were changed.

## Self-deployment reconnect

Only read-only `Status` polling is retried. Network failures and HTTP 502/503/504 responses show `Portal restarting — reconnecting...` and use bounded 1, 2, 4 and 8 second backoff. The selected application, build, branch, environment, displayed history and logs remain in place. A successful response automatically refreshes status and logs. HTTP 401/403 is treated as Windows Authentication failure and is not retried. Exhaustion shows a manual Refresh instruction. Queue, cancel build, approve, reject and cancel deployment POSTs are never automatically repeated.

## TFS API reference

Microsoft’s TFS/Azure DevOps Server 4.1 documentation: https://learn.microsoft.com/rest/api/azure/devops/release/releases/update-release-environment?view=azure-devops-rest-4.1
