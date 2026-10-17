using System.Text.Json;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PipelineController(PipelineService pipeline, ILogger<PipelineController> logger) : Controller
{
    [HttpGet]
    public IActionResult Index() => User.Identity?.IsAuthenticated == true ? View() : StatusCode(401, "Sign in using your Windows account to open CI/CD Pipeline.");

    [HttpGet]
    public IActionResult Applications() => User.Identity?.IsAuthenticated == true
        ? Json(pipeline.Applications()) : StatusCode(401, new { error = "Sign in using your Windows account to read TFS pipeline data." });

    [HttpGet]
    public Task<IActionResult> Options(string applicationKey, CancellationToken cancellationToken) =>
        Execute(() => pipeline.Options(applicationKey, User, cancellationToken));

    [HttpGet]
    public Task<IActionResult> Status(string applicationKey, int buildId, int releaseDefinitionId, int environmentId, CancellationToken cancellationToken) =>
        Execute(async () => (object)await pipeline.Status(applicationKey, buildId, releaseDefinitionId, environmentId, User, cancellationToken));

    [HttpGet]
    public Task<IActionResult> History(string applicationKey, int buildId, CancellationToken cancellationToken) =>
        Execute(async () => (object)await pipeline.Status(applicationKey, buildId, 0, 0, User, cancellationToken, historyOnly: true));

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Queue(string applicationKey, string branch, CancellationToken cancellationToken, int selectedBuildId = 0) =>
        Execute(async () => (object)new { buildId = await pipeline.Queue(applicationKey, branch, User, cancellationToken, selectedBuildId) }, true);

    [HttpGet]
    public Task<IActionResult> QueueReadiness(string applicationKey, CancellationToken cancellationToken) =>
        Execute(() => pipeline.QueueReadiness(applicationKey, User, cancellationToken));

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Cancel(string applicationKey, int buildId, CancellationToken cancellationToken) =>
        Execute(() => pipeline.CancelBuild(applicationKey, buildId, User, cancellationToken), true);

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Approve(string applicationKey, int buildId, int releaseDefinitionId, int environmentId, int releaseId, int approvalId, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            await pipeline.Approve(applicationKey, buildId, releaseDefinitionId, environmentId, releaseId, approvalId, User, cancellationToken);
            return (object)new { message = "TFS confirmed the approval. Deployment follows the configured TFS process." };
        }, true);

    [HttpGet]
    public Task<IActionResult> PendingReleases(string applicationKey, int releaseDefinitionId, int environmentId, CancellationToken cancellationToken) =>
        Execute(() => pipeline.PendingReleases(applicationKey, releaseDefinitionId, environmentId, User, cancellationToken));

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Reject(string applicationKey, int buildId, int releaseDefinitionId, int environmentId, int releaseId, int approvalId, CancellationToken cancellationToken) =>
        Execute(async () => {
            await pipeline.Reject(applicationKey, buildId, releaseDefinitionId, environmentId, releaseId, approvalId, User, cancellationToken);
            return (object)new { message = "TFS confirmed rejection. This approval cannot start deployment." };
        }, true);

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> CancelDeployment(string applicationKey, int buildId, int releaseDefinitionId, int environmentId, int releaseId,
        int releaseEnvironmentId, int deploymentAttemptId, int deploymentAttempt, CancellationToken cancellationToken) =>
        Execute(async () => {
            await pipeline.CancelDeployment(applicationKey, buildId, releaseDefinitionId, environmentId, releaseId,
                releaseEnvironmentId, deploymentAttemptId, deploymentAttempt, User, cancellationToken);
            return (object)new { message = "TFS confirmed the deployment cancellation request. Refreshing the exact release attempt." };
        }, true);

    private async Task<IActionResult> Execute(Func<Task<object>> action, bool mutation = false)
    {
        if (User.Identity?.IsAuthenticated != true) return StatusCode(401, new { error = "Sign in using your Windows account to access TFS." });
        if (!ModelState.IsValid) return BadRequest(new { error = "Invalid pipeline selection." });
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try { return Json(await action()); }
        catch (PipelineApprovalException ex)
        {
            logger.LogWarning("CD approval failed: Stage={Stage}, HttpStatus={HttpStatus}, Type={Type}, Code={Code}, Reference={Reference}.", ex.Stage, ex.HttpStatus, ex.UpstreamType, ex.TfsCode, HttpContext.TraceIdentifier);
            return StatusCode(ex.HttpStatus is 401 or 403 ? 403 : 502, new { error = ex.Message + " Reference: " + HttpContext.TraceIdentifier,
                stage = ex.Stage, upstreamStatus = ex.HttpStatus, upstreamType = ex.UpstreamType, mayHaveReachedTfs = ex.MayHaveReachedTfs });
        }
        catch (PipelineCancelException ex)
        {
            logger.LogWarning("CI cancel failed: Code={Code}, MayHaveReachedTfs={MayHaveReachedTfs}, Reference={Reference}.", ex.Code, ex.MayHaveReachedTfs, HttpContext.TraceIdentifier);
            return StatusCode(ex.Code.Contains("DENIED") || ex.Code.Contains("IDENTITY_REQUIRED") ? 403 : 409,
                new { error = ex.Message, code = ex.Code, mayHaveReachedTfs = ex.MayHaveReachedTfs, reference = HttpContext.TraceIdentifier });
        }
        catch (PipelineDeploymentCancellationException ex)
        {
            logger.LogWarning("CD deployment cancellation failed: HttpStatus={HttpStatus}, MayHaveReachedTfs={MayHaveReachedTfs}, PortalSid={PortalSid}, Reference={Reference}.",
                ex.HttpStatus, ex.MayHaveReachedTfs, PipelinePersonalSessions.Sid(User), HttpContext.TraceIdentifier);
            return StatusCode(ex.HttpStatus is 401 or 403 ? 403 : 409,
                new { error = ex.Message, mayHaveReachedTfs = ex.MayHaveReachedTfs, reference = HttpContext.TraceIdentifier });
        }
        catch (PipelineQueueException ex)
        {
            var reference = HttpContext.TraceIdentifier;
            logger.LogWarning("CI queue failed: Reference={Reference}, Code={Code}, Stage={Stage}, HttpStatus={HttpStatus}, UpstreamType={UpstreamType}, MayHaveQueued={MayHaveQueued}, User={User}.",
                reference, ex.Code, ex.Stage, (int?)ex.UpstreamStatus, ex.UpstreamType, ex.MayHaveQueued, User.Identity?.Name);
            return StatusCode(ex.UpstreamStatus is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? 403 : 502,
                new { error = $"{ex.UserMessage} Stage: {ex.Stage}. Code: {ex.Code}. Reference: {reference}.", code = ex.Code, stage = ex.Stage,
                    upstreamStatus = (int?)ex.UpstreamStatus, upstreamType = ex.UpstreamType, mayHaveQueued = ex.MayHaveQueued, reference });
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Pipeline authorization denied: PortalSid={PortalSid}, User={User}, Reference={Reference}.",
                PipelinePersonalSessions.Sid(User), User.Identity?.Name, HttpContext.TraceIdentifier);
            return StatusCode(403, new { error = ex.Message.StartsWith("CD approval blocked:", StringComparison.Ordinal)
            ? ex.Message : "Action unavailable: verify execution settings, Windows identity and TFS approval permissions." }); }
        catch (ArgumentException)
        { return BadRequest(new { error = "Selection is invalid or has changed. Refresh the application, build and environment." }); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or IOException)
        {
            logger.LogWarning("Pipeline {Operation} failed for {User}: {FailureType}.", mutation ? "action" : "read", User.Identity?.Name, ex.GetType().Name);
            var forbidden = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized };
            return StatusCode(forbidden ? 403 : 502, new { error = forbidden ? "TFS denied access for this identity." : mutation
                ? "TFS action outcome is unconfirmed. Refresh recent builds/status before retrying; the request may have reached TFS."
                : "TFS is disconnected, configuration is unavailable, or the response could not be verified. Last displayed data may be stale." });
        }
        finally
        {
            var milliseconds = elapsed.Elapsed.TotalMilliseconds;
            Response.Headers["Server-Timing"] = "pipeline;dur=" + milliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            logger.LogInformation("Pipeline request timing: Action={Action}, ElapsedMs={ElapsedMs}, Reference={Reference}.",
                HttpContext.Request.Path.Value, elapsed.ElapsedMilliseconds, HttpContext.TraceIdentifier);
        }
    }
}
