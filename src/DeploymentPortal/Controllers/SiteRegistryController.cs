using DeploymentPortal.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

[Route("SiteRegistry")]
public sealed class SiteRegistryController(SiteRegistryDraftService drafts) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index() => View(await drafts.ListAsync());

    [HttpGet("{key}")]
    public async Task<IActionResult> Details(string key)
    {
        var mappings = await drafts.ListAsync(key);
        return mappings.Count == 0 ? NotFound() : View(mappings);
    }
}
