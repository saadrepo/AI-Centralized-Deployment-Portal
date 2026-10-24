using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using Xunit;

namespace DeploymentPortal.Tests;

public class CloudExecutionProviderTests
{
    private const string Project = "11111111-1111-1111-1111-111111111111", Repository = "22222222-2222-2222-2222-222222222222";
    private sealed class Transport : ICloudProviderTransport, ISourceApiReader
    {
        public List<(HttpMethod Method, Uri Uri, object? Body)> Calls = [];
        public int Downloads, Writes, RunReads; public string Failure = ""; public bool Azure;
        private object Repo => Azure ? new { id = Repository, project = new { id = Project } } : (object)new { id = 10, name = "app", owner = new { login = "saadrepo" } };
        private object Run => Azure ? new { id = 42, buildNumber = "42", definition = new { id = Failure == "definition" ? 8 : 7 }, project = new { id = Project }, repository = new { id = Repository, type = "TfsGit" }, sourceBranch = "refs/heads/Dev", sourceVersion = new string('a', 40), status = "completed", result = Failure == "failed" ? "failed" : "succeeded" }
            : (object)new { id = 42, workflow_id = Failure == "definition" ? 8 : 7, run_attempt = 1, run_number = 42, repository = Repo, head_branch = Failure == "branch" ? "Other" : "Dev", head_sha = new string('a', 40), status = "completed", conclusion = Failure == "failed" ? "failure" : "success" };
        public Task<JsonDocument> JsonAsync(InfrastructureSource source, HttpMethod method, Uri uri, object? body, CancellationToken ct)
        {
            Assert.True(CloudExecutionRequestPolicy.Allows(source, method, uri), uri.AbsoluteUri); Calls.Add((method, uri, body));
            object value;
            if (method == HttpMethod.Post) { Writes++; value = Azure ? new { id = 42 } : (object)new { workflow_run_id = 42 }; }
            else {
                var p = uri.AbsolutePath;
                if (p.EndsWith("/runs/42") || p.EndsWith("/builds/42")) { RunReads++; value = Run; }
                else if (p.EndsWith("/artifacts")) value = Azure ? new { value = new[] { new { id = 9, name = "drop", resource = new { type = Failure == "type" ? "PipelineArtifact" : "Container", data = "#/10/drop", downloadUrl = "https://attacker.invalid/ignored" } } } }
                    : (object)new { total_count = 1, artifacts = new[] { new { id = 9, name = "drop", expired = Failure == "expired", size_in_bytes = 3, digest = "sha256:" + Convert.ToHexString(SHA256.HashData("zip"u8)), workflow_run = new { id = Failure == "artifact-run" ? 43 : 42 }, archive_download_url = "https://attacker.invalid/ignored" } } };
                else if (p.EndsWith("/workflows/7/runs")) value = new { workflow_runs = Array.Empty<object>() };
                else if (p.EndsWith("/_apis/build/builds")) value = new { value = Array.Empty<object>() };
                else if (p.EndsWith("/workflows/7")) value = new { id = 7, name = "CI" };
                else if (p.EndsWith("/definitions/7")) value = new { id = 7, project = new { id = Project }, repository = new { id = Repository, type = "TfsGit" } };
                else if (p.EndsWith("/branches/Dev")) value = new { name = "Dev" };
                else if (p.EndsWith("/refs")) value = new { value = new[] { new { name = "refs/heads/Dev" } } };
                else if (p.EndsWith("/_apis/projects/" + Project)) value = new { id = Project };
                else if (p.EndsWith("/app") || p.EndsWith("/repositories/" + Repository)) value = Repo;
                else throw new Exception("Unexpected mock request " + p);
            }
            return Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(value)));
        }
        public async Task<SourceApiPage> ReadJsonAsync(string reference, SourceTokenProvider provider, string destination, Uri uri, CancellationToken ct)
        {
            var source = new InfrastructureSource { Provider = Azure ? InfrastructureSourceProvider.AzureDevOps : InfrastructureSourceProvider.GitHub, Destination = destination, Enabled = true };
            return new(await JsonAsync(source, HttpMethod.Get, uri, null, ct), null, false);
        }
        public Task<byte[]> ArchiveAsync(InfrastructureSource source, Uri uri, string[] hosts, CancellationToken ct) {
            Assert.True(CloudExecutionRequestPolicy.Allows(source, HttpMethod.Get, uri, true)); Downloads++;
            Assert.DoesNotContain("attacker", uri.AbsoluteUri); return Task.FromResult("zip"u8.ToArray());
        }
    }
    private static (CloudExecutionProvider Provider, CloudBinding Binding, CloudSelection Selection, Transport Transport) Fixture(string provider)
    {
        var sources = new CloudBuildExplorerTests.Sources(); var transport = new Transport { Azure = provider == "AzureDevOps" };
        if (transport.Azure) { sources.Values[0].Provider = InfrastructureSourceProvider.AzureDevOps; sources.Values[0].Destination = "https://dev.azure.com/example/"; }
        var selection = new CloudSelection("Portal", provider, 1, transport.Azure ? Project : null, transport.Azure ? Repository : "app", "Dev", 7, 42);
        var mapping = new CloudExecutionMapping { Provider = provider, Repository = selection.Repository, Project = selection.Project, ArtifactName = "drop" };
        return (new(transport, new CloudBuildExplorer(sources, transport)), new(mapping, sources.Values[0], new(), new(), "binding"), selection, transport);
    }
    [Theory] [InlineData("GitHub")]
    public async Task QueuesOnlySelectedWorkflowOrDefinitionAndTracksReturnedRun(string provider)
    {
        var f = Fixture(provider); var id = await f.Provider.QueueAsync(f.Binding, f.Selection with { RunId = 0 }, default);
        Assert.Equal(42, id); Assert.Equal(1, f.Transport.Writes);
        var post = Assert.Single(f.Transport.Calls, x => x.Method == HttpMethod.Post); var body = JsonSerializer.Serialize(post.Body);
        Assert.Contains(provider == "GitHub" ? "\"ref\":\"Dev\"" : "refs/heads/Dev", body);
        Assert.Contains(provider == "GitHub" ? "/workflows/7/dispatches" : "/_apis/build/builds", post.Uri.AbsolutePath);
        Assert.True(f.Transport.RunReads >= 2); Assert.Equal(0, f.Transport.Downloads);
    }
    [Theory] [InlineData("GitHub")]
    public async Task ExactSuccessfulArtifactUsesCompiledDownloadEndpointNotProviderUrls(string provider)
    {
        var f = Fixture(provider); var evidence = await f.Provider.EvidenceAsync(f.Binding, f.Selection, default);
        var bytes = await f.Provider.DownloadAsync(f.Binding, f.Selection, evidence, default);
        Assert.Equal("zip"u8.ToArray(), bytes); Assert.Equal(1, f.Transport.Downloads); Assert.Equal(0, f.Transport.Writes);
    }
    [Theory] [InlineData("GitHub", "failed")] [InlineData("GitHub", "expired")] [InlineData("GitHub", "branch")]
    [InlineData("GitHub", "definition")] [InlineData("GitHub", "artifact-run")]
    public async Task UnsafeRunsAndArtifactsAreRejectedWithoutDownloading(string provider, string failure)
    {
        var f = Fixture(provider); f.Transport.Failure = failure;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Provider.EvidenceAsync(f.Binding, f.Selection, default));
        Assert.Equal(0, f.Transport.Downloads + f.Transport.Writes);
    }
    [Theory]
    [InlineData("http://store.blob.core.windows.net/a")]
    [InlineData("https://user:secret@store.blob.core.windows.net/a")]
    [InlineData("https://store.blob.core.windows.net:444/a")]
    [InlineData("https://evil.example/a")]
    [InlineData("https://127.0.0.1/a")]
    public void SignedDownloadCannotEscapeApprovedHttpsStorage(string uri) => Assert.False(CloudExecutionRequestPolicy.SignedDownload(new Uri(uri), ["store.blob.core.windows.net", "evil.example", "127.0.0.1"]));
    [Fact] public void SignedDownloadRequiresExactHostAndNoWildcards()
    {
        Assert.True(CloudExecutionRequestPolicy.SignedDownload(new("https://store.blob.core.windows.net/archive?sig=synthetic"), ["store.blob.core.windows.net"]));
        Assert.False(CloudExecutionRequestPolicy.SignedDownload(new("https://other.blob.core.windows.net/archive"), ["store.blob.core.windows.net"]));
        Assert.False(CloudExecutionRequestPolicy.SignedDownload(new("https://store.blob.core.windows.net/archive"), ["*.blob.core.windows.net"]));
    }

    [Fact] public async Task AzureExecutionIsRejectedEvenWhenAnAdapterIsCalledDirectly()
    {
        var f = Fixture("AzureDevOps");
        await Assert.ThrowsAsync<CloudExecutionException>(() => f.Provider.QueueAsync(f.Binding, f.Selection, default));
        await Assert.ThrowsAsync<CloudExecutionException>(() => f.Provider.EvidenceAsync(f.Binding, f.Selection, default));
        Assert.False(CloudExecutionRequestPolicy.Allows(f.Binding.Source, HttpMethod.Post, new Uri("https://dev.azure.com/example/" + Project + "/_apis/build/builds?api-version=7.1")));
        Assert.False(CloudExecutionRequestPolicy.Allows(f.Binding.Source, HttpMethod.Get, new Uri("https://dev.azure.com/example/" + Project + "/_apis/build/builds/42/artifacts?artifactName=drop&$format=zip&api-version=7.1"), true));
        Assert.Empty(f.Transport.Calls); Assert.Equal(0, f.Transport.Downloads);
    }
}
