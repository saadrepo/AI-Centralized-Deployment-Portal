# Pipeline source providers

Open `/Pipeline` and choose TFS, GitHub, or Azure DevOps in **Source Provider**.
Switching providers navigates to a fresh page and removes prior application,
source, repository, branch, workflow/pipeline, run, and status selections.

TFS remains the default. Its services, execution endpoints, personal identity,
polling, approval and deployment flow are unchanged, including existing
conditional cancellation/approval controls. The shared primary actions are
**Run CI Build**, **Approve & Deploy**, and **Refresh Status**.

## Cloud browsing

GitHub shows Application, GitHub Source, Repository, Branch, GitHub Actions
Workflow, and Existing workflow run. Azure DevOps additionally requires Project
and shows Azure DevOps Source, Pipeline, and Existing pipeline build.

Select an application, then each resource in order. Every upstream change clears
dependent selections and all displayed status. Refresh Status reloads provider
data and validates the prior selection again. Lists follow provider pagination
automatically, with a 100-page limit and 50-second browser request deadline;
empty, unavailable, timed-out, and unauthorized resources are reported explicitly.
There are no extra pagination action buttons on Pipeline.

The `/TfsGitBuilds` cloud explorer provides an **Open selected repository and
branch in CI/CD Pipeline** navigation link after branch selection. It transfers
only provider/application/source/project/repository/branch selectors. These are
untrusted hints: Pipeline reloads registered applications and sources and validates
repository ownership, project membership and branch existence through the server.
Workflow/pipeline and run selection are not inferred from an application name.
The application is browsing context, not a new deployment association.

Cloud execution and approval buttons are disabled by default. GitHub has an
explicitly authorized execution path described in [Controlled GitHub execution](github-controlled-execution.md).
Azure DevOps remains read-only, including when execution flags are enabled.
Existing provider run results and artifact metadata are shown. Expired GitHub
artifacts are identified. Successful CI never implies approval or deployment.
Refresh Status reads authoritative provider and tracked deployment status without
dispatching a build or deployment.

## Security and implementation

Cloud pages and reads use the existing Sources Management Windows authorization
policy, in addition to normal host authentication. TFS keeps its original guards.
The existing `ICloudBuildExplorer`, registered source service and `ISourceApiReader`
are shared with `/TfsGitBuilds`. Credentials stay server-side and retain the
existing DPAPI, ACL, provider/destination binding, revocation, redirect and URL
allowlist checks. The optional GitHub execution transport reuses these credentials
with a separate narrowly scoped dispatch/download policy; the explorer stays GET-only.

The explorer adds GET-only workflow/definition list/detail and exact-run reads.
Reads validate provider, enabled source, repository, branch, selected definition,
and exact run membership before returning artifacts. Azure definitions must use
the selected Azure Repos Git repository. Arbitrary URLs, dispatch, cancellation,
artifact download and other mutation paths remain outside the read allowlist.
Cloud JSON responses are not cached; errors and logs do not include provider
bodies, credential references, URLs or secrets. Existing antiforgery tokens and
TFS mutation protection remain intact. Provider text is rendered with `textContent`.

API references: [GitHub workflow runs](https://docs.github.com/en/rest/actions/workflow-runs),
[Azure build definitions](https://learn.microsoft.com/en-us/rest/api/azure/devops/build/definitions/list?view=azure-devops-rest-7.1).

## Validation

Run `dotnet test tests/DeploymentPortal.Tests/DeploymentPortal.Tests.csproj -c Release`.
Run `node tests/JavaScript/pipeline-cloud.test.cjs` for the client state tests
(Node.js 18 or later; no npm dependencies). These exercise both providers,
transferred selections, automatic pagination, run/artifact status, authorization
errors, stale-response rejection, GET-only reads and provider-switch clearing.
Provider adapter tests cover definition/run scoping and cross-repository rejection;
endpoint tests cover real Windows authorization, forged identities, invalid
selections, no-store responses, redacted errors, read-only methods, provider
fields and the three cloud action buttons. Existing TFS and source-security
tests remain part of the full suite. No TFS pipeline/deployment configuration
changes or production deployments are required.
