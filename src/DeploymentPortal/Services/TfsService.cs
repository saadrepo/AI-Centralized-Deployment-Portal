using System.Net.Http.Json;
using System.Text.Json;
using DeploymentPortal.ViewModels.Tfs;

namespace DeploymentPortal.Services;

public class TfsService(HttpClient client, IConfiguration configuration, TfsBuildConfirmationStore confirmations) : ITfsService
{
    public async Task<IReadOnlyList<TfsPipelineViewModel>> GetBuildDefinitionsAsync(string? search = null)
    {
        var path = GetApiPath("definitions") + "&$top=50&queryOrder=definitionNameAscending&includeAllProperties=true";
        if (!string.IsNullOrWhiteSpace(search))
            path += "&name=" + Uri.EscapeDataString(search.Trim() + "*");
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<BuildDefinitionsResponse>();
        if (page?.Value is null)
            throw new JsonException("TFS returned an invalid build definitions response.");
        return page.Value.Take(50).ToList();
    }
    private sealed class BuildDefinitionsResponse
    {
        public List<TfsPipelineViewModel>? Value { get; set; }
    }

    public async Task<TfsDefinitionViewModel> GetBuildDefinitionAsync(int definitionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(definitionId);
        using var response = await client.GetAsync(GetApiPath($"definitions/{definitionId}"));
        response.EnsureSuccessStatusCode();
        var definition = await response.Content.ReadFromJsonAsync<TfsDefinitionViewModel>();
        if (definition is null || definition.Id != definitionId || string.IsNullOrWhiteSpace(definition.Name))
            throw new JsonException("TFS returned an invalid build definition response.");
        return definition;
    }

    public async Task<IReadOnlyList<TfsBuildViewModel>> GetLatestBuildsAsync(int definitionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(definitionId);
        using var response = await client.GetAsync(GetApiPath("builds") + $"&definitions={definitionId}&$top=20&queryOrder=queueTimeDescending");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<BuildsResponse>();
        if (page?.Value is null)
            throw new JsonException("TFS returned an invalid build history response.");
        return page.Value.OrderByDescending(x => x.QueueTime).ThenByDescending(x => x.Id).Take(20).ToList();
    }

