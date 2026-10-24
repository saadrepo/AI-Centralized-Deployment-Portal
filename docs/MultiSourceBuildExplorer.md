# Multi-source CI build explorer

The existing `/TfsGitBuilds` page now has a Source Provider selector. TFS 2018 remains the default and retains its application selector, definition/Dev branch scope, recent builds, refresh, artifact page, existing client and existing access behavior. GitHub and Azure DevOps render a cloud explorer inside that same page; no separate provider explorer page was added.

## Browsing

Select GitHub or Azure DevOps Services and choose **Load provider**. Cloud browsing requires the existing `SourcesManagement` policy: native Windows authentication and an administrator-approved user SID. It does not grant access based on client headers or a generic SID claim. This new requirement applies only to the cloud page branch and its `TfsGitBuilds/Browse` metadata endpoint. TFS authorization and API calls are unchanged.

The source selector lists enabled registrations for the selected provider using `IInfrastructureSourceService`. Browser responses include source IDs and display names, not credential references or tokens. Source managers have the same access to registrations that the existing Sources Management policy grants; this change does not introduce a separate per-user source ownership model.

- **GitHub:** choose source, repository and branch, then **Load Builds**. Personal destinations such as `/users/saadrepo/` use authenticated `/user/repos` enumeration to include authorized private repositories. Results are filtered on the server to the registered account's owner login; repositories owned by other accounts never reach the dropdown. Organization destinations use `/orgs/{account}/repos`. No account type is inferred or changed by the explorer.
- **Azure DevOps:** choose source, project, repository and branch, then **Load Builds**. Projects use organization-scoped Project APIs, repositories and refs use project-scoped Git APIs, and builds are filtered to repository ID, `TfsGit` repository type and exact branch.
- **Refresh Builds** reloads the first build page using the current selection. Project, repository and branch lists have **Load more** controls. Build pages have **Next builds**; refresh returns to the newest page. Artifacts have a separate metadata list and **Load more artifacts** when needed. Selections are retained when appending list pages and dependent selections/results are cleared when an upstream selection changes.
- Each cloud build page contains up to 20 runs/builds. Artifact metadata is checked for each listed run/build so **View Artifacts** is offered when artifacts exist. Artifact details are shown in the same page, including GitHub expiry state and size where available. These controls do not download binaries, queue builds or prepare deployments.

Loading states disable conflicting controls and mark the explorer busy. Empty lists, empty build results, access failures, provider limits and transport errors have explicit messages and manual retry/refresh paths. API errors never become a successful empty result. No automatic retry loop is used.

## Backend and security

`CloudBuildExplorer` contains provider-specific read adapters. It validates the selected source ID, enabled state and provider on every request. Before browsing descendants it rechecks repository ownership and, for Azure DevOps, project membership. Before listing builds it validates the selected branch through the provider. Build and artifact requests revalidate the selected run/build ID, branch and repository/project ownership. Upstream URLs are never followed or returned as artifact download links.

`SourceCredentialProvider` implements a separate backend-only `ISourceApiReader`. It uses the existing private DPAPI `Read` path on every API request, including the existing reference/provider/destination binding and protected ACL validation. No credential cache, store format, provisioning script, application-pool permission, database migration or TFS credential change is introduced. The original status-only `GetAsync` method and its `AllowsRequest` guard remain unchanged.

The JSON reader has its own exact `SourceExplorerRequestPolicy`. All requests are GET and use existing direct HTTPS handling with certificate validation/revocation checks, no proxy, no default Windows credentials and no redirects. GitHub uses Bearer authentication plus the existing GitHub headers; Azure DevOps uses PAT Basic authentication. Only these metadata families are allowed:

| Provider | Allowed metadata |
| --- | --- |
| GitHub personal | `/user/repos` with fixed affiliation/visibility/sort and bounded page parameters; server filters returned owners |
| GitHub organization | `/orgs/{bound-owner}/repos` with fixed list parameters |
| GitHub repository | `/repos/{bound-owner}/{validated-repo}`, branches and a single validated branch, workflow runs and a single run, run artifacts |
| Azure DevOps organization | `_apis/projects` and one GUID project under the bound organization |
| Azure DevOps project | GUID repository list/detail, Git heads refs, filtered build list/detail and build artifacts, all at API version 7.1 |

Unknown query keys, duplicate keys, unapproved endpoints, cross-owner/organization paths, credentials in URLs, non-HTTPS origins, alternate ports, fragments and traversal are rejected. GitHub branch names are encoded as one endpoint parameter. Branches are limited to 250 characters and valid safe Git ref syntax; literal percent signs are not supported because double-encoded path input is rejected. A page containing an unsupported branch name fails with a sanitized error rather than sending an unsafe path.

Responses are limited to 2 MiB, JSON depth 32, and bounded metadata fields/collections. Each upstream request has a 15-second timeout; each explorer operation has a 45-second deadline. Decoded JSON strings/property names and continuation tokens are checked for reflected PAT/Basic-authorization material before the backend parser receives them. Arbitrary response bodies, headers, URLs and exception messages are not sent to the browser or logged. The controller returns only approved metadata DTOs and fixed/sanitized errors. The browser creates text nodes/options rather than interpreting provider content as HTML. Responses and fetches disable caching; browser storage is not used.

GitHub `Link` headers supply only a next-page boolean: the service reconstructs an approved URL from a validated page number and never follows the header URL. Azure continuation headers are bounded opaque data, escaped into an approved endpoint's query. Azure's repository and build-artifact list APIs do not offer upstream pagination, so their bounded lists are paginated locally in groups of 100. GitHub repository/branch/artifact pages and Azure project/ref pages use provider pagination. An owner-filtered personal repository page can be empty while more provider pages remain; the UI still offers Load more.

