# DeployHQ — Windows Server Deployment Portal

Phase 1 ASP.NET Core MVC application for Windows server inventory, controlled WinRM connection tests, confirmed IIS application deployments, and a first Docker build-and-run workflow. Uses .NET 10, Razor, locally bundled Bootstrap 5, EF Core and SQL Server. No AI/MCP, identity/RBAC, AD/Entra integration, pipelines, or advanced logging is implemented.

## Docker Deployments

The **Docker Deployments** sidebar page lists containers, status, ports, and tagged images for a selected Docker server. Servers are the Docker CLI contexts configured on the dashboard host; they are independent of the Windows/WinRM server inventory. Configure and test each Docker context outside the portal first. The portal process needs Docker CLI access to those contexts. Docker Engine must be available on each selected server.

Select an image already present on that Docker server, then enter a container name, host/container ports, optional `KEY=value` environment variables, and an optional named Docker volume with a Linux container mount path. The review step pins the selected image ID and current container ID for ten minutes. A deployment to an existing name stops and renames the previous container, then starts the selected image under the original name. The previous container is retained for **Rollback**. If the new run fails or exits immediately, restoration of the previous container is attempted. Rollback stops and preserves the current container, renames the previous one back, and starts it. Rollback restores the previous container configuration and image; it does not revert data in shared volumes. The inventory also offers start, stop, restart, delete, and the latest 200 log lines. Delete retains images and volumes. Docker Compose containers are visible because this is a live Docker inventory; managing them here may conflict with their Compose project.

Environment values are part of Docker container configuration and are passed to the Docker CLI. Do not use this field for secrets. Published host ports bind to all host interfaces. The portal does not install Docker, configure remote contexts, create firewall rules, or save Docker deployment history in SQL Server. Rollback requires its retained previous container; deleting that container removes the rollback target. This prototype has no user authentication, so keep Docker contexts limited to a trusted test environment.

**Build image from source** opens the original local build-and-run flow. Enter an absolute source folder path on the dashboard host with a `Dockerfile` at its root, a new container name, and the correct ports. It builds a unique `deployhq/<name>:<id>` image on the current Docker context and starts a new container. Existing containers are not replaced by this flow. Build/run errors are shown without raw build logs; inspect Docker on the host for details.

## What Phase 1 deploys

A **published application folder already staged on the target Windows server**, including `web.config`, into a new or existing IIS application with a dedicated application pool. Source and target paths are entered by the operator; they are not confined to hard-coded source/target roots. Use `/` as the application name to deploy an IIS site's root application. Before confirmation, the portal inspects IIS and shows Create new site, Add application, or Update existing application. Missing sites/applications are created only after approval; existing application updates require explicit approval. ZIP files, UNC shares, browser uploads and application-level HTTP health checks are outside this implementation.

DeployHQ validates the selected server/environment, tests WinRM, checks package and IIS bindings, stops the dedicated pool, backs up existing files, replaces files, restarts the pool and verifies IIS state. Stopping before backup avoids application writes during the copy. Failed file replacement or verification triggers a file rollback attempt; backups are never automatically deleted.

## Prerequisites

- A Windows dashboard host with the **.NET 10 SDK**, Windows PowerShell 5.1 and SQL Server connectivity. SQL Server LocalDB is suitable for local development; use a supported SQL Server instance for shared testing.
- A Windows Server target with WinRM, IIS, IIS Management Scripts and Tools (`Web-Scripting-Tools`), and the runtime/ASP.NET Core Hosting Bundle needed by the application being deployed.
- A dedicated test account able to create remoting sessions, manage the target's IIS pool, and read/write the configured directories. Phase 1 normally requires local administrator privileges on the target. Do not use this prototype on public networks.
- Firewall access from the dashboard host to the target WinRM listener, preferably HTTPS on 5986. No listener, firewall, TrustedHosts, IIS, or execution policy settings are changed automatically by the portal.

