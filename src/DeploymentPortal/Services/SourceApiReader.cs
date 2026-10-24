using System.Text.Json;
using System.Net;
using System.Text;

namespace DeploymentPortal.Services;

public sealed class SourceExplorerException(string message) : Exception(message)
{
    public const string Unavailable = "Source API unavailable. Verify source access, token permissions and connectivity, then retry.";
}
// Internal backend response. Callers must project approved metadata and never serialize this document directly.
public sealed record SourceApiPage(JsonDocument Json, string? Continuation, bool HasMore) : IDisposable
{
    public void Dispose() => Json.Dispose();
}
public interface ISourceApiReader
{
    Task<SourceApiPage> ReadJsonAsync(string reference, SourceTokenProvider provider, string destination, Uri requestUri, CancellationToken cancellationToken);
}

// Bounded, testable backend response boundary. Never serialize SourceApiPage to a browser.
public static class SourceApiResponse
{
    public static async Task<SourceApiPage> ParseAsync(HttpResponseMessage response, string token, CancellationToken cancellationToken)
    {
        JsonDocument? json = null;
        try {
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException();
            if ((int)response.StatusCode == 429 || (response.StatusCode == HttpStatusCode.Forbidden &&
                (response.Headers.Contains("Retry-After") || response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.Contains("0"))))
                throw new SourceExplorerException("Provider rate limit reached. Wait before refreshing; no automatic retries were made.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SourceExplorerException("Provider denied access. Verify token permissions and account authorization.");
            if (response.StatusCode == HttpStatusCode.NotFound) throw new SourceExplorerException("Selected resource was not found or is inaccessible to this source.");
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 2097152 ||
                response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/vnd.github+json")) throw new InvalidOperationException();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(bytes, cancellationToken)) > 0) {
                if (buffer.Length + count > 2097152) throw new InvalidOperationException();
                buffer.Write(bytes, 0, count);
            }
            json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token));
            bool Secret(string value) => value.Contains(token, StringComparison.Ordinal) || value.Contains(basic, StringComparison.Ordinal);
            // Check decoded strings and property names, so JSON escaping cannot bypass redaction.
            bool ContainsSecret(JsonElement value) => value.ValueKind switch {
                JsonValueKind.String => Secret(value.GetString()!),
                JsonValueKind.Array => value.EnumerateArray().Any(ContainsSecret),
                JsonValueKind.Object => value.EnumerateObject().Any(p => Secret(p.Name) || ContainsSecret(p.Value)),
                _ => false
            };
            if (ContainsSecret(json.RootElement)) throw new InvalidOperationException();
            var continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var tokens) ? tokens.SingleOrDefault() : null;
            if (!SourceExplorerRequestPolicy.Cursor(continuation) || continuation is not null && Secret(continuation)) throw new InvalidOperationException();
            var more = response.Headers.TryGetValues("Link", out var links) && links.Any(l => l.Contains("rel=\"next\"", StringComparison.Ordinal));
            var result = new SourceApiPage(json, continuation, more); json = null; return result;
        }
        catch (SourceExplorerException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new SourceExplorerException(SourceExplorerException.Unavailable); }
        finally { json?.Dispose(); }
    }
}
