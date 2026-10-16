namespace DeploymentPortal.Services;

public sealed class PipelineCiOptions
{
    public bool QueueEnabled { get; set; } // Independent, default-off CI capability; never enables CD.
}
