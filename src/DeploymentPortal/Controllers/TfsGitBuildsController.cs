using System.Text.Json;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace DeploymentPortal.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class TfsGitBuildsController(ITfsGitBuildService builds, ILogger<TfsGitBuildsController> logger, ISiteRegistry? registry = null, ICloudBuildExplorer? cloud = null) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string applicationKey = "Portal", string provider = "Tfs2018")
    {
        if (provider is not ("Tfs2018" or "GitHub" or "AzureDevOps")) return BadRequest();
        if (provider != "Tfs2018") {
            // Only the cloud branch adopts Sources Management authorization. Preserve native TFS behavior.
            var identity = await HttpContext.AuthenticateAsync(SourcesManagementAuthorization.Scheme);
            if (!identity.Succeeded || identity.Principal is null) return Challenge(SourcesManagementAuthorization.Scheme);
            var authorization = HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
            if (!(await authorization.AuthorizeAsync(identity.Principal, null, SourcesManagementAuthorization.Policy)).Succeeded)
                return Forbid(SourcesManagementAuthorization.Scheme);
            return View(new TfsGitCiBuildsPageViewModel { Provider = provider, ApplicationKey = applicationKey });
        }
        var app = registry?.Find(applicationKey);
        if (registry is not null && (app is null || !TfsApplicationScope.IsConfigured(app))) return NotFound();
        if (registry is null && applicationKey != "Portal") return NotFound();
        var model = new TfsGitCiBuildsPageViewModel { ApplicationKey = app?.Key ?? "Portal", Applications = registry?.List().Where(TfsApplicationScope.IsConfigured).ToList() ?? [] };
        try { model.Builds = app is null ? await builds.GetRecentBuildsAsync() : await builds.GetRecentBuildsAsync(app); }
        catch (Exception ex) when (IsApiFailure(ex))
        {
            model.Error = ReportFailure(ex, "Unable to load CI builds. Please try again later.");
        }
        return View(model);
    }

    [HttpGet]
    [Authorize(Policy = SourcesManagementAuthorization.Policy)]
    public async Task<IActionResult> Browse([FromQuery] CloudExplorerQuery query, CancellationToken cancellationToken)
    {
        if (cloud is null) return StatusCode(503, new { error = "Explorer service unavailable. Contact the portal administrator." });
        if (!ModelState.IsValid) return BadRequest(new { error = "Invalid explorer selection." });
        try { return Json(await cloud.BrowseAsync(query, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SourceExplorerException ex) { return StatusCode(502, new { error = ex.Message }); }
        catch (Exception ex) {
            // Never log raw provider payloads, URLs, credentials or transport exception details.
            logger.LogWarning("Cloud build explorer request failed ({FailureType}).", ex.GetType().Name);
            return StatusCode(502, new { error = query.Operation == "sources"
                ? "Source registrations could not be loaded from this portal's database. Retry or contact the administrator."
                : SourceExplorerException.Unavailable });
        }
    }

    [HttpGet]
    public async Task<IActionResult> Artifacts(int id, string applicationKey = "Portal")
    {
        var app = registry?.Find(applicationKey);
        if (registry is not null && (app is null || !TfsApplicationScope.IsConfigured(app))) return NotFound();
        if (registry is null && applicationKey != "Portal") return NotFound();
        var model = new TfsGitCiArtifactsPageViewModel { ApplicationKey = app?.Key ?? "Portal" };
        if (!ModelState.IsValid || id <= 0)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            model.Error = "Select a valid CI build.";
            return View(model);
        }
        try { model.Build = app is null ? await builds.GetBuildAsync(id) : await builds.GetBuildAsync(id, app); }
        catch (Exception ex) when (IsApiFailure(ex))
        {
            model.Error = ReportFailure(ex, "Unable to load the CI build. Please try again later.");
            return View(model);
        }
        if (model.Build is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            model.Error = "This build is not available for the Dev CI pipeline.";
            return View(model);
        }
        try { model.Artifacts = app is null ? await builds.GetArtifactsAsync(id) : await builds.GetArtifactsAsync(id, app); }
        catch (Exception ex) when (IsApiFailure(ex))
        {
            model.ArtifactsError = ReportFailure(ex, "Unable to load artifacts. Please try again later.");
        }
        return View(model);
    }

    private string ReportFailure(Exception ex, string message)
    {
        // Do not include upstream bodies, URLs, credentials, or exception messages in UI or logs.
        logger.LogWarning("TFS Git CI request failed ({FailureType}).", ex.GetType().Name);
        Response.StatusCode = StatusCodes.Status502BadGateway;
        return message;
    }

    private static bool IsApiFailure(Exception ex) =>
        ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException;
}
