using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DeploymentPortal.Models;

namespace DeploymentPortal.Services;

// Machine DPAPI permits administrator provisioning without impersonating IIS. The protected
// DACL is the identity boundary: only SYSTEM/admins can write and only the pool can read.
public sealed class ProductionCredentialProvider(IHostEnvironment environment) : ICredentialProvider
{
    public const string UnavailableMessage = "Production WinRM credential unavailable. Ask an administrator to provision the matching credential and verify its private NTFS permissions on this portal host.";
    public static string StoreRoot => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "DeploymentPortal", "WinRmCredentials");

    public static byte[] Binding(Server server)
    {
        if (string.IsNullOrWhiteSpace(server.HostNameOrIp) || server.HostNameOrIp.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':')) ||
            string.IsNullOrWhiteSpace(server.Username) || server.Username.Any(char.IsControl) || server.WinRmPort is < 1 or > 65535)
            throw new InvalidOperationException(UnavailableMessage);
        return Encoding.UTF8.GetBytes($"DeploymentPortal.WinRm.v1\n{server.HostNameOrIp.ToLowerInvariant()}\n{server.Username.ToLowerInvariant()}\n{server.WinRmPort}\n{(server.UseSsl ? "https" : "http")}");
    }

    public static string FileName(Server server) => Convert.ToHexString(SHA256.HashData(Binding(server))).ToLowerInvariant() + ".dpapi";

    public Task<RemoteCredential> GetAsync(Server server)
    {
        byte[]? plaintext = null;
        try
        {
            if (!OperatingSystem.IsWindows() || environment.IsDevelopment()) throw new InvalidOperationException();
            var root = Path.GetFullPath(StoreRoot);
            var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath));
            if (root.Equals(app, StringComparison.OrdinalIgnoreCase) || root.StartsWith(app + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            // Reject redirects anywhere along the store path, including ProgramData.
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
            var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
            ValidateSecurity(new DirectoryInfo(root).GetAccessControl(), sid);
            var file = new FileInfo(Path.Combine(root, FileName(server)));
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length is < 1 or > 65536) throw new InvalidOperationException();
            ValidateSecurity(file.GetAccessControl(), sid);
            var encrypted = File.ReadAllBytes(file.FullName);
            plaintext = ProtectedData.Unprotect(encrypted, Binding(server), DataProtectionScope.LocalMachine);
            var password = new UTF8Encoding(false, true).GetString(plaintext);
            if (string.IsNullOrEmpty(password)) throw new InvalidOperationException();
            return Task.FromResult(new RemoteCredential(server.Username, password));
        }
        catch
        {
            // Never propagate OS, path, cryptographic or secret-bearing exception details.
            throw new InvalidOperationException(UnavailableMessage);
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void ValidateSecurity(FileSystemSecurity security, SecurityIdentifier runtimeSid)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!security.AreAccessRulesProtected || !(admins.Equals(owner) || system.Equals(owner))) throw new InvalidOperationException(UnavailableMessage);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        foreach (var rule in rules.Where(r => r.AccessControlType == AccessControlType.Allow))
        {
            if (rule.IdentityReference.Equals(admins) || rule.IdentityReference.Equals(system)) continue;
            if (!rule.IdentityReference.Equals(runtimeSid) || (rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) != 0)
                throw new InvalidOperationException(UnavailableMessage);
        }
        if (!rules.Any(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(runtimeSid) &&
            (r.PropagationFlags & PropagationFlags.InheritOnly) == 0 && (r.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute))
            throw new InvalidOperationException(UnavailableMessage);
    }
}
