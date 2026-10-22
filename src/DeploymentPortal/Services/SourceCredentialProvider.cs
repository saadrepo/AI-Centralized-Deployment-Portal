using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeploymentPortal.Services;

public enum SourceTokenProvider { AzureDevOps, GitHub }
public sealed record SourceCredentialMetadata(string Reference, SourceTokenProvider Provider, string Destination);
public interface ISourceCredentialMetadata
{
    IReadOnlyList<SourceCredentialMetadata> List();
}

// Backend-only transport: never returns a token or a client carrying default authorization.
public interface ISourceCredentialProvider
{
    Task<HttpResponseMessage> GetAsync(string reference, SourceTokenProvider provider, string destination,
        Uri requestUri, CancellationToken cancellationToken = default);
}

public sealed class SourceCredentialProvider(IHostEnvironment environment) : ISourceCredentialProvider, ISourceCredentialMetadata
{
    public IReadOnlyList<SourceCredentialMetadata> List()
    {
        try {
            ValidateStore();
            var result = new List<SourceCredentialMetadata>();
            foreach (var file in new DirectoryInfo(StoreRoot).EnumerateFiles("*.json").Take(257)) {
                if (result.Count == 256 || !Regex.IsMatch(Path.GetFileNameWithoutExtension(file.Name), @"\A[a-f0-9]{64}\z")) throw new InvalidOperationException();
                // Read validates the directory/file ACL, DPAPI binding and payload before metadata is returned.
                // Bound file access is first verified using Read; preliminary metadata is never exposed.
                if (!OperatingSystem.IsWindows()) throw new InvalidOperationException();
                for (var directory = file.Directory; directory is not null; directory = directory.Parent)
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
                var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
                ProductionCredentialProvider.ValidateSecurity(file.Directory!.GetAccessControl(), sid);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length is < 1 or > 65536) throw new InvalidOperationException();
                ProductionCredentialProvider.ValidateSecurity(file.GetAccessControl(), sid);
                using var record = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
                var root = record.RootElement;
                if (!Enum.TryParse<SourceTokenProvider>(root.GetProperty("Provider").GetString(), out var provider) || !Enum.IsDefined(provider)) throw new InvalidOperationException();
                var reference = Path.GetFileNameWithoutExtension(file.Name);
                var destination = root.GetProperty("Destination").GetString()!;
                var secret = Read(reference, provider, destination);
                CryptographicOperations.ZeroMemory(secret);
                result.Add(new(reference, provider, destination));
            }
            return result;
        }
        catch { throw new InvalidOperationException(UnavailableMessage); }
    }
    public const string UnavailableMessage = "Source credential or destination unavailable. Ask a host administrator to verify provisioning and permissions.";
    public static string StoreRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeploymentPortal", "SourceCredentials");

    private void ValidateStore()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException();
        var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath));
        var store = Path.GetFullPath(StoreRoot);
        if (store.Equals(app, StringComparison.OrdinalIgnoreCase) || store.StartsWith(app + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        for (var directory = new DirectoryInfo(store); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
        ProductionCredentialProvider.ValidateSecurity(new DirectoryInfo(store).GetAccessControl(), WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException());
    }

    public static string CanonicalDestination(SourceTokenProvider provider, string destination)
    {
        // Deliberately support only the public cloud endpoints, with an explicit organization.
        var pattern = provider switch {
            SourceTokenProvider.AzureDevOps => @"\Ahttps://dev\.azure\.com/([A-Za-z0-9][A-Za-z0-9-]{0,99})/?\z",
            SourceTokenProvider.GitHub => @"\Ahttps://api\.github\.com/orgs/([A-Za-z0-9][A-Za-z0-9-]{0,38})/?\z",
            _ => throw new InvalidOperationException(UnavailableMessage)
        };
        if (!Regex.IsMatch(destination, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidOperationException(UnavailableMessage);
        return destination.TrimEnd('/').ToLowerInvariant() + "/";
    }

    public static byte[] Binding(string reference, SourceTokenProvider provider, string destination)
    {
        if (!Regex.IsMatch(reference, @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant)) throw new InvalidOperationException(UnavailableMessage);
        return Encoding.UTF8.GetBytes($"DeploymentPortal.SourceToken.v1\n{reference}\n{provider}\n{CanonicalDestination(provider, destination)}");
    }

    public static bool AllowsRequest(SourceTokenProvider provider, string destination, Uri request)
    {
        var approved = new Uri(CanonicalDestination(provider, destination));
        if (!request.IsAbsoluteUri || request.Scheme != "https" || request.Port != 443 || request.Host != approved.Host ||
            request.UserInfo.Length != 0 || request.Fragment.Length != 0) return false;
        // Do not permit normalization/encoded separators or a path escape to another organization.
        var originalPath = request.OriginalString.Split('?')[0];
        if (originalPath.Contains('%') || originalPath.Contains('\\') || originalPath.Split('/').Any(p => p is "." or "..")) return false;
        return request.AbsolutePath.Equals(approved.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            request.AbsolutePath.StartsWith(approved.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    public static HttpClientHandler CreateHandler() => new() {
        AllowAutoRedirect = false, UseProxy = false, UseDefaultCredentials = false, Credentials = null,
        CheckCertificateRevocationList = true
    };

    private byte[] Read(string reference, SourceTokenProvider provider, string destination)
    {
        byte[]? plaintext = null;
        try {
            var binding = Binding(reference, provider, destination);
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException();
            var root = Path.GetFullPath(StoreRoot);
            var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath));
            if (root.Equals(app, StringComparison.OrdinalIgnoreCase) || root.StartsWith(app + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
            var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
            ProductionCredentialProvider.ValidateSecurity(new DirectoryInfo(root).GetAccessControl(), sid);
            var file = new FileInfo(Path.Combine(root, reference + ".json"));
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length is < 1 or > 65536) throw new InvalidOperationException();
            ProductionCredentialProvider.ValidateSecurity(file.GetAccessControl(), sid);
            using var record = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
            var value = record.RootElement;
            if (value.GetProperty("Reference").GetString() != reference || value.GetProperty("Provider").GetString() != provider.ToString() ||
                value.GetProperty("Destination").GetString() != CanonicalDestination(provider, destination)) throw new InvalidOperationException();
            plaintext = ProtectedData.Unprotect(Convert.FromBase64String(value.GetProperty("Ciphertext").GetString()!), binding, DataProtectionScope.LocalMachine);
            if (plaintext.Length is < 1 or > 4096 || plaintext.Any(b => b < 33 || b > 126)) throw new InvalidOperationException();
            var result = plaintext;
            plaintext = null;
            return result;
        }
        catch { throw new InvalidOperationException(UnavailableMessage); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task<HttpResponseMessage> GetAsync(string reference, SourceTokenProvider provider, string destination, Uri requestUri, CancellationToken cancellationToken = default)
    {
        byte[]? secret = null;
        try {
            if (!AllowsRequest(provider, destination, requestUri)) throw new InvalidOperationException();
            secret = Read(reference, provider, destination); // Read on every request: rotation/revocation have no cache.
            using var handler = CreateHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.UserAgent.ParseAdd("DeploymentPortal/1.0");
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Authorization = provider == SourceTokenProvider.GitHub
                ? new AuthenticationHeaderValue("Bearer", Encoding.ASCII.GetString(secret))
                : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + Encoding.ASCII.GetString(secret))));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // Return status only, never provider bodies/headers (which could echo credentials).
            if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidOperationException();
            return new HttpResponseMessage(response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch { throw new InvalidOperationException(UnavailableMessage); }
        finally { if (secret is not null) CryptographicOperations.ZeroMemory(secret); }
    }
}
