using System.Text.Json;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class TfsController(ITfsService tfs, ILogger<TfsController> logger) : Controller
{
    [HttpGet]
    public async Task<IActionResult> BuildDetails(int id)
    {
        var model = new TfsBuildPageViewModel { BuildId = id };
        if (!ModelState.IsValid || id <= 0)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            model.Error = "Select a valid build from the pipeline history.";
            return View(model);
        }
        try { model.Build = await tfs.GetBuildAsync(id); }
        catch (Exception ex) when (IsTfsFailure(ex))
        {
            model.Error = ReportTfsFailure(ex, "Unable to load the build.");
            return View(model);
        }
        try { model.Logs = await tfs.GetBuildLogsAsync(id); }
        catch (Exception ex) when (IsTfsFailure(ex)) { model.LogsError = ReportTfsFailure(ex, "Build logs are unavailable."); }
        try { model.Artifacts = await tfs.GetBuildArtifactsAsync(id); }
        catch (Exception ex) when (IsTfsFailure(ex)) { model.ArtifactsError = ReportTfsFailure(ex, "Build artifacts are unavailable."); }
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> BuildLog(int id, int logId, int startLine = 0)
    {
        var model = new TfsLogPageViewModel { BuildId = id, LogId = logId, StartLine = startLine };
        if (!ModelState.IsValid || id <= 0 || logId <= 0 || startLine < 0 || startLine > int.MaxValue - TfsLogPageViewModel.PageSize)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            model.Error = "Select a valid build log and line range.";
            return View(model);
        }
        try
        {
            model.BuildNumber = (await tfs.GetBuildAsync(id)).BuildNumber;
            var log = (await tfs.GetBuildLogsAsync(id)).FirstOrDefault(x => x.Id == logId);
            if (log is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                model.Error = "This log was not found for the selected build.";
                return View(model);
            }
            model.LineCount = log.LineCount;
            model.Content = await tfs.GetBuildLogAsync(id, logId, startLine);
        }
        catch (Exception ex) when (IsTfsFailure(ex)) { model.Error = ReportTfsFailure(ex, "Unable to load this log range."); }
        return View(model);
    }

    [HttpGet]
    public Task<IActionResult> RunBuild(int id) => RunForm(new TfsRunBuildViewModel { DefinitionId = id });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewBuild([Bind("DefinitionId,SourceVersion")] TfsRunBuildViewModel form)
    {
        if (!ModelState.IsValid) return await RunForm(form);
        try { return View("ReviewBuild", await tfs.ReviewBuildAsync(form.DefinitionId, form.SourceVersion)); }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "TFS build review rejected for definition {DefinitionId}.", form.DefinitionId);
            ModelState.AddModelError("", ex.Message);
            return await RunForm(form);
        }
        catch (Exception ex) when (IsTfsFailure(ex))
        {
            form.Error = ReportTfsFailure(ex, "Unable to prepare the build review.");
            return View("RunBuild", form);
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewExistingBuild(int id)
    {
        if (!ModelState.IsValid || id <= 0)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("RunBuild", new TfsRunBuildViewModel { Error = "Select a valid build from the pipeline history." });
        }
        try { return View("ReviewBuild", await tfs.ReviewExistingBuildAsync(id)); }
        catch (ArgumentException ex)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("RunBuild", new TfsRunBuildViewModel { Error = ex.Message });
        }
        catch (Exception ex) when (IsTfsFailure(ex))
        {
            return View("RunBuild", new TfsRunBuildViewModel { Error = ReportTfsFailure(ex, "Unable to review the selected build.") });
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewCiBuild(int id)
    {
        if (!ModelState.IsValid || id <= 0)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("ReviewCiBuild", new TfsRunBuildViewModel { Error = "Select a valid pipeline." });
        }
        try { return View("ReviewCiBuild", await tfs.ReviewLatestCiBuildAsync(id)); }
        catch (ArgumentException ex)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("ReviewCiBuild", new TfsRunBuildViewModel { Error = ex.Message });
        }
        catch (Exception ex) when (IsTfsFailure(ex))
        {
            return View("ReviewCiBuild", new TfsRunBuildViewModel { Error = ReportTfsFailure(ex, "Unable to prepare the CI build review.") });
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmBuild(string confirmationToken)
    {
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(confirmationToken))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("RunBuild", new TfsRunBuildViewModel { Error = "A valid build review is required. Return to the pipeline and review again." });
        }
        try
        {
            var build = await tfs.QueueBuildAsync(confirmationToken);
            return RedirectToAction(nameof(BuildDetails), new { id = build.Id });
        }
        catch (TfsBuildRequestException ex)
        {
            logger.LogError(ex, "TFS confirmation failed for definition {DefinitionId}, stage {Stage}, HTTP {StatusCode}, type {ErrorType}, code {ErrorCode}.",
                ex.DefinitionId, ex.Stage, ex.StatusCode, ex.ErrorType, ex.ErrorCode);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            var status = ex.StatusCode is { } code ? $"HTTP {(int)code}" : "no HTTP response";
            var reason = ex.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "TFS denied access. Check the portal process Windows account's Queue builds permission.",
                System.Net.HttpStatusCode.BadRequest => "TFS rejected the build request. Check the changeset, source mapping and pipeline requirements.",
                System.Net.HttpStatusCode.NotFound => "TFS could not find the requested definition or endpoint.",
                _ => "The build request failed. Use the diagnostic details below when checking the portal logs."
            };
            var diagnostics = $" Stage: {ex.Stage}; {status}.";
            if (ex.ErrorType is not null) diagnostics += $" TFS error type: {ex.ErrorType}.";
            if (ex.ErrorCode is not null) diagnostics += $" TFS error code: {ex.ErrorCode}.";
            return View("RunBuild", new TfsRunBuildViewModel
            {
                DefinitionId = ex.DefinitionId,
                Error = reason + diagnostics + (ex.MayHaveQueued
                    ? " Check the pipeline's latest builds before trying again; TFS may already have accepted the request."
                    : ex.Stage == "definition validation" ? " No build POST was sent." : " TFS did not accept the build request.")
                    + " Return to the pipeline and create a new review; this confirmation cannot be reused."
            });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "TFS build confirmation rejected.");
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("RunBuild", new TfsRunBuildViewModel { Error = ex.Message });
        }
        catch (Exception ex) when (IsTfsFailure(ex))
        {
            var message = ReportTfsFailure(ex, "The build request could not be confirmed.");
            return View("RunBuild", new TfsRunBuildViewModel
            {
                Error = message + " Check the pipeline's latest builds before trying again: TFS may have accepted the request before the connection failed. This confirmation cannot be reused."
            });
        }
    }

    private async Task<IActionResult> RunForm(TfsRunBuildViewModel form)
    {
        if (form.DefinitionId <= 0 || (ModelState.TryGetValue("id", out var idState) && idState.Errors.Count > 0))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            form.Error = "Select a valid pipeline before running a build.";
            return View("RunBuild", form);
        }
        try { form.Definition = await tfs.GetBuildDefinitionAsync(form.DefinitionId); }
        catch (Exception ex) when (IsTfsFailure(ex)) { form.Error = ReportTfsFailure(ex, "Unable to load the pipeline."); }
        return View("RunBuild", form);
    }

    private static bool IsTfsFailure(Exception ex) => ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException;

    private string ReportTfsFailure(Exception ex, string message)
    {
        logger.LogError(ex, "{TfsOperationError}", message);
        Response.StatusCode = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
            ? StatusCodes.Status404NotFound : StatusCodes.Status502BadGateway;
        return ex switch
        {
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => message + " The requested TFS resource was not found.",
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => message + " TFS denied access to the portal process Windows account.",
            _ => message + " Check TFS availability and try again."
        };
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var model = new TfsPipelineDetailsViewModel();
        if (!ModelState.IsValid || id <= 0)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            model.Error = "Select a valid pipeline from the pipelines list.";
            return View(model);
        }

        try
        {
            model.Definition = await tfs.GetBuildDefinitionAsync(id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogError(ex, "Unable to load TFS build definition {DefinitionId}.", id);
            var notFound = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound };
            Response.StatusCode = notFound ? StatusCodes.Status404NotFound : StatusCodes.Status502BadGateway;
            model.Error = notFound ? "This pipeline was not found. It may have been removed or may no longer be accessible."
                : "Unable to load pipeline details. Check the TFS connection and the portal process Windows account's project access.";
            return View(model);
        }

        try
        {
            model.Builds = await tfs.GetLatestBuildsAsync(id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogError(ex, "Unable to load TFS build history for definition {DefinitionId}.", id);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            model.BuildsError = "Pipeline details were loaded, but build history is unavailable. Please try again later.";
        }
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? search = null)
    {
        search = search?.Trim();
        ViewData["Search"] = search;
        try
        {
            return View(await tfs.GetBuildDefinitionsAsync(search));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogError(ex, "Unable to load TFS build definitions.");
            Response.StatusCode = StatusCodes.Status502BadGateway;
            ViewData["TfsError"] = "Unable to load TFS pipelines. Check the TFS connection settings and ensure the portal process Windows account has access to the project.";
            return View(Array.Empty<TfsPipelineViewModel>());
        }
    }
}
