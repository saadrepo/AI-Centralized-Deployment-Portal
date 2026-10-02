using System.Collections.Concurrent;

namespace DeploymentPortal.Services;

public class TfsReleaseConfirmationStore
{
    private readonly ConcurrentDictionary<string, Entry> reviews = new();

    public string Add(TfsReleaseDeployConfirmation confirmation)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in reviews)
            if (pair.Value.Expires <= now) reviews.TryRemove(pair.Key, out _);
        var token = Guid.NewGuid().ToString("N");
        reviews[token] = new(confirmation, now.AddMinutes(10));
        return token;
    }

    public TfsReleaseDeployConfirmation Take(string? token)
    {
        if (string.IsNullOrEmpty(token) || !reviews.TryRemove(token, out var entry) || entry.Expires <= DateTimeOffset.UtcNow)
            throw new ArgumentException("This confirmation has expired or was already submitted. Review the deployment again before running it.");
        return entry.Confirmation;
    }

    private sealed record Entry(TfsReleaseDeployConfirmation Confirmation, DateTimeOffset Expires);
}

public sealed record TfsReleaseDeployConfirmation(
    int ReleaseId,
    int ReleaseDefinitionId,
    int DefinitionEnvironmentId,
    int EnvironmentId,
    string ReleaseName,
    string EnvironmentName,
    string EnvironmentStatus,
    int? LatestAttempt,
    string? LatestDeploymentStatus,
    string? LatestOperationStatus);
