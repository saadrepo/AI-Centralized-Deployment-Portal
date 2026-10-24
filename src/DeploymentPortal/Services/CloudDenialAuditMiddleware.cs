namespace DeploymentPortal.Services;

// Place before authorization: endpoint authorization can short-circuit MVC filters.
// Export these events to the organization's protected persistent logging sink.
public sealed class CloudDenialAuditMiddleware(RequestDelegate next, ILogger<CloudDenialAuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);
        if (!context.Request.Path.StartsWithSegments("/Pipeline/Cloud") || context.Response.StatusCode is not (400 or 401 or 403 or 409 or 503)) return;
        string sid;
        try { sid = CloudSecurity.Sid(context.User); } catch { sid = "unavailable"; }
        logger.LogWarning(new EventId(4103, "CloudRequestDenied"),
            "Cloud request denied. Status={Status}, ActorSid={ActorSid}, Reference={Reference}.", context.Response.StatusCode, sid, context.TraceIdentifier);
    }
}
