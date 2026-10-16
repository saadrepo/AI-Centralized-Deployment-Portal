using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Server.IISIntegration;

namespace DeploymentPortal.Services;

public static class LocalWindowsAuthentication
{
    public static IServiceCollection AddLocalWindowsAuthentication(this IServiceCollection services, IHostEnvironment environment)
    {
        // IIS continues to own production authentication. Kestrel needs its own handler locally.
        if (environment.IsDevelopment())
            services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
        return services;
    }

    public static IApplicationBuilder UseLocalWindowsAuthentication(this IApplicationBuilder app, IHostEnvironment environment)
    {
        if (environment.IsDevelopment()) app.UseAuthentication();
        // Only the protected Pipeline area requests a login. Dashboard access is unchanged.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/Pipeline", StringComparison.OrdinalIgnoreCase)
                && context.User.Identity?.IsAuthenticated != true)
            {
                if (environment.IsDevelopment())
                    await context.ChallengeAsync(NegotiateDefaults.AuthenticationScheme);
                else
                {
                    // IIS registers its native Windows handler. Explicitly challenge only
                    // Pipeline so anonymous Dashboard/public requests remain unchanged.
                    var schemes = context.RequestServices.GetService<IAuthenticationSchemeProvider>();
                    if (schemes is not null && await schemes.GetSchemeAsync(IISDefaults.AuthenticationScheme) is not null)
                    {
                        try
                        {
                            var authentication = await context.AuthenticateAsync(IISDefaults.AuthenticationScheme);
                            if (authentication.Succeeded && authentication.Principal?.Identity?.IsAuthenticated == true)
                            {
                                context.User = authentication.Principal;
                            }
                            else
                            {
                                await context.ChallengeAsync(IISDefaults.AuthenticationScheme);
                                return;
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(nameof(LocalWindowsAuthentication))
                                .LogWarning("Pipeline IIS Windows authentication handler is unavailable or disabled. Reference={Reference}.", context.TraceIdentifier);
                        }
                        if (context.User.Identity?.IsAuthenticated == true)
                        {
                            await next();
                            return;
                        }
                    }
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("Windows Authentication is unavailable for CI/CD Pipeline. Ask the IIS administrator to enable native Windows Authentication while retaining Anonymous Authentication for public pages.");
                }
                return;
            }
            await next();
        });
        return app;
    }
}
