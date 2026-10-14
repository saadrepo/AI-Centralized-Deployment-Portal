# Sites Registry v1

See [Demo application support](demo-application.md) for the separate definition-2
CI mapping, application selection and controlled CD safety checks. Portal's existing
definition-1 mapping remains unchanged. Demo uses the verified existing CdDemo
physical path `C:\Sites\CdDemo`; CD flags remain disabled.

Edit `src/DeploymentPortal/sites-registry.json`. Portal, API, and Finance ship unconfigured so Deploy is disabled until real mappings are supplied. The file is included in normal Web SDK build/publish output. Existing application configuration can override the `SitesRegistry` section (including environment variables such as `SitesRegistry__Applications__0__Destinations__0__ServerId`).

Example application entry (replace ID and paths with actual values):

```json
{
  "Key": "Portal",
  "DisplayName": "Portal",
  "SourceProvider": "PreStaged",
  "SourceReference": "C:\\Sites-Registry\\Portal",
  "Destinations": [
    {
      "ServerId": 7,
      "SiteName": "Portal",
      "TargetPath": "C:\\Sites\\Portal",
      "ApplicationName": "/",
      "ApplicationPool": null,
      "SitePort": 8080,
      "SiteHostName": null
    }
  ]
}
```

- Get the existing server ID from its `/Servers/Details/{id}` URL. Environment is always read from that registered server.
- For servers that may be deleted and registered again, replace `ServerId` with `"ServerHostNameOrIp": "your-registered-host"`. The registry resolves the current database ID on each request using that exact hostname/IP (case-insensitive). Configure only one selector. Missing or duplicate registered hosts disable preparation; no other server is chosen. Both the assistant and registry use this mapping. If the host address changes, update the registry explicitly.
- SourceReference is an absolute Windows folder **on the destination server**, containing published files including web.config. It is not a folder on the portal host. For multiple destinations, this source path must exist on each server.
- TargetPath is the remote IIS physical path; use the current physical path for existing sites. Source, target, and configured backup directories must not overlap.
- ApplicationName defaults to `/`. ApplicationPool is optional. Port defaults to 8080 and hostname is optional; these binding settings apply only to new sites.
- Keys are case-insensitively unique. Configure one destination per application/server pair. No credentials belong in this file.
- `/SiteRegistry` and application details only read configuration and registered server data. Readiness means configuration passed local checks, not that remote files or IIS have been verified.
- Deploy submits the prepared form with antiforgery protection to the existing `/Deployments/Review`. That action performs the remote preview, then requires existing confirmation before queuing. No registry endpoint executes deployments.
- `ISiteRegistry` separates storage; `IRegistrySourceProvider` separates source resolution. A later artifact integration can add a provider and an explicit preparation/staging step while keeping destination mappings and the Review/Confirm flow. v1 never downloads or copies files.

After publication, retain your configured registry file across releases. JSON syntax/type errors are configuration errors and must be corrected; missing entries or invalid deployment mappings are shown as not ready.
