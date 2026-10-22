# Source Management authorization

`SourcesManagement` is an opt-in authorization policy for future Source Management endpoints. It requires authentication through the host's Windows handler, a genuine authenticated `WindowsIdentity`, and an exact user SID match in the independently configured allowlist. It does not grant Pipeline or Server permissions.

No identities are enabled by default. Missing, empty, null or malformed allowlists deny everyone. If any entry is malformed, the entire list is denied. Entries must be canonical local/domain account SIDs (`S-1-5-21-...` with four unsigned 32-bit components after `21`); names, group SIDs, whitespace, alternate casing and leading-zero components are rejected. At most 256 entries are accepted. Configuration binding/validation failures also deny access. The handler reads current options on each authorization check, including configuration reloads supported by the configuration provider.

## LOCAL and SOURCE configuration

Treat LOCAL and SOURCE as separate deployments with separate approved identities. These labels do not change ASP.NET Core's environment selection. Never copy a local workstation SID into the SOURCE allowlist without verifying and approving the identity actually authenticated there.

Start both deployments with the following deny-all configuration (the code already defaults to this; no appsettings file was changed):

```json
{
  "SourcesManagement": {
    "AllowedUserSids": []
  }
}
```

- **LOCAL:** In the local `Development` environment, set `SourcesManagement:AllowedUserSids` in developer-controlled local configuration. Negotiate is registered by the existing local Windows authentication setup. After verifying the local browser's authenticated SID, explicitly place only approved user SIDs into the array. For environment variables, array entries use `SourcesManagement__AllowedUserSids__0`, `SourcesManagement__AllowedUserSids__1`, etc.
- **SOURCE:** On the workgroup IIS host, an administrator must independently approve server-local accounts and configure their verified request SIDs through the existing host configuration process. The production/native IIS Windows scheme is used for any non-Development environment. Do not change the server's environment to Development to enable authentication. No SOURCE settings or allowlist entries are supplied or modified by this phase.

Access to configuration must remain administrator-controlled. A workgroup account is normally named `HOSTNAME\username`; authorize the request SID, not the name. The same username on a workstation and server can have different SIDs. This policy requires no group lookup, domain join, Kerberos delegation or upstream TFS impersonation. IIS/browser security policy must still permit Windows authentication; this change does not modify that configuration.

## Applying the policy

Apply it to the entire future Sources controller, covering list/read actions, CRUD, enable/disable, credential metadata and connection tests:

```csharp
[Authorize(Policy = SourcesManagementAuthorization.Policy)]
public sealed class SourcesController : Controller
{
    // Future Source Management actions.
}
```

For a future endpoint mapped directly, use `.RequireAuthorization(SourcesManagementAuthorization.Policy)`. Do not add `[AllowAnonymous]` to protected Source actions. For a shared Servers controller, apply the policy to every Source-specific action without changing existing server actions. If Source data is rendered by a shared action, explicitly authorize the Sources branch before querying or rendering any Source data; hiding a tab is insufficient. Prefer separate protected actions to keep the boundary clear.

The policy authenticates using the dedicated `SourcesManagementWindows` scheme, which delegates exclusively to existing Development Negotiate or native IIS Windows authentication. It does not accept an identity supplied by request headers, query/form values or an unrelated prepopulated principal. Anonymous requests receive HTTP 401 and a native Windows challenge when the handler is available. A missing/unavailable native handler returns 401 without a fallback identity. Authenticated users without a genuine Windows identity or an allowed SID receive HTTP 403. No global authorization fallback or existing default authentication scheme is changed.

Keep existing global antiforgery protection on MVC mutations. Future connection tests must use POST plus antiforgery and the same policy, because they exercise stored backend credentials. Authorization does not replace provider/destination credential binding or URL validation. Credential provisioning/rotation/revocation stays host-only through the Phase 1A elevated PowerShell tool.

## Verifying the browser identity safely

This phase adds no diagnostic endpoint. In a controlled local debugging session, stop after native authentication/inside the authorization handler and inspect the **request principal**:

```csharp
context.User.Identity?.IsAuthenticated
context.User.Identity is System.Security.Principal.WindowsIdentity
// Only after confirming the WindowsIdentity type:
identity.Name
identity.User?.Value
identity.AuthenticationType
```

Use `HttpContext.User` when inspecting an MVC request and `AuthorizationHandlerContext.User` inside this handler. Do not use `WindowsIdentity.GetCurrent()` to infer the browser identity: it normally identifies the portal process. A shell's `whoami /user` identifies that shell's account; it does not prove which account the browser authenticated as. Do not derive authorization from an `X-User` header, a client-supplied SID or a name/PrimarySid claim in a generic `ClaimsIdentity`.

On SOURCE, use an approved request-scoped debugger/diagnostic process to inspect those same values after IIS authentication. If a self-identity diagnostic is added in a later phase, protect it with this policy and HTTPS, return only the caller's own name/SID/authentication status, disable caching, and never return headers, cookies, access tokens or credential metadata. Bootstrap the initial administrator-approved server-local SID from host account administration, then verify the actual browser request matches it. No public diagnostic is needed to bootstrap access.

Browser network tools can confirm a 401 Windows challenge and subsequent request result; a successful handshake alone does not prove the application-side SID. Never export authorization headers or raw authentication traces to establish identity.

Expected verification cases: anonymous 401; approved genuine Windows identity succeeds; authenticated nonallowlisted or claims-only identity 403; empty/malformed configuration denies; absent IIS handler 401; public Server routes and existing Pipeline authorization retain their prior behavior.
