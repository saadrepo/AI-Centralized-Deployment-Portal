namespace DeploymentPortal.ViewModels.Tfs;

public class TfsPipelineViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int Revision { get; set; }
    public string QueueStatus { get; set; } = "";
    public TfsQueueViewModel? Queue { get; set; }
    public TfsRepositoryViewModel? Repository { get; set; }
    public bool CanRun => string.Equals(QueueStatus, "enabled", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Repository?.Type, "TfsVersionControl", StringComparison.OrdinalIgnoreCase)
        && Queue?.Id is > 0 && !string.IsNullOrWhiteSpace(Repository?.DefaultBranch);
}
