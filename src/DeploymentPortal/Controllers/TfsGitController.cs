using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class TfsGitController(ITfsGitService tfsGit, ILogger<TfsGitController> logger) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        try
        {
            var repos = await tfsGit.GetRepositoriesAsync();
            var repositories = new List<TfsGitRepositoryWithBranchesViewModel>();
            foreach (var r in repos)
            {
                IReadOnlyList<TfsGitBranchViewModel> branches = Array.Empty<TfsGitBranchViewModel>();
                string? branchesError = null;
                try { branches = await tfsGit.GetBranchesAsync(r.Id!); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to load branches for repo {Repo}", r.Name);
                    branchesError = "Unable to load branches. Please try again later.";
                    Response.StatusCode = StatusCodes.Status502BadGateway;
                }
                var defaultBranchName = r.DefaultBranch is null ? null : (r.DefaultBranch.StartsWith("refs/heads/") ? r.DefaultBranch.Substring("refs/heads/".Length) : r.DefaultBranch);
                repositories.Add(new TfsGitRepositoryWithBranchesViewModel { Id = r.Id, Name = r.Name, DefaultBranchName = defaultBranchName, Branches = branches, BranchesError = branchesError });
            }
            return View(new TfsGitPageViewModel { Repositories = repositories });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to load TFS Git repositories.");
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return View(new TfsGitPageViewModel { Error = "Unable to load repositories. Please try again later." });
        }
    }
}
