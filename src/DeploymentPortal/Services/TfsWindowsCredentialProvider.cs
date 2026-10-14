using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DeploymentPortal.Services;

public interface ITfsWindowsCredentialProvider
{
    NetworkCredential GetCredential(Uri collection);
}

// Independent store/purpose from WinRM. Only Development retains process credentials.
public sealed class TfsWindowsCredentialProvider(IHostEnvironment environment) : ITfsWindowsCredentialProvider
{
    public const string UnavailableMessage = "Production TFS Windows credential unavailable. Ask an administrator to provision this collection credential and verify private NTFS permissions on the portal host.";
    public static string StoreRoot => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "DeploymentPortal", "TfsGitCredentials");

    public static byte[] Binding(Uri collection) => Encoding.UTF8.GetBytes(
        "DeploymentPortal.TfsGit.Windows.v1\n" + TfsGitClientConfiguration.GetBaseAddress(collection.AbsoluteUri).AbsoluteUri);

    public static string FileName(Uri collection) => Convert.ToHexString(SHA256.HashData(Binding(collection))).ToLowerInvariant() + ".dpapi";

    public NetworkCredential GetCredential(Uri collection)
    {
        if (environment.IsDevelopment()) return CredentialCache.DefaultNetworkCredentials;
        byte[]? plaintext = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException();
            var binding = Binding(collection); // Apply the existing endpoint allowlist before disk access.
            var root = Path.GetFullPath(StoreRoot);
            var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath));
            if (root.Equals(app, StringComparison.OrdinalIgnoreCase) || root.StartsWith(app + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
            var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
            // Reuse the same read-only IIS ACL policy as the existing WinRM DPAPI store.
            ProductionCredentialProvider.ValidateSecurity(new DirectoryInfo(root).GetAccessControl(), sid);
            var file = new FileInfo(Path.Combine(root, FileName(collection)));
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length is < 1 or > 65536) throw new InvalidOperationException();
            ProductionCredentialProvider.ValidateSecurity(file.GetAccessControl(), sid);
            plaintext = ProtectedData.Unprotect(File.ReadAllBytes(file.FullName), binding, DataProtectionScope.LocalMachine);
            return DecodePayload(plaintext);
        }
        catch { throw new InvalidOperationException(UnavailableMessage); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static NetworkCredential DecodePayload(byte[] plaintext)
    {
        try
        {
            // UTF8 username + NUL delimiter + UTF8 password; this payload is stored only encrypted.
            var separator = Array.IndexOf(plaintext, (byte)0);
            if (separator <= 0 || separator == plaintext.Length - 1) throw new InvalidOperationException();
            var utf8 = new UTF8Encoding(false, true);
            var username = utf8.GetString(plaintext, 0, separator);
            var password = utf8.GetString(plaintext, separator + 1, plaintext.Length - separator - 1);
            if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsControl)) throw new InvalidOperationException();
            var slash = username.IndexOf('\\');
            if (slash < 0) return new NetworkCredential(username, password); // UPN or local username.
            if (slash == 0 || slash == username.Length - 1 || username.IndexOf('\\', slash + 1) >= 0) throw new InvalidOperationException();
            return new NetworkCredential(username[(slash + 1)..], password, username[..slash]);
        }
        catch { throw new InvalidOperationException(UnavailableMessage); }
    }
}
