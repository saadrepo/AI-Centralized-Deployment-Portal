using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeploymentPortal.Controllers;

public class SitesController(IDeploymentService deployments, IServerService servers, SiteDeletionService deletion, ApplicationDbContext db) : Controller
{
    private Task<bool> Busy(Deployment d) => db.Deployments.AnyAsync(x => x.ServerId == d.ServerId && (x.Status == DeploymentStatus.Queued || x.Status == DeploymentStatus.Running));

    public async Task<IActionResult> Index(int? serverId)
    {
        ViewBag.Servers = await servers.ListAsync();
        ViewBag.ServerId = serverId;
        IReadOnlyList<IisSite> sites = [];
        if (serverId.HasValue)
        {
            var server = await servers.FindAsync(serverId.Value);
            if (server is null) return NotFound();
            try { sites = await deletion.ListAsync(server); }
            catch (InvalidOperationException ex) { ViewData["InventoryError"] = ex.Message; }
        }
        return View(sites);
    }

    public async Task<IActionResult> Delete(Guid? id, int? serverId, string? siteName)
    {
        // Historical deployment links open live inventory rather than assuming the old site still exists.
        if (id.HasValue)
        {
            var previous = await deployments.FindAsync(id.Value);
            if (previous is null) return NotFound();
            TempData["Success"] = "Select the current IIS site below. Deployment history may refer to a site that was removed or renamed.";
            return RedirectToAction(nameof(Index), new { serverId = previous.ServerId });
        }
        var d = await Destination(serverId, siteName);
        if (d is null) return NotFound();
        try
        {
            if (await Busy(d)) throw new InvalidOperationException("Wait for this server's pending deployments to finish before deleting a site.");
            var preview = await deletion.PreviewAsync(d);
            ViewData["Token"] = preview.Token;
            ViewData["Message"] = preview.Message;
            return View(d);
        }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; return RedirectToAction(nameof(Index), new { serverId }); }
    }

    private async Task<Deployment?> Destination(int? serverId, string? siteName)
    {
        if (!serverId.HasValue || string.IsNullOrWhiteSpace(siteName)) return null;
        var server = await servers.FindAsync(serverId.Value);
        return server is null ? null : new Deployment { ServerId = server.Id, Server = server, SiteName = siteName };
    }

    [HttpPost]
    public async Task<IActionResult> ConfirmDelete(int serverId, string? siteName, string? token, string? confirmedName)
    {
        var d = await Destination(serverId, siteName);
        if (d is null) return NotFound();
        try
        {
            if (await Busy(d)) throw new InvalidOperationException("Wait for this server's pending deployments to finish before deleting a site.");
            var result = await deletion.DeleteAsync(d, token, confirmedName);
            TempData[result.Success ? "Success" : "Error"] = result.Message;
        }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index), new { serverId });
    }
}
