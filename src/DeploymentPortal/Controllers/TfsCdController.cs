using System.Text.Json;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

public sealed record CdPreparePage(int BuildId, string ApplicationKey, CdSelection? Selection,
    IReadOnlyList<(string Host, string Name, string Site)> Targets, bool Enabled, string? Error);

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TfsCdController(ITfsCdPreparation preparation, ISiteRegistry registry, IServerService servers, ILogger<TfsCdController> logger, ICdDeploymentFlow? flow = null, Microsoft.Extensions.Options.IOptions<TfsCdOptions>? cdOptions = null) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Prepare(int id, string applicationKey = "Portal", string? serverHost = null, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid || id <= 0) return BadRequest("Select a valid successful Dev build.");
        var app = registry.Find(applicationKey);
        if (app is null) return NotFound();
        var inventory = await servers.ListAsync();
        var targets = app.Destinations.SelectMany(d => inventory.Where(d.Matches).Select(s => (s.HostNameOrIp, s.DisplayName, d.SiteName))).ToList();
        try
        {
            var selection = serverHost is null ? null : await preparation.SelectAsync(app.Key, id, serverHost, cancellationToken);
            return View(new CdPreparePage(id, app.Key, selection, targets, preparation.Enabled, null));
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            return View(new CdPreparePage(id, app.Key, null, targets, preparation.Enabled, SafeFailure(ex)));
        }
    }

    [HttpPost]
    public async Task<IActionResult> Stage(int buildId, string applicationKey, string serverHost, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || buildId <= 0) return BadRequest("Select a valid build and registered target.");
        try
        {
            var staged = await preparation.StageAsync(applicationKey, buildId, serverHost, cancellationToken);
            return RedirectToAction(nameof(Details), new { id = staged.Id });
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            Response.StatusCode = ex is ArgumentException ? 400 : 502;
            return View("Error", SafeFailure(ex));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var local = await preparation.FindAsync(id, cancellationToken);
            ViewBag.RemoteEnabled = cdOptions?.Value is { Enabled: true, ExecutionEnabled: true };
            if (flow is not null)
            {
                try { ViewBag.CdDraft = await flow.DraftAsync(id); }
                catch (CdPreparationException) { /* Local preparation remains inspectable while remote staging is disabled or incomplete. */ }
            }
            return View(local);
        }
        catch (Exception ex) when (IsFailure(ex)) { Response.StatusCode = 400; return View("Error", SafeFailure(ex)); }
    }

    [HttpPost]
    public async Task<IActionResult> StageRemote(Guid id)
    {
        if (!ModelState.IsValid || id == Guid.Empty) return BadRequest();
        try
        {
            if (flow is null) throw new CdPreparationException("CD integration is disabled.");
            await flow.StageRemoteAsync(id);
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (Exception ex) when (IsFailure(ex)) { Response.StatusCode = 400; return View("Error", SafeFailure(ex)); }
    }

    private static bool IsFailure(Exception ex) => ex is ArgumentException or InvalidOperationException or HttpRequestException or JsonException or IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.DataAnnotations.ValidationException;
    private string SafeFailure(Exception ex)
    {
        logger.LogWarning("CD preparation failed ({FailureType}).", ex.GetType().Name);
        // No raw path, URL, upstream response, configuration value or credential is rendered.
        return ex is CdPreparationException ? ex.Message : "Artifact preparation could not complete safely. Verify TFS access, private staging permissions, package limits and provenance. No deployment was queued.";
    }
}
