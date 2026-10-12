using System.Net.Http.Json;
using System.Text.Json;
using DeploymentPortal.ViewModels.Tfs;

namespace DeploymentPortal.Services;

public class TfsGitService(HttpClient client, IConfiguration configuration) : ITfsGitService
{
    private sealed class ReposResponse
    {
        public List<TfsGitRepositoryViewModel>? Value { get; set; }
    }

    private sealed class RefsResponse
    {
        public List<GitRef>? Value { get; set; }
    }

    private sealed class GitRef
    {
        public string? Name { get; set; }
    }

    public async Task<IReadOnlyList<TfsGitRepositoryViewModel>> GetRepositoriesAsync()
    {
        var path = GetApiPath($"git/repositories");
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<ReposResponse>();
        if (page?.Value is null) throw new JsonException("TFS returned an invalid git repositories response.");
        return page.Value;
    }

    public async Task<IReadOnlyList<TfsGitBranchViewModel>> GetBranchesAsync(string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(repositoryId)) throw new ArgumentException("Repository id is required.", nameof(repositoryId));
        var path = GetApiPath($"git/repositories/{Uri.EscapeDataString(repositoryId)}/refs") + "&filter=heads/";
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<RefsResponse>();
        if (page?.Value is null) throw new JsonException("TFS returned an invalid git refs response.");
        return page.Value.Select(r => new TfsGitBranchViewModel { Name = TrimRef(r.Name) }).ToList();
    }

    private static string TrimRef(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;
        const string heads = "refs/heads/";
        if (fullName.StartsWith(heads, StringComparison.OrdinalIgnoreCase)) return fullName.Substring(heads.Length);
        return fullName;
    }

    private string GetApiPath(string resource)
    {
        var project = configuration["TfsGit:Project"];
        var apiVersion = configuration["TfsGit:ApiVersion"];
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(apiVersion))
            throw new InvalidOperationException("TfsGit project and API version must be configured.");
        return $"{Uri.EscapeDataString(project)}/_apis/{resource}?api-version={Uri.EscapeDataString(apiVersion)}";
    }
}
