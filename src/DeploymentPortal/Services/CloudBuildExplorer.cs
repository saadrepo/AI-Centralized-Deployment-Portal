using System.Globalization;
using System.Text.Json;
using DeploymentPortal.Models;

namespace DeploymentPortal.Services;

public sealed class CloudExplorerQuery
{
    public string Provider { get; set; } = "";
    public string Operation { get; set; } = "sources";
    public int SourceId { get; set; }
    // Optional until the corresponding browsing stage; required values are checked by the adapter.
    public string? Project { get; set; }
    public string? Repository { get; set; }
    public string? Branch { get; set; }
    public long BuildId { get; set; }
    public string? Cursor { get; set; }
}
public sealed record ExplorerItem(string Id, string Name, bool Private = false);
public sealed record ExplorerBuild(long Id, string Number, string Branch, string Commit, string Status, string Result, bool HasArtifacts);
public sealed record ExplorerArtifact(string Name, string Type, long? Size);
public sealed class CloudExplorerPage
{
    public IReadOnlyList<ExplorerItem> Items { get; init; } = [];
    public IReadOnlyList<ExplorerBuild> Builds { get; init; } = [];
    public IReadOnlyList<ExplorerArtifact> Artifacts { get; init; } = [];
    public string? Next { get; init; }
}
public interface ICloudBuildExplorer
{
    Task<CloudExplorerPage> BrowseAsync(CloudExplorerQuery query, CancellationToken cancellationToken);
}

// Uses only registered, enabled sources. The provider adapters never consume upstream URLs.
public sealed class CloudBuildExplorer(IInfrastructureSourceService sources, ISourceApiReader reader) : ICloudBuildExplorer
{
    private sealed record Scope(InfrastructureSource Source, SourceTokenProvider Provider, string Destination, string Account, CancellationToken Token);
    public async Task<CloudExplorerPage> BrowseAsync(CloudExplorerQuery query, CancellationToken cancellationToken)
    {
        if (query.Provider is not ("GitHub" or "AzureDevOps") || query.Operation is not ("sources" or "projects" or "repositories" or "branches" or "builds" or "artifacts") || !SourceExplorerRequestPolicy.Cursor(query.Cursor))
            throw new SourceExplorerException("Select a valid provider and explorer operation.");
        var provider = query.Provider == "GitHub" ? InfrastructureSourceProvider.GitHub : InfrastructureSourceProvider.AzureDevOps;
        if (query.Operation == "sources") return new() {
            Items = (await sources.ListAsync()).Sources.Where(s => s.Enabled && s.Provider == provider)
                .OrderBy(s => s.DisplayName).Select(s => new ExplorerItem(s.Id.ToString(CultureInfo.InvariantCulture), s.DisplayName)).ToList()
        };
        var source = query.SourceId > 0 ? await sources.FindAsync(query.SourceId) : null;
        if (source is null || !source.Enabled || source.Provider != provider)
            throw new SourceExplorerException("Select an enabled registered source matching this provider.");
        var tokenProvider = InfrastructureSourceService.TokenProvider(provider);
        var destination = SourceCredentialProvider.CanonicalDestination(tokenProvider, source.Destination);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var scope = new Scope(source, tokenProvider, destination, new Uri(destination).Segments.Last().Trim('/'), deadline.Token);
        return tokenProvider == SourceTokenProvider.GitHub ? await GitHubAsync(scope, query) : await AzureAsync(scope, query);
    }