Microsoft references: [ASP.NET Core MVC models and EF Core](https://learn.microsoft.com/en-us/aspnet/core/tutorials/first-mvc-app/adding-model?view=aspnetcore-10.0), [PowerShell Remoting and predefined remote scripts](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/running-remote-commands?view=powershell-7.6).

## Quick start

Run from the solution root in PowerShell:

```powershell
dotnet restore
dotnet tool restore
dotnet build
dotnet ef database update --project src/DeploymentPortal
dotnet run --project src/DeploymentPortal --launch-profile https
```

Open the URL printed by the application. Trust the local development HTTPS certificate if needed with `dotnet dev-certs https --trust`.

The default database is `(localdb)\MSSQLLocalDB`, database `DeploymentPortal`, with integrated Windows authentication. **Migrations are applied explicitly**, not during startup. If SQL Server is unavailable or migrations are missing, requests show a sanitized error page and the deployment worker retries periodically.

### SDK installed during this workspace's implementation

If .NET 10 is not on PATH, this workspace contains an ignored local SDK under `.tools/dotnet`. To use it in the current shell:

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.tools/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $PWD '.tools/packages'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --version
```

These ignored files are not part of the source deliverable; other machines should install the .NET 10 SDK normally.

## SQL Server and migrations

`ApplicationDbContext` defines `Servers` and `Deployments`. The committed `InitialCreate` migration and model snapshot are in `src/DeploymentPortal/Data/Migrations`. Deployment history prevents server deletion. Host/port combinations are unique. No password column exists.

Use user secrets or an environment variable for your connection string:

```powershell
dotnet user-secrets set 'ConnectionStrings:DefaultConnection' 'Server=YOUR-SQL-SERVER;Database=DeploymentPortal;Integrated Security=True;Encrypt=True;TrustServerCertificate=False' --project src/DeploymentPortal
dotnet ef database update --project src/DeploymentPortal -- --environment Development
```

For SQL authentication, keep the connection string in user secrets or a secure environment configuration, never source control. The LocalDB example's `TrustServerCertificate=True` is for local development only.

The requested commands are:

```powershell
# Only when generating the initial schema in a NEW project with no existing migration:
dotnet ef migrations add InitialCreate --project src/DeploymentPortal --output-dir Data/Migrations
# Apply the migration already included in this repository:
dotnet ef database update --project src/DeploymentPortal
# For future model changes, use a NEW descriptive migration name:
dotnet ef migrations add DescribeSchemaChange --project src/DeploymentPortal --output-dir Data/Migrations
```

Do not regenerate `InitialCreate` over the committed migration. The initial-schema SQL script is in `database/InitialCreate.sql`. Apply all current migrations with `dotnet ef database update`, or generate a complete script using `dotnet ef migrations script --idempotent --project src/DeploymentPortal`.

## Configuration and credentials

See `appsettings.example.json`. Copy the relevant settings into `src/DeploymentPortal/appsettings.json`, or override them using environment variables such as `Automation__BackupRoot`. Settings is a read-only view of the path policy, backup root and timeouts; it never shows passwords or connection strings.

Source and target are operator-selected **remote server paths**. The backup root remains administrator-configured. These directories must not overlap or contain one another. Drive roots, protected Windows/program directories, user-profile roots, relative paths, UNC paths, alternate data streams, wildcard paths, junctions and symbolic links are rejected. Secure these directories and parents against untrusted writes. The source must contain a complete published application and `web.config`; a missing target directory is created after approval. Existing IIS applications must use their existing physical path; moving them is not included.

For a missing site, enter an HTTP port and optional host header. Existing bindings are never replaced. Conflicting bindings are rejected. New pools use No Managed Code. For a named application on a new site, the script creates an empty site-root directory beneath `BackupRoot/SiteRoots`, with a separate root pool; the confirmation displays that extra path. For `/`, the site root is your selected target. DNS, TLS and firewall changes are not automatic.

The confirmation signs the request and the inspected IIS/target state. If an application is created, removed, remapped, its pool changes, bindings change, or the target existence/empty state changes after review, execution fails before modification and requires fresh review. Existing target files are backed up before replacement even when IIS is being created. Rollback restores previous files and removes newly created IIS objects where possible; empty newly created directories are retained for inspection.

`ICredentialProvider` isolates secret retrieval. The included `DevelopmentCredentialProvider`:

- Works **only** when `ASPNETCORE_ENVIRONMENT=Development`.
- Resolves a password by the server's lowercase hostname/IP.
- Uses the username saved in the server record.
- Reads .NET user secrets/configuration; no password is accepted by a form or stored in SQL.

Example for a host registered as `web-01.internal`:

```powershell
dotnet user-secrets set 'DevelopmentCredentials:Passwords:web-01.internal' '<test-account-password>' --project src/DeploymentPortal
```

Replace the example value locally. User secrets are **not encrypted**, and entering a password in a shell can leave it in shell history. For local use, you may edit the user-secrets file with a trusted editor instead. Do not paste actual credentials into tickets, screenshots, checked-in JSON or tool logs. Environment variables are also supported, but remain development-only configuration, not a vault.

The runner passes credentials over redirected process standard input, not command-line arguments or temporary script files. It drains but does not log raw PowerShell output/errors. Non-Development environments use Windows DPAPI with a private NTFS credential store. See [Production WinRM setup](docs/production-winrm-credentials.md) for administrator provisioning, SOURCE VM commands, rotation and HTTPS migration. This does not add portal authentication or enable CD execution.

## Prepare a test server

Perform these steps manually on a disposable Windows Server as an administrator. Adapt paths/account names to your environment.

1. Install IIS and its scripting tools, plus the required application runtime/Hosting Bundle.
2. Enable PowerShell Remoting with `Enable-PSRemoting -Force`. Configure a WinRM HTTPS listener using a certificate trusted by the dashboard host and matching the registered hostname. Restrict its firewall rule to the dashboard host. The portal uses `Negotiate` authentication and never skips certificate checks, enables Basic authentication or configures CredSSP.
3. For a workgroup/local-account scenario, verify remote administration permissions and authentication using your organization's WinRM guidance. HTTP/5985 is configurable for a controlled lab, but may require an exact TrustedHosts entry on the host; do not use a wildcard. Prefer HTTPS. Do not disable remote UAC protections globally to make the prototype work.
4. Create the three separate roots, an initial application directory and a release directory:

```powershell
New-Item -ItemType Directory -Force -Path 'C:\DeploymentPackages\CustomerPortal\release-001', 'C:\inetpub\apps\CustomerPortal', 'C:\DeploymentBackups'
```

5. Put a complete `dotnet publish` output, including `web.config`, into the release directory. Put a known working previous release into the target directory. Keep upload/data directories outside the application target: deployment replaces **all** files under the target.
6. In IIS Manager, create or select a site, create a dedicated pool (for ASP.NET Core, typically No Managed Code), and create an application named `CustomerPortal` mapped to `C:\inetpub\apps\CustomerPortal`. Assign its dedicated pool. Start the site/pool and verify the old application works. Do not share the pool with other applications, or place another application's content beneath this target.
7. Grant the remoting account the required file access to the package, target and backup folders. Ensure the application pool identity can read the deployed content. Copying preserves target-root ACL inheritance; this is a file backup, not a full IIS/ACL configuration backup.

Before using the portal, you can verify the connection from the dashboard host without exposing the password:

```powershell
$testCredential = Get-Credential
$testSession = New-PSSession -ComputerName 'web-01.internal' -Port 5986 -UseSSL -Authentication Negotiate -Credential $testCredential
Invoke-Command -Session $testSession -ScriptBlock { $env:COMPUTERNAME }
Remove-PSSession -Session $testSession
```

No real server was configured or deployed to during implementation.

## Use the dashboard

1. Open **Servers → Add server**. Enter display name, hostname/IP, environment, port, TLS and username. Configure the matching development credential on the dashboard host.
2. **Test connection** works before saving. Save the server, then use its connection test to persist Online/Offline status, last check time and OS details. Status is the last manual check, not continuous monitoring.
3. Choose **Deploy** from the server, or open **IIS Deployments**. Select the matching environment, new or existing IIS site/application, your staged source folder and your target physical path. Pool name is optional; an existing assigned pool is used, or a dedicated pool is created for a new application.
4. Click **Deploy**, review the server-validated summary in the confirmation modal, then **Confirm deployment**. The signed confirmation expires after ten minutes and is bound to the request and server connection details. Duplicate submissions reuse the same deployment ID.
5. The result page shows Queued/Running and refreshes every five seconds. It eventually shows Successful or Failed, duration, sanitized result and the reserved backup path. View recent records under **Deployment History** (latest 100).

## Execution and failure behavior

- Controllers only call services. The PowerShell runner exposes an enum of three backend-defined operations, not a script/command endpoint. Fixed scripts receive structured parameters; request values are never concatenated into executable command strings.
- SQL-backed queue records survive a browser disconnect. A hosted worker processes one deployment at a time using a SQL session application lock across portal instances. A target-side named mutex also prevents simultaneous file operations from these scripts on the same machine.
- Server edits are blocked while that server has queued/running deployments. Serializable transactions protect confirmation queuing against simultaneous edits. Historical records preserve deployment configuration; server display/connection metadata is a reference to inventory and may subsequently change.
- At worker recovery, interrupted Running records become Failed with an explicit **unconfirmed remote state** message. Queued records remain eligible. An interrupted or timed-out operation may still be running remotely; inspect the server and backup before retrying. Phase 1 does not provide guaranteed distributed execution recovery or remote cancellation.
- During graceful shutdown, the worker is given deployment timeout +120 seconds to finish. Configure IIS hosting shutdown time limits accordingly, enable AlwaysRunning/preload if hosting the dashboard in IIS, and avoid recycling the host during a deployment. An external hard kill can still interrupt work.
- Source must already exist. Missing target directories are created after approval; for existing IIS applications, target must match the current physical path. Shared pools and other IIS bindings at/below the target are rejected.
- The pool is stopped, a timestamp/GUID backup is created, and files are copied. If replacement fails, the script stops the pool and attempts to restore prior files from that backup and restart. The result explicitly states rollback success/failure. Manual recovery is needed if rollback fails.
- Backups contain application files only; they do not back up databases, IIS configuration, ACLs, running state, or external data. Backups are not automatically deleted. Operators must monitor disk capacity and manage retention separately.
- Success verifies file presence plus IIS site/pool state. It does **not** assert that the application answers HTTP correctly, that dependencies are healthy, or that a database migration succeeded.
- Standalone `PowerShell/Deploy-IISApplication.ps1` returns JSON and exits nonzero on failure. The remoting wrapper uses object output and maps failure to its own nonzero process exit. Routine error messages omit raw exception and command details.

## Architecture

```text
src/DeploymentPortal/
  Controllers/   MVC endpoints, anti-forgery and form responses
  Models/        Server and Deployment entities
  ViewModels/    Validated forms and UI projections
  Data/          SQL Server DbContext and EF migrations
  Services/      Server, WinRM, credentials, confirmation, deployment and worker
  PowerShell/    Fixed remoting entrypoint and defensive IIS deployment script
  Views/         Razor dashboard, inventory, deployment and settings screens
  wwwroot/       Local Bootstrap, CSS and browser interactions
tests/           Automated service safety and browser workflow tests
database/        Reviewable migration SQL
```

Future callers can reuse `IServerService`, `IWinRmService`, `IDeploymentService` and `ICredentialProvider`. There are no MCP/AI dependencies or placeholders for core deployment operations.

## Validation

```powershell
dotnet build
dotnet test
dotnet ef migrations has-pending-model-changes --project src/DeploymentPortal
powershell -NoProfile -NonInteractive -File tests/PowerShell.Workflow.Tests.ps1
```

The automated tests cover rejected paths, overlapping roots, confirmation requirements/tampering, environment/server changes, duplicate submissions, result persistence and the production credential guard. They use an in-memory database and fixed fake remoting responses; they do not replace a real target-server acceptance test.

The PowerShell tests perform real filesystem copies in unique `.tools/ps-tests` fixtures, while mocking only IIS management operations. They verify successful replacement, backup preservation, rollback success/failure, backup failure, out-of-root rejection and junction rejection. Test fixtures are retained for inspection.

`tests/browser_smoke.py` uses Python Playwright with installed Microsoft Edge. Run it **only against a fresh disposable database with no credential configured for `phase1-verification.invalid`**. Start the portal on `http://127.0.0.1:5187`, install Playwright in your test environment, and run `python tests/browser_smoke.py`. It creates one inventory record and verifies forms, anti-forgery, custom paths, overlap rejection, failed preview blocking confirmation/queueing, settings and responsive layout. Confirmation signing and create/update action selection are covered by the C# tests; PowerShell workflow tests cover state revalidation and file recovery. Screenshots go into ignored `.tools/verification`.

## Security limitations

### Temporary TFS Git HTTP PoC

**TFS Git CI Builds** in the sidebar shows the latest 25 builds for `deployment-portal-poc-CI` (definition ID `1`) on `refs/heads/Dev`, including build number, branch, commit, status, and result. Select **View artifacts** to inspect artifact names/types, including `drop`. This uses the same `TfsGit` configuration, Windows authentication, and GET-only security handler as repository browsing. Selected builds are checked against the definition and branch before artifacts are requested. There are no artifact downloads, build queueing, deployments, background polling, or automatic execution. The existing PMSNZ CI/CD and IIS workflows are unchanged.

Successful Dev builds offer **Explore files** and **Prepare Deployment** for a validated Container `drop`. Portal is explicitly mapped to definition 1 / Dev / drop; the deterministic Text Assistant resolves the same preparation and reports build/commit provenance in recorded deployment status. Approved CD integration supports private local/WinRM staging, conservative target-configuration preservation and existing IIS application updates through the same **Review → human Confirm → queue → worker → history**. Explorer, preparation and execution flags remain **off by default**. The additive `AddControlledCd` migration is generated but not applied. WinRM authentication and live acceptance testing remain blockers; no deployment was performed. See [controlled TFS Git CD implementation, policy and exact verification steps](docs/tfs-git-cd.md).

The read-only TFS Git integration uses `TfsGit:BaseUrl=http://netserver:8080/tfs/DefaultCollection`, project `deployment-portal-poc`, and API version `4.1`. HTTP is temporarily accepted only for that exact host, port, and collection (an optional trailing slash is allowed). Other HTTP endpoints, embedded credentials, query strings, fragments, and non-HTTP(S) schemes are rejected at startup. HTTPS endpoints remain configurable for the future migration. The separate `Tfs` configuration and PMSNZ build/release clients are unchanged.

Git requests use GET only and are constrained to the configured origin and collection. Redirects are disabled, including redirects within the same server; a redirect is reported as an API failure. The client connects directly without system/environment HTTP proxies. The existing Windows runtime identity is supplied only for Negotiate/NTLM authentication within that collection; Basic authentication and manually supplied Authorization headers are not supported. No passwords or tokens are added to source configuration. Repository and branch failures show generic messages, with no upstream response body or exception detail displayed.

**Temporary security risk:** Windows authentication over HTTP does not provide TLS encryption or server-certificate validation. Endpoint restrictions do not prevent DNS spoofing, network interception, NTLM relay, or tampering with repository/branch responses. Use only the trusted internal PoC network with a least-privileged identity granted repository read access, and keep portal access restricted. This is a temporary risk acceptance, not a production-secure connection.

**HTTPS migration TODO:** Have the TFS/infrastructure administrator confirm an approved HTTPS hostname and port, or provision an IIS HTTPS binding with an organization-approved certificate. Confirm DNS, firewall access, certificate name/chain trust on the portal host, and Windows authentication under the portal runtime identity. Coordinate any TFS public URL changes with existing CI/CD owners. Then update only `TfsGit:BaseUrl` to the approved HTTPS collection URL, test repository/branch reads, and remove the HTTP exception and its tests. Keep certificate validation enabled and redirects disabled. Do not replace HTTP Windows authentication with a plaintext PAT/password as a workaround.

This is an **unauthenticated, privileged internal prototype**. Default development profiles bind localhost. Do not expose it publicly or to untrusted users; anyone who can use the portal can invoke its permitted operations against configured servers. Anti-forgery protection is not authentication. `AllowedHosts` controls host headers, not user access. No RBAC, identity integration or advanced audit trail is included.

Use only trusted published packages. Their application code runs on IIS with the application identity's permissions. A compromised dashboard host or privileged target administrator is outside the protection boundary. Protect the scripts, configuration, user-secrets file, data-protection keys and deployment roots with OS permissions. These limitations are intentional Phase 1 scope, not production readiness.
