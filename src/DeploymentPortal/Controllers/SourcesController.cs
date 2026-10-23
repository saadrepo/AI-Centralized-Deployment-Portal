using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace DeploymentPortal.Controllers;

[Authorize(Policy = SourcesManagementAuthorization.Policy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SourcesController(IInfrastructureSourceService sources, IGitHubAccountDetector github) : Controller
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
    public async Task<IActionResult> ResolveGitHubAccount(string? destination, CancellationToken cancellationToken)
    {
        try { return Json(await github.DetectAsync(destination ?? "", cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
    [HttpGet]
    public async Task<IActionResult> ProvisioningCommand(string? provider, string? destination, CancellationToken cancellationToken)
    {
        // Only generate instructions. Never invoke a process or accept a secret here.
        if (provider is not ("AzureDevOps" or "GitHub") || destination is null || destination.Length > 400)
            return BadRequest(new { error = "Select Azure DevOps or GitHub and enter its approved organization URL." });
        try {
            var canonical = provider == "GitHub" ? (await github.DetectAsync(destination, cancellationToken)).Destination
                : SourceCredentialProvider.CanonicalDestination(SourceTokenProvider.AzureDevOps, destination);
            // Only fixed HTTPS hosts and alphanumeric/hyphen accounts can enter the command.
            return Json(new { destination = canonical, command = $"& \"C:\\AdminTools\\DeploymentPortal\\Manage-SourceCredential.ps1\" -Action Provision -Provider {provider} -Destination \"{canonical}\" -AppPoolName \"DeploymentPortal\"" });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException) {
            return BadRequest(new { error = "Enter a supported account destination matching the selected provider." });
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
        catch (Exception ex) when (ex is ArgumentException or ValidationException) {
            if (form.Provider == DeploymentPortal.Models.InfrastructureSourceProvider.GitHub)
                ModelState.Remove(nameof(SourceForm.Destination)); // Display the detected destination if binding validation failed.
            ModelState.AddModelError("", ex.Message);
        }
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
