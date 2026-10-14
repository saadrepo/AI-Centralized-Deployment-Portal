using System.Text.Json;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class TfsGitBuildsController(ITfsGitBuildService builds, ILogger<TfsGitBuildsController> logger, ISiteRegistry? registry = null) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string applicationKey = "Portal")
    {
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
