# LOCAL GitHub source dropdown investigation — 2026-10-24

## Root cause

The initial browser request for `TfsGitBuilds/Browse` sent `operation=sources&sourceId=0&project=&repository=&branch=`. `CloudExplorerQuery.Project`, `Repository` and `Branch` were non-nullable strings. MVC converted the explicit empty query values to null and applied implicit required validation. `Browse` therefore returned HTTP 400, `Invalid explorer selection.`, **before** calling the source-list service, querying registrations or reading a credential. The same mismatch also affected later browsing stages with an unused blank selector, such as GitHub requests without an Azure project.

Six new HTTP regression cases reproduced the pre-fix failure for source discovery and downstream browsing stages. Earlier endpoint tests omitted unused fields rather than sending explicit empty fields, while earlier browser checks intercepted the endpoint and bypassed MVC binding. Those tests did not catch the mismatch.

There is also a separate local data prerequisite: the configured LOCAL database currently has **zero source registrations**, including zero enabled GitHub sources. Fixing binding therefore produces an explicit empty-source state on this machine, rather than a repository list.

## Database verification

- VS's selected `https` launch profile specifies `Development`, with `https://localhost:7182` and `http://localhost:5140`.
- The default connection resolves to SQL Server `(localdb)\MSSQLLocalDB`, database `DeploymentPortal`, using integrated Windows authentication.
- No DefaultConnection override was found in Development appsettings, launch-profile environment settings, the selected VS debug-profile file, Development user secrets, or current/user/machine environment settings.
- A read-only query against that explicitly checked local server confirmed `DB_NAME() = DeploymentPortal`, an existing `dbo.InfrastructureSources` table, and **0 rows**. The query selected only ID, display name, provider, destination and enabled status; it did not select credential references or tokens.
- No running DeploymentPortal/IIS worker process was present during inspection. Thus this establishes the configured Development target and its current data, rather than claiming inspection of a live process's environment. Historical `.tools` logs point at an older project location and were not treated as runtime configuration evidence.

No database row, connection string, secret, credential file, authorization allowlist or SOURCE configuration was changed. A valid LOCAL registration must be added/enabled through Sources Management using an approved local credential before repository browsing can be accepted against a live provider. No production registration or credential was copied or automatically seeded.

## Fix

- Make project/repository/branch query fields nullable until their browsing stage. Provider adapters retain operation-specific source, ownership, repository, project and branch checks. No global MVC validation suppression or authorization bypass was added.
- Send only the selectors needed by each browser request. Initial source discovery sends only provider and operation, without dummy IDs or empty downstream selectors.
- Display **No GitHub source configured** (or the Azure DevOps equivalent) for a successful empty registration list. Explain that the operator must add or enable a source in Sources Management on the current portal host. Dependent dropdowns/build buttons remain disabled and no provider API is called.
- When registrations exist, require an explicit source selection. Source selection loads repositories (or Azure projects), repository selection loads branches, and branch selection enables build browsing. No repository request is issued merely because the source list loaded.
- Keep failed database/source-list requests as distinct sanitized errors. Do not label them as missing registrations or PAT failures. A missing explorer service returns HTTP 503 with an availability message instead of suggesting an invalid user selection.
- Retain native Windows/Sources Management authorization and the existing credential-reader request allowlists, DPAPI binding, ACL checks and TFS path.

## Changed files for this fix

- `src/DeploymentPortal/Services/CloudBuildExplorer.cs`
- `src/DeploymentPortal/Controllers/TfsGitBuildsController.cs`
- `src/DeploymentPortal/wwwroot/js/cloud-build-explorer.js`
- `tests/DeploymentPortal.Tests/CloudBuildExplorerEndpointTests.cs`
- `tests/DeploymentPortal.Tests/CloudBuildExplorerTests.cs`
- `tests/Browser/cloud-build-explorer.py`
- `docs/GitHubSourceDropdownFix.md`

The preceding multi-source explorer implementation remains in the working tree; its other changed files were not changed by this fix. Browser checks use the actual cloud partial/client code with intercepted responses; HTTP tests exercise real MVC binding and the existing authorization policy. Data/service tests use synthetic registrations and never read stored credentials.

## Verification results

- Before the fix: all six new explicit-empty-selector HTTP regression cases reproduced HTTP 400.
- After the fix: **540 .NET tests passed, 0 failed, 0 skipped**, including those six cases, distinct empty/failed-source-list tests, missing-source validation, authorization, cloud APIs and existing TFS behavior. Both application and test projects built successfully; the new nullable-field warning was resolved.
- Headless Edge browser checks passed for initial requests without selectors, missing GitHub/Azure source messages, disabled downstream controls, explicit source selection, dynamic repository/branch loading, pagination, refresh, artifacts, safe rendering and rate-limit errors.
- Final read-only LocalDB aggregate query confirmed `TotalSources=0`, `GitHubSources=0`, `EnabledGitHubSources=0` in `DeploymentPortal` on `(localdb)\MSSQLLocalDB`.
- `git diff --check` passed. No deployment, push, build trigger, production configuration, database mutation or credential operation was performed.

The tests and read-only LocalDB inspection ran outside the filesystem sandbox to support local compiler, test-listener, browser-process and Windows LocalDB access. No full portal process was launched, so its hosted deployment worker was not started for diagnosis.
