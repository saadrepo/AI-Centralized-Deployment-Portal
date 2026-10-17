using DeploymentPortal.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using static DeploymentPortal.Services.PipelineState;

namespace DeploymentPortal.Controllers;

// This route stays inside the existing Windows-authenticated Pipeline area.
[Route("Pipeline/Identity"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PipelineIdentityController(IOptions<PipelineWorkgroupOptions> settings, IOptions<TfsGitConnectionOptions> connection,
    PipelinePersonalSessions sessions, ILogger<PipelineIdentityController> logger, IPipelinePersonalClientFactory personalClients) : Controller
{
    [HttpGet]
    public IActionResult Status()
    {
        var sid = PipelinePersonalSessions.Sid(User);
        if (sid is null) return Unauthorized(new { error = "A real Windows Portal login is required." });
        var session = sessions.Find(Request.Cookies[PipelineWorkgroupTransport.Cookie], sid);
        var enabled = !settings.Value.ServiceCdEnabled && settings.Value.PerUserAuthenticationEnabled && settings.Value.PerUserTfsIdentities.TryGetValue(sid, out var expected)
            && PipelineWorkgroupTransport.PersonalIdentityAllowed(settings.Value, sid, expected);
        var collection = "";
        try { collection = PipelineWorkgroupTransport.PersonalCollection(settings.Value, connection.Value); } catch (InvalidOperationException) { }
        var secure = Request.IsHttps && collection.Length > 0;
        return Json(new { enabled, secure, connected = enabled && secure && session is not null
            && session.Collection == collection && settings.Value.PerUserTfsIdentities.TryGetValue(sid, out var mapped) && mapped.Equals(session.TfsId, StringComparison.OrdinalIgnoreCase),
            expires = session?.Expires, message = !enabled ? "Personal TFS authentication is not configured for this Portal Windows user."
                : !secure ? "Personal TFS authentication requires HTTPS on both Portal and TFS."
                : "Connect your own TFS PAT. TFS approver permissions and requester restrictions still apply." });
    }

    [HttpPost("Connect"), ValidateAntiForgeryToken, RequestSizeLimit(4096), RequestFormLimits(ValueLengthLimit = 2048)]
    public async Task<IActionResult> Connect([FromForm] string personalAccessToken, CancellationToken cancellationToken)
    {
        var sid = PipelinePersonalSessions.Sid(User);
        if (settings.Value.ServiceCdEnabled)
            return StatusCode(403, new { error = "Single-account CD mode is selected. Personal TFS login is available separately when that mode is disabled." });
        if (sid is null || !settings.Value.PerUserAuthenticationEnabled
            || !settings.Value.PerUserTfsIdentities.TryGetValue(sid, out var expected) || !Guid.TryParse(expected, out _))
            return StatusCode(403, new { error = "This Portal user is not mapped to an authorized personal TFS identity." });
        string collection;
        try { collection = PipelineWorkgroupTransport.PersonalCollection(settings.Value, connection.Value); }
        catch (InvalidOperationException) { return BadRequest(new { error = "Personal TFS login needs a trusted HTTPS binding for the same configured TFS host/collection. No PAT was sent." }); }
        if (!Request.IsHttps) return BadRequest(new { error = "Trusted HTTPS is required on Portal; no PAT was sent." });
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(personalAccessToken) || personalAccessToken.Length > 2048 || personalAccessToken.Any(char.IsControl))
            return BadRequest(new { error = "Enter a valid personal token." });
        ModelState.Clear();
        try
        {
            using var http = personalClients.Create(collection, personalAccessToken);
            var data = await PipelineService.ReadAsync(http, "_apis/connectionData?api-version=4.1", cancellationToken);
            var identity = Text(data["authenticatedUser"], "id");
            if (!PipelineWorkgroupTransport.PersonalIdentityAllowed(settings.Value, sid, identity))
            {
                logger.LogWarning("Personal TFS binding refused: PortalSid={PortalSid}, Reference={Reference}.", sid, HttpContext.TraceIdentifier);
                return StatusCode(403, new { error = "TFS authenticated a different identity from this user's approved mapping. Connection refused." });
            }
            sessions.Remove(Request.Cookies[PipelineWorkgroupTransport.Cookie], sid);
            var expires = DateTimeOffset.UtcNow.AddMinutes(10);
            var key = sessions.Store(new(sid, identity!, personalAccessToken, expires, collection));
            Response.Cookies.Append(PipelineWorkgroupTransport.Cookie, key, new CookieOptions {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = Request.PathBase + "/Pipeline", Expires = expires });
            logger.LogInformation("Personal TFS identity connected: PortalSid={PortalSid}, TfsActor={TfsActor}, Reference={Reference}.", sid, identity, HttpContext.TraceIdentifier);
            return Json(new { message = "Personal TFS identity verified. Approval permissions are checked per release.", expires });
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or IOException)
        { return StatusCode(502, new { error = "TFS personal identity verification failed. Check HTTPS, PAT validity/scope and TFS access. No approval was submitted." }); }
    }

    [HttpPost("Disconnect"), ValidateAntiForgeryToken]
    public IActionResult Disconnect()
    {
        var sid = PipelinePersonalSessions.Sid(User);
        if (sid is null) return Unauthorized();
        sessions.Remove(Request.Cookies[PipelineWorkgroupTransport.Cookie], sid);
        Response.Cookies.Delete(PipelineWorkgroupTransport.Cookie, new CookieOptions { Path = Request.PathBase + "/Pipeline", Secure = true });
        return Json(new { message = "Personal TFS session disconnected." });
    }
}
