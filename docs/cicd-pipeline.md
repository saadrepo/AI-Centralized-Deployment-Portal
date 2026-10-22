# CI/CD Pipeline integration

Open `/Pipeline` from the sidebar. The existing Dashboard is unchanged.

## Configuration and identity prerequisites

No production settings, registry data, credentials, database, IIS configuration, release scripts or TFS definitions are changed by this implementation.

- Reads reuse the existing `TfsGit` collection endpoint validation, Windows credential provider and GET-only request guard. Each application uses its registered `TfsGitProject`, `TfsGitRepository` and `TfsGitCiDefinitionId`. Branches are discovered rather than fixed to Dev. Applications without a CI mapping are omitted.
- The effective `TfsCd.Enabled` and `TfsCd.ExecutionEnabled` options must both be true for approval actions. CI queueing has its own default-off `Pipeline:Ci:QueueEnabled` setting; it never enables CD. Existing `Program.cs` binds CD options through its current registry/tfs-cd/environment configuration precedence. The older artifact explorer gate is not changed.
- All new pages/data/actions require an authenticated HTTP user. Development Kestrel registers the real Windows Negotiate handler and challenges anonymous `/Pipeline` requests. Production retains existing IIS Windows authentication. No fabricated operator identity is used; Dashboard access is unchanged.
- Approvals require a Windows identity with **Delegation** impersonation level. CI uses the same path, or in Development only, the interactive process credentials if the authenticated visitor has the exact same Windows SID. Both paths use a fresh HTTP handler, never stored TFS credentials. The TFS connection-data identity descriptor must match the visitor's Windows SID before any mutation is sent. A mismatched process/service identity is rejected.
- IIS Windows authentication/Kerberos constrained delegation and appropriate SPNs to the TFS HTTP service require administrator configuration outside this repository. They have not been changed or live-verified. Review the actual hosting/domain topology before configuring them. Local Windows accounts may not support the necessary domain delegation.
- TFS must grant the visiting user queue-build permission and the applicable release approval permission. A Portal administrator role cannot substitute for these permissions. Group approvals use TFS's user-filtered approval listing, including group assignments. Unknown/unverifiable identities fail closed.
- Existing release triggers must retain pre-deployment approval. The page only approves an existing pending approval; it does not create releases, start/retry environments, or change policies. TFS advances the environment through its configured process after approval.

Do not overwrite a server JSON file to resolve a disabled-action message. Review the effective deployed configuration and existing gates with the administrator first.

## Application and release discovery

Only the application's registered CI definition is offered. Its Git repository and project are validated against TFS. Release definitions are discovered by their build-artifact definition ID **and project ID**, not by names. Release instances additionally require the exact build artifact version ID. Environments come from those release definitions.

The existing CD trigger may take time to create a release. Polling waits without creating one. Multiple matching releases disable approval because the target is ambiguous. Portal-CD is never hardcoded as a Demo deployment pipeline. No discovered Demo CD definition means CI is available while CD is shown as unconfigured/unavailable.

Approval rechecks the successful build, published expected artifact, release correlation, environment, pending approval ID/revision, attempt, rank, user/group eligibility and requester restriction. The actual PATCH is authenticated as the visiting user and TFS remains authoritative. Failed or ambiguous mutations are never automatically retried. Use Refresh Status to reconcile recent builds/status first.

## Health verification

Health is pending until actual evidence is available. A successful release is not presented as a verified healthy website.

An administrator can optionally configure an explicit trusted HTTPS endpoint through existing configuration providers, for example these environment-variable keys:

```text
Pipeline__HealthChecks__Portal__Url=https://deployment-portal.vitonta.com/
Pipeline__HealthChecks__Portal__ExpectedStatusCode=200
```

These are documented keys, not applied settings. Select the actual health endpoint for each registered application. Query strings, embedded credentials and non-HTTPS URLs are rejected. The health client sends no Windows credentials, does not follow redirects, and checks only after the selected environment reports success. TLS validation remains enabled.

