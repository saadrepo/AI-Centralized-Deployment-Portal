using System.Text.Json;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class TfsGitBuildsController(ITfsGitBuildService builds, ILogger<TfsGitBuildsController> logger) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var model = new TfsGitCiBuildsPageViewModel();
        try { model.Builds = await builds.GetRecentBuildsAsync(); }
        catch (Exception ex) when (IsApiFailure(ex))
        {
            model.Error = ReportFailure(ex, "Unable to load CI builds. Please try again later.");
        }
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Artifacts(int id)
    {
        var model = new TfsGitCiArtifactsPageViewModel();
        if (!ModelState.IsValid || id <= 0)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            model.Error = "Select a valid CI build.";
            return View(model);
        }
        try { model.Build = await builds.GetBuildAsync(id); }
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
        try { model.Artifacts = await builds.GetArtifactsAsync(id); }
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
