using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;

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
        if (!environment.IsDevelopment()) return app;
        app.UseAuthentication();
        // Only the protected Pipeline area requests a login. Dashboard access is unchanged.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/Pipeline", StringComparison.OrdinalIgnoreCase)
                && context.User.Identity?.IsAuthenticated != true)
            {
                await context.ChallengeAsync(NegotiateDefaults.AuthenticationScheme);
                return;
            }
            await next();
        });
        return app;
    }
}
