using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Extensions.Options;

namespace DeploymentPortal.Services;

public sealed class SourcesManagementOptions
{
    public string[] AllowedUserSids { get; set; } = [];
}

public static class SourcesManagementAuthorization
{
    public const string Policy = "SourcesManagement";
    public const string Scheme = "SourcesManagementWindows";

    public static IServiceCollection AddSourcesManagementAuthorization(this IServiceCollection services)
    {
        services.AddOptions<SourcesManagementOptions>().BindConfiguration("SourcesManagement");
        // An endpoint-specific scheme: does not replace any existing default scheme or Pipeline guard.
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SourcesManagementWindowsHandler>(Scheme, _ => { });
        services.AddAuthorization(options => options.AddPolicy(Policy, policy => {
            policy.AddAuthenticationSchemes(Scheme);
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new SourcesManagementRequirement());
        }));
        services.AddSingleton<IAuthorizationHandler, SourcesManagementHandler>();
        return services;
    }

    // Only canonical local/domain account SIDs, never account/group names or built-in group SIDs.
    public static bool IsValidUserSid(string? value) => value is not null &&
        Regex.IsMatch(value, @"\AS-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})\z", RegexOptions.CultureInvariant) &&
        value.Split('-').Skip(4).All(part => uint.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _));
}

public sealed class SourcesManagementRequirement : IAuthorizationRequirement;

public sealed class SourcesManagementHandler(IOptionsMonitor<SourcesManagementOptions> settings)
    : AuthorizationHandler<SourcesManagementRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SourcesManagementRequirement requirement)
    {
        try {
            var allowed = settings.CurrentValue.AllowedUserSids;
            // A single malformed entry invalidates the whole allowlist, including otherwise valid entries.
            if (allowed is not { Length: > 0 and <= 256 } || allowed.Any(sid => !SourcesManagementAuthorization.IsValidUserSid(sid)))
                return Task.CompletedTask;
            if (OperatingSystem.IsWindows() && context.User.Identity is WindowsIdentity { IsAuthenticated: true } identity &&
                identity.User?.Value is { } sid && allowed.Contains(sid, StringComparer.Ordinal))
                context.Succeed(requirement);
        }
        catch {
            // Invalid configuration, disposed tokens and unavailable Windows identity always deny.
            // Never log the configuration, request headers, token handles or identity exception details.
        }
        return Task.CompletedTask;
    }
}

// Authenticate exclusively through the real host scheme. No header/claim/parameter identity fallback.
public sealed class SourcesManagementWindowsHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IHostEnvironment environment, IAuthenticationSchemeProvider schemes)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private string NativeScheme => environment.IsDevelopment() ? NegotiateDefaults.AuthenticationScheme : IISDefaults.AuthenticationScheme;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try {
            if (await schemes.GetSchemeAsync(NativeScheme) is null) return AuthenticateResult.NoResult();
            var result = await Context.AuthenticateAsync(NativeScheme);
            return result.Succeeded && result.Principal is not null
                ? AuthenticateResult.Success(new AuthenticationTicket(result.Principal, Scheme.Name))
                : AuthenticateResult.NoResult();
        }
        catch (InvalidOperationException) { return AuthenticateResult.NoResult(); }
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        try {
            if (await schemes.GetSchemeAsync(NativeScheme) is not null)
                await Context.ChallengeAsync(NativeScheme, properties);
        }
        catch (InvalidOperationException) { Response.StatusCode = StatusCodes.Status401Unauthorized; }
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
