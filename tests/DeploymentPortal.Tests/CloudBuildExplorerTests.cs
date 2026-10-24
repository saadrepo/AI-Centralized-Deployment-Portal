using System.Text.Json;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class CloudBuildExplorerTests
{
    private const string Project = "11111111-1111-1111-1111-111111111111";
    private const string Repository = "22222222-2222-2222-2222-222222222222";
    internal static SourceApiPage Page(object body, string? continuation = null, bool more = false) => new(JsonDocument.Parse(JsonSerializer.Serialize(body)), continuation, more);
    private static object Repo(string owner = "saadrepo") => new { id = 10, name = "app", @private = true, owner = new { login = owner } };
    private static object Run(string owner = "saadrepo", string branch = "feature/main") => new {
        id = 42, workflow_id = 7, run_number = 7, head_branch = branch, head_sha = "abc123", status = "completed", conclusion = "success", repository = Repo(owner)
    };
    private static object AzureRepo(string project = Project) => new { id = Repository, name = "app", project = new { id = project } };
    private static object AzureBuild(string project = Project, string branch = "refs/heads/feature/main") => new {
        id = 42, definition = new { id = 7 }, buildNumber = "CI-42", sourceBranch = branch, sourceVersion = "abc123", status = "completed", result = "succeeded",
        project = new { id = project }, repository = new { id = Repository, type = "TfsGit" }
    };
    internal sealed class Sources : IInfrastructureSourceService
    {
        public List<InfrastructureSource> Values = [new() { Id = 1, DisplayName = "Personal", Provider = InfrastructureSourceProvider.GitHub, Destination = "https://api.github.com/users/saadrepo/", CredentialReference = new string('a', 64) }];
        public Task<SourcesPage> ListAsync() => Task.FromResult(new SourcesPage(Values, []));
        public Task<InfrastructureSource?> FindAsync(int id) => Task.FromResult(Values.SingleOrDefault(s => s.Id == id));
        public IReadOnlyList<ConfiguredSource> Configured() => [];
        public IReadOnlyList<SourceCredentialMetadata> Credentials() => throw new Exception("Explorer must not enumerate credential metadata");
        public Task SaveAsync(SourceForm form) => throw new Exception("No mutation allowed");
        public Task<bool> SetEnabledAsync(int id, bool enabled) => throw new Exception("No mutation allowed");
        public Task<bool> DeleteAsync(int id) => throw new Exception("No mutation allowed");
        public Task<bool> TestAsync(int id, CancellationToken token) => throw new Exception("No connection test or mutation allowed");
    }
    internal sealed class Reader(Func<Uri, SourceApiPage> reply) : ISourceApiReader
    {
        public List<Uri> Calls = [];
        public Task<SourceApiPage> ReadJsonAsync(string reference, SourceTokenProvider provider, string destination, Uri requestUri, CancellationToken token)
        {
            Assert.Equal(new string('a', 64), reference);
            Assert.True(SourceExplorerRequestPolicy.Allows(provider, destination, requestUri), requestUri.AbsoluteUri);
            Calls.Add(requestUri); return Task.FromResult(reply(requestUri));
        }
    }
    private static CloudExplorerQuery GitHub(string operation) => new() { Provider = "GitHub", Operation = operation, SourceId = 1, Repository = "app", Branch = "feature/main", BuildId = 42 };
    private static CloudExplorerQuery Azure(string operation) => new() { Provider = "AzureDevOps", Operation = operation, SourceId = 1, Project = Project, Repository = Repository, Branch = "feature/main", BuildId = 42 };
    private static SourceApiPage GitHubReply(Uri uri) => uri.AbsolutePath switch {
        "/user/repos" => Page(new[] { Repo(), Repo("other") }, more: true),
        "/orgs/saadrepo/repos" => Page(new[] { Repo() }),
        "/repos/saadrepo/app" => Page(Repo()),
        "/repos/saadrepo/app/branches" => Page(new[] { new { name = "feature/main" } }, more: true),
        "/repos/saadrepo/app/branches/feature%2Fmain" => Page(new { name = "feature/main" }),
        "/repos/saadrepo/app/actions/workflows" => Page(new { workflows = new[] { new { id = 7, name = "Build only" } } }, more: true),
        "/repos/saadrepo/app/actions/workflows/7" => Page(new { id = 7, name = "Build only" }),
        "/repos/saadrepo/app/actions/workflows/7/runs" => Page(new { workflow_runs = new[] { Run() } }),
        "/repos/saadrepo/app/actions/runs" => Page(new { workflow_runs = new[] { Run() } }, more: true),
        "/repos/saadrepo/app/actions/runs/42" => Page(Run()),
        "/repos/saadrepo/app/actions/runs/42/artifacts" => Page(new { total_count = 1, artifacts = new[] { new { name = "drop", expired = false, size_in_bytes = 123, archive_download_url = "https://evil.example/not-followed" } } }),
        _ => throw new Exception("Unexpected request " + uri)
    };
    private static SourceApiPage AzureReply(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path.EndsWith("/_apis/projects")) return Page(new { value = new[] { new { id = Project, name = "Project" } } }, "opaque+next");
        if (path.EndsWith("/_apis/projects/" + Project)) return Page(new { id = Project });
        if (path.EndsWith("/_apis/git/repositories")) return Page(new { value = new[] { AzureRepo() } });
        if (path.EndsWith("/_apis/git/repositories/" + Repository)) return Page(AzureRepo());
        if (path.EndsWith("/refs")) return Page(new { value = new[] { new { name = "refs/heads/feature/main" } } }, "branch+next");
        if (path.EndsWith("/_apis/build/definitions")) return Page(new { value = new[] { new { id = 7, name = "Build only", project = new { id = Project } } } }, "definitions+next");
        if (path.EndsWith("/_apis/build/definitions/7")) return Page(new { id = 7, project = new { id = Project }, repository = new { id = Repository, type = "TfsGit" } });
        if (path.EndsWith("/_apis/build/builds")) return Page(new { value = new[] { AzureBuild() } }, "build+next");
        if (path.EndsWith("/builds/42")) return Page(AzureBuild());
        if (path.EndsWith("/builds/42/artifacts")) return Page(new { value = new[] { new { name = "drop", resource = new { type = "Container", downloadUrl = "https://evil.example/not-followed" } } } });
        throw new Exception("Unexpected request " + uri);
    }
    private static Sources AzureSources() { var sources = new Sources(); sources.Values[0].Provider = InfrastructureSourceProvider.AzureDevOps; sources.Values[0].Destination = "https://dev.azure.com/example/"; return sources; }

    [Fact]
    public async Task EmptyRegistrationListNeedsNoSelectionOrCredentialAndNeverContactsProvider()
    {
        var sources = new Sources(); sources.Values.Clear(); var reader = new Reader(_ => throw new Exception("No credential/API access during source listing"));
        var result = await new CloudBuildExplorer(sources, reader).BrowseAsync(new CloudExplorerQuery { Provider = "GitHub", Operation = "sources" }, default);
        Assert.Empty(result.Items); Assert.Empty(reader.Calls);
    }
    [Theory]
    [InlineData("repositories")] [InlineData("branches")] [InlineData("builds")]
    public async Task MissingSourceSelectionIsStillRejectedBeforeCredentialAccess(string operation)
    {
        var reader = new Reader(_ => throw new Exception());
        await Assert.ThrowsAsync<SourceExplorerException>(() => new CloudBuildExplorer(new Sources(), reader).BrowseAsync(new CloudExplorerQuery {
            Provider = "GitHub", Operation = operation
        }, default));
        Assert.Empty(reader.Calls);
    }
    [Fact]
    public async Task SourceListFiltersProviderAndDisabledRecordsAndNeverExposesCredentialReference()
    {
        var sources = new Sources();
        sources.Values.Add(new() { Id = 2, Provider = InfrastructureSourceProvider.GitHub, Enabled = false });
        sources.Values.Add(new() { Id = 3, Provider = InfrastructureSourceProvider.AzureDevOps });
        var reader = new Reader(_ => throw new Exception());
        var page = await new CloudBuildExplorer(sources, reader).BrowseAsync(GitHub("sources"), default);
        Assert.Equal("1", Assert.Single(page.Items).Id); Assert.Empty(reader.Calls);
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("CredentialReference", json); Assert.DoesNotContain(new string('a', 64), json);
    }
    [Fact]
    public async Task PersonalPrivateRepositoriesAreFilteredByRegisteredOwnerAndPaginationIsReconstructed()
    {
        var reader = new Reader(GitHubReply); var service = new CloudBuildExplorer(new Sources(), reader);
        var q = GitHub("repositories"); q.Cursor = "2";
        var page = await service.BrowseAsync(q, default);
        Assert.True(Assert.Single(page.Items).Private); Assert.Equal("app", page.Items[0].Name); Assert.Equal("3", page.Next);
        Assert.Equal("/user/repos", Assert.Single(reader.Calls).AbsolutePath);
        Assert.Contains("page=2", reader.Calls[0].Query); Assert.Contains("visibility=all", reader.Calls[0].Query);
    }
    [Fact]
    public async Task OrganizationRepositoriesUseBoundOrganizationEndpoint()
    {
        var sources = new Sources(); sources.Values[0].Destination = "https://api.github.com/orgs/saadrepo/";
        var reader = new Reader(GitHubReply);
        Assert.Single((await new CloudBuildExplorer(sources, reader).BrowseAsync(GitHub("repositories"), default)).Items);
        Assert.Equal("/orgs/saadrepo/repos", Assert.Single(reader.Calls).AbsolutePath);
    }
    [Theory]
    [InlineData("branches")] [InlineData("builds")] [InlineData("artifacts")]
    public async Task GitHubReadsBranchesRunsAndArtifactMetadataWithinScope(string operation)
    {
        var reader = new Reader(GitHubReply);
        var page = await new CloudBuildExplorer(new Sources(), reader).BrowseAsync(GitHub(operation), default);
        if (operation == "branches") { Assert.Equal("feature/main", Assert.Single(page.Items).Name); Assert.Equal("2", page.Next); }
        if (operation == "builds") { var build = Assert.Single(page.Builds); Assert.Equal("7", build.Number); Assert.Equal("success", build.Result); Assert.True(build.HasArtifacts); Assert.Equal("2", page.Next); }
        if (operation == "artifacts") { var artifact = Assert.Single(page.Artifacts); Assert.Equal("drop", artifact.Name); Assert.Equal(123, artifact.Size); Assert.DoesNotContain("evil", JsonSerializer.Serialize(page)); }
        Assert.All(reader.Calls, uri => Assert.Equal("api.github.com", uri.Host));
    }
    [Theory]
    [InlineData("projects")] [InlineData("repositories")] [InlineData("branches")] [InlineData("builds")] [InlineData("artifacts")]
    public async Task AzureProjectsRepositoriesBranchesBuildsAndArtifactsUseBoundOrganization(string operation)
    {
        var reader = new Reader(AzureReply);
        var page = await new CloudBuildExplorer(AzureSources(), reader).BrowseAsync(Azure(operation), default);
        if (operation == "projects") { Assert.Equal(Project, Assert.Single(page.Items).Id); Assert.Equal("opaque+next", page.Next); }
        if (operation == "repositories") Assert.Equal(Repository, Assert.Single(page.Items).Id);
        if (operation == "branches") { Assert.Equal("feature/main", Assert.Single(page.Items).Id); Assert.Equal("branch+next", page.Next); }
        if (operation == "builds") { Assert.Equal("CI-42", Assert.Single(page.Builds).Number); Assert.True(page.Builds[0].HasArtifacts); Assert.Equal("build+next", page.Next); }
        if (operation == "artifacts") { Assert.Equal("Container", Assert.Single(page.Artifacts).Type); Assert.DoesNotContain("evil", JsonSerializer.Serialize(page)); }
        Assert.All(reader.Calls, uri => Assert.StartsWith("https://dev.azure.com/example/", uri.AbsoluteUri));
    }
    [Fact]
    public async Task AzureContinuationIsDataNeverAnOutboundUrl()
    {
        var reader = new Reader(AzureReply); var q = Azure("projects"); q.Cursor = "https://evil.example/?secret=x";
        await new CloudBuildExplorer(AzureSources(), reader).BrowseAsync(q, default);
        var uri = Assert.Single(reader.Calls); Assert.Equal("dev.azure.com", uri.Host);
        Assert.Contains("continuationToken=https%3A", uri.Query);
    }
    [Theory]
    [InlineData("missing")] [InlineData("disabled")] [InlineData("provider")]
    public async Task InvalidOrDisabledSourcesNeverReachTransport(string failure)
    {
        var sources = new Sources(); var reader = new Reader(GitHubReply); var query = GitHub("repositories");
        if (failure == "missing") query.SourceId = 99;
        if (failure == "disabled") sources.Values[0].Enabled = false;
        if (failure == "provider") query.Provider = "AzureDevOps";
        await Assert.ThrowsAsync<SourceExplorerException>(() => new CloudBuildExplorer(sources, reader).BrowseAsync(query, default)); Assert.Empty(reader.Calls);
    }
    [Theory]
    [InlineData("owner")] [InlineData("branch")]
    public async Task ForeignRunCannotLeakBuildsOrRequestArtifacts(string failure)
    {
        var reader = new Reader(uri => uri.AbsolutePath.EndsWith("/actions/runs")
            ? Page(new { workflow_runs = new[] { failure == "owner" ? Run("other") : Run(branch: "other") } }) : GitHubReply(uri));
        await Assert.ThrowsAsync<SourceExplorerException>(() => new CloudBuildExplorer(new Sources(), reader).BrowseAsync(GitHub("builds"), default));
        Assert.DoesNotContain(reader.Calls, uri => uri.AbsolutePath.EndsWith("/artifacts"));
    }
    [Fact]
    public async Task ForeignAzureProjectInRepositoryFailsBeforeBranchOrBuildRequest()
    {
        var reader = new Reader(uri => uri.AbsolutePath.EndsWith("/repositories/" + Repository) ? Page(AzureRepo("33333333-3333-3333-3333-333333333333")) : AzureReply(uri));
        await Assert.ThrowsAsync<SourceExplorerException>(() => new CloudBuildExplorer(AzureSources(), reader).BrowseAsync(Azure("builds"), default));
        Assert.DoesNotContain(reader.Calls, uri => uri.AbsolutePath.EndsWith("/refs") || uri.AbsolutePath.EndsWith("/builds"));
    }
    [Theory]
    [InlineData("../other")] [InlineData("https://evil.example")]
    public async Task InvalidRepositoryNamesCannotReachTransport(string name)
    {
        var reader = new Reader(GitHubReply); var q = GitHub("branches"); q.Repository = name;
        await Assert.ThrowsAsync<SourceExplorerException>(() => new CloudBuildExplorer(new Sources(), reader).BrowseAsync(q, default)); Assert.Empty(reader.Calls);
    }

    [Theory]
    [InlineData("GitHub", "definitions")]
    [InlineData("GitHub", "builds")]
    [InlineData("GitHub", "status")]
    [InlineData("GitHub", "artifacts")]
    [InlineData("AzureDevOps", "definitions")]
    [InlineData("AzureDevOps", "builds")]
    [InlineData("AzureDevOps", "status")]
    [InlineData("AzureDevOps", "artifacts")]
    public async Task PipelineReadsAreBoundToRepositoryBranchAndSelectedDefinition(string provider, string operation)
    {
        var github = provider == "GitHub";
        var query = github ? GitHub(operation) : Azure(operation); query.DefinitionId = 7;
        var reader = new Reader(github ? GitHubReply : AzureReply);
        var result = await new CloudBuildExplorer(github ? new Sources() : AzureSources(), reader).BrowseAsync(query, default);
        if (operation == "definitions") Assert.Equal("7", Assert.Single(result.Items).Id);
        if (operation is "builds" or "status") Assert.Equal(42, Assert.Single(result.Builds).Id);
        if (operation == "status") Assert.DoesNotContain(reader.Calls, u => u.AbsolutePath.EndsWith("/artifacts"));
        if (operation == "builds") Assert.Contains(reader.Calls, u => github ? u.AbsolutePath.EndsWith("/workflows/7/runs") : u.Query.Contains("definitions=7"));
        Assert.All(reader.Calls, u => Assert.DoesNotContain("dispatch", u.AbsoluteUri));
    }

    [Theory]
    [InlineData("GitHub", "status")]
    [InlineData("GitHub", "artifacts")]
    [InlineData("AzureDevOps", "status")]
    [InlineData("AzureDevOps", "artifacts")]
    public async Task RunFromDifferentDefinitionIsRejectedBeforeArtifactAccess(string provider, string operation)
    {
        var github = provider == "GitHub"; var query = github ? GitHub(operation) : Azure(operation); query.DefinitionId = 7;
        var reader = new Reader(uri => {
            if (uri.AbsolutePath.EndsWith("/runs/42") || uri.AbsolutePath.EndsWith("/builds/42")) {
                var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(github ? Run() : AzureBuild()))!;
                if (github) json["workflow_id"] = 8; else json["definition"]!["id"] = 8;
                return Page(json);
            }
            return github ? GitHubReply(uri) : AzureReply(uri);
        });
        await Assert.ThrowsAsync<SourceExplorerException>(() => new CloudBuildExplorer(github ? new Sources() : AzureSources(), reader).BrowseAsync(query, default));
        Assert.DoesNotContain(reader.Calls, u => u.AbsolutePath.EndsWith("/artifacts"));
    }

    [Fact]
    public async Task AzureDefinitionFromAnotherRepositoryCannotBeSelected()
    {
        var query = Azure("builds"); query.DefinitionId = 7;
        var reader = new Reader(u => u.AbsolutePath.EndsWith("/definitions/7")
            ? Page(new { id = 7, project = new { id = Project }, repository = new { id = "33333333-3333-3333-3333-333333333333", type = "TfsGit" } }) : AzureReply(u));
        await Assert.ThrowsAsync<SourceExplorerException>(() => new CloudBuildExplorer(AzureSources(), reader).BrowseAsync(query, default));
        Assert.DoesNotContain(reader.Calls, u => u.AbsolutePath.EndsWith("/builds"));
    }

    [Theory]
    [InlineData("GitHub")]
    [InlineData("AzureDevOps")]
    public async Task TransferredBranchMustExistBeforePipelineDefinitionsAreRead(string provider)
    {
        var github = provider == "GitHub"; var query = github ? GitHub("definitions") : Azure("definitions");
        query.Branch = "deleted-branch";
        // Return the selected repository's actual branch, never trust the URL hint.
        var validatingReader = new Reader(u => github && u.AbsolutePath.Contains("/branches/") ? Page(new { name = "feature/main" }) : (github ? GitHubReply(u) : AzureReply(u)));
        await Assert.ThrowsAsync<SourceExplorerException>(() => new CloudBuildExplorer(github ? new Sources() : AzureSources(), validatingReader).BrowseAsync(query, default));
        Assert.DoesNotContain(validatingReader.Calls, u => u.AbsolutePath.EndsWith("/workflows") || u.AbsolutePath.EndsWith("/definitions"));
    }
}
