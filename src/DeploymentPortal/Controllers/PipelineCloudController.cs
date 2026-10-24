using DeploymentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

// Read-only surface. No cloud queue, approval, download or deployment actions exist.
[Route("Pipeline/Cloud")]
[Authorize(Policy = SourcesManagementAuthorization.Policy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PipelineCloudController(ICloudBuildExplorer explorer, ISiteRegistry registry,
    ILogger<PipelineCloudController> logger) : Controller
{
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
