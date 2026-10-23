using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeploymentPortal.Services;

public sealed record GitHubAccount(string Login, string Type, string Destination);
public interface IGitHubAccountDetector
{
    Task<GitHubAccount> DetectAsync(string input, CancellationToken cancellationToken = default);
}

// Public account discovery only. No credential lookup, redirects, proxy, default credentials or HTTP logging.
public sealed class GitHubAccountDetector(HttpClient client) : IGitHubAccountDetector, IDisposable
{
    public const string InvalidInput = "Enter a GitHub username, https://github.com/username, or an approved GitHub account API URL.";
    public const string Unavailable = "GitHub account detection failed. Check connectivity and try again; no account type was selected.";
    public static string ParseLogin(string input)
    {
        if (input is null || input.Length is < 1 or > 400) throw new ArgumentException(InvalidInput);
        // Match the original input, not a URI-normalized path: reject ports, userinfo, escapes, queries and traversal.
        var match = Regex.Match(input, @"\A(?:https://github\.com/|https://api\.github\.com/(?:users|orgs)/)?(?<login>[A-Za-z0-9][A-Za-z0-9-]{0,38})/?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) throw new ArgumentException(InvalidInput);
        return match.Groups["login"].Value.ToLowerInvariant();
    }

    public static void SetHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd("DeploymentPortal/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<GitHubAccount> DetectAsync(string input, CancellationToken cancellationToken = default)
    {
        var login = ParseLogin(input);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try {
            // Always construct the sole allowed discovery endpoint. Never fetch the submitted URL.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/users/{login}");
            SetHeaders(request);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new ArgumentException("GitHub denied account detection or its rate limit was reached. Wait before retrying; no account type was selected.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new ArgumentException("GitHub account was not found or is not publicly accessible. Verify the account; no account type was selected.");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ArgumentException("GitHub did not authorize public account detection; no account type was selected.");
            if (response.StatusCode != HttpStatusCode.OK) throw new ArgumentException(Unavailable);
            if (response.Content.Headers.ContentLength > 65536) throw new ArgumentException(Unavailable);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0) {
                if (buffer.Length + count > 65536) throw new ArgumentException(Unavailable);
                buffer.Write(chunk, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (!root.TryGetProperty("login", out var returnedLogin) || returnedLogin.GetString() is not { } name ||
                !name.Equals(login, StringComparison.OrdinalIgnoreCase) || !root.TryGetProperty("type", out var accountType))
                throw new ArgumentException(Unavailable);
            var type = accountType.GetString();
            var segment = type switch { "User" => "users", "Organization" => "orgs", _ => throw new ArgumentException("GitHub returned an unsupported account type; no destination was selected.") };
            return new(login, type!, SourceCredentialProvider.CanonicalDestination(SourceTokenProvider.GitHub, $"https://api.github.com/{segment}/{login}/"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidOperationException) {
            // Never expose remote bodies, headers or transport exception details.
            throw new ArgumentException(Unavailable);
        }
    }

    public void Dispose() => client.Dispose();
}
