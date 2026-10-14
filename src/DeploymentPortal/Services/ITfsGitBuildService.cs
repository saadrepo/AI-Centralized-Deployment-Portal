using DeploymentPortal.ViewModels.Tfs;

namespace DeploymentPortal.Services;

public interface ITfsGitBuildService
{
    Task<IReadOnlyList<TfsGitCiBuildViewModel>> GetRecentBuildsAsync();
    // Default supports existing test/custom implementations. Production queries TFS with success filters.
    async Task<TfsGitCiBuildViewModel?> GetLatestSuccessfulBuildAsync() => (await GetRecentBuildsAsync()).FirstOrDefault(x => x.Status == "completed" && x.Result == "succeeded");
    Task<TfsGitCiBuildViewModel?> GetBuildAsync(int buildId);
    Task<IReadOnlyList<TfsGitCiArtifactViewModel>> GetArtifactsAsync(int buildId);
    Task<IReadOnlyList<TfsGitCiBuildViewModel>> GetRecentBuildsAsync(RegistryApplication app) { TfsApplicationScope.RequireLegacyScope(app); return GetRecentBuildsAsync(); }
    Task<TfsGitCiBuildViewModel?> GetLatestSuccessfulBuildAsync(RegistryApplication app) { TfsApplicationScope.RequireLegacyScope(app); return GetLatestSuccessfulBuildAsync(); }
    Task<TfsGitCiBuildViewModel?> GetBuildAsync(int buildId, RegistryApplication app) { TfsApplicationScope.RequireLegacyScope(app); return GetBuildAsync(buildId); }
    Task<IReadOnlyList<TfsGitCiArtifactViewModel>> GetArtifactsAsync(int buildId, RegistryApplication app) { TfsApplicationScope.RequireLegacyScope(app); return GetArtifactsAsync(buildId); }
}
