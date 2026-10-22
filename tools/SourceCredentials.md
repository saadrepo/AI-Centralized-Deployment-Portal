Source credentials are administered only on the Windows portal host using an elevated Windows PowerShell 5.1 session. Do not run provisioning from the portal or as the IIS pool. No browser endpoint is provided.

Build/review these repository changes first. Copy the reviewed management script to the host through your normal administrator process; this phase does not deploy it automatically.

Examples (tokens are entered only at the hidden interactive prompt):

```powershell
.\Manage-SourceCredential.ps1 -Action Provision -Provider AzureDevOps -Destination https://dev.azure.com/example/ -AppPoolName DeploymentPortal
.\Manage-SourceCredential.ps1 -Action Provision -Provider GitHub -Destination https://api.github.com/orgs/example/ -AppPoolName DeploymentPortal
.\Manage-SourceCredential.ps1 -Action List -AppPoolName DeploymentPortal
.\Manage-SourceCredential.ps1 -Action Rotate -Reference <64-character-reference> -Provider GitHub -Destination https://api.github.com/orgs/example/ -AppPoolName DeploymentPortal
.\Manage-SourceCredential.ps1 -Action Revoke -Reference <64-character-reference> -AppPoolName DeploymentPortal
```

Use minimally scoped, organization-restricted read tokens. Provision returns a random opaque reference. Rotation preserves that reference and requires the original provider/organization. Changing the binding requires a new credential. List returns reference, provider, organization and update time only. Revocation deletes the local encrypted record; also revoke the token at the upstream provider when retiring or responding to compromise.

Records reside in `%ProgramData%\DeploymentPortal\SourceCredentials`, outside the application and database. Tokens use existing machine-scoped Windows DPAPI, with purpose, reference, provider and canonical organization URL as additional entropy. Metadata is plaintext; tokens are ciphertext. Protected NTFS ACLs permit only Administrators/SYSTEM to write and the specified IIS pool to read. An insecure existing store fails closed and must be investigated by an administrator; the script does not silently repair it. Use the same pool name for all operations.

The backend `ISourceCredentialProvider` resolves fresh ciphertext on every call, validates ACLs and binding, and performs only GET requests within the bound organization. It uses direct HTTPS with certificate validation, no proxy/default Windows credentials, no redirects, and no HttpClientFactory request logging. It returns only an HTTP status response; remote headers and bodies are discarded to prevent reflected-secret exposure. It never returns tokens or an authenticated client. No UI, database schema or CI/CD integration is added here; existing TFS Windows credentials remain separate.

Supported destinations are exactly `https://dev.azure.com/{organization}/` and `https://api.github.com/orgs/{organization}/`. GitHub Enterprise, legacy Azure URLs, personal GitHub accounts and unrelated API paths require separate reviewed support. Organization names are canonicalized to lowercase. References and destinations can be stored in future Source records; secrets cannot.

DPAPI persists across application/pool restarts on the same Windows host and does not depend on the portal Data Protection key ring. Do not copy these files to another machine expecting them to decrypt: reprovision on host replacement. No credential cache exists; rotation/revocation takes effect on the next request. An already-started request cannot be recalled. Local administrators/SYSTEM and trusted code running as the pool are inside this security boundary.

Security tests use synthetic credentials in a temporary repository fixture. They do not execute the elevated management script or access the real credential store.
