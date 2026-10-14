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

public class TfsGitBuildsControllerTests
{
    [Fact]
    public async Task ApplicationSelectorAndArtifactLinksRetainDemoScope()
    {
        var builds = await Render("Index", new TfsGitCiBuildsPageViewModel { ApplicationKey = "Demo", Applications = [new() { Key="Portal", DisplayName="Portal", TfsGitCiDefinitionId=1 }, new() { Key="Demo", DisplayName="Demo", TfsGitCiDefinitionId=2 }], Builds=[StubService.Build] });
        Assert.Contains("name=\"applicationKey\"", builds);
        Assert.Contains("value=\"Demo\" selected=\"selected\"", builds);
        Assert.Contains("definition 2", builds);
        Assert.Contains("applicationKey=Demo", builds);
        AssertReadOnly(builds);
        var artifacts = await Render("Artifacts", new TfsGitCiArtifactsPageViewModel { ApplicationKey="Demo", Build=StubService.Build, Artifacts=[new() { Name="drop",Resource=new() { Type="Container" } }] });
        Assert.Contains("/TfsArtifacts", artifacts); Assert.Contains("/TfsCd/Prepare", artifacts);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(artifacts, "applicationKey=Demo").Count >= 4);
        AssertReadOnly(artifacts);
    }
    [Fact]
    public async Task RecentBuildsRenderRequiredFieldsAndArtifactSelectionOnly()
    {
        var service = new StubService();
        var controller = Create(service);
        var result = Assert.IsType<ViewResult>(await controller.Index());
        var model = Assert.IsType<TfsGitCiBuildsPageViewModel>(result.Model);
        var html = await Render("Index", model);
        Assert.Single(model.Builds);
        Assert.Equal(200, controller.Response.StatusCode);
        Assert.Contains("CI-42", html);
        Assert.Contains("Dev", html);
        Assert.Contains("abcdef123456", html);
        Assert.Contains("completed", html);
        Assert.Contains("succeeded", html);
        Assert.Contains("View artifacts", html);
        Assert.Contains("/TfsGitBuilds/Artifacts/42", html);
        Assert.Equal(0, service.BuildRequests);
        Assert.Equal(0, service.ArtifactRequests);
        AssertReadOnly(html);
    }

    [Fact]
    public async Task EmptyBuildsRenderEmptyStateWithoutFailure()
    {
        var controller = Create(new StubService { Builds = [] });
        var result = Assert.IsType<ViewResult>(await controller.Index());
        var model = Assert.IsType<TfsGitCiBuildsPageViewModel>(result.Model);
        var html = await Render("Index", model);
        Assert.Null(model.Error);
        Assert.Equal(200, controller.Response.StatusCode);
        Assert.Contains("No CI builds found for Dev.", html);
        Assert.DoesNotContain("Unable to load CI builds", html);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("build")]
    [InlineData("artifacts")]
    public async Task AuthenticationFailuresRenderSafeErrorsInsteadOfEmptyStates(string stage)
    {
        var controller = Create(new StubService { FailureStage = stage });
        var result = Assert.IsType<ViewResult>(stage == "list" ? await controller.Index() : await controller.Artifacts(42));
        string html;
        if (stage == "list")
        {
            var model = Assert.IsType<TfsGitCiBuildsPageViewModel>(result.Model);
            Assert.NotNull(model.Error);
            html = await Render("Index", model);
        }
        else
        {
            var model = Assert.IsType<TfsGitCiArtifactsPageViewModel>(result.Model);
            if (stage == "build")
            {
                Assert.NotNull(model.Error);
                Assert.Null(model.Build);
            }
            else
            {
                Assert.NotNull(model.ArtifactsError);
                Assert.Equal("CI-42", model.Build?.BuildNumber);
            }
            html = await Render("Artifacts", model);
        }
        Assert.Equal(502, controller.Response.StatusCode);
        Assert.Contains("Unable to load", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.DoesNotContain("private-token", html);
        Assert.DoesNotContain("internal-server", html);
        Assert.DoesNotContain("No CI builds", html);
        Assert.DoesNotContain("No artifacts are available", html);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidBuildIdsDoNotCallService(int id)
    {
        var service = new StubService();
        var controller = Create(service);
        var result = Assert.IsType<ViewResult>(await controller.Artifacts(id));
        Assert.NotNull(Assert.IsType<TfsGitCiArtifactsPageViewModel>(result.Model).Error);
        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Equal(0, service.BuildRequests);
        Assert.Equal(0, service.ArtifactRequests);
    }

    [Fact]
    public async Task InvalidModelStateDoesNotCallService()
    {
        var service = new StubService();
        var controller = Create(service);
        controller.ModelState.AddModelError("id", "Invalid ID");
        await controller.Artifacts(42);
        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Equal(0, service.BuildRequests);
        Assert.Equal(0, service.ArtifactRequests);
    }

    [Fact]
    public async Task MissingOrOutOfScopeBuildDoesNotRequestArtifacts()
    {
        var service = new StubService { MissingBuild = true };
        var controller = Create(service);
        var result = Assert.IsType<ViewResult>(await controller.Artifacts(42));
        var model = Assert.IsType<TfsGitCiArtifactsPageViewModel>(result.Model);
        Assert.Null(model.Build);
        Assert.NotNull(model.Error);
        Assert.Equal(404, controller.Response.StatusCode);
        Assert.Equal(1, service.BuildRequests);
        Assert.Equal(0, service.ArtifactRequests);
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("other")]
    [InlineData(null)]
    public async Task ArtifactViewsHandleDropMissingDropAndEmptyResults(string? name)
    {
        var service = new StubService
        {
            Artifacts = name is null ? [] : [new() { Name = name, Resource = new() { Type = "Container" } }]
        };
        var controller = Create(service);
        var result = Assert.IsType<ViewResult>(await controller.Artifacts(42));
        var model = Assert.IsType<TfsGitCiArtifactsPageViewModel>(result.Model);
        var html = await Render("Artifacts", model);
        Assert.Equal(42, service.SelectedId);
        Assert.Equal("CI-42", model.Build?.BuildNumber);
        Assert.Equal(200, controller.Response.StatusCode);
        Assert.Null(model.ArtifactsError);
        if (name == "drop")
        {
            Assert.Contains("drop", html);
            Assert.Contains("CI artifact", html);
            Assert.Contains("Container", html);
            Assert.DoesNotContain("not available for this build", html);
        }
        else if (name is null)
            Assert.Contains("No artifacts are available for this build yet.", html);
        else
        {
            Assert.Contains("other", html);
            Assert.Contains("The drop artifact is not available for this build.", html);
        }
        AssertReadOnly(html);
    }

    [Fact]
    public async Task UpstreamNamesAreHtmlEncodedAndMissingResultHasPlaceholder()
    {
        var builds = await Render("Index", new TfsGitCiBuildsPageViewModel
        {
            Builds = [new() { Id = 42, BuildNumber = "<script>build</script>", SourceVersion = "<script>commit</script>" }]
        });
        var artifacts = await Render("Artifacts", new TfsGitCiArtifactsPageViewModel
        {
            Build = StubService.Build,
            Artifacts = [new() { Name = "<script>artifact</script>", Resource = null }]
        });
        Assert.DoesNotContain("<script>", builds);
        Assert.DoesNotContain("<script>", artifacts);
        Assert.Contains("&lt;script&gt;build&lt;/script&gt;", builds);
        Assert.Contains("&lt;script&gt;artifact&lt;/script&gt;", artifacts);
        Assert.Contains("Not available yet", builds);
    }

    private static void AssertReadOnly(string html)
    {
        Assert.DoesNotContain("method=\"post\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("download", html, StringComparison.OrdinalIgnoreCase);
        // Preparation navigation is allowed; this metadata page still has no submit/approval boundary.
        Assert.DoesNotContain("/Deployments/Create", html);
        Assert.DoesNotContain("Confirm", html);
        Assert.DoesNotContain("Queue", html);
        Assert.DoesNotContain("<script", html);
    }

    private static TfsGitBuildsController Create(ITfsGitBuildService service) => new(service, NullLogger<TfsGitBuildsController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private static async Task<string> Render<T>(string action, T model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TfsGitBuildsController).Assembly.GetName().Name
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(TfsGitBuildsController).Assembly);
        await using var app = builder.Build();
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        // Register the conventional endpoints with LinkGenerator without starting a web listener.
        app.UseRouting();
        app.UseEndpoints(_ => { });
        using var scope = app.Services.CreateScope();
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "View rendering"));
        var routeData = new RouteData();
        routeData.Values["controller"] = "TfsGitBuilds";
        routeData.Values["action"] = action;
        var context = new ActionContext(httpContext, routeData, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var engine = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>();
        var view = engine.GetView(null, $"/Views/TfsGitBuilds/{action}.cshtml", false);
        Assert.True(view.Success, string.Join(", ", view.SearchedLocations ?? []));
        using var writer = new StringWriter();
        var viewData = new ViewDataDictionary<T>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var tempData = new TempDataDictionary(httpContext, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        await view.View.RenderAsync(new ViewContext(context, view.View, viewData, tempData, writer, new HtmlHelperOptions()));
        return writer.ToString();
    }

    private sealed class StubService : ITfsGitBuildService
    {
        public static TfsGitCiBuildViewModel Build => new()
        {
            Id = 42, BuildNumber = "CI-42", SourceBranch = "refs/heads/Dev", SourceVersion = "abcdef123456",
            Status = "completed", Result = "succeeded", Definition = new() { Id = 1 }
        };
        public IReadOnlyList<TfsGitCiBuildViewModel> Builds { get; init; } = [Build];
        public IReadOnlyList<TfsGitCiArtifactViewModel> Artifacts { get; init; } = [];
        public string? FailureStage { get; init; }
        public bool MissingBuild { get; init; }
        public int BuildRequests { get; private set; }
        public int ArtifactRequests { get; private set; }
        public int SelectedId { get; private set; }

        public Task<IReadOnlyList<TfsGitCiBuildViewModel>> GetRecentBuildsAsync() => FailureStage == "list"
            ? Task.FromException<IReadOnlyList<TfsGitCiBuildViewModel>>(Failure()) : Task.FromResult(Builds);

        public Task<TfsGitCiBuildViewModel?> GetBuildAsync(int buildId)
        {
            BuildRequests++;
            SelectedId = buildId;
            return FailureStage == "build" ? Task.FromException<TfsGitCiBuildViewModel?>(Failure())
                : Task.FromResult<TfsGitCiBuildViewModel?>(MissingBuild ? null : Build);
        }

        public Task<IReadOnlyList<TfsGitCiArtifactViewModel>> GetArtifactsAsync(int buildId)
        {
            ArtifactRequests++;
            SelectedId = buildId;
            return FailureStage == "artifacts" ? Task.FromException<IReadOnlyList<TfsGitCiArtifactViewModel>>(Failure()) : Task.FromResult(Artifacts);
        }

        private static Exception Failure() => new HttpRequestException("password=private-token http://internal-server/private", null, System.Net.HttpStatusCode.Unauthorized);
    }
}
