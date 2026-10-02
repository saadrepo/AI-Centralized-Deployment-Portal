using DeploymentPortal.Models;

namespace DeploymentPortal.Services;
public sealed class RemoteCredential(string username, string password)
{
    public string Username { get; } = username;
    public string Password { get; } = password;
    public override string ToString() => "[redacted credential]";
}
public interface ICredentialProvider
{
    Task<RemoteCredential> GetAsync(Server server);
}
// Prototype only: user secrets are not an encrypted vault. Never bind this section to a view or log it.
public class DevelopmentCredentialProvider(IConfiguration configuration, IHostEnvironment environment) : ICredentialProvider
{
    public Task<RemoteCredential> GetAsync(Server server)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Development credentials are disabled outside Development. Configure a secure credential provider.");
        var password = configuration[$"DevelopmentCredentials:Passwords:{server.HostNameOrIp.ToLowerInvariant()}"];
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("No development credential is configured for this server. See the local setup guide.");
        return Task.FromResult(new RemoteCredential(server.Username, password));
    }
}
