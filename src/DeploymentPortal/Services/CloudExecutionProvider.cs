using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DeploymentPortal.Models;

namespace DeploymentPortal.Services;

public sealed class CloudExecutionProvider(ICloudProviderTransport transport, ICloudBuildExplorer explorer) : ICloudProvider
{
    private static bool GitHub(CloudBinding b) => b.Mapping.Provider == "GitHub";
    private static Uri UriFor(CloudBinding b, string resource)
    {
        var root = SourceCredentialProvider.CanonicalDestination(InfrastructureSourceService.TokenProvider(b.Source.Provider), b.Source.Destination);
        return new Uri(GitHub(b) ? $"https://api.github.com/repos/{new Uri(root).Segments.Last().Trim('/')}/{b.Mapping.Repository}/{resource}"
            : root + b.Mapping.Project + "/_apis/build/" + resource);
    }
    private Task<JsonDocument> Read(CloudBinding b, string resource, CancellationToken ct) => transport.JsonAsync(b.Source, HttpMethod.Get,
        UriFor(b, resource + (GitHub(b) ? "" : "?api-version=7.1")), null, ct);
    private static string Text(JsonElement e, string field) => e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String &&
        v.GetString() is { Length: <= 512 } value && !value.Any(char.IsControl) ? value : throw new CloudExecutionException("PROVIDER_METADATA");
    private static long Id(JsonElement e, string field = "id") => e.TryGetProperty(field, out var v) && v.TryGetInt64(out var id) && id is > 0 and < 1000000000000000000
        ? id : throw new CloudExecutionException("PROVIDER_ID");
    private static CloudExplorerQuery Query(CloudSelection s, string operation) => new() { Provider = s.Provider, SourceId = s.SourceId,
        Project = s.Project, Repository = s.Repository, Branch = s.Branch, DefinitionId = s.DefinitionId, BuildId = s.RunId, Operation = operation };
    public async Task<long> QueueAsync(CloudBinding binding, CloudSelection selection, CancellationToken ct)
    {
        CloudSecurity.Require(selection.Provider == "GitHub", "AZURE_EXECUTION_NOT_IMPLEMENTED");
        // Validate current source ownership, branch and definition using the existing explorer.
        await explorer.BrowseAsync(Query(selection with { RunId = 0 }, "builds"), ct);
        using var response = await transport.JsonAsync(binding.Source, HttpMethod.Post,
            UriFor(binding, GitHub(binding) ? $"actions/workflows/{selection.DefinitionId}/dispatches" : "builds?api-version=7.1"),
            GitHub(binding) ? new { @ref = selection.Branch, return_run_details = true } : (object)new {
                definition = new { id = selection.DefinitionId }, sourceBranch = "refs/heads/" + selection.Branch,
                repository = new { id = selection.Repository, type = "TfsGit" }
            }, ct);
        var id = Id(response.RootElement, GitHub(binding) ? "workflow_run_id" : "id");
        // Never infer the queued run from a latest-run listing, URL, timestamp or branch.
        await RunAsync(binding, selection with { RunId = id }, ct);
        return id;
    }
    public async Task<CloudRun> RunAsync(CloudBinding binding, CloudSelection selection, CancellationToken ct)
    {
        CloudSecurity.Require(selection.RunId > 0, "RUN_REQUIRED");
        var verified = await explorer.BrowseAsync(Query(selection, "status"), ct);
        CloudSecurity.Require(verified.Builds.Count == 1 && verified.Builds[0].Id == selection.RunId, "RUN_SCOPE");
        using var raw = await Read(binding, GitHub(binding) ? $"actions/runs/{selection.RunId}" : $"builds/{selection.RunId}", ct);
        var e = raw.RootElement; var run = verified.Builds[0];
        CloudSecurity.Require(Id(e) == selection.RunId, "RUN_SCOPE");
        var commit = Text(e, GitHub(binding) ? "head_sha" : "sourceVersion");
        CloudSecurity.Require(commit == run.Commit && commit.Length is 40 or 64 && commit.All(Uri.IsHexDigit), "COMMIT_IDENTITY");
        // The second metadata read must still agree with the validated run scope.
        if (GitHub(binding)) {
            CloudSecurity.Require(Id(e, "workflow_id") == selection.DefinitionId && Text(e, "head_branch") == selection.Branch &&
                Text(e.GetProperty("repository"), "name").Equals(selection.Repository, StringComparison.OrdinalIgnoreCase) &&
                Text(e.GetProperty("repository").GetProperty("owner"), "login").Equals(new Uri(binding.Source.Destination).Segments.Last().Trim('/'), StringComparison.OrdinalIgnoreCase), "RUN_SCOPE");
        } else CloudSecurity.Require(Id(e.GetProperty("definition")) == selection.DefinitionId && Text(e, "sourceBranch") == "refs/heads/" + selection.Branch &&
            Text(e.GetProperty("repository"), "id").Equals(selection.Repository, StringComparison.OrdinalIgnoreCase) &&
            Text(e.GetProperty("repository"), "type") == "TfsGit" && Text(e.GetProperty("project"), "id").Equals(selection.Project, StringComparison.OrdinalIgnoreCase), "RUN_SCOPE");
        var result = e.TryGetProperty(GitHub(binding) ? "conclusion" : "result", out var conclusion) && conclusion.ValueKind == JsonValueKind.String ? Text(e, GitHub(binding) ? "conclusion" : "result") : "";
        return new(selection.RunId, run.Number, commit, Text(e, "status"), result, GitHub(binding) ? Id(e, "run_attempt") : 1);
    }
    public async Task<CloudEvidence> EvidenceAsync(CloudBinding binding, CloudSelection selection, CancellationToken ct)
    {
        CloudSecurity.Require(selection.Provider == "GitHub", "AZURE_EXECUTION_NOT_IMPLEMENTED");
        var run = await RunAsync(binding, selection, ct);
        CloudSecurity.Require(run.Status == "completed" && run.Result == (GitHub(binding) ? "success" : "succeeded"), "RUN_NOT_SUCCESSFUL");
        using var response = await Read(binding, GitHub(binding) ? $"actions/runs/{selection.RunId}/artifacts?per_page=100&page=1" : $"builds/{selection.RunId}/artifacts", ct);
        var root = response.RootElement;
        // Fail closed rather than overlooking duplicate artifacts beyond the bounded page.
        if (GitHub(binding)) CloudSecurity.Require(root.GetProperty("total_count").GetInt32() <= 100, "ARTIFACT_COUNT");
        var rows = root.GetProperty(GitHub(binding) ? "artifacts" : "value");
        CloudSecurity.Require(rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() <= 100, "ARTIFACT_COUNT");
        var matches = rows.EnumerateArray().Where(a => Text(a, "name") == binding.Mapping.ArtifactName).ToArray();
        CloudSecurity.Require(matches.Length == 1, "ARTIFACT_SELECTION"); var artifact = matches[0];
        string digest = "", type = "Container"; long size = 0;
        if (GitHub(binding)) {
            CloudSecurity.Require(!artifact.GetProperty("expired").GetBoolean(), "ARTIFACT_EXPIRED");
            digest = Text(artifact, "digest"); size = artifact.GetProperty("size_in_bytes").GetInt64(); type = "WorkflowArtifact";
            CloudSecurity.Require(digest.StartsWith("sha256:", StringComparison.Ordinal) && digest.Length == 71 && digest[7..].All(Uri.IsHexDigit) && size is > 0 and <= 67108864, "ARTIFACT_DIGEST");
            CloudSecurity.Require(Id(artifact.GetProperty("workflow_run")) == selection.RunId, "ARTIFACT_RUN");
        } else CloudSecurity.Require(Text(artifact.GetProperty("resource"), "type") == "Container", "ARTIFACT_TYPE");
        // Identity excludes untrusted download URLs; only compiled API endpoints are followed.
        var identity = GitHub(binding) ? CloudSecurity.Hash(new { id = Id(artifact), size, digest, run.Attempt })
            : CloudSecurity.Hash(new { id = Id(artifact), data = Text(artifact.GetProperty("resource"), "data"), run.Commit });
        return new(run, new(Id(artifact), binding.Mapping.ArtifactName, type, size, digest, identity));
    }
    public async Task<byte[]> DownloadAsync(CloudBinding binding, CloudSelection selection, CloudEvidence evidence, CancellationToken ct)
    {
        CloudSecurity.Require(selection.Provider == "GitHub", "AZURE_EXECUTION_NOT_IMPLEMENTED");
        var uri = UriFor(binding, GitHub(binding) ? $"actions/artifacts/{evidence.Artifact.Id}/zip" :
            $"builds/{selection.RunId}/artifacts?artifactName={Uri.EscapeDataString(evidence.Artifact.Name)}&$format=zip&api-version=7.1");
        var bytes = await transport.ArchiveAsync(binding.Source, uri, binding.Mapping.ArtifactHosts, ct);
        if (GitHub(binding)) CloudSecurity.Require(Convert.ToHexString(SHA256.HashData(bytes)).Equals(evidence.Artifact.Digest[7..], StringComparison.OrdinalIgnoreCase), "ARCHIVE_DIGEST");
        return bytes;
    }
}
