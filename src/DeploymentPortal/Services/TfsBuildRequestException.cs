using System.Net;

namespace DeploymentPortal.Services;

public sealed class TfsBuildRequestException(int definitionId, string stage, bool mayHaveQueued,
    HttpStatusCode? statusCode = null, string? errorType = null, int? errorCode = null, Exception? inner = null)
    : HttpRequestException($"TFS build request failed during {stage} (HTTP {(statusCode is null ? "unavailable" : ((int)statusCode).ToString())}, type {errorType ?? "unavailable"}, code {errorCode?.ToString() ?? "unavailable"}).", inner, statusCode)
{
    public int DefinitionId { get; } = definitionId;
    public string Stage { get; } = stage;
    public bool MayHaveQueued { get; } = mayHaveQueued;
    public string? ErrorType { get; } = errorType;
    public int? ErrorCode { get; } = errorCode;
}
