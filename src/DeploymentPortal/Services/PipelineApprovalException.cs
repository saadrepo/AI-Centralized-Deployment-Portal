namespace DeploymentPortal.Services;

public sealed class PipelineApprovalException(string stage, int? httpStatus, string? upstreamType, string? tfsCode, bool sent)
    : Exception($"TFS approval action failed at {stage}. HTTP {httpStatus?.ToString() ?? "unavailable"}; type {upstreamType ?? "unavailable"}; code {tfsCode ?? "unavailable"}. "
        + (httpStatus is 401 or 403 ? "TFS denied the current Windows user's access. " : httpStatus is 409 or 412 ? "Approval state/revision changed; refresh before retrying. " : "")
        + (sent ? "Verify current TFS status before retrying; no automatic retry was made." : "No decision was submitted."))
{
    public string Stage { get; } = stage;
    public int? HttpStatus { get; } = httpStatus;
    public string? UpstreamType { get; } = upstreamType;
    public string? TfsCode { get; } = tfsCode;
    public bool MayHaveReachedTfs { get; } = sent;
}