The UI labels the scope honestly: HTTP endpoint status is checked; deployed build/version is not verified. Version-aware application health responses can be integrated later. HTTP-only Demo endpoints need a trusted HTTPS health endpoint before this optional check can pass.

## UI and API behavior

- Five dependent selectors, three primary actions, six evidence-based stages, deployment task timeline, actual build/release logs, branch overview, AI preview and recent build activity.
- Running blue, completed green, failed red, pending grey. Awaiting approval remains grey with “Approval Pending”. No invented percentages or events.
- Automatic status polling pauses in hidden tabs, avoids overlapping status calls, cancels stale selection requests, backs off on failure and polls terminal states less frequently.
- Manual Refresh Status reloads real metadata/recent builds as well as status. Selected/queued build IDs are stored as browser session preferences for recovery after Portal self-deployment. Approval state always comes from TFS.
- Log display is bounded: tails of the latest three build logs and up to three available release task logs. Repeated snapshots replace the panel rather than duplicate lines. Auto-scroll can be disabled. Upstream log URLs are not followed.
- Log content is rendered with `textContent`; supplementary credential-pattern redaction does not replace proper secret masking in the TFS pipeline itself.
- AI text/voice remains a clearly disabled preview. Branches are read-only; there are no merges or PR mutations.

New controller actions: `Index`, `Applications`, `Options`, `Status`, POST `Queue`, POST `Approve`. Mutation actions require antiforgery tokens. GET actions never mutate TFS. Error responses and operational audit messages omit upstream response bodies and secrets.

## Verification boundaries

Automated tests use synthetic HTTP fixtures and never queue/approve/deploy on TFS. Live TFS 2018 response/version compatibility, IIS delegated identity, actual server option values, Demo release availability and optional health connectivity require environment verification. No live deployment is needed to review the code.

