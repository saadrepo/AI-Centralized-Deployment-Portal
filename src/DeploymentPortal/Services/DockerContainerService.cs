using System.Diagnostics;
using System.Text.Json;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.DataProtection;

namespace DeploymentPortal.Services;

public record DockerRunResult(bool Success, string Message, string? ContainerId = null, string? ImageTag = null);

public interface IDockerContainerService
{
    Task<string> ReviewAsync(DockerContainerForm form);
    Task<DockerRunResult> RunAsync(DockerContainerForm form);
}

public class DockerContainerService(IDataProtectionProvider protection) : IDockerContainerService
{
    private readonly IDataProtector protector = protection.CreateProtector("DockerContainerConfirmation.v1");
    private record Confirmation(DateTimeOffset Expires, string SourcePath, string ContainerName, int HostPort, int ContainerPort);

    public async Task<string> ReviewAsync(DockerContainerForm form)
    {
        var path = ValidateSource(form.SourcePath);
        var status = await DockerAsync(TimeSpan.FromSeconds(20), "info", "--format", "{{.ServerVersion}}");
        if (!status.Success) throw new ArgumentException("Docker Engine is unavailable on the dashboard host.");
        if (await ExistsAsync(form.ContainerName)) throw new ArgumentException("A container with this name already exists. Choose another name.");
        return protector.Protect(JsonSerializer.Serialize(new Confirmation(DateTimeOffset.UtcNow.AddMinutes(10), path, form.ContainerName, form.HostPort, form.ContainerPort)));
    }

    public async Task<DockerRunResult> RunAsync(DockerContainerForm form)
    {
        Confirmation? confirmation;
        try { confirmation = JsonSerializer.Deserialize<Confirmation>(protector.Unprotect(form.ConfirmationToken ?? "")); }
        catch { throw new ArgumentException("Review the Docker configuration again."); }
        if (confirmation is null || confirmation.Expires < DateTimeOffset.UtcNow || confirmation.SourcePath != Path.GetFullPath(form.SourcePath) ||
            confirmation.ContainerName != form.ContainerName || confirmation.HostPort != form.HostPort || confirmation.ContainerPort != form.ContainerPort)
            throw new ArgumentException("Confirmation expired or Docker settings changed. Review them again.");

        var path = ValidateSource(form.SourcePath);
        if (await ExistsAsync(form.ContainerName)) throw new ArgumentException("A container with this name already exists. Choose another name.");
        var tag = $"deployhq/{form.ContainerName}:{Guid.NewGuid():N}";
        var build = await DockerAsync(TimeSpan.FromMinutes(15), "build", "--tag", tag, "--file", Path.Combine(path, "Dockerfile"), path);
        if (!build.Success) return new(false, "Image build failed or timed out. Check the Dockerfile and Docker Engine, then retry.");

        var run = await DockerAsync(TimeSpan.FromMinutes(2), "run", "--detach", "--name", form.ContainerName,
            "--publish", $"{form.HostPort}:{form.ContainerPort}", tag);
        if (!run.Success) return new(false, RunFailureMessage(run.Error, form.HostPort, form.ContainerName), ImageTag: tag);
        return new(true, "Container created and started. Check its Docker status and application logs to confirm the app is ready.", run.Output.Trim(), tag);
    }

    private static string ValidateSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source)) throw new ArgumentException("Enter an absolute source folder path on the dashboard host.");
        var path = Path.GetFullPath(source);
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "Dockerfile")))
            throw new ArgumentException("The source folder must exist and contain a Dockerfile at its root.");
        return path;
    }

    private static async Task<bool> ExistsAsync(string name)
    {
        var result = await DockerAsync(TimeSpan.FromSeconds(20), "container", "inspect", "--format", "{{.Id}}", name);
        return result.Success;
    }

    private static string RunFailureMessage(string error, int hostPort, string containerName)
    {
        if (error.Contains("ports are not available", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("bind:", StringComparison.OrdinalIgnoreCase))
            return $"Image built, but host port {hostPort} is unavailable. Choose a different host port. Docker may have left a created container named {containerName}; remove it before retrying with the same name.";
        if (error.Contains("Conflict. The container name", StringComparison.OrdinalIgnoreCase))
            return $"Image built, but a container named {containerName} already exists. Choose another name or remove the existing container.";
        return $"Image built, but Docker could not start the container. Inspect Docker for details. A created container named {containerName} may need to be removed before retrying.";
    }

    private static async Task<(bool Success, string Output, string Error)> DockerAsync(TimeSpan timeout, params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            process.Start();
            var output = args[0] == "build" ? DrainAsync(process.StandardOutput) : process.StandardOutput.ReadToEndAsync();
            var error = DrainAsync(process.StandardError, args[0] == "run");
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            var errorText = await error; // Only classify Docker run errors; never expose raw diagnostics to the browser.
            return (process.ExitCode == 0, await output, errorText);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return (false, "", "");
        }
        catch { return (false, "", ""); }
    }

    private static async Task<string> DrainAsync(TextReader reader, bool capture = false)
    {
        var buffer = new char[8192];
        var result = capture ? new System.Text.StringBuilder() : null;
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
            if (result is not null && result.Length < 4096) result.Append(buffer, 0, Math.Min(count, 4096 - result.Length));
        return result?.ToString() ?? "";
    }
}
