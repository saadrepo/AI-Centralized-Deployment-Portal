using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

public class DockerDeploymentsController(IDockerFleetService fleet) : Controller
{
    public async Task<IActionResult> Index(string? context)
    {
        try { return View(await fleet.DashboardAsync(context)); }
        catch (ArgumentException ex) { return View(new DockerDashboardViewModel([], "", [], [], ex.Message)); }
    }

    public async Task<IActionResult> Create(string? context, string? image)
    {
        var form = new DockerDeploymentForm { Context = context ?? "", Image = image ?? "" };
        return await Form(form);
    }

    private async Task<IActionResult> Form(DockerDeploymentForm form)
    {
        try
        {
            var dashboard = await fleet.DashboardAsync(form.Context);
            form.Context = dashboard.Context;
            ViewBag.Dashboard = dashboard;
        }
        catch (ArgumentException ex)
        {
            ViewBag.Dashboard = new DockerDashboardViewModel([], "", [], [], ex.Message);
        }
        return View("Create", form);
    }

    [HttpPost]
    public async Task<IActionResult> Review(DockerDeploymentForm form)
    {
        if (!ModelState.IsValid) return await Form(form);
        try { return View(await fleet.ReviewAsync(form)); }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return await Form(form); }
    }

    [HttpPost]
    public async Task<IActionResult> Deploy(DockerDeploymentForm form)
    {
        if (!ModelState.IsValid) return await Form(form);
        try { ViewBag.Context = form.Context; return View("Result", await fleet.DeployAsync(form)); }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return await Form(form); }
    }

    [HttpPost]
    public async Task<IActionResult> Manage(string context, string id, string operation)
    {
        try { TempData["Success"] = await fleet.ActionAsync(context, id, operation); }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index), new { context });
    }

    [HttpPost]
    public async Task<IActionResult> Rollback(string context, string id)
    {
        try { TempData["Success"] = await fleet.RollbackAsync(context, id); }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index), new { context });
    }

    public async Task<IActionResult> Logs(string context, string id)
    {
        try
        {
            var (container, logs) = await fleet.LogsAsync(context, id);
            ViewBag.Context = context;
            ViewBag.Container = container;
            return View("Logs", logs);
        }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; return RedirectToAction(nameof(Index), new { context }); }
    }
}
