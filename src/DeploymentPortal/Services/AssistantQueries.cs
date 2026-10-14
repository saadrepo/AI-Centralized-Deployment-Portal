using DeploymentPortal.Models;

namespace DeploymentPortal.Services;

public sealed record AssistantQueryResult(string Message, int? BuildId = null, Guid? DeploymentId = null);

// Read-only: no confirmation, execution, remoting or staging dependencies.
public sealed class AssistantQueries(ISiteRegistry registry, IServerService servers, ITfsGitBuildService builds, IDeploymentService deployments)
{
    public async Task<AssistantQueryResult> ResolveAsync(AssistantCommand command)
    {
        var matches = registry.List().Where(x => string.Equals(x.Key, command.Application, StringComparison.OrdinalIgnoreCase) || string.Equals(x.DisplayName, command.Application, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) throw new ArgumentException(matches.Count == 0 ? "Application was not found in Sites Registry. Specify a registered application key." : "Application is ambiguous. Specify a unique registry key.");
        var app = matches[0];
        if (command.Intent == AssistantIntent.ShowLatestBuild)
        {
            if (app.TfsGitCiDefinitionId != TfsGitBuildService.CiDefinitionId || app.TfsGitCiBranch != TfsGitBuildService.CiBranch || app.TfsGitArtifactName != "drop")
                throw new ArgumentException("This application has no explicit association with the Dev Git CI pipeline. Ask the administrator to configure TfsGitCiDefinitionId=1 for the intended registry application.");
            var build = await builds.GetLatestSuccessfulBuildAsync();
            if (build is null) return new("No completed, successful Dev CI build was found.");
            var artifacts = await builds.GetArtifactsAsync(build.Id);
            var drops = artifacts.Where(x => x.Name == "drop").ToList();
            return new($"{app.DisplayName}: latest successful Dev build {build.Id} ({build.BuildNumber ?? "unnamed"}). Commit: {build.SourceVersion ?? "unavailable"}. Status: {build.Status ?? "unknown"}; result: {build.Result ?? "pending"}. Artifact: {(drops.Count == 1 ? "drop (" + (drops[0].Resource?.Type ?? "unknown type") + ")" : drops.Count == 0 ? "drop unavailable" : "ambiguous drop artifacts")}. This is a read-only query; no deployment was prepared.", build.Id);
        }
        if (command.Intent != AssistantIntent.CheckDeploymentStatus) throw new ArgumentException("Unsupported read-only command.");
        var registered = await servers.ListAsync();
        var destinations = app.Destinations.SelectMany(d => registered.Where(d.Matches).Select(s => (d, s))).ToList();
        if (destinations.Count == 0) throw new ArgumentException("No registered destinations exist for this application. Configure a registry destination first.");
        if (!string.IsNullOrWhiteSpace(command.Server))
            destinations = destinations.Where(x => string.Equals(x.s.DisplayName, command.Server, StringComparison.OrdinalIgnoreCase) || string.Equals(x.s.HostNameOrIp, command.Server, StringComparison.OrdinalIgnoreCase)).ToList();
        if (destinations.Count != 1) throw new ArgumentException("Specify a unique registered destination: Check " + app.Key + " deployment status on <server hostname or display name>.");
        var history = await deployments.ListAsync();
        var relevant = history.Where(x => destinations.Any(pair => pair.s.Id == x.ServerId && string.Equals(pair.d.SiteName, x.SiteName, StringComparison.OrdinalIgnoreCase) && string.Equals(pair.d.ApplicationName, x.ApplicationName, StringComparison.OrdinalIgnoreCase))).OrderByDescending(x => x.StartedAt).ToList();
        if (relevant.Count == 0) return new($"No matching {app.DisplayName} deployment was found in the latest 100 history records.");
        var last = relevant[0];
        var provenance = last.CdSource is { } source ? $" TFS build {source.BuildId}; commit {source.Commit}; artifact {source.Artifact}. Verification: {(source.VerifiedAt is null ? "not recorded" : source.VerifiedAt.Value.ToString("u"))}." : "";
        return new($"{app.DisplayName}: deployment {last.Id}, {last.Status}, server {destinations[0].s.DisplayName}, site {last.SiteName}, application {last.ApplicationName}. {last.ResultMessage}{provenance} Recorded status only; no live IIS check was performed.", DeploymentId: last.Id);
    }
}
