using DeploymentPortal.ViewModels.Tfs;

namespace DeploymentPortal.Services;

public interface ITfsService
{
    Task<IReadOnlyList<TfsPipelineViewModel>> GetBuildDefinitionsAsync(string? search = null);
    Task<TfsDefinitionViewModel> GetBuildDefinitionAsync(int definitionId);
    Task<IReadOnlyList<TfsBuildViewModel>> GetLatestBuildsAsync(int definitionId);
    Task<TfsBuildDetailsViewModel> GetBuildAsync(int buildId);
    Task<IReadOnlyList<TfsBuildLogViewModel>> GetBuildLogsAsync(int buildId);
    Task<string> GetBuildLogAsync(int buildId, int logId, int startLine = 0);
    Task<IReadOnlyList<TfsBuildArtifactViewModel>> GetBuildArtifactsAsync(int buildId);
    Task<TfsRunBuildViewModel> ReviewBuildAsync(int definitionId, string sourceVersion);
    Task<TfsRunBuildViewModel> ReviewExistingBuildAsync(int buildId);
    Task<TfsRunBuildViewModel> ReviewLatestCiBuildAsync(int definitionId);
    Task<TfsBuildDetailsViewModel> QueueBuildAsync(string confirmationToken);
}
