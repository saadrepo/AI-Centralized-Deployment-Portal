using System.Text.Json;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class TfsReleasesController(ITfsReleaseService tfs, ILogger<TfsReleasesController> logger) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? search = null)
    {
        ViewData["Search"] = search?.Trim();
        var model = new TfsReleasePageViewModel();
        try { model.Definitions = await tfs.GetDefinitionsAsync(search?.Trim()); }
        catch (Exception ex) when (IsTfsFailure(ex)) { model.Error = Report(ex, "Unable to load release definitions."); }
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Definition(int id)
    {
        var model = new TfsReleasePageViewModel();
        if (!ValidId(id, model)) return View(model);
        try { model.Definition = await tfs.GetDefinitionAsync(id); }
        catch (Exception ex) when (IsTfsFailure(ex))
        {
            model.Error = Report(ex, "Unable to load the release definition.");
            return View(model);
        }
        try { model.Releases = await tfs.GetReleasesAsync(id); }
        catch (Exception ex) when (IsTfsFailure(ex)) { model.ReleasesError = Report(ex, "Recent releases are unavailable."); }
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var model = new TfsReleasePageViewModel();
        if (!ValidId(id, model)) return View(model);
        try { model.Release = await tfs.GetReleaseDetailsAsync(id); }
        catch (Exception ex) when (IsTfsFailure(ex)) { model.Error = Report(ex, "Unable to load release details."); }
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Deploy(int id)
    {
        var model = new TfsReleasePageViewModel();
        if (!ValidId(id, model)) return View("Definition", model);
        try
        {
            var release = await tfs.GetLatestDeployableReleaseAsync(id);
            return RedirectToAction(nameof(Details), new { id = release.Id });
        }
        catch (ArgumentException ex)
        {
            model.Error = ex.Message;
            try { model.Definition = await tfs.GetDefinitionAsync(id); }
            catch (Exception inner) when (IsTfsFailure(inner)) { model.Error = Report(inner, "Unable to load the release definition."); }
            return View("Definition", model);
        }
        catch (Exception ex) when (IsTfsFailure(ex))
        {
            logger.LogError(ex, "CD deploy resolve controller failure for definition {DefinitionId}. Exception: {ExceptionType}: {ExceptionMessage}",
                id, ex.GetType().FullName, ex.Message);
            model.Error = Report(ex, "Unable to resolve the latest deployable release.");
            return View("Definition", model);
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewDeploy(int releaseId, int environmentId)
    {
        if (!ModelState.IsValid || releaseId <= 0 || environmentId <= 0)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("Details", new TfsReleasePageViewModel { Error = "Select a valid release environment before deploying." });
        }
        try
        {
            return View("ReviewDeploy", await tfs.ReviewDeployAsync(releaseId, environmentId));
        }
        catch (ArgumentException ex)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            logger.LogWarning(ex, "TFS release deploy review rejected for release {ReleaseId}, environment {EnvironmentId}.", releaseId, environmentId);
            return View("Details", new TfsReleasePageViewModel { Release = await TryLoadRelease(releaseId), Error = ex.Message });
        }
        catch (Exception ex) when (IsTfsFailure(ex))
        {
            return View("Details", new TfsReleasePageViewModel
            {
                Release = await TryLoadRelease(releaseId),
                Error = Report(ex, "Unable to prepare the deployment review.")
            });
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmDeploy(string confirmationToken)
    {
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(confirmationToken))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("Details", new TfsReleasePageViewModel { Error = "A valid deployment review is required. Return to the release and review again." });
        }
        try
        {
            var release = await tfs.DeployEnvironmentAsync(confirmationToken);
            return RedirectToAction(nameof(Details), new { id = release.Id });
        }
        catch (TfsReleaseRequestException ex)
        {
            logger.LogError(ex, "TFS deploy confirmation failed for release {ReleaseId}, environment {EnvironmentId}, stage {Stage}, HTTP {StatusCode}.",
                ex.ReleaseId, ex.EnvironmentId, ex.Stage, ex.StatusCode);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            var status = ex.StatusCode is { } code ? $"HTTP {(int)code}" : "no HTTP response";
            var reason = ex.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "TFS denied access. Check the portal process Windows account's release deployment permissions.",
                System.Net.HttpStatusCode.BadRequest => "TFS rejected the deployment request. Refresh the release and review the current environment state.",
                System.Net.HttpStatusCode.NotFound => "TFS could not find the requested release environment.",
                _ => "The deployment request failed. Use the diagnostic details below when checking the portal logs."
            };
            return View("Details", new TfsReleasePageViewModel
            {
                Release = await TryLoadRelease(ex.ReleaseId),
                Error = $"{reason} Stage: {ex.Stage}; {status}. "
                    + (ex.MayHaveStarted
                        ? "Refresh the release details before trying again; TFS may already have accepted the deployment request."
                        : "No deployment PATCH was sent.")
                    + " This confirmation cannot be reused."
            });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "TFS deploy confirmation rejected.");
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("Details", new TfsReleasePageViewModel { Error = ex.Message });
        }
        catch (Exception ex) when (IsTfsFailure(ex))
        {
            var message = Report(ex, "The deployment request could not be confirmed.");
            return View("Details", new TfsReleasePageViewModel
            {
                Error = message + " Refresh the release details before trying again; TFS may have accepted the request before the connection failed. This confirmation cannot be reused."
            });
        }
    }

    private async Task<TfsReleaseViewModel?> TryLoadRelease(int releaseId)
    {
        try { return releaseId > 0 ? await tfs.GetReleaseDetailsAsync(releaseId) : null; }
        catch (Exception ex) when (IsTfsFailure(ex))
        {
            logger.LogWarning(ex, "Unable to reload release {ReleaseId} after deployment action failure.", releaseId);
            return null;
        }
    }

    private bool ValidId(int id, TfsReleasePageViewModel model)
    {
        if (ModelState.IsValid && id > 0) return true;
        Response.StatusCode = StatusCodes.Status400BadRequest;
        model.Error = "Select a valid release or definition from CD Releases.";
        return false;
    }

    private static bool IsTfsFailure(Exception ex) => ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException;

    private string Report(Exception ex, string message)
    {
        logger.LogError(ex, "{TfsReleaseError}", message);
        Response.StatusCode = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } ? 404 : 502;
        return message + (ex switch
        {
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => " The requested TFS resource was not found.",
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => " TFS denied access to the portal process Windows account.",
            _ => " Check TFS availability and try again."
        });
    }
}
