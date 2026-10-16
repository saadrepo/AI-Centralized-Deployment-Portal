using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using static DeploymentPortal.Services.PipelineState;

namespace DeploymentPortal.Services;

public sealed class PipelineCancelException(string code, string message, bool mayHaveReachedTfs = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool MayHaveReachedTfs { get; } = mayHaveReachedTfs;
}

public sealed partial class PipelineService
{
    private const string BuildSecurityNamespace = "33344d9c-fc72-4d6f-aba5-fa317101a7e9";

    private static async Task<bool> CanStopBuild(HttpClient http, JsonNode definition, CancellationToken ct)
    {
        var namespaces = await ReadAsync(http, $"_apis/securitynamespaces/{BuildSecurityNamespace}?api-version=4.1", ct);
        var ns = Items(namespaces["value"]).SingleOrDefault(n => Text(n, "namespaceId")?.Equals(BuildSecurityNamespace, StringComparison.OrdinalIgnoreCase) == true);
        var stop = Items(ns?["actions"]).SingleOrDefault(a => Text(a, "name") == "StopBuilds");
        var bit = Number(stop, "bit");
        if (bit <= 0 || (bit & (bit - 1)) != 0 || Text(ns, "separatorValue") != "/" || string.IsNullOrWhiteSpace(Text(definition["project"], "id")))
            throw new PipelineCancelException("CI_STOP_PERMISSION_UNAVAILABLE", "TFS Stop builds permission metadata could not be verified. Cancellation is disabled.");
        var token = $"{Text(definition["project"], "id")}/{Number(definition, "id")}";
        var permission = await ReadAsync(http, $"_apis/permissions/{BuildSecurityNamespace}/{bit}?api-version=4.1&alwaysAllowAdministrators=false&tokens={Uri.EscapeDataString(token)}", ct);
        var values = permission is JsonArray ? permission : permission["value"];
        if (values is not JsonArray { Count: 1 } array || array[0]?.ToJsonString() is not ("true" or "false"))
            throw new PipelineCancelException("CI_STOP_PERMISSION_UNAVAILABLE", "TFS did not return a verifiable effective Stop builds permission. Cancellation is disabled.");
        return array[0]!.GetValue<bool>();
    }

    private async Task CancellationEligibility(RegistryApplication app, JsonNode definition, JsonNode build, ClaimsPrincipal user, PipelineSnapshot result, CancellationToken ct)
    {
        if (!PipelineProgress.IsCancellable(Text(build, "status")))
        { result.CancelReason = result.BuildActive ? "Cancellation already requested in TFS." : "Only queued/running CI builds can be cancelled."; return; }
        if (!CanQueue(user)) { result.CancelReason = QueueReason(user); return; }
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            result.CanCancel = await users.RunBuildAsync(user, (http, identity) => CanStopBuild(http, definition, budget.Token));
            result.CancelReason = result.CanCancel ? "TFS Stop builds permission verified for this definition." : "TFS Stop builds permission is denied for your Windows account.";
        }
        catch (Exception ex) when (ex is PipelineCancelException or PipelineQueueException or HttpRequestException or JsonException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        { result.CanCancel = false; result.CancelReason = "TFS Stop builds permission could not be verified. Refresh status; cancellation is disabled."; }
    }

    public async Task<object> CancelBuild(string key, int buildId, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!CanQueue(user)) throw new PipelineCancelException("CI_CANCEL_IDENTITY_REQUIRED", "CI execution and a verified current Windows identity are required to cancel a build.");
        var app = App(key);
        var sent = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45)); ct = deadline.Token;
        try
        {
            return await users.RunBuildAsync(user, async (http, identity) =>
            {
                var definition = await Definition(app, ct, http);
                var build = await Build(app, buildId, definition, ct, http);
                if (!PipelineProgress.IsCancellable(Text(build, "status")))
                    throw new PipelineCancelException("CI_BUILD_NOT_CANCELLABLE", "Selected CI build is completed or cancellation is already in progress. Refresh status.");
                if (!await CanStopBuild(http, definition, ct))
                    throw new PipelineCancelException("CI_STOP_PERMISSION_DENIED", "TFS denied Stop builds permission for your Windows account on this definition.");
                // Re-read after permission evaluation: do not cancel a build that completed while confirming.
                build = await Build(app, buildId, definition, ct, http);
                if (!PipelineProgress.IsCancellable(Text(build, "status"))) throw new PipelineCancelException("CI_BUILD_STATE_CHANGED", "Build state changed; no cancellation was sent. Refresh status.");
                using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { status = "cancelling" }));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                using var request = new HttpRequestMessage(HttpMethod.Patch, Path(app, $"build/builds/{buildId}")) { Content = content };
                logger.LogInformation("CI cancel requested: BuildId={BuildId}, DefinitionId={DefinitionId}, User={User}.", buildId, app.TfsGitCiDefinitionId, user.Identity?.Name);
                sent = true;
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    throw new PipelineCancelException($"CI_CANCEL_HTTP_{(int)response.StatusCode}", $"TFS cancellation returned HTTP {(int)response.StatusCode}. Verify Stop builds permission and current build state.", (int)response.StatusCode >= 500);
                // Only a fresh TFS read can establish cancellation completion. No CD endpoints are involved.
                var confirmed = await Build(app, buildId, definition, ct, http);
                var cancelled = Text(confirmed, "status") == "completed" && Text(confirmed, "result") is "canceled" or "cancelled";
                logger.LogInformation("CI cancel response verified: BuildId={BuildId}, Status={Status}, Result={Result}.", buildId, Text(confirmed, "status"), Text(confirmed, "result"));
                return (object)new { buildId, cancelled, message = cancelled ? "TFS confirmed: Cancelled." : "Cancellation request accepted. Waiting for TFS to confirm the final build status." };
            });
        }
        catch (PipelineCancelException) { throw; }
        catch (Exception ex) when (ex is PipelineQueueException or HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { throw new PipelineCancelException("CI_CANCEL_UNCONFIRMED", sent ? "Cancellation outcome is unconfirmed. Poll status before retrying; no cancellation retry was sent." : "Cancellation was not sent: identity, build access or Stop builds permission could not be verified.", sent); }
    }
}
