using DeploymentPortal.Services;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class SourceExplorerRequestPolicyTests
{
    private const string User = "https://api.github.com/users/saadrepo/";
    private const string Org = "https://dev.azure.com/example/";
    private const string Project = "11111111-1111-1111-1111-111111111111";
    private const string Repo = "22222222-2222-2222-2222-222222222222";
    [Theory]
    [InlineData("https://api.github.com/user/repos?affiliation=owner%2Ccollaborator&visibility=all&sort=full_name&per_page=100&page=1")]
    [InlineData("https://api.github.com/repos/saadrepo/app")]
    [InlineData("https://api.github.com/repos/saadrepo/app/branches?per_page=100&page=2")]
    [InlineData("https://api.github.com/repos/saadrepo/app/branches/feature%2Fmain")]
    [InlineData("https://api.github.com/repos/saadrepo/app/branches/release%2Fv1.0%2Bmetadata")]
    [InlineData("https://api.github.com/repos/saadrepo/app/actions/runs?branch=feature%2Fmain&per_page=20&page=1")]
    [InlineData("https://api.github.com/repos/saadrepo/app/actions/runs/42")]
    [InlineData("https://api.github.com/repos/saadrepo/app/actions/runs/42/artifacts?per_page=100&page=1")]
    public void GitHubExplicitReadEndpointsAreAllowedOnlyOnExplorerBoundary(string uri)
    {
        Assert.True(SourceExplorerRequestPolicy.Allows(SourceTokenProvider.GitHub, User, new Uri(uri)));
        Assert.False(SourceCredentialProvider.AllowsRequest(SourceTokenProvider.GitHub, User, new Uri(uri)));
    }
    [Theory]
    [InlineData("https://api.github.com/repos/other/app")]
    [InlineData("https://evil.example/repos/saadrepo/app")]
    [InlineData("http://api.github.com/repos/saadrepo/app")]
    [InlineData("https://api.github.com:444/repos/saadrepo/app")]
    [InlineData("https://user:secret@api.github.com/repos/saadrepo/app")]
    [InlineData("https://api.github.com/repos/saadrepo/app#secret")]
    [InlineData("https://api.github.com/repos/saadrepo/app/actions/workflows/42/dispatches")]
    [InlineData("https://api.github.com/repos/saadrepo/app/actions/runs/42/cancel")]
    [InlineData("https://api.github.com/repos/saadrepo/app/actions/artifacts/42/zip")]
    [InlineData("https://api.github.com/repos/saadrepo/app/contents/config")]
    [InlineData("https://api.github.com/repos/saadrepo/app/branches/..%2Fother")]
    [InlineData("https://api.github.com/repos/saadrepo/app/branches/feature%252Fmain")]
    [InlineData("https://api.github.com/repos/saadrepo/app/../other")]
    [InlineData("https://api.github.com/repos/saadrepo/app?token=secret")]
    [InlineData("https://api.github.com/repos/saadrepo/app/branches?per_page=100&page=1&page=2")]
    [InlineData("https://api.github.com/repos/saadrepo/app/branches?per_page=1000&page=1")]
    [InlineData("https://api.github.com/repos/saadrepo/app/branches?per_page=100&page=1&url=https://evil.example")]
    public void GitHubRejectsUnboundOwnersArbitraryHostsPathsQueriesAndMutations(string uri) => Assert.False(SourceExplorerRequestPolicy.Allows(SourceTokenProvider.GitHub, User, new Uri(uri)));

    [Fact]
    public void OrganizationEnumerationCannotUsePersonalAuthenticatedUserEnumeration()
    {
        var destination = "https://api.github.com/orgs/example/";
        Assert.True(SourceExplorerRequestPolicy.Allows(SourceTokenProvider.GitHub, destination, new Uri("https://api.github.com/orgs/example/repos?type=all&sort=full_name&per_page=100&page=1")));
        Assert.False(SourceExplorerRequestPolicy.Allows(SourceTokenProvider.GitHub, destination, new Uri("https://api.github.com/user/repos?affiliation=owner%2Ccollaborator&visibility=all&sort=full_name&per_page=100&page=1")));
        Assert.False(SourceExplorerRequestPolicy.Allows(SourceTokenProvider.GitHub, destination, new Uri("https://api.github.com/orgs/other/repos?type=all&sort=full_name&per_page=100&page=1")));
    }
    [Fact]
    public void AzureAllowsOnlyVersionedProjectGitAndBuildMetadataEndpoints()
    {
        foreach (var path in new[] { "_apis/projects?$top=100", "_apis/projects/" + Project + "?", Project + "/_apis/git/repositories?",
            Project + "/_apis/git/repositories/" + Repo + "?", Project + "/_apis/git/repositories/" + Repo + "/refs?filter=heads%2F&$top=100",
            Project + "/_apis/build/builds?repositoryId=" + Repo + "&repositoryType=TfsGit&branchName=refs%2Fheads%2Fmain&$top=20&queryOrder=queueTimeDescending",
            Project + "/_apis/build/builds/42?", Project + "/_apis/build/builds/42/artifacts?" }) {
            var uri = new Uri(Org + path + "&api-version=7.1");
            Assert.True(SourceExplorerRequestPolicy.Allows(SourceTokenProvider.AzureDevOps, Org, uri), uri.ToString());
        }
        foreach (var uri in new[] {
            Org + Project + "/_apis/build/builds/42/logs?api-version=7.1",
            Org + Project + "/_apis/git/repositories/" + Repo + "/items?api-version=7.1",
            Org + "_apis/projects?api-version=5.0&$top=100",
            "https://dev.azure.com/other/_apis/projects?api-version=7.1&$top=100",
            "https://example.visualstudio.com/_apis/projects?api-version=7.1&$top=100",
            Org + "_apis/projects?api-version=7.1&$top=100&redirect=https://evil.example"
        }) Assert.False(SourceExplorerRequestPolicy.Allows(SourceTokenProvider.AzureDevOps, Org, new Uri(uri)), uri);
    }
}
