using DeploymentPortal.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeploymentPortal.Controllers;

[Route("Assistant")]
public sealed class AssistantController(IAssistantCommandParser parser, AssistantDeploymentResolver resolver) : Controller
{
    [HttpPost("Prepare")]
    public async Task<IActionResult> Prepare(string? text)
    {
        try
        {
            var command = parser.Parse(text);
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
    }
}
