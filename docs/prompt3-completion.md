# Prompt 3 implementation and verification

This initial implementation report is supplemented by the later
[safety hardening and readiness report](prompt3-safety-readiness.md). Its validation
counts and lifecycle description below describe the initial verification pass.

The existing Dev workspace changes are preserved. GitHub execution is implemented
behind default-off flags and separate expiring operational authorization. Azure
DevOps execution is explicitly blocked; its existing read-only integration remains.
No production configuration, TFS pipeline configuration or TFS build definition
was changed. No migration was applied, live operation triggered, commit created
or push performed.

## Implemented behavior

- Explicit authorized workflow dispatch with an exact returned run ID and durable
  uncertain-operation handling. No automatic deployment after CI.
- Exact successful run/attempt/commit/artifact validation, SHA-256 verification,
  bounded ZIP extraction and private staging through existing credential services.
- Protected, expiring approvals bound to actor, provider, repository, branch,
  workflow/run, artifact, configuration and registered target/environment.
- Shared deployment-worker integration with existing backup, protected
  configuration, integrity, IIS and rollback safeguards; required HTTPS health
  verification for cloud deployment only.
- Authoritative refresh, active operation recovery, CSRF/HTTPS enforcement,
  authorization, duplicate guards and audit transitions using the existing three
  buttons. Azure execution is denied in UI, flow, adapter and transport.
- EF migration and model snapshot for protected operation/approval records,
  filtered unique indexes and rowversion concurrency protection.

## Changed files

Existing tracked files modified:

- `docs/pipeline-providers.md`
- `src/DeploymentPortal/Controllers/PipelineCloudController.cs`
- `src/DeploymentPortal/Data/ApplicationDbContext.cs`
- `src/DeploymentPortal/Data/Migrations/ApplicationDbContextModelSnapshot.cs`
- `src/DeploymentPortal/Models/Deployment.cs`
- `src/DeploymentPortal/PowerShell/Deploy-IISApplication.ps1`
- `src/DeploymentPortal/PowerShell/Invoke-RemoteOperation.ps1`
- `src/DeploymentPortal/Program.cs`
- `src/DeploymentPortal/Services/CdDeploymentFlow.cs`
- `src/DeploymentPortal/Services/DeploymentService.cs`
- `src/DeploymentPortal/Services/PowerShellService.cs`
- `src/DeploymentPortal/Services/SourceCredentialProvider.cs`
- `src/DeploymentPortal/Views/Pipeline/Cloud.cshtml`
- `src/DeploymentPortal/wwwroot/js/pipeline-cloud.js`
- `tests/DeploymentPortal.Tests/PipelineCloudEndpointTests.cs`
- `tests/DeploymentPortal.Tests/PowerShellCdSecurityTests.cs`
- `tests/JavaScript/pipeline-cloud.test.cjs`
- `tests/PowerShell/SourceCredentialProvisioning.Tests.ps1`

New files:

- `docs/github-controlled-execution.md`
- `docs/prompt3-completion.md`
- `src/DeploymentPortal/Data/Migrations/20261024174124_AddCloudExecution.cs`
- `src/DeploymentPortal/Data/Migrations/20261024174124_AddCloudExecution.Designer.cs`
- `src/DeploymentPortal/Models/CloudExecutionRecord.cs`
- `src/DeploymentPortal/Services/CloudExecutionModels.cs`
- `src/DeploymentPortal/Services/CloudExecutionFlow.cs`
- `src/DeploymentPortal/Services/CloudExecutionProvider.cs`
- `src/DeploymentPortal/Services/CloudProviderTransport.cs`
- `src/DeploymentPortal/Services/CloudPackageStager.cs`
- `tests/DeploymentPortal.Tests/CloudExecutionTests.cs`
- `tests/DeploymentPortal.Tests/CloudExecutionProviderTests.cs`
- `tests/DeploymentPortal.Tests/CloudPackageStagerTests.cs`
- `tests/DeploymentPortal.Tests/CloudMigrationTests.cs`
- `tests/JavaScript/pipeline-cloud-execution.test.cjs`
- `tests/PowerShell/CloudIisHealth.Tests.ps1`

## Test scope and operational limitations

Final validation on Dev:

| Check | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Full .NET suite, final rebuilt Release binaries | 955 | 0 | 1 |
| Focused cloud/endpoint/migration .NET suite | 67 | 0 | 0 |
| Dedicated PowerShell security suite | 8 | 0 | 0 |
| JavaScript regression suites | 3 suites | 0 | 0 |

Release solution build succeeded with zero errors and one existing xUnit2031
warning at PipelineTests.cs:858. `git diff --check` passed. The existing Windows
authentication test is skipped because this process lacks interactive SSPI
credentials; no test was disabled or removed. The final full-suite TRX is
`tests/DeploymentPortal.Tests/TestResults/prompt3-verified.trx`; dedicated results
are in `prompt3-powershell-final.trx` and `prompt3-relevant.trx` in that directory.
Test outputs and build logs are local ignored artifacts, not source changes.

The existing build-only GitHub Actions workflow remains unchanged: successful
tests allow its publish and `deployment-portal-build` artifact upload steps to
run. This session did not trigger a GitHub Actions run or verify a remote upload.

The new tests cover adapter run/artifact scoping, failed and substituted artifacts,
archive safety, native identity authorization, expired/revoked/tampered approvals,
duplicate operations, disabled defaults, Azure denial, HTTPS/CSRF endpoints,
worker health evidence, and actual local file rollback with mocked IIS/network.
Migration verification compares the snapshot/model and generates SQL offline;
it does not connect to or alter a database.

An initial full run exposed intermittent Windows error 1175 (unable to remove the
file being replaced) in the synthetic source-credential rotation fixture's atomic
File.Replace operation. Four subsequent isolated runs passed. Diagnostics now
include the base HRESULT without exposing exception text or secret values; no
encryption, ACL checks or production credential script was changed. This Windows
environment failure has not been proven eliminated. Overlapping test invocations
also contended on the production IIS mutex; final validation must run sequentially.

Live GitHub interoperability, real SQL Server concurrency/locking, and IIS/WinRM
acceptance remain unverified. See [activation prerequisites](github-controlled-execution.md)
for migration review, protected key storage, exact authorized mappings, private
staging/retention, restricted credentials, workflow review, health/backup/rollback
acceptance and separate live authorization. This report does not authorize activation.
