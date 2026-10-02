using System.Collections.Concurrent;

namespace DeploymentPortal.Services;

// Short-lived, single-use reviews. A portal restart expires pending confirmations.
public class TfsBuildConfirmationStore
{
    private readonly ConcurrentDictionary<string, Entry> reviews = new();
    public string Add(TfsBuildConfirmation confirmation)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in reviews)
            if (pair.Value.Expires <= now) reviews.TryRemove(pair.Key, out _);
        var token = Guid.NewGuid().ToString("N");
        reviews[token] = new(confirmation, now.AddMinutes(10));
        return token;
    }

    public TfsBuildConfirmation Take(string? token)
    {
        if (string.IsNullOrEmpty(token) || !reviews.TryRemove(token, out var entry) || entry.Expires <= DateTimeOffset.UtcNow)
            throw new ArgumentException("This confirmation has expired or was already submitted. Review the build again before running it.");
        return entry.Confirmation;
    }

    private sealed record Entry(TfsBuildConfirmation Confirmation, DateTimeOffset Expires);
}

public sealed record TfsBuildConfirmation(int DefinitionId, int Revision, int QueueId, string? QueueName,
    string Name, string SourceBranch, string SourceVersion);
