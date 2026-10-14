namespace DeploymentPortal.ViewModels.Tfs;

public sealed class TfsGitCiBuildsPageViewModel
{
    public string ApplicationKey { get; set; } = "Portal";
    public IReadOnlyList<DeploymentPortal.Services.RegistryApplication> Applications { get; set; } = [];
    public IReadOnlyList<TfsGitCiBuildViewModel> Builds { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class TfsGitCiArtifactsPageViewModel
{
    public string ApplicationKey { get; set; } = "Portal";
    public TfsGitCiBuildViewModel? Build { get; set; }
    public IReadOnlyList<TfsGitCiArtifactViewModel> Artifacts { get; set; } = [];
    public string? Error { get; set; }
    public string? ArtifactsError { get; set; }
}

public sealed class TfsGitCiBuildViewModel
{
    public int Id { get; set; }
    public string? BuildNumber { get; set; }
    public string? SourceBranch { get; set; }
    public string? SourceVersion { get; set; }
    public string? Status { get; set; }
    public string? Result { get; set; }
    public TfsGitCiDefinitionViewModel? Definition { get; set; }
    public TfsGitCiProjectViewModel? Project { get; set; }
    public TfsGitCiRepositoryViewModel? Repository { get; set; }
    public string? BranchName => SourceBranch?.StartsWith("refs/heads/", StringComparison.Ordinal) == true
        ? SourceBranch["refs/heads/".Length..] : SourceBranch;
}

public sealed class TfsGitCiProjectViewModel { public Guid Id { get; set; } public string? Name { get; set; } }
public sealed class TfsGitCiRepositoryViewModel { public string? Id { get; set; } public string? Name { get; set; } public string? Type { get; set; } }

public sealed class TfsGitCiDefinitionViewModel
{
    public int Id { get; set; }
}

public sealed class TfsGitCiArtifactViewModel
{
    public string? Name { get; set; }
    public TfsGitCiArtifactResourceViewModel? Resource { get; set; }
}

public sealed class TfsGitCiArtifactResourceViewModel
{
    // Deliberately omit artifact URLs, download URLs, and filesystem paths.
    public string? Type { get; set; }
}