    public async Task<TfsBuildDetailsViewModel> GetBuildAsync(int buildId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buildId);
        using var response = await client.GetAsync(GetApiPath($"builds/{buildId}"));
        response.EnsureSuccessStatusCode();
        var build = await response.Content.ReadFromJsonAsync<TfsBuildDetailsViewModel>();
        if (build is null || build.Id != buildId) throw new JsonException("TFS returned an invalid build response.");
        return build;
    }

    public Task<IReadOnlyList<TfsBuildLogViewModel>> GetBuildLogsAsync(int buildId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buildId);
        return GetListAsync<TfsBuildLogViewModel>($"builds/{buildId}/logs");
    }

    public Task<IReadOnlyList<TfsBuildArtifactViewModel>> GetBuildArtifactsAsync(int buildId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buildId);
        return GetListAsync<TfsBuildArtifactViewModel>($"builds/{buildId}/artifacts");
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string resource)
    {
        using var response = await client.GetAsync(GetApiPath(resource));
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<TfsListResponse<T>>();
        if (page?.Value is null || page.Value.Any(x => x is null))
            throw new JsonException("TFS returned an invalid list response.");
        return page.Value;
    }

    public async Task<string> GetBuildLogAsync(int buildId, int logId, int startLine = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buildId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logId);
        if (startLine < 0 || startLine > int.MaxValue - TfsLogPageViewModel.PageSize)
            throw new ArgumentException("Invalid log line range.");
        var path = GetApiPath($"builds/{buildId}/logs/{logId}") + $"&startLine={startLine}&endLine={startLine + TfsLogPageViewModel.PageSize - 1}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/plain");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "text/plain")
            throw new JsonException("TFS did not return a plain text log.");
        // Bound memory even if the server ignores the requested line range.
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        var buffer = new char[1_000_001];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length), timeout.Token);
            if (read == 0) break;
            length += read;
        }
        if (length == buffer.Length) throw new InvalidOperationException("The selected log range is too large to display.");
        return new string(buffer, 0, length);
    }

    public async Task<TfsRunBuildViewModel> ReviewBuildAsync(int definitionId, string sourceVersion)
        => await CreateReviewAsync(definitionId, sourceVersion);

    public async Task<TfsRunBuildViewModel> ReviewLatestCiBuildAsync(int definitionId)
    {
        var definition = await GetBuildDefinitionAsync(definitionId);
        ValidateRunnableDefinition(definition);
        var source = definition.Repository!.DefaultBranch!;
        if (!source.StartsWith("$/", StringComparison.Ordinal) || source.Length <= 2)
            throw new ArgumentException("The pipeline must have a configured TFVC source path.");
        var project = configuration["Tfs:Project"]!;
        var version = configuration["Tfs:ApiVersion"]!;
        var path = $"{Uri.EscapeDataString(project)}/_apis/tfvc/changesets?api-version={Uri.EscapeDataString(version)}"
            + "&$top=1&$orderby=id%20desc&searchCriteria.itemPath=" + Uri.EscapeDataString(source);
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<TfsListResponse<TfvcChangeset>>();
        if (page?.Value is null || page.Value.Count > 1 || page.Value.Any(x => x is null || x.ChangesetId <= 0))
            throw new JsonException("TFS returned an invalid changeset response.");
        if (page.Value.Count == 0)
            throw new ArgumentException("No checked-in changeset was found for this pipeline's source.");
        var sourceVersion = page.Value[0].ChangesetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new TfsRunBuildViewModel
        {
            DefinitionId = definition.Id, Definition = definition, SourceVersion = sourceVersion,
            ConfirmationToken = confirmations.Add(new TfsBuildConfirmation(definition.Id, definition.Revision,
                definition.Queue!.Id!.Value, definition.Queue.Name, definition.Name, source, sourceVersion))
        };
    }

    private sealed class TfvcChangeset
    {
        public int ChangesetId { get; set; }
    }

    public async Task<TfsRunBuildViewModel> ReviewExistingBuildAsync(int buildId)
    {
        var build = await GetBuildAsync(buildId);
        if (build.BuildAction is null || build.Definition?.Id is not > 0)
            throw new ArgumentException("Select a completed succeeded, failed or canceled build with a valid pipeline.");
        if (string.IsNullOrWhiteSpace(build.SourceBranch))
            throw new ArgumentException("The original build has no source branch. Run a new build manually instead.");
        return await CreateReviewAsync(build.Definition.Id, build.SourceVersion ?? "", build.SourceBranch);
    }

    private async Task<TfsRunBuildViewModel> CreateReviewAsync(int definitionId, string sourceVersion, string? originalBranch = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(definitionId);
        if (string.IsNullOrEmpty(sourceVersion) || sourceVersion.Length > 10 || sourceVersion.Any(c => c < '0' || c > '9')
            || !int.TryParse(sourceVersion, out var changeset) || changeset <= 0)
            throw new ArgumentException("Enter a positive numeric TFVC changeset ID.");
        var definition = await GetBuildDefinitionAsync(definitionId);
        ValidateRunnableDefinition(definition);
        if (originalBranch is not null && originalBranch != definition.Repository!.DefaultBranch)
            throw new ArgumentException("The original build source differs from the pipeline's current default source. Run a new build manually instead.");
        var review = new TfsBuildConfirmation(definition.Id, definition.Revision, definition.Queue!.Id!.Value,
            definition.Queue.Name, definition.Name, definition.Repository!.DefaultBranch!, sourceVersion);
        return new TfsRunBuildViewModel
        {
            DefinitionId = definitionId, Definition = definition, SourceVersion = sourceVersion,
            ConfirmationToken = confirmations.Add(review)
        };
    }

    public async Task<TfsBuildDetailsViewModel> QueueBuildAsync(string confirmationToken)
    {
        // Consume before any HTTP call: repeated submissions cannot queue another build.
        var review = confirmations.Take(confirmationToken);
        TfsDefinitionViewModel definition;
        try { definition = await GetBuildDefinitionAsync(review.DefinitionId); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            throw new TfsBuildRequestException(review.DefinitionId, "definition validation", false,
                (ex as HttpRequestException)?.StatusCode, inner: ex);
        }
        ValidateRunnableDefinition(definition);
        if (definition.Revision != review.Revision || definition.Queue!.Id != review.QueueId
            || definition.Queue.Name != review.QueueName || definition.Name != review.Name
            || definition.Repository!.DefaultBranch != review.SourceBranch)
            throw new ArgumentException("The pipeline changed after review. Review the current definition before running it.");

        HttpResponseMessage response;
        try
        {
            // Buffer this small payload so legacy TFS/IIS receives Content-Length,
            // rather than JsonContent's streamed/chunked request body.
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                definition = new { id = definition.Id },
                sourceBranch = definition.Repository!.DefaultBranch,
                sourceVersion = review.SourceVersion
            });
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            response = await client.PostAsync(GetApiPath("builds"), content);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new TfsBuildRequestException(review.DefinitionId, "sending build request", true, inner: ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string? errorType = null;
                int? errorCode = null;
                try
                {
                    await response.Content.LoadIntoBufferAsync(16384);
                    using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (error.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (error.RootElement.TryGetProperty("typeKey", out var type) && type.ValueKind == JsonValueKind.String)
                        {
                            var value = type.GetString();
                            if (value is { Length: > 0 and <= 120 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')) errorType = value;
                        }
                        if (error.RootElement.TryGetProperty("errorCode", out var code) && code.TryGetInt32(out var number)) errorCode = number;
                    }
                }
                catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or InvalidOperationException)
                {
                    // Preserve the HTTP failure even when the diagnostic body is HTML, oversized or malformed.
                    throw new TfsBuildRequestException(review.DefinitionId, "TFS response", (int)response.StatusCode >= 500,
                        response.StatusCode, inner: ex);
                }
                throw new TfsBuildRequestException(review.DefinitionId, "TFS response", (int)response.StatusCode >= 500,
                    response.StatusCode, errorType, errorCode);
            }
            try
            {
                var build = await response.Content.ReadFromJsonAsync<TfsBuildDetailsViewModel>();
                if (build is null || build.Id <= 0) throw new JsonException("TFS did not return a valid queued build ID.");
                return build;
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
            {
                throw new TfsBuildRequestException(review.DefinitionId, "reading accepted build response", true, response.StatusCode, inner: ex);
            }
        }
    }

    private static void ValidateRunnableDefinition(TfsDefinitionViewModel definition)
    {
        if (!string.Equals(definition.QueueStatus, "enabled", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This pipeline is paused or disabled and cannot queue builds.");
        if (!string.Equals(definition.Repository?.Type, "TfsVersionControl", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Run New Build currently supports TFVC pipelines only. Other source types remain read-only.");
        if (definition.Queue?.Id is not > 0 || string.IsNullOrWhiteSpace(definition.Repository?.DefaultBranch))
            throw new ArgumentException("The pipeline must have a configured queue and default source before it can run.");
    }

    private sealed class TfsListResponse<T>
    {
        public List<T>? Value { get; set; }
    }

    private string GetApiPath(string resource)
    {
        var project = configuration["Tfs:Project"];
        var apiVersion = configuration["Tfs:ApiVersion"];
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(apiVersion))
            throw new InvalidOperationException("TFS project and API version must be configured.");
        return $"{Uri.EscapeDataString(project)}/_apis/build/{resource}?api-version={Uri.EscapeDataString(apiVersion)}";
    }

    private sealed class BuildsResponse
    {
        public List<TfsBuildViewModel>? Value { get; set; }
    }
}
