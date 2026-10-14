using System.Text.RegularExpressions;

namespace DeploymentPortal.Services;

public sealed class AssistantArtifactResolver(ISiteRegistry registry, IServerService servers, ITfsGitBuildService builds, ITfsCdPreparation preparation)
{
    public async Task<CdSelection> ResolveAsync(AssistantCommand command)
    {
        if (!preparation.Enabled) throw new ArgumentException("TFS Git CD preparation is disabled. An administrator must approve and enable it; no Review, confirmation or deployment was started.");
        var apps = registry.List().Where(x => string.Equals(x.Key, command.Application, StringComparison.OrdinalIgnoreCase) || string.Equals(x.DisplayName, command.Application, StringComparison.OrdinalIgnoreCase)).ToList();
        if (apps.Count != 1) throw new ArgumentException("Specify one unique registered application key.");
        var app = apps[0];
        if (app.TfsGitCiDefinitionId != 1 || app.TfsGitCiBranch != TfsGitBuildService.CiBranch || app.TfsGitArtifactName != "drop") throw new ArgumentException("Application needs an explicit Dev CI definition 1/drop mapping.");
        var inventory = await servers.ListAsync();
        var matches = inventory.Where(s => Equal(s.DisplayName, command.Server) || Equal(s.HostNameOrIp, command.Server)).ToList();
        if (matches.Count == 0)
        {
            var alias = Regex.Replace(command.Server, @"\s+server$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var environment = alias.ToLowerInvariant() switch { "dev" => "Development", "prod" => "Production", "stage" => "Staging", _ => alias };
            matches = inventory.Where(s => Equal(s.Environment, environment) && app.Destinations.Any(d => d.Matches(s))).ToList();
        }
        if (matches.Count != 1) throw new ArgumentException("Specify a unique registered server hostname or display name for this application.");
        var buildId = command.BuildId ?? (await builds.GetLatestSuccessfulBuildAsync())?.Id ?? throw new ArgumentException("No completed, successful Dev build was found.");
        return await preparation.SelectAsync(app.Key, buildId, matches[0].HostNameOrIp);
    }
    private static bool Equal(string left, string right) => string.Equals(left, right.Trim(), StringComparison.OrdinalIgnoreCase);
}
