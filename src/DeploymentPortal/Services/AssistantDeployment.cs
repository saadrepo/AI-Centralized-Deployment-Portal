using System.Text.RegularExpressions;
using DeploymentPortal.ViewModels;

namespace DeploymentPortal.Services;

public enum AssistantIntent { DeploySite }
public sealed record AssistantCommand(AssistantIntent Intent, string Application, string Server);
public interface IAssistantCommandParser
{
    AssistantCommand Parse(string? text);
}
public sealed class AssistantCommandParser : IAssistantCommandParser
{
    public AssistantCommand Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            throw new ArgumentException("Enter a command of up to 1000 characters: Deploy Portal on DEV server.");
        var match = Regex.Match(text.Trim(), @"^deploy\s+(.+?)\s+on\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success)
            throw new ArgumentException("Specify an application and server: Deploy Portal on DEV server. Only deployment preparation is supported.");
        return new(AssistantIntent.DeploySite, match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
    }
}

// Accepts structured intent independently of its parser. No execution or confirmation dependencies.
public sealed class AssistantDeploymentResolver(ISiteRegistry registry, IServerService servers, SiteRegistryDraftService drafts)
{
    public async Task<DeploymentForm> ResolveAsync(AssistantCommand command)
    {
        if (command.Intent != AssistantIntent.DeploySite) throw new ArgumentException("Only DeploySite is supported.");
        if (string.IsNullOrWhiteSpace(command.Application) || string.IsNullOrWhiteSpace(command.Server))
            throw new ArgumentException("Specify both an application and a server.");
        var applications = registry.List().Where(x => Equal(x.Key, command.Application) || Equal(x.DisplayName, command.Application)).ToList();
        if (applications.Count == 0) throw new ArgumentException($"Application '{command.Application}' was not found in Sites Registry.");
        if (applications.Count != 1) throw new ArgumentException("Application is ambiguous. Use a unique registry application key.");
        var app = applications[0];
        var registered = await servers.ListAsync();
        // Prefer an exact registered identity. Environment aliases only narrow registered registry destinations.
        var matches = registered.Where(x => Equal(x.DisplayName, command.Server) || Equal(x.HostNameOrIp, command.Server)).ToList();
        if (matches.Count == 0)
        {
            var name = Regex.Replace(command.Server.Trim(), @"\s+server$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            matches = registered.Where(x => Equal(x.DisplayName, name) || Equal(x.HostNameOrIp, name)).ToList();
            if (matches.Count == 0)
            {
                var environment = name.ToLowerInvariant() switch
                {
                    "dev" => "Development", "prod" => "Production", "stage" => "Staging", _ => name
                };
                matches = registered.Where(x => Equal(x.Environment, environment) && app.Destinations.Any(d => d.Matches(x))).ToList();
            }
        }
        if (matches.Count == 0) throw new ArgumentException($"No registered destination matches '{command.Server}' for {app.DisplayName}. Check Sites Registry and Servers.");
        if (matches.Count > 1) throw new ArgumentException("Server is ambiguous. Use a specific registered hostname: " + string.Join(", ", matches.Select(x => $"{x.DisplayName} ({x.HostNameOrIp})")));
        return (await drafts.PrepareAsync(app.Key, matches[0].Id)).Form!;
    }
    private static bool Equal(string left, string right) => string.Equals(left, right.Trim(), StringComparison.OrdinalIgnoreCase);
}
