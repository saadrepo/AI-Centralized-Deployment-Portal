using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using DeploymentPortal.ViewModels;
using Microsoft.AspNetCore.DataProtection;

namespace DeploymentPortal.Services;

public record DockerCommandResult(bool Success, string Output, string Error);
public interface IDockerCommandRunner
{
    Task<DockerCommandResult> RunAsync(string context, TimeSpan timeout, params string[] args);
}

public class DockerCommandRunner : IDockerCommandRunner
{
    public async Task<DockerCommandResult> RunAsync(string context, TimeSpan timeout, params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("docker")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        if (!string.IsNullOrEmpty(context)) { process.StartInfo.ArgumentList.Add("--context"); process.StartInfo.ArgumentList.Add(context); }
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            process.Start();
            var stdout = ReadLimitedAsync(process.StandardOutput, 131072);
            var stderr = ReadLimitedAsync(process.StandardError, 65536);
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            return new(process.ExitCode == 0, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return new(false, "", "Docker operation timed out.");
        }
        catch { return new(false, "", "Docker CLI could not be started."); }
    }

    private static async Task<string> ReadLimitedAsync(TextReader reader, int limit)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < limit) result.Append(buffer, 0, Math.Min(count, limit - result.Length));
        return result.ToString();
    }
}

public interface IDockerFleetService
{
    Task<DockerDashboardViewModel> DashboardAsync(string? context);
    Task<DockerDeploymentReview> ReviewAsync(DockerDeploymentForm form);
    Task<DockerRunResult> DeployAsync(DockerDeploymentForm form);
    Task<string> ActionAsync(string context, string id, string action);
    Task<(DockerContainerInfo Container, string Logs)> LogsAsync(string context, string id);
    Task<string> RollbackAsync(string context, string id);
}

public class DockerFleetService(IDockerCommandRunner docker, IDataProtectionProvider protection) : IDockerFleetService
{
    private readonly IDataProtector protector = protection.CreateProtector("DockerFleetDeployment.v1");
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(25);
    private record Confirmation(DateTimeOffset Expires, string Request, string ImageId, string? ExistingId);
    private static string Request(DockerDeploymentForm f) => JsonSerializer.Serialize(new
    { f.Context, f.Image, f.ContainerName, f.HostPort, f.ContainerPort, f.EnvironmentVariables, f.VolumeName, f.MountPath });

    public async Task<IReadOnlyList<DockerContextInfo>> ContextsAsync()
    {
        var result = await docker.RunAsync("", Quick, "context", "ls", "--format", "{{json .}}");
        if (!result.Success) throw new ArgumentException("Docker contexts could not be loaded. Check Docker CLI access on the dashboard host.");
        return JsonLines(result.Output).Select(x => new DockerContextInfo(
            Get(x, "Name"), Get(x, "Description"), x.TryGetProperty("Current", out var current) && current.ValueKind == JsonValueKind.True)).ToList();
    }

    private async Task<string> ContextAsync(string? name)
    {
        var contexts = await ContextsAsync();
        var selected = string.IsNullOrWhiteSpace(name) ? contexts.FirstOrDefault(x => x.Current)?.Name : name;
        if (selected is null || !contexts.Any(x => x.Name == selected)) throw new ArgumentException("Select an available Docker server context.");
        return selected;
    }

    public async Task<DockerDashboardViewModel> DashboardAsync(string? context)
    {
        var contexts = await ContextsAsync();
        var selected = string.IsNullOrWhiteSpace(context) ? contexts.FirstOrDefault(x => x.Current)?.Name : context;
        if (selected is null || !contexts.Any(x => x.Name == selected)) throw new ArgumentException("Select an available Docker server context.");
        var containers = await docker.RunAsync(selected, Quick, "ps", "--all", "--no-trunc", "--format", "{{json .}}");
        var images = await docker.RunAsync(selected, Quick, "image", "ls", "--no-trunc", "--format", "{{json .}}");
        if (!containers.Success || !images.Success)
            return new(contexts, selected, [], [], "Could not connect to this Docker server. Check its context and Docker Engine.");
        var containerRows = JsonLines(containers.Output).Select(x => new DockerContainerInfo(Get(x, "ID"), Get(x, "Names"), Get(x, "Image"), Get(x, "Status"), Get(x, "Ports"),
            Get(x, "Labels").Contains("deployhq.previous-container=", StringComparison.Ordinal))).ToList();
        var imageRows = JsonLines(images.Output).Select(x => new DockerImageInfo(Get(x, "Repository") + ":" + Get(x, "Tag"), Get(x, "ID"), Get(x, "Size")))
            .Where(x => !x.Name.Contains("<none>")).ToList();
        return new(contexts, selected, containerRows, imageRows);
    }

    public async Task<DockerDeploymentReview> ReviewAsync(DockerDeploymentForm form)
    {
        Validate(form);
        var context = await ContextAsync(form.Context);
        var imageId = await ImageIdAsync(context, form.Image);
        var existing = await ContainerAsync(context, form.ContainerName);
        form.ConfirmationToken = protector.Protect(JsonSerializer.Serialize(new Confirmation(DateTimeOffset.UtcNow.AddMinutes(10), Request(form), imageId, existing?.Id)));
        return new(form, imageId, existing?.Id);
    }

