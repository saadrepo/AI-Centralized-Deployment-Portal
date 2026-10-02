# Phase 1 verification

Verified on the implementation machine using a workspace-local .NET 10.0.401 SDK and Windows PowerShell 5.1.

| Check | Result |
| --- | --- |
| Solution compilation, including Razor | Passed, zero warnings/errors |
| EF InitialCreate migration on SQL Server LocalDB | Applied to isolated `DeploymentPortal_Phase1_Verification` database |
| EF model/migration consistency | No pending model changes |
| C# automated safety/service tests | 18 passed |
| PowerShell filesystem workflow tests | 6 passed |
| Edge desktop/mobile browser workflow | Passed |

## Browser coverage

Dashboard rendering; server add, edit and development-credential failure; persisted Offline status; deployment path rejection; confirmation modal; signed request tamper rejection; SQL queue processing; sanitized failure result; deployment history; idempotent duplicate submission; history-protected server deletion; missing anti-forgery token rejection; settings; mobile navigation and viewport overflow. No browser JavaScript errors were observed.

The browser test uses `phase1-verification.invalid`, without a configured password. It verifies the honest failure path, not successful WinRM connectivity. The isolated verification database retains one test server and one intentionally failed deployment. The default application database is not populated with mock records.

## PowerShell coverage

Real filesystem operations in unique ignored `.tools/ps-tests` directories, with IIS management calls mocked:

1. Successful file replacement and removal of obsolete files, with old files retained in backup.
2. Deployment copy failure followed by successful file rollback and pool restart.
3. Deployment and rollback copy failures reported as requiring manual recovery.
4. Out-of-root target rejection before modifying the target.
5. Backup failure preserves existing files and restarts the pool.
6. Source junction rejection preserves both target files and the junction destination.

## Not verified against a real target

Live WinRM authentication, TLS/firewall configuration, actual IIS pool transitions, application HTTP health, real target ACL behavior and a remote production-size deployment. No Windows Server target or credentials were supplied. Follow README setup and perform acceptance testing on a disposable Windows Server before using this prototype for real releases.

All temporary verification web processes were stopped after testing. Screenshots and test fixtures are in ignored `.tools/verification` and `.tools/ps-tests`.
