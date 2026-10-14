using DeploymentPortal.ViewModels.Tfs;

namespace DeploymentPortal.Services;

public static class TfsApplicationScope
{
    public static bool IsConfigured(RegistryApplication app) => app.TfsGitCiDefinitionId > 0 &&
        app.TfsGitCiBranch == TfsGitBuildService.CiBranch && app.TfsGitArtifactName == "drop" &&
        !string.IsNullOrWhiteSpace(app.TfsGitProject) && !string.IsNullOrWhiteSpace(app.TfsGitRepository);

    public static void Validate(RegistryApplication app)
    {
        if (!IsConfigured(app)) throw new ArgumentException("Application needs an explicit project, repository, CI definition, Dev branch and drop artifact mapping.");
    }

    public static bool Matches(RegistryApplication app, TfsGitCiBuildViewModel build) =>
        IsConfigured(app) && build.Id > 0 && build.Definition?.Id == app.TfsGitCiDefinitionId &&
        build.SourceBranch == app.TfsGitCiBranch && build.Project?.Name == app.TfsGitProject &&
        build.Repository?.Name == app.TfsGitRepository && build.Repository.Type == "TfsGit";

    // Compatibility for existing providers is restricted to the original Portal scope.
    public static void RequireLegacyScope(RegistryApplication app)
    {
        Validate(app);
        if (app.TfsGitCiDefinitionId != 1 || app.TfsGitProject != "deployment-portal-poc" || app.TfsGitRepository != "deployment-portal-poc")
            throw new InvalidOperationException("This provider does not support the selected application scope.");
    }
}
