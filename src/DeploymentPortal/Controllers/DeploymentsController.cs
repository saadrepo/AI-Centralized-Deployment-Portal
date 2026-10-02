using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;
public class DeploymentsController(IDeploymentService deployments, IServerService servers) : Controller
{
    public async Task<IActionResult> Index() => View(await deployments.ListAsync());
    public async Task<IActionResult> Create(int? serverId)
    {
        var server = serverId.HasValue ? await servers.FindAsync(serverId.Value) : null;
        return await Form(new() { ServerId = server?.Id ?? 0, Environment = server?.Environment ?? "Development" });
    }
    private async Task<IActionResult> Form(DeploymentForm form) { ViewBag.Servers = await servers.ListAsync(); return View("Create", form); }
    [HttpPost]
    public async Task<IActionResult> Review(DeploymentForm form)
    {
        if (!ModelState.IsValid) return await Form(form);
        try
        {
            form.ConfirmationToken = await deployments.ConfirmAsync(form);
            return View("Confirm", new ConfirmDeploymentViewModel(form, await deployments.ValidateAsync(form)));
        }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return await Form(form); }
    }
    [HttpPost]
    public async Task<IActionResult> Create(DeploymentForm form)
    {
        if (!ModelState.IsValid) return await Form(form);
        try { var id = await deployments.QueueAsync(form); return RedirectToAction(nameof(Details), new { id }); }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return await Form(form); }
    }
    public async Task<IActionResult> Details(Guid id) => await deployments.FindAsync(id) is { } deployment ? View(deployment) : NotFound();
}
