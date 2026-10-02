using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

public class DockerContainersController(IDockerContainerService docker) : Controller
{
    public IActionResult Index() => View(new DockerContainerForm());

    [HttpPost]
    public async Task<IActionResult> Review(DockerContainerForm form)
    {
        if (!ModelState.IsValid) return View("Index", form);
        try
        {
            form.ConfirmationToken = await docker.ReviewAsync(form);
            return View(form);
        }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return View("Index", form); }
    }

    [HttpPost]
    public async Task<IActionResult> Run(DockerContainerForm form)
    {
        if (!ModelState.IsValid) return View("Index", form);
        try { return View("Result", await docker.RunAsync(form)); }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return View("Index", form); }
    }
}
