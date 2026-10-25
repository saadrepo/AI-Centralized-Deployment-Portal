using DeploymentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

// Cloud browsing and explicit, default-off GitHub operations. No automatic deployment.
[Route("Pipeline/Cloud")]
[Authorize(Policy = SourcesManagementAuthorization.Policy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PipelineCloudController(ICloudBuildExplorer explorer, ISiteRegistry registry,
    ILogger<PipelineCloudController> logger, CloudExecutionFlow? execution = null) : Controller
{
    [HttpGet("Readiness")]
    public Task<IActionResult> Readiness([FromQuery] CloudSelection selection) => Execute(() => execution!.ReadinessAsync(selection, User));

    [HttpGet("Operation")]
    public Task<IActionResult> Operation(Guid id, CancellationToken ct) => Execute(() => execution!.StatusAsync(id, User, ct));

    [HttpPost("Queue"), ValidateAntiForgeryToken]
    public Task<IActionResult> Queue(string ticket, CancellationToken ct) => Execute(() => execution!.QueueAsync(ticket, User, ct), true);

    [HttpPost("Prepare"), ValidateAntiForgeryToken]
    public Task<IActionResult> Prepare(string ticket, CancellationToken ct) => Execute(() => execution!.PrepareAsync(ticket, User, ct), true);

    [HttpPost("Approve"), ValidateAntiForgeryToken]
    public Task<IActionResult> Approve(string token, CancellationToken ct) => Execute(() => execution!.ApproveAsync(token, User, ct), true);

    private async Task<IActionResult> Execute(Func<Task<object>> action, bool mutation = false)
    {
        if (!ModelState.IsValid) { Denied("InvalidSelection"); return BadRequest(new { error = "Invalid cloud selection." }); }
        if (execution is null) return mutation ? StatusCode(503, new { error = "Cloud execution unavailable." }) : Json(new { canQueue = false, canApprove = false });
        if (mutation && !Request.IsHttps) { Denied("HttpsRequired"); return StatusCode(403, new { error = "HTTPS is required for cloud execution." }); }
        try { return Json(await action()); }
        catch (UnauthorizedAccessException) { Denied("Authorization"); return StatusCode(403, new { error = "The current Windows account is not authorized for this mapping and target." }); }
        catch (CloudExecutionException ex) { Denied(ex.Code); return StatusCode(409, new { error = ex.Message, code = ex.Code }); }
        catch (Exception ex) {
            logger.LogWarning("Cloud execution request unconfirmed: Type={FailureType}, Reference={Reference}.", ex.GetType().Name, HttpContext.TraceIdentifier);
            return StatusCode(502, new { error = "Cloud operation unconfirmed. Refresh Status and inspect its recorded outcome before retrying." });
        }
    }
    private void Denied(string code) => logger.LogWarning("Cloud request denied. Code={Code}, Reference={Reference}.", code, HttpContext.TraceIdentifier);
    [HttpGet("Applications")]
    public IActionResult Applications() => Json(registry.List().Select(a => new ExplorerItem(a.Key, a.DisplayName)));

    [HttpGet("Browse")]
    public async Task<IActionResult> Browse([FromQuery] CloudExplorerQuery query, string applicationKey, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || registry.Find(applicationKey) is null ||
            query.Provider is not ("GitHub" or "AzureDevOps") ||
            query.Operation is not ("sources" or "projects" or "repositories" or "branches" or "definitions" or "builds" or "status" or "artifacts") ||
            (query.Operation is "builds" or "status" or "artifacts") && query.DefinitionId <= 0)
            return BadRequest(new { error = "Select a registered application and valid provider resources." });
        try {
            var page = await explorer.BrowseAsync(query, cancellationToken);
            logger.LogInformation("Cloud pipeline read: Provider={Provider}, Operation={Operation}, SourceId={SourceId}, Reference={Reference}.",
                query.Provider, query.Operation, query.SourceId, HttpContext.TraceIdentifier);
            return Json(page);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SourceExplorerException) { return BadRequest(new { error = "Selection is unavailable or does not belong to the selected source, repository, branch and pipeline." }); }
        catch (Exception ex) {
            logger.LogWarning("Cloud pipeline read failed: Operation={Operation}, Type={FailureType}, Reference={Reference}.",
                query.Operation, ex.GetType().Name, HttpContext.TraceIdentifier);
            return StatusCode(502, new { error = "Provider status unavailable. Check source permissions and refresh. Displayed data may be stale." });
        }
    }
}
