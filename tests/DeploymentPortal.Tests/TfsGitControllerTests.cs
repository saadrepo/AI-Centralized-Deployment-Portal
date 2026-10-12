using DeploymentPortal.Controllers;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeploymentPortal.Tests;

public class TfsGitControllerTests
{
    private const string SensitiveDetails = "password=private-token http://internal-server/private";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryFailureIsDistinctFromEmptyResults(bool fails)
    {
        var controller = Create(new StubService { RepositoryFails = fails, Repositories = [] });
        var result = Assert.IsType<ViewResult>(await controller.Index());
        var model = Assert.IsType<TfsGitPageViewModel>(result.Model);
        var html = await Render(model);

        Assert.Empty(model.Repositories);
        Assert.Equal(fails ? 502 : 200, controller.Response.StatusCode);
        if (fails)
        {
            Assert.Equal("Unable to load repositories. Please try again later.", model.Error);
            Assert.Contains(model.Error!, html);
            Assert.DoesNotContain("No repositories found.", html);
            Assert.DoesNotContain(SensitiveDetails, html);
            Assert.DoesNotContain("private-token", html);
            Assert.DoesNotContain("internal-server", html);
        }
        else
        {
            Assert.Null(model.Error);
            Assert.Contains("No repositories found.", html);
            Assert.DoesNotContain("Unable to load repositories", html);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BranchFailureIsDistinctFromEmptyAndPreservesOtherRepositories(bool fails)
    {
        var controller = Create(new StubService { BranchFails = fails });
        var result = Assert.IsType<ViewResult>(await controller.Index());
        var model = Assert.IsType<TfsGitPageViewModel>(result.Model);
        var html = await Render(model);

        Assert.Null(model.Error);
        Assert.Equal(2, model.Repositories.Count);
        Assert.Equal(fails ? 502 : 200, controller.Response.StatusCode);
        Assert.Empty(model.Repositories[0].Branches!);
        Assert.Single(model.Repositories[1].Branches!);
        Assert.Contains("healthy-branch", html);
        if (fails)
        {
            Assert.Equal("Unable to load branches. Please try again later.", model.Repositories[0].BranchesError);
            Assert.Contains(model.Repositories[0].BranchesError!, html);
            Assert.DoesNotContain("no branches", html);
            Assert.DoesNotContain("private-token", html);
            Assert.DoesNotContain("internal-server", html);
        }
        else
        {
            Assert.Null(model.Repositories[0].BranchesError);
            Assert.Contains("no branches", html);
            Assert.DoesNotContain("Unable to load branches", html);
        }
    }

    [Theory]
    [InlineData("Dev")]
    [InlineData("release/stable")]
    [InlineData("main")]
    public async Task DefaultBranchBadgeUsesActualBranchName(string defaultBranch)
    {
        var controller = Create(new StubService
        {
            Repositories = [new() { Id = "healthy", Name = "Repo", DefaultBranch = "refs/heads/" + defaultBranch }],
            HealthyBranch = defaultBranch
        });
        var result = Assert.IsType<ViewResult>(await controller.Index());
        var model = Assert.IsType<TfsGitPageViewModel>(result.Model);
        Assert.Equal(defaultBranch, Assert.Single(model.Repositories).DefaultBranchName);
        var html = await Render(model);
        Assert.Contains($"class=\"badge bg-success\" title=\"Default branch\">{defaultBranch}</span>", html);
        Assert.DoesNotContain(">Main</span>", html);
    }

    [Fact]
    public async Task BranchNamesAreEncodedAndDefaultMatchingIsCaseSensitive()
    {
        var html = await Render(new TfsGitPageViewModel
        {
            Repositories = [new()
            {
                Name = "<script>private</script>", DefaultBranchName = "Dev",
                Branches = [new() { Name = "dev" }, new() { Name = "<script>branch</script>" }]
            }]
        });
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;branch&lt;/script&gt;", html);
        Assert.DoesNotContain("badge bg-success", html);
    }

    private static TfsGitController Create(ITfsGitService service) => new(service, NullLogger<TfsGitController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private static async Task<string> Render(TfsGitPageViewModel model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TfsGitController).Assembly.GetName().Name
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(TfsGitController).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var context = new ActionContext(new DefaultHttpContext { RequestServices = scope.ServiceProvider },
            new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var engine = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>();
        var view = engine.GetView(null, "/Views/TfsGit/Index.cshtml", false);
        Assert.True(view.Success, string.Join(", ", view.SearchedLocations ?? []));
        using var writer = new StringWriter();
        var viewData = new ViewDataDictionary<TfsGitPageViewModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var tempData = new TempDataDictionary(context.HttpContext, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        await view.View.RenderAsync(new ViewContext(context, view.View, viewData, tempData, writer, new HtmlHelperOptions()));
        return writer.ToString();
    }

    private sealed class StubService : ITfsGitService
    {
        public bool RepositoryFails { get; init; }
        public bool BranchFails { get; init; }
        public string HealthyBranch { get; init; } = "healthy-branch";
        public IReadOnlyList<TfsGitRepositoryViewModel> Repositories { get; init; } =
            [new() { Id = "empty", Name = "Empty" }, new() { Id = "healthy", Name = "Healthy" }];

        public Task<IReadOnlyList<TfsGitRepositoryViewModel>> GetRepositoriesAsync() => RepositoryFails
            ? Task.FromException<IReadOnlyList<TfsGitRepositoryViewModel>>(new HttpRequestException(SensitiveDetails))
            : Task.FromResult(Repositories);

        public Task<IReadOnlyList<TfsGitBranchViewModel>> GetBranchesAsync(string repositoryId) => repositoryId == "empty"
            ? BranchFails
                ? Task.FromException<IReadOnlyList<TfsGitBranchViewModel>>(new HttpRequestException(SensitiveDetails))
                : Task.FromResult<IReadOnlyList<TfsGitBranchViewModel>>([])
            : Task.FromResult<IReadOnlyList<TfsGitBranchViewModel>>([new() { Name = HealthyBranch }]);
    }
}