The adapters use the documented [GitHub repository APIs](https://docs.github.com/en/rest/repos/repos), [workflow-run APIs](https://docs.github.com/en/rest/actions/workflow-runs), [artifact APIs](https://docs.github.com/en/rest/actions/artifacts), [Azure repositories](https://learn.microsoft.com/en-us/rest/api/azure/devops/git/repositories/list?view=azure-devops-rest-7.1), [refs](https://learn.microsoft.com/en-us/rest/api/azure/devops/git/refs/list?view=azure-devops-rest-7.1), [build lists](https://learn.microsoft.com/en-us/rest/api/azure/devops/build/builds/list?view=azure-devops-rest-7.1), and [build artifacts](https://learn.microsoft.com/en-us/rest/api/azure/devops/build/artifacts/list?view=azure-devops-rest-7.1).

## Local verification and remaining prerequisites

All automated checks use synthetic provider responses, in-memory sources, localhost test listeners or unique local credential fixtures. No live provider, SOURCE server, production database, build queue or deployment was contacted by these checks.

The solution built successfully outside the filesystem sandbox after the sandbox canceled the compiler. The expanded source/cloud/TFS regression suite passed **528 tests, 0 failed, 0 skipped**. The only compiler warning was the pre-existing xUnit2031 at `PipelineTests.cs:858`. `tests/Browser/cloud-build-explorer.py` passed in headless Edge using the actual cloud partial/client script and intercepted API responses, including provider switching, every pagination control, loading, refresh, artifact rendering, safe text, rate-limit failure and empty sources. Browser checks require Playwright and Edge; this workspace uses `.tools/python` through `PYTHONPATH`.

The four existing synthetic PowerShell credential fixtures initially returned **3 passed, 1 failed**. Source-token provisioning, TFS credential provisioning and portal storage passed. The unchanged WinRM fixture failed during `File.Replace` with “Unable to remove the file to be replaced”; an isolated rerun of that same unmodified fixture then passed all checks. No script, ACL, credential or test assertion was weakened to obtain that pass. This transient local filesystem failure remains recorded rather than reporting the original four-case test run as green.

Commands:

```powershell
dotnet build DeploymentPortal.sln --no-restore --disable-build-servers -p:UseSharedCompilation=false --verbosity minimal
dotnet test tests/DeploymentPortal.Tests/DeploymentPortal.Tests.csproj --no-restore --disable-build-servers -p:UseSharedCompilation=false --filter 'FullyQualifiedName~CloudBuildExplorer|FullyQualifiedName~SourceExplorerRequestPolicy|FullyQualifiedName~SourceApiResponse|FullyQualifiedName~Tfs|FullyQualifiedName~MultiApplicationCi|FullyQualifiedName~SourceCredentialTests|FullyQualifiedName~InfrastructureSourceTests|FullyQualifiedName~SourcesEndpointTests|FullyQualifiedName~SourcesManagementAuthorizationTests|FullyQualifiedName~GitHubAccountDetectorTests' --verbosity minimal
dotnet test tests/DeploymentPortal.Tests/DeploymentPortal.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PowerShellCredentialProvisioningTests' --verbosity minimal
python tests/Browser/cloud-build-explorer.py
```

Live acceptance remains an operator task after a separately approved deployment. Existing enabled source registrations, database/schema availability, approved Sources Management access, outbound trusted HTTPS, and correctly bound credentials are required. GitHub tokens need access to the intended private repositories and the metadata/contents/Actions read permissions required by the selected APIs; Azure DevOps PATs need project, code and build read access. Provider policies, token scope, account access, API rate limits and response-size limits still apply. Run artifact checks add up to 20 bounded metadata requests per build page; unavailable Actions/build read access can fail that page instead of hiding the error.

The previously incorrect `/orgs/saadrepo/` binding is never rewritten or reused for `/users/saadrepo/`. The explorer consumes whichever enabled registration the operator selected and fails inaccessible/misbound destinations. No provisioning script update is required specifically for this explorer. Personal-user provisioning still requires the earlier approved `/users/`-capable script on SOURCE. No push, deployment, real build trigger, SOURCE setup, credential rotation or production database modification is included.

## Changed files

- `src/DeploymentPortal/Controllers/TfsGitBuildsController.cs`
- `src/DeploymentPortal/Program.cs`
- `src/DeploymentPortal/Services/SourceCredentialProvider.cs`
- `src/DeploymentPortal/Services/SourceApiReader.cs`
- `src/DeploymentPortal/Services/SourceExplorerRequestPolicy.cs`
- `src/DeploymentPortal/Services/CloudBuildExplorer.cs`
- `src/DeploymentPortal/ViewModels/Tfs/TfsGitCiViewModels.cs`
- `src/DeploymentPortal/Views/TfsGitBuilds/Index.cshtml`
- `src/DeploymentPortal/Views/TfsGitBuilds/_CloudExplorer.cshtml`
- `src/DeploymentPortal/wwwroot/js/cloud-build-explorer.js`
- `tests/DeploymentPortal.Tests/CloudBuildExplorerTests.cs`
- `tests/DeploymentPortal.Tests/CloudBuildExplorerEndpointTests.cs`
- `tests/DeploymentPortal.Tests/SourceExplorerRequestPolicyTests.cs`
- `tests/DeploymentPortal.Tests/SourceApiResponseTests.cs`
- `tests/Browser/cloud-build-explorer.py`
- `docs/MultiSourceBuildExplorer.md`
