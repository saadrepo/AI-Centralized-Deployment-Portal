using DeploymentPortal.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

[Route("Assistant")]
public sealed class AssistantController(IAssistantCommandParser parser, AssistantDeploymentResolver resolver, AssistantQueries queries, ILogger<AssistantController> logger, AssistantArtifactResolver? artifacts = null) : Controller
{
    [HttpPost("Prepare")]
    public async Task<IActionResult> Prepare(string? text)
    {
        try
        {
            var command = parser.Parse(text);
            if (command.Intent == AssistantIntent.PrepareArtifact)
            {
                if (artifacts is null) throw new ArgumentException("TFS Git CD preparation is disabled.");
                var selection = await artifacts.ResolveAsync(command);
                return Json(new { intent = command.Intent.ToString(), message = $"Build {selection.Listing.Build.Id}, Dev, commit {selection.Listing.Build.SourceVersion}, artifact {selection.Listing.Artifact}, target {selection.Server.DisplayName} ({selection.Server.HostNameOrIp}), IIS {selection.Destination.SiteName}, destination {selection.Destination.TargetPath}. Open the shared preparation page to inspect/stage. Remote preflight and human confirmation are still required; nothing is queued.",
                    link = Url.Action("Prepare", "TfsCd", new { id = selection.Listing.Build.Id, applicationKey = selection.Application.Key, serverHost = selection.Server.HostNameOrIp }) });
            }
            if (command.Intent is AssistantIntent.ShowLatestBuild or AssistantIntent.CheckDeploymentStatus)
            {
                var result = await queries.ResolveAsync(command);
                return Json(new { intent = command.Intent.ToString(), message = result.Message,
                    link = result.BuildId is { } buildId ? Url.Action("Artifacts", "TfsGitBuilds", new { id = buildId }) : result.DeploymentId is { } deploymentId ? Url.Action("Details", "Deployments", new { id = deploymentId }) : null });
            }
            var form = await resolver.ResolveAsync(command);
            // Only form inputs cross this boundary, never approval tokens or executable instructions.
            return Json(new { intent = command.Intent.ToString(), fields = new Dictionary<string, string?>
            {
                ["ServerId"] = form.ServerId.ToString(), ["Environment"] = form.Environment,
                ["SiteName"] = form.SiteName, ["ApplicationName"] = form.ApplicationName,
                ["AppPoolName"] = form.AppPoolName, ["SourcePath"] = form.SourcePath,
                ["TargetPath"] = form.TargetPath, ["SitePort"] = form.SitePort.ToString(), ["SiteHostName"] = form.SiteHostName
            } });
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning("Assistant query failed ({FailureType}).", ex.GetType().Name);
            return StatusCode(502, new { message = "Unable to read build or deployment information. Please try again later." });
        }
    }
}