    public async Task<DockerRunResult> DeployAsync(DockerDeploymentForm form)
    {
        Validate(form);
        Confirmation? confirmation;
        try { confirmation = JsonSerializer.Deserialize<Confirmation>(protector.Unprotect(form.ConfirmationToken ?? "")); }
        catch { throw new ArgumentException("Review and confirm this Docker deployment again."); }
        if (confirmation is null || confirmation.Expires < DateTimeOffset.UtcNow || confirmation.Request != Request(form))
            throw new ArgumentException("Confirmation expired or deployment settings changed. Review again.");
        var context = await ContextAsync(form.Context);
        if (confirmation.ImageId != await ImageIdAsync(context, form.Image)) throw new ArgumentException("The selected image changed. Review again.");
        var existing = await ContainerAsync(context, form.ContainerName);
        if (existing?.Id != confirmation.ExistingId) throw new ArgumentException("The container changed since review. Review again.");
        var backup = existing is null ? null : $"{form.ContainerName}--backup--{Guid.NewGuid():N}";
        if (existing is not null)
        {
            var stop = await docker.RunAsync(context, Quick, "stop", existing.Id);
            if (!stop.Success && existing.Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase))
                return new(false, "Existing container could not be stopped. It was not replaced.");
            var rename = await docker.RunAsync(context, Quick, "rename", existing.Id, backup!);
            if (!rename.Success) { if (stop.Success) await docker.RunAsync(context, Quick, "start", existing.Id); return new(false, "Existing container could not be preserved for rollback."); }
        }
        var args = new List<string> { "run", "--detach", "--name", form.ContainerName,
            "--label", "deployhq.managed=true", "--publish", $"{form.HostPort}:{form.ContainerPort}" };
        if (backup is not null) { args.Add("--label"); args.Add($"deployhq.previous-container={backup}"); }
        foreach (var entry in EnvironmentEntries(form.EnvironmentVariables)) { args.Add("--env"); args.Add(entry); }
        if (!string.IsNullOrWhiteSpace(form.VolumeName))
        { args.Add("--mount"); args.Add($"type=volume,source={form.VolumeName},target={form.MountPath}"); }
        args.Add(confirmation.ImageId);
        var run = await docker.RunAsync(context, TimeSpan.FromMinutes(2), args.ToArray());
        var started = run.Success ? await ContainerAsync(context, form.ContainerName) : null;
        if (!run.Success || started is null || !started.Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase))
        {
            // Docker can leave a created container after a failed port bind. Remove only the one using our reviewed image.
            var failed = await ContainerAsync(context, form.ContainerName);
            if (failed is not null && await ContainerImageIdAsync(context, failed.Id) == confirmation.ImageId && await IsManagedAsync(context, failed.Id))
                await docker.RunAsync(context, Quick, "rm", "--force", failed.Id);
            if (backup is not null)
            {
                await docker.RunAsync(context, Quick, "rename", backup, form.ContainerName);
                if (existing!.Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase)) await docker.RunAsync(context, Quick, "start", form.ContainerName);
            }
            var detail = run.Error.Contains("ports are not available", StringComparison.OrdinalIgnoreCase) || run.Error.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase) || run.Error.Contains("bind:", StringComparison.OrdinalIgnoreCase)
                ? $"Host port {form.HostPort} is unavailable." : run.Success ? "The new container exited immediately." : "Docker could not start the new container.";
            return new(false, $"{detail} Previous container restoration was attempted. Check the Docker server before retrying.", ImageTag: form.Image);
        }
        return new(true, backup is null ? "Container deployed and started." : $"Container deployed. Previous version is retained as {backup} for rollback.", run.Output.Trim(), form.Image);
    }

    public async Task<string> ActionAsync(string context, string id, string action)
    {
        context = await ContextAsync(context);
        if (action is not ("start" or "stop" or "restart" or "delete")) throw new ArgumentException("Unsupported container action.");
        var container = await ContainerAsync(context, id) ?? throw new ArgumentException("Container no longer exists.");
        var command = action == "delete" ? new[] { "rm", "--force", container.Id } : new[] { action, container.Id };
        var result = await docker.RunAsync(context, Quick, command);
        if (!result.Success) throw new ArgumentException($"Docker could not {action} this container. Check its current state on the selected server.");
        return action == "delete" ? "Container deleted. Its image and volumes were retained." : $"Container {action} completed.";
    }

    public async Task<(DockerContainerInfo Container, string Logs)> LogsAsync(string context, string id)
    {
        context = await ContextAsync(context);
        var container = await ContainerAsync(context, id) ?? throw new ArgumentException("Container no longer exists.");
        var result = await docker.RunAsync(context, Quick, "logs", "--tail", "200", "--timestamps", container.Id);
        if (!result.Success) throw new ArgumentException("Could not read container logs.");
        return (container, (result.Output + result.Error)[..Math.Min((result.Output + result.Error).Length, 100000)]);
    }

    public async Task<string> RollbackAsync(string context, string id)
    {
        context = await ContextAsync(context);
        var current = await ContainerAsync(context, id) ?? throw new ArgumentException("Container no longer exists.");
        var previous = await PreviousContainerAsync(context, current.Id);
        if (string.IsNullOrEmpty(previous)) throw new ArgumentException("No previous container version is available for rollback.");
        var backup = await ContainerAsync(context, previous);
        if (backup is null) throw new ArgumentException("The previous container was removed; rollback is unavailable.");
        var wasRunning = current.Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase);
        if (wasRunning && !(await docker.RunAsync(context, Quick, "stop", current.Id)).Success) throw new ArgumentException("Could not stop the current container.");
        var failedName = $"{current.Name}--replaced--{Guid.NewGuid():N}";
        if (!(await docker.RunAsync(context, Quick, "rename", current.Id, failedName)).Success)
        { if (wasRunning) await docker.RunAsync(context, Quick, "start", current.Id); throw new ArgumentException("Could not preserve the current container."); }
        if (!(await docker.RunAsync(context, Quick, "rename", backup.Id, current.Name)).Success)
        { await docker.RunAsync(context, Quick, "rename", current.Id, current.Name); if (wasRunning) await docker.RunAsync(context, Quick, "start", current.Id); throw new ArgumentException("Could not restore the previous container name."); }
        if (!(await docker.RunAsync(context, Quick, "start", backup.Id)).Success)
        {
            await docker.RunAsync(context, Quick, "rename", backup.Id, previous);
            await docker.RunAsync(context, Quick, "rename", current.Id, current.Name);
            if (wasRunning) await docker.RunAsync(context, Quick, "start", current.Id);
            throw new ArgumentException("Previous container could not start. Current version restoration was attempted.");
        }
        return $"Previous container restored and started. The replaced version is retained as {failedName}.";
    }

    private static void Validate(DockerDeploymentForm form)
    {
        Validator.ValidateObject(form, new(form), true);
        var results = form.Validate(new(form)).ToList();
        if (results.Count > 0) throw new ArgumentException(results[0].ErrorMessage);
    }
    private async Task<string> ImageIdAsync(string context, string image)
    {
        var result = await docker.RunAsync(context, Quick, "image", "inspect", "--format", "{{.Id}}", image);
        if (!result.Success || !result.Output.Trim().StartsWith("sha256:", StringComparison.Ordinal)) throw new ArgumentException("Selected image is unavailable on this Docker server.");
        return result.Output.Trim();
    }
    private async Task<DockerContainerInfo?> ContainerAsync(string context, string id)
    {
        var result = await docker.RunAsync(context, Quick, "container", "inspect", "--format", "{{json .}}", id);
        if (!result.Success) return null;
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        var state = root.GetProperty("State").GetProperty("Status").GetString() ?? "unknown";
        var name = root.GetProperty("Name").GetString()?.TrimStart('/') ?? id;
        return new(root.GetProperty("Id").GetString() ?? id, name,
            root.GetProperty("Config").GetProperty("Image").GetString() ?? "", state == "running" ? "Up" : state, "");
    }
    private async Task<string?> ContainerImageIdAsync(string context, string id)
    {
        var result = await docker.RunAsync(context, Quick, "container", "inspect", "--format", "{{.Image}}", id);
        return result.Success ? result.Output.Trim() : null;
    }
    private async Task<string?> PreviousContainerAsync(string context, string id)
    {
        var result = await docker.RunAsync(context, Quick, "container", "inspect", "--format", "{{json .Config.Labels}}", id);
        if (!result.Success || result.Output.Trim() == "null") return null;
        using var doc = JsonDocument.Parse(result.Output);
        return doc.RootElement.TryGetProperty("deployhq.previous-container", out var previous) ? previous.GetString() : null;
    }
    private async Task<bool> IsManagedAsync(string context, string id)
    {
        var result = await docker.RunAsync(context, Quick, "container", "inspect", "--format", "{{json .Config.Labels}}", id);
        if (!result.Success || result.Output.Trim() == "null") return false;
        using var doc = JsonDocument.Parse(result.Output);
        return doc.RootElement.TryGetProperty("deployhq.managed", out var managed) && managed.GetString() == "true";
    }
    private static IEnumerable<string> EnvironmentEntries(string? value) =>
        (value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static IReadOnlyList<JsonElement> JsonLines(string value)
    {
        var items = new List<JsonElement>();
        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { using var doc = JsonDocument.Parse(line); items.Add(doc.RootElement.Clone()); }
            catch (JsonException) { throw new ArgumentException("Docker returned incomplete inventory data. Refresh this server and try again."); }
        }
        return items;
    }
    private static string Get(JsonElement value, string property) => value.TryGetProperty(property, out var x) ? x.ToString() : "";
}
