namespace DeploymentPortal.ViewModels.Tfs;

public class TfsBuildDetailsViewModel : TfsBuildViewModel
{
    public TfsPipelineViewModel? Definition { get; set; }
    public TfsQueueViewModel? Queue { get; set; }
    public string Duration => StartTime is not { } start ? "Not started"
        : FinishTime is not { } finish ? (Status == "completed" ? "Not available" : "In progress")
        : finish < start ? "Not available" : (finish - start).ToString(@"d\.hh\:mm\:ss");
}

public class TfsBuildPageViewModel
{
    public int BuildId { get; set; }
    public TfsBuildDetailsViewModel? Build { get; set; }
    public IReadOnlyList<TfsBuildLogViewModel> Logs { get; set; } = [];
    public IReadOnlyList<TfsBuildArtifactViewModel> Artifacts { get; set; } = [];
    public string? Error { get; set; }
    public string? LogsError { get; set; }
    public string? ArtifactsError { get; set; }
}
