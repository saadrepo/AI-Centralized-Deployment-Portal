using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.Extensions.Options;

namespace DeploymentPortal.Controllers;

public class HomeController(IServerService servers, IDeploymentService deployments, IOptions<AutomationOptions> options) : Controller
{
    public async Task<IActionResult> Index() => View(new DashboardViewModel(await servers.ListAsync(), await deployments.ListAsync(6), await deployments.CountAsync()));
    [HttpGet("/Settings")]
    public IActionResult Settings() => View(options.Value);
    [HttpGet("/PowerShell")]
    public IActionResult PowerShell() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
