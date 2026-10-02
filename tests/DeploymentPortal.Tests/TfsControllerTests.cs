using System.Net;
using DeploymentPortal.Controllers;
using DeploymentPortal.Services;
using DeploymentPortal.ViewModels.Tfs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeploymentPortal.Tests;

public class TfsControllerTests
{
    [Fact]
    public async Task SearchIsForwardedAndPreservedOnFailure()
    {
        var service = new StubService();
        var controller = CreateController(service);
        Assert.IsType<ViewResult>(await controller.Index("  PMS  "));
        Assert.Equal("PMS", service.Search);
        Assert.Equal("PMS", controller.ViewData["Search"]);
        Assert.NotNull(controller.ViewData["TfsError"]);
        Assert.Equal(502, controller.Response.StatusCode);
    }
    [Fact]
    public async Task HistoryFailurePreservesDefinition()
    {
        var controller = CreateController(new StubService { HistoryFails = true });
        var view = Assert.IsType<ViewResult>(await controller.Details(42));
        var model = Assert.IsType<TfsPipelineDetailsViewModel>(view.Model);
        Assert.Equal(42, model.Definition?.Id);
        Assert.NotNull(model.BuildsError);
        Assert.Null(model.Error);
        Assert.Equal(502, controller.Response.StatusCode);
    }

    [Fact]
    public async Task MissingDefinitionShowsFriendlyErrorWithoutLoadingBuilds()
    {
        var service = new StubService { MissingDefinition = true };
        var controller = CreateController(service);
        var view = Assert.IsType<ViewResult>(await controller.Details(42));
        var model = Assert.IsType<TfsPipelineDetailsViewModel>(view.Model);
        Assert.NotNull(model.Error);
        Assert.False(service.HistoryRequested);
        Assert.Equal(404, controller.Response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidIdDoesNotCallTfs(int id)
    {
        var service = new StubService();
        var controller = CreateController(service);
        Assert.IsType<ViewResult>(await controller.Details(id));
        Assert.False(service.DefinitionRequested);
        Assert.Equal(400, controller.Response.StatusCode);
    }

    private static TfsController CreateController(ITfsService service) => new(service, NullLogger<TfsController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private sealed class StubService : ITfsService
    {
        public bool HistoryFails { get; init; }
        public bool MissingDefinition { get; init; }
        public bool DefinitionRequested { get; private set; }
        public bool HistoryRequested { get; private set; }
        public string? Search { get; private set; }
        public Task<TfsBuildDetailsViewModel> GetBuildAsync(int buildId) => throw new NotSupportedException();
        public Task<IReadOnlyList<TfsBuildLogViewModel>> GetBuildLogsAsync(int buildId) => throw new NotSupportedException();
        public Task<string> GetBuildLogAsync(int buildId, int logId, int startLine = 0) => throw new NotSupportedException();
        public Task<IReadOnlyList<TfsBuildArtifactViewModel>> GetBuildArtifactsAsync(int buildId) => throw new NotSupportedException();
        public Task<TfsRunBuildViewModel> ReviewBuildAsync(int definitionId, string sourceVersion) => throw new NotSupportedException();
        public Task<TfsRunBuildViewModel> ReviewExistingBuildAsync(int buildId) => throw new NotSupportedException();
        public Task<TfsRunBuildViewModel> ReviewLatestCiBuildAsync(int definitionId) => throw new NotSupportedException();
        public Task<TfsBuildDetailsViewModel> QueueBuildAsync(string confirmationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TfsPipelineViewModel>> GetBuildDefinitionsAsync(string? search = null)
        {
            Search = search;
            throw new HttpRequestException("Unavailable");
        }
        public Task<TfsDefinitionViewModel> GetBuildDefinitionAsync(int definitionId)
        {
            DefinitionRequested = true;
            if (MissingDefinition) throw new HttpRequestException("Missing", null, HttpStatusCode.NotFound);
            return Task.FromResult(new TfsDefinitionViewModel { Id = definitionId, Name = "PMSNZ CI" });
        }
        public Task<IReadOnlyList<TfsBuildViewModel>> GetLatestBuildsAsync(int definitionId)
        {
            HistoryRequested = true;
            if (HistoryFails) throw new HttpRequestException("Unavailable");
            return Task.FromResult<IReadOnlyList<TfsBuildViewModel>>([]);
        }
    }
}
