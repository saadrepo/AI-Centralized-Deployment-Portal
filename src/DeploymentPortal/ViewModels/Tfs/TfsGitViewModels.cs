namespace DeploymentPortal.ViewModels.Tfs;

public sealed class TfsGitPageViewModel
{
    public IReadOnlyList<TfsGitRepositoryWithBranchesViewModel> Repositories { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class TfsGitRepositoryViewModel
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    // Default branch is returned as full ref (e.g. refs/heads/main)
    public string? DefaultBranch { get; set; }
}

public sealed class TfsGitBranchViewModel
{
    public string? Name { get; set; }
}

public sealed class TfsGitRepositoryWithBranchesViewModel
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? DefaultBranchName { get; set; }
    public IReadOnlyList<TfsGitBranchViewModel>? Branches { get; set; }
    public string? BranchesError { get; set; }
}
