using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.DataProtection;

namespace DeploymentPortal.Services;

public static class PortalDataProtection
{
    public const string ApplicationName = "DeploymentPortal";
    public const string UnavailableMessage = "Persistent Data Protection storage is unavailable or insecure. Ask an administrator to provision the private key directory on this portal host.";
    public static string KeyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeploymentPortal", "DataProtectionKeys");

    public static IServiceCollection AddPortalDataProtection(this IServiceCollection services, IHostEnvironment environment)
    {
        if (environment.IsDevelopment()) { services.AddDataProtection(); return services; }
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Production Data Protection requires the Windows portal host.");
        ConfigurePersistentKeyRing(services, new DirectoryInfo(KeyRoot));
        // Run before the deployment worker and before serving requests. Never fall back to ephemeral keys.
        services.AddHostedService<PortalDataProtectionStartupCheck>();
        return services;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void ConfigurePersistentKeyRing(IServiceCollection services, DirectoryInfo directory) => services.AddDataProtection()
        .SetApplicationName(ApplicationName)
        .PersistKeysToFileSystem(directory)
        .ProtectKeysWithDpapi(protectToLocalMachine: true);

    public static void ValidateDirectory(string root, string contentRoot)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException();
            root = Path.GetFullPath(root);
            var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
            if (root.Equals(app, StringComparison.OrdinalIgnoreCase) || root.StartsWith(app + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
            var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
            var directoryAcl = new DirectoryInfo(root).GetAccessControl();
            ValidateSecurity(directoryAcl, sid, directory: true);
            foreach (var file in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                if ((file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new InvalidOperationException();
                ValidateSecurity(new FileInfo(file.FullName).GetAccessControl(), sid, directory: false);
            }
        }
        catch { throw new InvalidOperationException(UnavailableMessage); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void ValidateSecurity(FileSystemSecurity security, SecurityIdentifier runtime, bool directory)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!(admins.Equals(owner) || system.Equals(owner) || (!directory && runtime.Equals(owner)))) throw new InvalidOperationException(UnavailableMessage);
        if (directory) WindowsPrivateCdStagingRoot.ValidateSecurity((DirectorySecurity)security, runtime);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        if (rules.Any(r => r.AccessControlType == AccessControlType.Allow && !r.IdentityReference.Equals(admins) && !r.IdentityReference.Equals(system) && !r.IdentityReference.Equals(runtime)))
            throw new InvalidOperationException(UnavailableMessage);
    }
}

public sealed class PortalDataProtectionStartupCheck(IHostEnvironment environment, IDataProtectionProvider protection, ILogger<PortalDataProtectionStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            PortalDataProtection.ValidateDirectory(PortalDataProtection.KeyRoot, environment.ContentRootPath);
            var probe = protection.CreateProtector("DeploymentPortal.StorageCheck.v1");
            const string value = "persistent-key-storage-check";
            if (probe.Unprotect(probe.Protect(value)) != value) throw new InvalidOperationException();
            PortalDataProtection.ValidateDirectory(PortalDataProtection.KeyRoot, environment.ContentRootPath);
            logger.LogInformation("Persistent Windows DPAPI Data Protection storage is ready.");
            return Task.CompletedTask;
        }
        catch
        {
            logger.LogCritical("Persistent Data Protection storage validation failed. Portal startup is stopped; provision its private directory and verify IIS permissions.");
            throw new InvalidOperationException(PortalDataProtection.UnavailableMessage);
        }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
