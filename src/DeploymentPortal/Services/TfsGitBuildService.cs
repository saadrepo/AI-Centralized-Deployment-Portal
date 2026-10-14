using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeploymentPortal.ViewModels.Tfs;

namespace DeploymentPortal.Services;

public class TfsGitBuildService(HttpClient client, IConfiguration configuration) : ITfsGitBuildService
{
    public const int CiDefinitionId = 1;
    public const string CiBranch = "refs/heads/Dev";
    public const int RecentBuildLimit = 25;

    private sealed class CollectionResponse<T>
    {
        public List<T>? Value { get; set; }
    }

    public async Task<IReadOnlyList<TfsGitCiBuildViewModel>> GetRecentBuildsAsync()
    {
        var path = GetApiPath("build/builds") +
            $"&definitions={CiDefinitionId}&branchName={Uri.EscapeDataString(CiBranch)}&$top={RecentBuildLimit}&queryOrder=queueTimeDescending";
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<CollectionResponse<TfsGitCiBuildViewModel>>();
        if (page?.Value is null || page.Value.Any(build => build is null)) throw new JsonException("Invalid CI builds response.");
        return page.Value.Where(IsCiBuild).Take(RecentBuildLimit).ToList();
    }

    public async Task<TfsGitCiBuildViewModel?> GetBuildAsync(int buildId)
    {
        if (buildId <= 0) throw new ArgumentOutOfRangeException(nameof(buildId));
        using var response = await client.GetAsync(GetApiPath($"build/builds/{buildId}"));
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var build = await response.Content.ReadFromJsonAsync<TfsGitCiBuildViewModel>()
            ?? throw new JsonException("Invalid CI build response.");
        return build.Id == buildId && IsCiBuild(build) ? build : null;
    }

    public async Task<TfsGitCiBuildViewModel?> GetLatestSuccessfulBuildAsync()
    {
        // Filter on TFS, not within the latest 25 builds. Pin the returned ID before any preparation.
        var path = GetApiPath("build/builds") + $"&definitions={CiDefinitionId}&branchName={Uri.EscapeDataString(CiBranch)}&statusFilter=completed&resultFilter=succeeded&$top=1&queryOrder=finishTimeDescending";
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<CollectionResponse<TfsGitCiBuildViewModel>>();
        if (page?.Value is null || page.Value.Count > 1 || page.Value.Any(x => x is null || !IsCiBuild(x) || x.Status != "completed" || x.Result != "succeeded")) throw new JsonException("Invalid successful CI build response.");
        return page.Value.SingleOrDefault();
    }

    public async Task<IReadOnlyList<TfsGitCiArtifactViewModel>> GetArtifactsAsync(int buildId)
    {
        // Recheck the build scope even when this method is called outside the controller.
        if (await GetBuildAsync(buildId) is null) throw new InvalidOperationException("Build is not available in the configured CI scope.");
        using var response = await client.GetAsync(GetApiPath($"build/builds/{buildId}/artifacts"));
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<CollectionResponse<TfsGitCiArtifactViewModel>>();
        if (page?.Value is null || page.Value.Any(artifact => artifact is null)) throw new JsonException("Invalid CI artifacts response.");
        return page.Value;
    }

    private static bool IsCiBuild(TfsGitCiBuildViewModel build) =>
        build.Id > 0 && build.Definition?.Id == CiDefinitionId &&
        string.Equals(build.SourceBranch, CiBranch, StringComparison.Ordinal);

    private string GetApiPath(string resource)
    {
        var project = configuration["TfsGit:Project"];
        var apiVersion = configuration["TfsGit:ApiVersion"];
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(apiVersion))
            throw new InvalidOperationException("TfsGit project and API version must be configured.");
        return $"{Uri.EscapeDataString(project)}/_apis/{resource}?api-version={Uri.EscapeDataString(apiVersion)}";
    }
}
