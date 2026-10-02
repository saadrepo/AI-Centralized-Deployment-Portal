using DeploymentPortal.ViewModels.Tfs;

namespace DeploymentPortal.Services;

public interface ITfsReleaseService
{
    Task<IReadOnlyList<TfsReleaseDefinitionViewModel>> GetDefinitionsAsync(string? search = null);
    Task<TfsReleaseDefinitionViewModel> GetDefinitionAsync(int id);
    Task<IReadOnlyList<TfsReleaseViewModel>> GetReleasesAsync(int definitionId);
    Task<TfsReleaseViewModel> GetReleaseAsync(int id);
    Task<TfsReleaseViewModel> GetReleaseDetailsAsync(int id);
    Task<TfsReleaseViewModel> GetLatestDeployableReleaseAsync(int definitionId);
    Task<TfsReleaseDeployReviewViewModel> ReviewDeployAsync(int releaseId, int environmentId);
    Task<TfsReleaseViewModel> DeployEnvironmentAsync(string confirmationToken);
}
