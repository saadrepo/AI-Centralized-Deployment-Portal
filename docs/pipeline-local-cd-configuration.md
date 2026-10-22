# Local CD approval configuration

The reported disabled-execution message comes from `PipelineService.ExecutionReason`: either `TfsCd:Enabled` or `TfsCd:ExecutionEnabled` is false. The local `src/DeploymentPortal/tfs-cd.json` sets both false. Before this fix, the local Development settings supplied no override.

Program constructs the registry/CD configuration with `sites-registry.json`, then `tfs-cd.json`, then the standard ASP.NET configuration. Therefore `appsettings.Development.json` overrides those registry defaults when the launch profile runs Development. User secrets, environment variables and command-line values in the standard configuration can override the Development file as usual.

Changed only the ignored local `src/DeploymentPortal/appsettings.Development.json` to add:

```json
"TfsCd": {
  "Enabled": true,
  "ExecutionEnabled": true
}
```

This does not change ExplorerEnabled or the production environment. Standard IOptions requires a local app/debugger restart. Launch the existing `http` or `https` Development profile, then open `/Pipeline`. New Development-only startup logging reports the effective values and the winning provider/file for these two switches. It does not enumerate configuration or expose secrets. A higher-priority false override remains respected and its provider will be reported instead.

Enabling configuration is necessary but does not authorize a user. Approve and Reject still require a verified real Windows identity, a TFS-authenticated SID matching that identity, eligible individual/group approval, rank/current-attempt checks, requester restrictions, exact build/release/environment correlation and TFS acceptance of the decision. Updated local identity handling: CD now supports Development only when the authenticated real Windows browser SID equals the interactive process SID, and TFS connectionData independently confirms the same SID before any callback runs. Different users, claims-only identities and production process fallback remain prohibited. Production still requires delegation. Approver eligibility and requester restrictions remain unchanged.

A read-only TFS connectionData/current-user approval query was attempted using Windows credentials, but connectionData failed with WebException in this execution environment. Current-user TFS approver permissions are therefore **unverified**, not confirmed or denied. Once a delegated session can reach TFS, the existing status/pending-release reads perform eligibility checks as that caller. Requester restrictions may still forbid that caller even when listed as an approver.

Changed files: ignored local appsettings.Development.json, Program.cs (Development-only diagnostics), PipelineConfigurationDiagnostics.cs, PipelineConfigurationDiagnosticsTests.cs, PipelineTests.cs and this report. Production JSON, SOURCE IIS, SQL, credentials, registry files and deployment scripts were not modified. No live approval, rejection, deployment, commit or push was executed.

Validation: solution build succeeded (0 errors; existing Pkcs dependency advisory warnings remain). Focused Pipeline/configuration/Windows authentication tests: 91 passed, 1 interactive SSPI test skipped, 0 failures. Client-script tests: 8 passed. Tests explicitly confirm that enabled CD switches cannot authorize either decision without delegation, and diagnostics reject secret keys.


Follow-up identity fix: PipelineUserTransport previously rejected non-delegated localhost CD logins before checking TFS eligibility, even though CI accepted the same interactive Windows user. Local CD now uses that user only under the SID restrictions above. PipelineService reports identity/HTTP verification failure instead of only a generic release-unavailable notice. Updated tests cover real local identity, forged identity rejection before callback and unchanged production delegation. No live approval or rejection was performed; real remote approver eligibility remains unverified here.
