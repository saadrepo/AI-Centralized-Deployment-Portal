namespace DeploymentPortal.ViewModels.Tfs;

public class TfsBuildLogViewModel
{
    public int Id { get; set; }
    public string? Type { get; set; }
    public long? LineCount { get; set; }
}

public class TfsLogPageViewModel
{
    public const int PageSize = 1000;
    public int BuildId { get; set; }
    public int LogId { get; set; }
    public string? BuildNumber { get; set; }
    public long? LineCount { get; set; }
    public int StartLine { get; set; }
    public string Content { get; set; } = "";
    public string? Error { get; set; }
    public bool HasNext => LineCount is { } count && count > (long)StartLine + PageSize;
}
