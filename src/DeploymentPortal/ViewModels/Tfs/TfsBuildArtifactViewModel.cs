namespace DeploymentPortal.ViewModels.Tfs;

public class TfsBuildArtifactViewModel
{
    public string? Name { get; set; }
    public TfsArtifactResourceViewModel? Resource { get; set; }
}

public class TfsArtifactResourceViewModel
{
    public string? Type { get; set; }
    public string? Data { get; set; }
    public string? Url { get; set; }
}
