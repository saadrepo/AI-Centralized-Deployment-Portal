using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace DeploymentPortal.Controllers;

[Authorize(Policy = SourcesManagementAuthorization.Policy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SourcesController(IInfrastructureSourceService sources) : Controller
{
    [HttpGet("/Servers/Sources")]
    public async Task<IActionResult> Index()
    {
        try { return View(await sources.ListAsync()); }
        catch (DbUpdateException) { return StatusCode(503, "Sources schema unavailable. Ask an administrator to review and apply the generated migration."); }
        catch (Microsoft.Data.SqlClient.SqlException) { return StatusCode(503, "Sources database unavailable. Verify the reviewed migration and database access."); }
    }
    [HttpGet]
    public IActionResult Create() => Form(new());
    [HttpGet]
    public async Task<IActionResult> Edit(int id) => await sources.FindAsync(id) is { } source ? Form(SourceForm.From(source)) : NotFound();
    private IActionResult Form(SourceForm form)
    {
        // Never bind these metadata collections from posted fields.
        form.Configured = sources.Configured();
        try { form.Credentials = sources.Credentials(); }
        catch (InvalidOperationException) { form.Credentials = []; ViewData["CredentialWarning"] = "Token credentials are unavailable. A host administrator must provision the private credential store. Configured TFS Windows connections remain available."; }
        return View("Form", form);
    }
    [HttpGet]
    public IActionResult ProvisioningCommand(string? provider, string? destination)
    {
        // Only generate instructions. Never invoke a process or accept a secret here.
        if (provider is not ("AzureDevOps" or "GitHub") || destination is null || destination.Length > 400)
            return BadRequest(new { error = "Select Azure DevOps or GitHub and enter its approved organization URL." });
        try {
            var canonical = SourceCredentialProvider.CanonicalDestination(Enum.Parse<SourceTokenProvider>(provider), destination);
            // CanonicalDestination accepts only fixed HTTPS hosts and alphanumeric/hyphen organizations.
            return Json(new { command = $"& \"C:\\AdminTools\\DeploymentPortal\\Manage-SourceCredential.ps1\" -Action Provision -Provider {provider} -Destination \"{canonical}\" -AppPoolName \"DeploymentPortal\"" });
        }
        catch (InvalidOperationException) {
            return BadRequest(new { error = "Enter a supported organization URL matching the selected provider. GitHub requires https://api.github.com/orgs/organization/." });
        }
    }
    [HttpGet]
    public IActionResult Credentials()
    {
        try { return Json(sources.Credentials()); }
        catch (InvalidOperationException) { return StatusCode(503, new { error = "Credential metadata unavailable. Contact the host administrator." }); }
    }
    [HttpPost]
    public Task<IActionResult> Create([Bind("DisplayName,Provider,Destination,CredentialReference,Enabled")] SourceForm form) { form.Id = 0; return Save(form); }
    [HttpPost]
    public Task<IActionResult> Edit(int id, [Bind("Id,DisplayName,Provider,Destination,CredentialReference,Enabled")] SourceForm form) => id != form.Id ? Task.FromResult<IActionResult>(BadRequest()) : Save(form);
    private async Task<IActionResult> Save(SourceForm form)
    {
        if (!ModelState.IsValid) return Form(form);
        try { await sources.SaveAsync(form); TempData["Success"] = "Source saved. Existing CI/CD configuration is unchanged."; return RedirectToAction(nameof(Index)); }
        catch (Exception ex) when (ex is ArgumentException or ValidationException) { ModelState.AddModelError("", ex.Message); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Source could not be saved. Check for a duplicate connection or reload and retry."); }
        return Form(form);
    }
    [HttpPost]
    public async Task<IActionResult> SetEnabled(int id, bool enabled) => await Change(() => sources.SetEnabledAsync(id, enabled), "Source status updated.");
    [HttpPost]
    public async Task<IActionResult> Delete(int id) => await Change(() => sources.DeleteAsync(id), "Source registration deleted. Configuration and host credentials are unchanged.");
    [HttpPost]
    public async Task<IActionResult> TestConnection(int id, CancellationToken cancellationToken) => await Change(() => sources.TestAsync(id, cancellationToken), "Connection test completed; see the latest status below.");
    private async Task<IActionResult> Change(Func<Task<bool>> action, string message)
    {
        try { if (!await action()) return NotFound(); TempData["Success"] = message; }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        catch (DbUpdateException) { TempData["Error"] = "The source changed or could not be updated. Reload and retry."; }
        return RedirectToAction(nameof(Index));
    }
}
