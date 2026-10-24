using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace DeploymentPortal.Services;

// A separate read boundary; the existing status-only connection-test allowlist remains unchanged.
public static class SourceExplorerRequestPolicy
{
    public static bool RepositoryName(string? name) => name is not null && name is not ("." or "..") && Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]{1,100}\z");
    public static bool BranchName(string? name) => name is { Length: > 0 and <= 250 } && name != "@" &&
        !name.StartsWith('/') && !name.StartsWith('-') && !name.EndsWith('/') && !name.EndsWith('.') &&
        !name.Contains("..") && !name.Contains("//") && !name.Contains("@{") &&
        !name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "~^:?*[\\%".Contains(c)) &&
        name.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    public static bool Cursor(string? value) => value is null || value.Length <= 512 && value.All(c => c is >= '!' and <= '~');

    public static bool Allows(SourceTokenProvider provider, string destination, Uri request)
    {
        try {
            var approved = new Uri(SourceCredentialProvider.CanonicalDestination(provider, destination));
            if (!request.IsAbsoluteUri || request.Scheme != "https" || request.Port != 443 || request.Host != approved.Host ||
                request.UserInfo.Length != 0 || request.Fragment.Length != 0 || request.OriginalString.Contains('\\')) return false;
            var raw = request.OriginalString.Split('?')[0];
            if (raw.Split('/').Any(s => s is "." or "..") || raw.Contains("%25", StringComparison.OrdinalIgnoreCase)) return false;
            var q = QueryHelpers.ParseQuery(request.Query);
            if (q.Any(x => x.Value.Count != 1 || x.Value[0] is not { Length: <= 1024 } v || v.Any(char.IsControl))) return false;
            string Value(string key) => q.TryGetValue(key, out var value) ? value.ToString() : "";
            bool Only(params string[] keys) => q.Keys.All(k => keys.Contains(k, StringComparer.Ordinal));
            bool Page(int size) => Value("per_page") == size.ToString() && int.TryParse(Value("page"), out var n) && n is >= 1 and <= 10000;
            var path = request.AbsolutePath;
            if (provider == SourceTokenProvider.GitHub) {
                var parts = approved.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var owner = Regex.Escape(parts[1]);
                if (raw.Contains('%') && !Regex.IsMatch(path, $@"\A/repos/{owner}/[A-Za-z0-9_.-]{{1,100}}/branches/[^/]+\z", RegexOptions.IgnoreCase)) return false;
                if (path == "/user/repos") return parts[0] == "users" && Only("affiliation", "visibility", "sort", "per_page", "page") &&
                    Value("affiliation") == "owner,collaborator" && Value("visibility") == "all" && Value("sort") == "full_name" && Page(100);
                if (path.Equals($"/orgs/{parts[1]}/repos", StringComparison.OrdinalIgnoreCase)) return parts[0] == "orgs" &&
                    Only("type", "sort", "per_page", "page") && Value("type") == "all" && Value("sort") == "full_name" && Page(100);
                var match = Regex.Match(path, $@"\A/repos/{owner}/(?<repo>[A-Za-z0-9_.-]{{1,100}})(?<tail>/.*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success || !RepositoryName(match.Groups["repo"].Value)) return false;
                var tail = match.Groups["tail"].Value;
                if (tail == "") return q.Count == 0;
                if (tail == "/branches") return Only("per_page", "page") && Page(100);
                if (tail.StartsWith("/branches/", StringComparison.Ordinal)) return q.Count == 0 && BranchName(Uri.UnescapeDataString(tail[10..]));
                if (tail == "/actions/workflows") return Only("per_page", "page") && Page(100);
                if (Regex.IsMatch(tail, @"\A/actions/workflows/[1-9][0-9]{0,17}\z")) return q.Count == 0;
                if (Regex.IsMatch(tail, @"\A/actions/workflows/[1-9][0-9]{0,17}/runs\z")) return Only("per_page", "page", "branch") && Page(20) && BranchName(Value("branch"));
                if (tail == "/actions/runs") return Only("per_page", "page", "branch") && Page(20) && BranchName(Value("branch"));
                if (Regex.IsMatch(tail, @"\A/actions/runs/[1-9][0-9]{0,17}\z")) return q.Count == 0;
                return Regex.IsMatch(tail, @"\A/actions/runs/[1-9][0-9]{0,17}/artifacts\z") && Only("per_page", "page") && Page(100);
            }
            if (raw.Contains('%') || Value("api-version") != "7.1" || !Cursor(Value("continuationToken"))) return false;
            var prefix = approved.AbsolutePath;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var relative = path[prefix.Length..];
            const string guid = "[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}";
            if (relative == "_apis/projects") return Only("api-version", "$top", "continuationToken") && Value("$top") == "100";
            if (Regex.IsMatch(relative, $@"\A_apis/projects/{guid}\z")) return Only("api-version");
            if (Regex.IsMatch(relative, $@"\A{guid}/_apis/git/repositories(?:/{guid})?\z")) return Only("api-version");
            if (Regex.IsMatch(relative, $@"\A{guid}/_apis/git/repositories/{guid}/refs\z")) return Only("api-version", "filter", "$top", "continuationToken") &&
                Value("$top") == "100" && (Value("filter") == "heads/" || Value("filter").StartsWith("heads/", StringComparison.Ordinal) && BranchName(Value("filter")[6..]));
            if (Regex.IsMatch(relative, $@"\A{guid}/_apis/build/definitions\z")) return Only("api-version", "repositoryId", "repositoryType", "$top", "continuationToken") &&
                Guid.TryParseExact(Value("repositoryId"), "D", out _) && Value("repositoryType") == "TfsGit" && Value("$top") == "100";
            if (Regex.IsMatch(relative, $@"\A{guid}/_apis/build/definitions/[1-9][0-9]{{0,9}}\z")) return Only("api-version");
            if (Regex.IsMatch(relative, $@"\A{guid}/_apis/build/builds\z")) return Only("api-version", "repositoryId", "repositoryType", "branchName", "$top", "queryOrder", "continuationToken", "definitions") &&
                Guid.TryParseExact(Value("repositoryId"), "D", out _) && Value("repositoryType") == "TfsGit" && Value("$top") == "20" &&
                (!q.ContainsKey("definitions") || int.TryParse(Value("definitions"), out var definitionId) && definitionId > 0) &&
                Value("queryOrder") == "queueTimeDescending" && Value("branchName").StartsWith("refs/heads/", StringComparison.Ordinal) && BranchName(Value("branchName")[11..]);
            return Regex.IsMatch(relative, $@"\A{guid}/_apis/build/builds/[1-9][0-9]{{0,9}}(?:/artifacts)?\z") && Only("api-version");
        }
        catch { return false; }
    }
}
