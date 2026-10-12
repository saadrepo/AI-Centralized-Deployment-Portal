using DeploymentPortal.ViewModels.Tfs;

namespace DeploymentPortal.Services;

public interface ITfsGitService
{
    Task<IReadOnlyList<TfsGitRepositoryViewModel>> GetRepositoriesAsync();
    Task<IReadOnlyList<TfsGitBranchViewModel>> GetBranchesAsync(string repositoryId);
}
