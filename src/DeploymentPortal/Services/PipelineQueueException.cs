using System.ComponentModel;
using System.Net;
using System.Text.Json;

namespace DeploymentPortal.Services;

// Only safe, locally-authored diagnostics. Never include upstream messages/bodies or credential material.
public sealed class PipelineQueueException(string code, string stage, string reason, bool mayHaveQueued = false,
    HttpStatusCode? upstreamStatus = null, string? upstreamType = null) : Exception(reason)
{
    public string Code { get; } = code;
    public string Stage { get; } = stage;
    public bool MayHaveQueued { get; } = mayHaveQueued;
    public HttpStatusCode? UpstreamStatus { get; } = upstreamStatus;
    public string? UpstreamType { get; } = upstreamType;
    public string UserMessage => Message + (MayHaveQueued
        ? " The queue request may have reached TFS. Refresh recent builds before retrying; do not submit it again automatically."
        : Stage == "sending queue POST" ? " TFS rejected the queue request; no successful build acceptance was returned."
        : " The Portal did not send a build queue POST.");

    public static PipelineQueueException From(Exception ex, string stage, bool sent = false)
    {
        if (ex is HttpRequestException { StatusCode: { } status }) return Http(stage, status, sent);
        if (ex is UnauthorizedAccessException) return new("CI_IDENTITY_NOT_VERIFIED", stage,
            "Windows/TFS identity could not be verified. Use the same Windows account for the browser and local Portal; remote users require delegation.");
        if (ex is Win32Exception || ex.InnerException is Win32Exception)
            return new("CI_WINDOWS_CREDENTIALS_UNAVAILABLE", stage, "Windows SSPI credentials are unavailable. Run the Portal from your normal signed-in Windows session.", sent);
        if (ex is OperationCanceledException) return new("CI_TIMEOUT", stage, "TFS timed out or the request was canceled during " + stage + ".", sent);
        if (ex is JsonException) return new("CI_INVALID_TFS_RESPONSE", stage, "TFS returned an invalid JSON response during " + stage + ".", sent);
        if (ex is HttpRequestException or IOException) return new("CI_CONNECTION_FAILED", stage, "Unable to connect to TFS during " + stage + ". Check the configured collection URL, DNS and Windows authentication.", sent);
        return new("CI_CONFIGURATION_OR_RESPONSE", stage, "TFS configuration or response could not be verified during " + stage + ".", sent);
    }

    public static PipelineQueueException Http(string stage, HttpStatusCode status, bool sent, string? type = null)
    {
        var hint = status switch
        {
            HttpStatusCode.Unauthorized => "TFS rejected Windows authentication (HTTP 401). Verify the signed-in account and TFS authentication configuration.",
            HttpStatusCode.Forbidden => "TFS denied access (HTTP 403). Verify this user's project, repository and Queue builds permissions.",
            HttpStatusCode.NotFound => "TFS endpoint or resource was not found (HTTP 404). Verify collection URL, project, definition and API compatibility.",
            HttpStatusCode.BadRequest => "TFS rejected the request (HTTP 400). Verify definition/agent queue, branch, required parameters and TFS 2018 API support.",
            HttpStatusCode.LengthRequired => "TFS requires a fixed Content-Length (HTTP 411). Verify IIS/proxy request handling.",
            _ when (int)status is >= 300 and < 400 => "TFS redirected the request. Redirects are blocked to protect Windows credentials; verify the collection URL.",
            _ => $"TFS returned HTTP {(int)status} during {stage}."
        };
        return new($"CI_TFS_HTTP_{(int)status}", stage, hint, sent && ((int)status >= 500 || (int)status is >= 300 and < 400 || status == HttpStatusCode.RequestTimeout), status, type);
    }
}
