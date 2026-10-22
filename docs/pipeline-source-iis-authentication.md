# SOURCE IIS Windows login for Pipeline

## Code change

`PipelineController.Index` correctly refuses an anonymous principal. The missing behavior was the login challenge: `UseLocalWindowsAuthentication` returned immediately outside Development, so an anonymous IIS request reached that controller and received plain text 401 without asking the IIS Windows handler to authenticate it.

The middleware now protects `/Pipeline` in both environments. Development retains Negotiate. Production uses the native IIS `Windows` authentication scheme registered by ASP.NET Core IIS integration, authenticates/challenges only Pipeline requests, and leaves Dashboard/public routes alone. Missing/disabled native authentication fails closed with a configuration message. No request header is accepted as a substitute identity, no production Kestrel login fallback is registered, and CD/TFS transport and permission rules are unchanged.

SOURCE IIS was not accessed or changed during implementation. The reported controller message proves that its request was anonymous; it does not prove the current IIS feature/provider settings. These must be checked by the server administrator.

## Exact manual SOURCE steps

1. On SOURCE `192.168.4.11`, record the existing settings before making any changes. In an elevated PowerShell window, the following **read-only** commands inspect only the site's authentication sections:

   ```powershell
   & "$env:windir\System32\inetsrv\appcmd.exe" list config 'DeploymentPortal' /section:system.webServer/security/authentication/windowsAuthentication
   & "$env:windir\System32\inetsrv\appcmd.exe" list config 'DeploymentPortal' /section:system.webServer/security/authentication/anonymousAuthentication
   ```

2. If the Windows Authentication feature is absent: Server Manager > Add Roles and Features > Web Server (IIS) > Web Server > Security > **Windows Authentication**. This is an administrator action; the code cannot install it.
3. IIS Manager > Sites > **DeploymentPortal** > Authentication: **Windows Authentication = Enabled**, **Anonymous Authentication = Enabled**. Keep anonymous enabled so Dashboard/public pages retain their existing behavior. Do not apply a server-wide setting or disable anonymous for the entire site. The new middleware requests Windows login only for Pipeline.
4. Windows Authentication > Providers: confirm **Negotiate** is present, normally ahead of NTLM. Retain the site's approved provider/security policy. Do not change app pool identity, credentials, bindings, certificates, kernel-mode settings or global authorization rules to force a login.
5. Confirm the existing ASP.NET Core IIS integration is intact. For out-of-process hosting, Windows token forwarding must not be disabled (`forwardWindowsAuthToken` defaults to true). Inspect the existing deployed `web.config`; do not replace/regenerate it. Do not set the SOURCE environment to Development, and do not copy local Development settings to SOURCE.
6. Publish/release the updated application through the existing reviewed deployment process **when separately authorized**. This implementation did not deploy it. Preserve production JSON, registry/settings, persistent credentials/keys and existing release scripts. After an authorized deployment, the normal application restart loads the new middleware; no global `iisreset` is required by this fix.
7. From a domain office PC, open `https://deployment-portal.vitonta.com/Pipeline`. For integrated sign-in, the browser must be permitted by office policy to send Windows credentials to this exact intranet hostname. Have IT configure its intranet/Windows authentication allowlist if needed; don't use wildcard trust or disable browser security. Verify an anonymous request initially gets 401/Negotiate, followed by an authenticated request. `/Home/Index` must still open without a new login requirement.

## Page login versus TFS approval

Successful IIS login permits access to the existing protected page; it does not grant TFS approver permissions. Production TFS mutations still require the visiting user's delegated Windows identity, matching remote TFS SID, eligible approver/group, requester restriction, approval rank/revision and exact release/build/environment checks. The local same-process-user Development path is unavailable in Production.

If page login works but CD actions report missing delegation, the AD/IIS administrator must inspect Kerberos/SPNs and the approved constrained-delegation policy for the actual IIS service identity and TFS service. Do not enable unconstrained delegation, substitute app-pool credentials for the visitor, or change service accounts in this task. Keep current execution gates unchanged.

Microsoft references: [Windows authentication and anonymous/public routes](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0#allow-anonymous-access), [native IIS Windows scheme](https://github.com/dotnet/aspnetcore/blob/main/src/Servers/IIS/IISIntegration/src/IISDefaults.cs).

## Changed files and validation scope

- `src/DeploymentPortal/Services/LocalWindowsAuthentication.cs`
- `tests/DeploymentPortal.Tests/PipelineIisAuthenticationTests.cs`
- This document.

Tests use a mock native Windows authentication handler to verify production challenge routing, public access, authenticated identity handling, forged-header rejection and missing-handler denial. They do not represent a live SOURCE IIS/Kerberos handshake. No IIS, production configuration, credentials, SQL, Dashboard, TFS pipelines or release scripts were modified; no live build/deployment/approval was executed.

Validation: solution build succeeded, 0 errors. Focused Pipeline/configuration/Windows-authentication tests: 108 passed, 1 interactive SSPI test skipped, 0 failures. Git whitespace check passed. Existing Pkcs dependency advisory warnings remain.
