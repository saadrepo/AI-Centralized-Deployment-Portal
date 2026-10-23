# Automatic GitHub account detection

LOCAL implementation only. No SOURCE configuration, database, credential or script installation was performed; no push or deployment is included.

## Behavior

With GitHub selected, Add/Edit Source accepts a username, a GitHub profile URL, or the existing `/users/{login}/` and `/orgs/{login}/` API URL formats. The helper waits 600 ms after input and calls the authorized, no-store `Sources/ResolveGitHubAccount` endpoint. The detected canonical URL replaces the destination input and appears in a status message. No account-type controls or PAT fields were added. Source saves and command generation independently revalidate detection on the backend, including when JavaScript is unavailable or bypassed.

`GitHubAccountDetector` extracts a strictly validated account name and sends a public, unauthenticated `GET https://api.github.com/users/{login}`. It requires the returned login to match and accepts only exact `type` values `User` and `Organization`, producing `/users/{login}/` or `/orgs/{login}/` respectively. A submitted API path never decides the account type. Invalid input, missing accounts, unauthorized/forbidden responses, rate limits, network errors, timeouts, malformed or unsupported responses fail with a sanitized validation error. There is no fallback or automatic retry.

The implementation follows GitHub's [user API](https://docs.github.com/en/rest/users/users?apiVersion=2022-11-28), [request header guidance](https://docs.github.com/en/rest/using-the-rest-api/getting-started-with-the-rest-api), and [rate-limit guidance](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api). Requests send `User-Agent: DeploymentPortal/1.0`, `Accept: application/vnd.github+json` and `X-GitHub-Api-Version: 2022-11-28`.

## Security and credential compatibility

Input is matched before URI normalization. Only usernames, exact `github.com` profile URLs and exact `api.github.com` account API URLs are accepted. Ports, userinfo, encoded separators, traversal, extra path segments, query strings, fragments and arbitrary hosts are rejected. Discovery never fetches the submitted URL: it constructs one fixed-host `/users/{login}` endpoint. The transport disables redirects, proxies and default credentials, retains TLS certificate validation and revocation checks, and has a ten-second timeout covering response-body reads. Discovery bodies are limited to 64 KiB and JSON depth 16. No HttpClientFactory logging handler, PAT or credential-store access is involved in discovery. Only login, type and canonical destination leave the detector.

The controller retains the existing Sources Management policy. Mutations retain antiforgery protection. The DPAPI format, machine scope, reference generation, additional-entropy binding, and store ACLs are unchanged. Source credential validation and the management script now accept `/users/` in addition to `/orgs/`. GitHub credential requests are restricted to the exact bound account endpoint without query strings or child paths. Azure DevOps request rules and TFS transports are unchanged.

Test Connection first detects the account and validates credential metadata. It then uses the existing credential provider to send an authenticated Bearer request to the bound account endpoint with GitHub headers. Non-success statuses (including 401, 403, 404 and 429) and network failures fail safely without exposing remote response content or PATs. An existing registration whose detected destination differs is not retargeted or migrated. The connection status is updated, but its destination and reference remain intact.

## Manual provisioning after deployment

The **Credentials Provisioning** text link and **Refresh Credentials** remain available. Commands use the detected canonical destination, the absolute SOURCE path `C:\AdminTools\DeploymentPortal\Manage-SourceCredential.ps1`, and pool name `DeploymentPortal`. Display and clipboard use the same returned command. Administrators run it manually in elevated Windows PowerShell 5.1 on SOURCE and enter the PAT only at the existing hidden prompt. No script execution is initiated by the portal.

After a separately approved deployment, the SOURCE administrator must replace the approved script at that path with the updated `tools/Manage-SourceCredential.ps1` before provisioning personal-user credentials. For `saadrepo`, when detection confirms `User`, the generated command is:

```powershell
& "C:\AdminTools\DeploymentPortal\Manage-SourceCredential.ps1" -Action Provision -Provider GitHub -Destination "https://api.github.com/users/saadrepo/" -AppPoolName "DeploymentPortal"
```

The old `/orgs/saadrepo/` reference must not be migrated, rotated across destinations, deleted or reused automatically. The administrator must manually provision a new reference against `/users/saadrepo/` and the operator must explicitly select it. This implementation does not access that SOURCE credential. Existing organization references remain compatible. No database migration is required.

## Limitations and local verification

Detection depends on publicly accessible GitHub account metadata and the host's outbound HTTPS connectivity. Unauthenticated GitHub rate limits apply; operators must wait before retrying denied/rate-limited detection. Enterprise Managed Users or other nonpublic accounts that require authentication cannot be detected by this public discovery path. GitHub Enterprise, bots, renamed-account redirects, arbitrary account endpoints and repository URLs are unsupported. No successful-detection cache is used: form detection, command generation, saves and connection tests can each consume a public API request. Public account metadata success does not establish repository access or PAT permissions to private resources.

Regression coverage uses synthetic GitHub responses and an in-memory database. `GitHubAccountDetectorTests`, `SourcesEndpointTests`, `InfrastructureSourceTests` and `SourceCredentialTests` cover both account types, all supported inputs, exact bindings, authorization, command generation, failure cases and safe transport restrictions. `tests/PowerShell/SourceCredentialProvisioning.Tests.ps1` provisions synthetic Azure DevOps, GitHub organization and GitHub user records in a unique local fixture; it verifies ciphertext, rotation, cross-type rejection, revocation, insecure-ACL rejection and cleanup. `tests/Browser/source-credentials.py` checks automatic detection, canonical URL display, failure without guessing, command display/copy and refresh/selection with intercepted responses. No live PAT or provider access is used by those tests.

Final LOCAL results: **208 .NET tests passed, 0 failed, 0 skipped**; the synthetic PowerShell source-credential fixture passed; headless Edge browser checks passed; `git diff --check` passed. The .NET build emitted only the existing xUnit2031 warning at `PipelineTests.cs:858`.

```powershell
dotnet test tests/DeploymentPortal.Tests/DeploymentPortal.Tests.csproj --no-restore --filter 'FullyQualifiedName~GitHubAccountDetectorTests|FullyQualifiedName~SourceCredentialTests|FullyQualifiedName~SourcesManagementAuthorizationTests|FullyQualifiedName~SourcesEndpointTests|FullyQualifiedName~InfrastructureSourceTests|FullyQualifiedName~TfsGitSecurityTests|FullyQualifiedName~TfsWindowsCredentialTests' --verbosity minimal
& "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -NonInteractive -File tests/PowerShell/SourceCredentialProvisioning.Tests.ps1
python tests/Browser/source-credentials.py
```

The PowerShell and browser checks ran outside the filesystem sandbox to support Windows ACL fixture handling and Playwright subprocess pipes. Both remained LOCAL and used synthetic data only. Browser checks require Playwright and Edge; this workspace used the existing `.tools/python` packages through `PYTHONPATH`.
