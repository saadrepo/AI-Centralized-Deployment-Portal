using DeploymentPortal.Services;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace DeploymentPortal.Tests;

public class DockerFleetTests
{
    private const string ImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static DockerDeploymentForm Form() => new()
    {
        Context = "desktop-linux", Image = "example:1", ContainerName = "example", HostPort = 18080, ContainerPort = 5055,
        EnvironmentVariables = "APP_MODE=test", VolumeName = "example-data", MountPath = "/app/data"
    };

    [Fact]
    public async Task ConfirmationPinsImageAndSettingsBeforeRun()
    {
        var runner = new FakeRunner();
        var service = new DockerFleetService(runner, new EphemeralDataProtectionProvider());
        var form = Form();
        await service.ReviewAsync(form);
        form.HostPort = 18081;
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeployAsync(form));
        Assert.DoesNotContain(runner.Calls, x => x[0] == "run");
    }

    [Fact]
    public async Task NewDeploymentUsesReviewedImagePortEnvironmentAndVolume()
    {
        var runner = new FakeRunner();
        var service = new DockerFleetService(runner, new EphemeralDataProtectionProvider());
        var form = Form();
        await service.ReviewAsync(form);
        var result = await service.DeployAsync(form);
        Assert.True(result.Success);
        var run = Assert.Single(runner.Calls, x => x[0] == "run");
        Assert.Contains("18080:5055", run);
        Assert.Contains("APP_MODE=test", run);
        Assert.Contains("type=volume,source=example-data,target=/app/data", run);
        Assert.Equal(ImageId, run[^1]);
    }

    [Fact]
    public async Task RollbackPreservesCurrentContainerAndStartsPrevious()
    {
        var runner = new FakeRunner { Existing = true };
        var service = new DockerFleetService(runner, new EphemeralDataProtectionProvider());
        var message = await service.RollbackAsync("desktop-linux", "current");
        Assert.Contains("restored and started", message);
        Assert.Contains(runner.Calls, x => x.SequenceEqual(["stop", "current"]));
        Assert.Contains(runner.Calls, x => x[0] == "rename" && x[1] == "current" && x[2].StartsWith("example--replaced--"));
        Assert.Contains(runner.Calls, x => x.SequenceEqual(["rename", "previous", "example"]));
        Assert.Contains(runner.Calls, x => x.SequenceEqual(["start", "previous"]));
    }

    private sealed class FakeRunner : IDockerCommandRunner
    {
        public bool Existing { get; set; }
        public List<string[]> Calls { get; } = [];
        public Task<DockerCommandResult> RunAsync(string context, TimeSpan timeout, params string[] args)
        {
            Calls.Add(args);
            DockerCommandResult result;
            if (args[0] == "context") result = new(true, "{\"Name\":\"desktop-linux\",\"Description\":\"Test\",\"Current\":true}", "");
            else if (args[0] == "image") result = new(true, ImageId, "");
            else if (args[0] == "container" && args[1] == "inspect" && args.Contains("{{json .Config.Labels}}"))
                result = new(true, "{\"deployhq.previous-container\":\"previous\"}", "");
            else if (args[0] == "container" && args[1] == "inspect" && Existing)
            {
                var id = args[^1];
                result = id switch
                {
                    "current" => new(true, "{\"Id\":\"current\",\"Name\":\"/example\",\"State\":{\"Status\":\"running\"},\"Config\":{\"Image\":\"example:2\"}}", ""),
                    "previous" => new(true, "{\"Id\":\"previous\",\"Name\":\"/example--backup\",\"State\":{\"Status\":\"exited\"},\"Config\":{\"Image\":\"example:1\"}}", ""),
                    _ => new(false, "", "not found")
                };
            }
            else if (args[0] == "container" && args[1] == "inspect" && args[^1] == "example")
                result = Calls.Any(x => x[0] == "run")
                    ? new(true, "{\"Id\":\"container-id\",\"Name\":\"/example\",\"State\":{\"Status\":\"running\"},\"Config\":{\"Image\":\"example:1\"}}", "")
                    : new(false, "", "not found");
            else if (args[0] == "container") result = new(false, "", "not found");
            else if (args[0] == "run") result = new(true, "container-id", "");
            else result = new(true, "", "");
            return Task.FromResult(result);
        }
    }
}