API references used while implementing the TFS 4.1 routes:
- [Microsoft release approval API](https://learn.microsoft.com/en-us/previous-versions/azure/devops/integrate/previous-apis/rm/approvals?view=tfs-2017)
- [Microsoft release task log API](https://learn.microsoft.com/en-us/rest/api/azure/devops/release/releases/get-task-log?view=azure-devops-rest-5.0)

The documentation covers neighboring API versions; actual installed TFS 2018 API behavior still needs read-only live verification. Unsupported responses fail closed rather than falling back to cloud APIs or release creation.

## Changed files

- `src/DeploymentPortal/Controllers/PipelineController.cs`: authenticated read endpoints, antiforgery-protected action endpoints and sanitized failures.
- `src/DeploymentPortal/Services/PipelineModels.cs`: public page DTOs, evidence-based state mapping and structured artifact matching.
- `src/DeploymentPortal/Services/PipelineService.cs`: application-scoped discovery, queueing, exact release tracking, approval, timeline and logs.
- `src/DeploymentPortal/Services/PipelineUserTransport.cs`: delegated user transport and collection request guard.
- `src/DeploymentPortal/Services/PipelineHealthCheck.cs`: optional configured HTTPS health checks without credentials.
- `src/DeploymentPortal/Services/PipelineLogRedactor.cs`: supplementary common credential-pattern redaction.
- `src/DeploymentPortal/Services/TfsGitClientConfiguration.cs`: new service/client registrations; existing read-only guard retained.
- `src/DeploymentPortal/Views/Pipeline/Index.cshtml`: new CI/CD page.
- `src/DeploymentPortal/Views/Shared/_Layout.cshtml`: one sidebar navigation entry only.
- `src/DeploymentPortal/wwwroot/css/pipeline.css`: styles scoped to the new page.
- `src/DeploymentPortal/wwwroot/js/pipeline.js`: actual endpoint calls, automatic refresh, selection cancellation and action controls.
- `tests/DeploymentPortal.Tests/PipelineTests.cs`: 30 test cases, including local MVC/Razor HTTP rendering without database/worker services.
- `docs/cicd-pipeline.md`: implementation and environment handoff.

## Local validation results

- `dotnet build DeploymentPortal.sln --no-restore -p:UseSharedCompilation=false -m:1`: passed, zero warnings/errors.
- `dotnet test DeploymentPortal.sln --no-restore -p:UseSharedCompilation=false -m:1 --filter FullyQualifiedName~PipelineTests`: all 30 pipeline cases passed.
- Full solution test run: 469 passed, 3 failed, 472 total. Failures are the existing `PowerShellCredentialProvisioningTests` cases for `PortalStorage.Tests.ps1`, `WinRmCredentialProvisioning.Tests.ps1`, and `TfsCredentialProvisioning.Tests.ps1`. Their synthetic fixture directories under `.tools` could not receive private Windows ACLs in this sandbox. No credential/ACL protection was weakened to make them pass.
- Installed Visual Studio Node runtime `--check src/DeploymentPortal/wwwroot/js/pipeline.js`: passed.
- `git diff --check`: passed; Git additionally reports normal LF-to-CRLF normalization notices.
- Headless Edge could not start its crash handler because of sandbox access denial. Browser visual/runtime verification is incomplete; Razor/HTTP rendering and JavaScript syntax were checked separately.
- The repository's `tfs-cd.json` currently records `Enabled=false`, `ExplorerEnabled=false`, `ExecutionEnabled=false`. These static file values are not a verification of production effective runtime configuration. The file was not modified.

The pre-existing untracked `.verification/` directory was preserved. No live TFS build, release, approval or deployment was initiated. There was no commit or push.

## Local Windows authentication fix

`LocalWindowsAuthentication.cs` registers `Microsoft.AspNetCore.Authentication.Negotiate` only in Development and runs authentication before authorization. Anonymous Pipeline requests receive a real Negotiate challenge instead of only the controller's explanatory 401 body. The controller's authenticated-user checks remain intact. Only the Pipeline area requests a login; no global authorization policy was introduced.

Both existing Project launch profiles now open `Pipeline`. `windowsAuthentication` in an IIS Express profile would not enable Windows authentication in the existing Kestrel Project profiles, so no IIS/IIS Express configuration was changed.

From the repository root, stop the old debug process and run:

```powershell
dotnet run --project src/DeploymentPortal/DeploymentPortal.csproj --launch-profile http
```

Open `http://localhost:5140/Pipeline` in normal Microsoft Edge or Chrome under your Windows login. This existing profile binds localhost. If the browser asks for Windows credentials, use your actual Windows account in its native login prompt; never put credentials into source/configuration or chat. The browser must permit integrated Windows authentication for localhost. Managed browser policies may require administrator review; do not disable authentication or approve broad host allowlists.

The HTTPS profile remains available at `https://localhost:7182/Pipeline` with `--launch-profile https`, provided your normal Windows session already has a valid trusted development HTTPS certificate. The sandbox's read-only `dotnet dev-certs https --check` found no valid certificate; no certificate was created or trusted automatically.

Local page login does **not** grant delegation to TFS or authorize approval. Approval execution flags and delegated-identity checks are unchanged. CI has the separately verified local-user path described below. A successfully logged-in local user may still have actions disabled if the relevant identity or permission cannot be verified.

New authentication tests verify actual HTTP Negotiate challenges on Pipeline routes, absence of a new Dashboard challenge, rejection of forged user headers, and no production service registrations. A separate real current-Windows-credentials test runs only when SSPI credentials are available. Its initial sandbox attempt failed with `SEC_E_NO_CREDENTIALS`; the test now explicitly skips in that environment. Run it from a normal interactive Windows session to verify the complete handshake. Test hosts do not register the database, deployment worker, or live TFS services.

Authentication-fix files: `Program.cs`, `DeploymentPortal.csproj`, `Properties/launchSettings.json`, new `Services/LocalWindowsAuthentication.cs`, new `tests/DeploymentPortal.Tests/LocalWindowsAuthenticationTests.cs`, and this report. `PipelineController` was inspected and its existing authentication/approval checks were retained.

Latest verification after the authentication fix:
- Solution build: zero warnings/errors.
- Authentication plus pipeline tests: 36 passed, 1 explicitly skipped (interactive SSPI credentials unavailable).
- Full suite: 475 passed, 3 existing private-ACL fixture failures, 1 skipped; 479 total.
- Dashboard and protected configuration diffs remain empty. No live deployment, TFS mutation, commit or push was performed.

[Microsoft Windows authentication guidance for Kestrel/Negotiate](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0).

## CI queue gate and approved-design fixes

The original CI action incorrectly shared CD's execution gate and delegation-only requirement. CI now uses `Pipeline:Ci:QueueEnabled` independently. `src/DeploymentPortal/appsettings.Development.json` explicitly sets this to `true`. The option is false by default in other environments; production configuration was not modified. `TfsCd.Enabled`, `TfsCd.ExecutionEnabled`, approval rules, artifact/release/environment checks and the approval transport are unchanged.

In local Development, start the Portal from the same Windows user used by the browser. CI may use that process's default Windows credentials only after matching the visitor's real Windows SID. It then reads TFS connection data and verifies the TFS-authenticated SID matches as well, before submitting a build request. TFS enforces that user's **Queue builds** permission on the selected definition. Generic claims/name headers, different users, stored TFS credentials and a Portal admin label cannot substitute for this identity. Production retains the delegated-user requirement.

The real queue path was not invoked during this work. An enabled button indicates local eligibility, not proof that TFS has granted Queue builds. An unavailable Windows/SSPI identity or denied TFS permission still fails closed. If a higher-priority environment variable or user secret overrides the Development file, review `Pipeline__Ci__QueueEnabled` / `Pipeline:Ci:QueueEnabled`; the intended local value is `true`. Do **not** enable CD to fix CI. Any future production CI enablement requires an explicitly approved `Pipeline:Ci:QueueEnabled=true` through the existing production configuration process, without overwriting server files.

The Pipeline-only UI now uses larger explicit typography, a full-width content area, reference-blue action buttons, six readable stage summaries, time-only timeline labels with full UTC timestamps in tooltips, actual durations, safely colored log text, aligned lower cards and stacked responsive layouts. Complete stage evidence remains in tooltips; the AI preview remains disabled. There are no new statuses, fabricated logs, merges, approvals or release-creation operations. Shared sidebar and Dashboard files/styles were not edited for this correction. A browser screenshot comparison still requires verification in the normal interactive browser session.

The Negotiate package was updated from 10.0.0 to 10.0.13 after restore reported security advisories for the old version. Existing transitive `System.Security.Cryptography.Pkcs` 9.0.4 advisories remain and require a separate dependency review; audit warnings were not suppressed.

Files changed for this correction:
- `src/DeploymentPortal/Program.cs`
- `src/DeploymentPortal/appsettings.Development.json`
- `src/DeploymentPortal/DeploymentPortal.csproj`
- `src/DeploymentPortal/Services/PipelineCiOptions.cs` (new)
- `src/DeploymentPortal/Services/PipelineService.cs`
- `src/DeploymentPortal/Services/PipelineUserTransport.cs`
- `src/DeploymentPortal/Views/Pipeline/Index.cshtml`
- `src/DeploymentPortal/wwwroot/css/pipeline.css`
- `src/DeploymentPortal/wwwroot/js/pipeline.js`
- `tests/DeploymentPortal.Tests/PipelineTests.cs`
- `tests/DeploymentPortal.Tests/LocalWindowsAuthenticationTests.cs`
- `docs/cicd-pipeline.md`

The local running process locked normal build output, so validation used `--artifacts-path .tools/pipeline-validation`. The running process was not stopped and no live build, approval or deployment was queued. Stop/restart your existing local debug session to load the new backend, then hard-refresh `/Pipeline` (Ctrl+F5). Keep browser zoom at 100% when comparing the design.

Final validation for these corrections: isolated solution build passed with zero compiler errors and four existing Pkcs dependency-audit warnings (two advisories repeated for app/tests). Targeted pipeline/authentication tests: 40 passed, 1 interactive-SSPI skip. Full suite: 479 passed, 3 existing sandbox private-ACL failures, 1 skipped, 483 total. JavaScript syntax and Git whitespace checks passed. Actual TFS queue permission, queued-build behavior and browser visual rendering were not live-verified.