    private Task<SourceApiPage> Read(Scope s, string path, params (string Key, string Value)[] query)
    {
        var root = s.Provider == SourceTokenProvider.GitHub ? "https://api.github.com/" : s.Destination;
        var values = s.Provider == SourceTokenProvider.AzureDevOps ? query.Prepend(("api-version", "7.1")) : query;
        var text = string.Join("&", values.Select(p => Uri.EscapeDataString(p.Item1) + "=" + Uri.EscapeDataString(p.Item2)));
        var uri = new Uri(root + path + (text.Length == 0 ? "" : "?" + text));
        return reader.ReadJsonAsync(s.Source.CredentialReference, s.Provider, s.Destination, uri, s.Token);
    }
    private static string Text(JsonElement element, string name, bool optional = false)
    {
        if (optional && (!element.TryGetProperty(name, out var found) || found.ValueKind == JsonValueKind.Null)) return "";
        var value = element.GetProperty(name).GetString();
        if (value is null || value.Length > 512 || value.Any(char.IsControl)) throw new JsonException();
        return value;
    }
    private static long Id(JsonElement e, string name = "id") { var n = e.GetProperty(name).GetInt64(); return n is > 0 and < 1000000000000000000 ? n : throw new JsonException(); }
    private static JsonElement[] Array(JsonElement root, string? name = null)
    {
        var array = name is null ? root : root.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 5000) throw new JsonException();
        return array.EnumerateArray().ToArray();
    }
    private static int Page(string? cursor) => cursor is null ? 1 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and < 10000 ? n : throw new SourceExplorerException("Invalid page selection.");
    private static string? Next(SourceApiPage page, int number) => page.HasMore && number < 9999 ? (number + 1).ToString(CultureInfo.InvariantCulture) : null;
    private static void Require(bool condition) { if (!condition) throw new SourceExplorerException("Selected resource does not belong to this source, repository or branch."); }
    private static string GuidId(string? value) => Guid.TryParseExact(value, "D", out var id) ? id.ToString("D") : throw new SourceExplorerException("Select a valid project or repository.");
    private static bool Same(string a, string? b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static bool GitHubOwned(JsonElement repo, Scope s) => Same(Text(repo.GetProperty("owner"), "login"), s.Account);
    private async Task<CloudExplorerPage> GitHubAsync(Scope s, CloudExplorerQuery q)
    {
        var page = Page(q.Cursor);
        var pageText = page.ToString(CultureInfo.InvariantCulture);
        if (q.Operation == "projects") throw new SourceExplorerException("GitHub does not use Azure DevOps projects.");
        if (q.Operation == "repositories") {
            var personal = new Uri(s.Destination).AbsolutePath.StartsWith("/users/", StringComparison.Ordinal);
            using var response = personal
                ? await Read(s, "user/repos", ("affiliation", "owner,collaborator"), ("visibility", "all"), ("sort", "full_name"), ("per_page", "100"), ("page", pageText))
                : await Read(s, $"orgs/{s.Account}/repos", ("type", "all"), ("sort", "full_name"), ("per_page", "100"), ("page", pageText));
            var items = Array(response.Json.RootElement).Where(r => GitHubOwned(r, s)).Select(r => {
                var name = Text(r, "name"); Require(SourceExplorerRequestPolicy.RepositoryName(name)); Id(r);
                return new ExplorerItem(name, name, r.GetProperty("private").GetBoolean());
            }).ToList();
            return new() { Items = items, Next = Next(response, page) };
        }
        Require(SourceExplorerRequestPolicy.RepositoryName(q.Repository));
        var repoPath = $"repos/{s.Account}/{q.Repository}";
        using var repoResponse = await Read(s, repoPath);
        var repo = repoResponse.Json.RootElement;
        Require(GitHubOwned(repo, s) && Same(Text(repo, "name"), q.Repository));
        var repoId = Id(repo);
        if (q.Operation == "branches") {
            using var response = await Read(s, repoPath + "/branches", ("per_page", "100"), ("page", pageText));
            return new() { Items = Array(response.Json.RootElement).Select(b => {
                var name = Text(b, "name"); Require(SourceExplorerRequestPolicy.BranchName(name)); return new ExplorerItem(name, name);
            }).ToList(), Next = Next(response, page) };
        }
        Require(SourceExplorerRequestPolicy.BranchName(q.Branch));
        using var branchResponse = await Read(s, repoPath + "/branches/" + Uri.EscapeDataString(q.Branch!));
        Require(Text(branchResponse.Json.RootElement, "name") == q.Branch);
        bool InScope(JsonElement run) => GitHubOwned(run.GetProperty("repository"), s) && Id(run.GetProperty("repository")) == repoId && Text(run, "head_branch") == q.Branch;
        if (q.Operation == "builds") {
            using var response = await Read(s, repoPath + "/actions/runs", ("branch", q.Branch!), ("per_page", "20"), ("page", pageText));
            var runs = Array(response.Json.RootElement, "workflow_runs"); Require(runs.Length <= 20);
            var builds = new List<ExplorerBuild>();
            foreach (var run in runs) {
                Require(InScope(run)); var id = Id(run);
                using var artifacts = await Read(s, $"{repoPath}/actions/runs/{id}/artifacts", ("per_page", "100"), ("page", "1"));
                var count = artifacts.Json.RootElement.GetProperty("total_count").GetInt32();
                builds.Add(new(id, Id(run, "run_number").ToString(CultureInfo.InvariantCulture), q.Branch!,
                    Text(run, "head_sha"), Text(run, "status"), Text(run, "conclusion", true), count > 0));
            }
            return new() { Builds = builds, Next = Next(response, page) };
        }
        Require(q.BuildId is > 0 and < 1000000000000000000);
        using var runResponse = await Read(s, $"{repoPath}/actions/runs/{q.BuildId}");
        Require(Id(runResponse.Json.RootElement) == q.BuildId && InScope(runResponse.Json.RootElement));
        using var artifactResponse = await Read(s, $"{repoPath}/actions/runs/{q.BuildId}/artifacts", ("per_page", "100"), ("page", pageText));
        return new() {
            Artifacts = Array(artifactResponse.Json.RootElement, "artifacts").Select(a => new ExplorerArtifact(Text(a, "name"),
                a.GetProperty("expired").GetBoolean() ? "Expired" : "Workflow artifact", a.GetProperty("size_in_bytes").GetInt64())).ToList(), Next = Next(artifactResponse, page)
        };
    }

    private async Task<CloudExplorerPage> AzureAsync(Scope s, CloudExplorerQuery q)
    {
        (string, string)[] Paging(params (string, string)[] values) => q.Cursor is null ? values : [.. values, ("continuationToken", q.Cursor)];
        if (q.Operation == "projects") {
            using var response = await Read(s, "_apis/projects", Paging(("$top", "100")));
            return new() { Items = Array(response.Json.RootElement, "value").Select(p => new ExplorerItem(GuidId(Text(p, "id")), Text(p, "name"))).ToList(), Next = response.Continuation };
        }
        var project = GuidId(q.Project);
        using var projectResponse = await Read(s, "_apis/projects/" + project);
        Require(Same(Text(projectResponse.Json.RootElement, "id"), project));
        var repoPath = project + "/_apis/git/repositories";
        if (q.Operation == "repositories") {
            using var response = await Read(s, repoPath);
            var repos = Array(response.Json.RootElement, "value");
            foreach (var r in repos) Require(Same(Text(r.GetProperty("project"), "id"), project));
            var page = Page(q.Cursor); // Azure's repositories endpoint is not paginated: paginate its bounded list locally.
            return new() { Items = repos.Skip((page - 1) * 100).Take(100).Select(r => new ExplorerItem(GuidId(Text(r, "id")), Text(r, "name"))).ToList(),
                Next = page * 100 < repos.Length ? (page + 1).ToString(CultureInfo.InvariantCulture) : null };
        }
        var repository = GuidId(q.Repository);
        using var repoResponse = await Read(s, repoPath + "/" + repository);
        Require(Same(Text(repoResponse.Json.RootElement, "id"), repository) && Same(Text(repoResponse.Json.RootElement.GetProperty("project"), "id"), project));
        repoPath += "/" + repository;
        if (q.Operation == "branches") {
            using var response = await Read(s, repoPath + "/refs", Paging(("filter", "heads/"), ("$top", "100")));
            return new() { Items = Array(response.Json.RootElement, "value").Select(b => {
                var name = Text(b, "name"); Require(name.StartsWith("refs/heads/", StringComparison.Ordinal) && SourceExplorerRequestPolicy.BranchName(name[11..]));
                return new ExplorerItem(name[11..], name[11..]);
            }).ToList(), Next = response.Continuation };
        }
        Require(SourceExplorerRequestPolicy.BranchName(q.Branch));
        using var branchResponse = await Read(s, repoPath + "/refs", ("filter", "heads/" + q.Branch), ("$top", "100"));
        Require(Array(branchResponse.Json.RootElement, "value").Any(b => Text(b, "name") == "refs/heads/" + q.Branch));
        bool InScope(JsonElement b) => Same(Text(b.GetProperty("project"), "id"), project) && Same(Text(b.GetProperty("repository"), "id"), repository) &&
            Same(Text(b.GetProperty("repository"), "type"), "TfsGit") && Text(b, "sourceBranch") == "refs/heads/" + q.Branch;
        var buildsPath = project + "/_apis/build/builds";
        if (q.Operation == "builds") {
            using var response = await Read(s, buildsPath, Paging(("repositoryId", repository), ("repositoryType", "TfsGit"),
                ("branchName", "refs/heads/" + q.Branch), ("$top", "20"), ("queryOrder", "queueTimeDescending")));
            var builds = new List<ExplorerBuild>(); var rows = Array(response.Json.RootElement, "value"); Require(rows.Length <= 20);
            foreach (var build in rows) {
                Require(InScope(build)); var id = Id(build); Require(id <= int.MaxValue);
                using var artifacts = await Read(s, $"{buildsPath}/{id}/artifacts");
                builds.Add(new(id, Text(build, "buildNumber"), q.Branch!, Text(build, "sourceVersion"), Text(build, "status"), Text(build, "result", true), Array(artifacts.Json.RootElement, "value").Length > 0));
            }
            return new() { Builds = builds, Next = response.Continuation };
        }
        Require(q.BuildId is > 0 and <= int.MaxValue);
        using var buildResponse = await Read(s, $"{buildsPath}/{q.BuildId}");
        Require(Id(buildResponse.Json.RootElement) == q.BuildId && InScope(buildResponse.Json.RootElement));
        using var artifactResponse = await Read(s, $"{buildsPath}/{q.BuildId}/artifacts");
        var artifactsList = Array(artifactResponse.Json.RootElement, "value"); var artifactPage = Page(q.Cursor);
        return new() { Artifacts = artifactsList.Skip((artifactPage - 1) * 100).Take(100).Select(a => new ExplorerArtifact(Text(a, "name"), Text(a.GetProperty("resource"), "type"), null)).ToList(),
            Next = artifactPage * 100 < artifactsList.Length ? (artifactPage + 1).ToString(CultureInfo.InvariantCulture) : null };
    }
}
