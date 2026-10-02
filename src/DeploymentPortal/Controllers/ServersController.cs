using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeploymentPortal.Controllers;
public class ServersController(IServerService servers, IWinRmService winRm) : Controller
{
    public async Task<IActionResult> Index() => View(await servers.ListAsync());
    public IActionResult Create() => View("Form", new ServerForm());
    public async Task<IActionResult> Edit(int id) => await servers.FindAsync(id) is { } server ? View("Form", ServerForm.From(server)) : NotFound();
    public async Task<IActionResult> Details(int id) => await servers.FindAsync(id) is { } server ? View(server) : NotFound();
    [HttpPost]
    public Task<IActionResult> Create(ServerForm form) { form.Id = 0; return Save(form); }
    [HttpPost]
    public Task<IActionResult> Edit(int id, ServerForm form) => id != form.Id ? Task.FromResult<IActionResult>(BadRequest()) : Save(form);
    private async Task<IActionResult> Save(ServerForm form)
    {
        if (!ModelState.IsValid) return View("Form", form);
        try { var id = await servers.SaveAsync(form); TempData["Success"] = "Server saved."; return RedirectToAction(nameof(Details), new { id }); }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); }
        catch (DbUpdateException) { ModelState.AddModelError("", "The server could not be saved. Check for a duplicate host and port, then retry."); }
        return View("Form", form);
    }
    [HttpPost]
    public async Task<IActionResult> TestConnection(int id)
    {
        var result = await servers.TestAsync(id);
        if (result is null) return NotFound();
        TempData[result.Success ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Details), new { id });
    }
    [HttpPost]
    public async Task<IActionResult> TestDraft(ServerForm form)
    {
        if (ModelState.IsValid)
        {
            var result = await winRm.TestConnectionAsync(form.ToServer());
            ViewData[result.Success ? "ConnectionSuccess" : "ConnectionError"] = result.Message;
        }
        return View("Form", form);
    }
    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        try { if (!await servers.DeleteAsync(id)) return NotFound(); TempData["Success"] = "Server removed."; }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; return RedirectToAction(nameof(Details), new { id }); }
        catch (DbUpdateException) { TempData["Error"] = "This server has deployment history and cannot be deleted."; return RedirectToAction(nameof(Details), new { id }); }
        return RedirectToAction(nameof(Index));
    }
}
