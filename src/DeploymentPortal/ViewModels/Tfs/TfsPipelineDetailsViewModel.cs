namespace DeploymentPortal.ViewModels.Tfs;

public class TfsPipelineDetailsViewModel
{
    public TfsDefinitionViewModel? Definition { get; set; }
    public IReadOnlyList<TfsBuildViewModel> Builds { get; set; } = [];
    public string? Error { get; set; }
    public string? BuildsError { get; set; }
}

public class TfsDefinitionViewModel : TfsPipelineViewModel
{
}

public class TfsQueueViewModel
{
    public int? Id { get; set; }
    public string? Name { get; set; }
}

public class TfsRepositoryViewModel
{
    public string? Type { get; set; }
    public string? DefaultBranch { get; set; }
}
