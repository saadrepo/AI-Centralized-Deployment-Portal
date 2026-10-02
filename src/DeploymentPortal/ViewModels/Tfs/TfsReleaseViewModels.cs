namespace DeploymentPortal.ViewModels.Tfs;

public class TfsReleaseResourceViewModel
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public List<TfsReleaseArtifactViewModel>? Artifacts { get; set; }
}

public class TfsReleaseDefinitionViewModel : TfsReleaseResourceViewModel
{
    public List<TfsReleaseDefinitionEnvironmentViewModel>? Environments { get; set; }
}

public class TfsReleaseViewModel : TfsReleaseResourceViewModel
{
    public string? Status { get; set; }
    public DateTimeOffset? CreatedOn { get; set; }
    public TfsReleaseDefinitionViewModel? ReleaseDefinition { get; set; }
    public List<TfsReleaseEnvironmentViewModel>? Environments { get; set; }
}

public class TfsReleaseArtifactViewModel
{
    public string? Alias { get; set; }
    public string? Type { get; set; }
    public Dictionary<string, TfsReleaseReference?>? DefinitionReference { get; set; }
    public TfsReleaseReference? Definition => Reference("definition");
    public TfsReleaseReference? Version => Reference("version");
    private TfsReleaseReference? Reference(string key) => DefinitionReference?.GetValueOrDefault(key);
}

public class TfsReleaseReference
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public class TfsReleaseDefinitionEnvironmentViewModel
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public TfsReleaseApprovalConfigurationViewModel? PreDeployApprovals { get; set; }
    public TfsReleaseApprovalConfigurationViewModel? PostDeployApprovals { get; set; }
}

public class TfsReleaseEnvironmentViewModel
{
    public int Id { get; set; }
    public int DefinitionEnvironmentId { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
    public List<TfsReleaseDeploymentAttemptViewModel>? DeploySteps { get; set; }
    public List<TfsReleaseApprovalViewModel>? PreDeployApprovals { get; set; }
    public List<TfsReleaseApprovalViewModel>? PostDeployApprovals { get; set; }
    public TfsReleaseDeploymentAttemptViewModel? LatestDeployment => DeploySteps?
        .Where(x => x is not null)
        .OrderByDescending(x => x.Attempt)
        .ThenByDescending(x => x.Id)
        .FirstOrDefault();
    public IReadOnlyList<TfsReleaseApprovalViewModel> PendingApprovals => (PreDeployApprovals ?? [])
        .Concat(PostDeployApprovals ?? [])
        .Where(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.Rank)
        .ThenBy(x => x.Id)
        .ToList();
    public bool CanDeploy => TfsReleaseDeploymentEligibility.CanDeploy(this);
    public TfsReleaseEnvironmentAction Action => TfsReleaseDeploymentEligibility.GetAction(this);
    public string? ActionLabel => Action switch
    {
        TfsReleaseEnvironmentAction.Deploy => "Deploy",
        TfsReleaseEnvironmentAction.Redeploy => "Redeploy",
        _ => null
    };
    public string EffectiveDeploymentStatus => TfsReleaseDeploymentEligibility.GetEffectiveDeploymentStatus(this);
}

public class TfsReleaseDeploymentAttemptViewModel
{
    public int Id { get; set; }
    public int Attempt { get; set; }
    public string? Status { get; set; }
    public string? OperationStatus { get; set; }
    public DateTimeOffset? QueuedOn { get; set; }
    public DateTimeOffset? LastModifiedOn { get; set; }
}

public class TfsReleaseApprovalConfigurationViewModel
{
    public List<TfsReleaseApprovalViewModel>? Approvals { get; set; }
    public TfsReleaseApprovalOptionsViewModel? ApprovalOptions { get; set; }
}

public class TfsReleaseApprovalOptionsViewModel
{
    public bool? ReleaseCreatorCanBeApprover { get; set; }
    public bool? AutoTriggeredAndPreviousEnvironmentApprovedCanBeSkipped { get; set; }
    public int? RequiredApproverCount { get; set; }
    public int? TimeoutInMinutes { get; set; }
    public string? ExecutionOrder { get; set; }
}

public class TfsReleaseApprovalViewModel
{
    public int Id { get; set; }
    public int Revision { get; set; }
    public int Rank { get; set; }
    public int Attempt { get; set; }
    public string? ApprovalType { get; set; }
    public string? Status { get; set; }
    public TfsReleaseReference? Approver { get; set; }
}

public class TfsReleasePageViewModel
{
    public IReadOnlyList<TfsReleaseDefinitionViewModel> Definitions { get; set; } = [];
    public TfsReleaseDefinitionViewModel? Definition { get; set; }
    public IReadOnlyList<TfsReleaseViewModel> Releases { get; set; } = [];
    public TfsReleaseViewModel? Release { get; set; }
    public string? Error { get; set; }
    public string? ReleasesError { get; set; }
    public TfsReleaseDeployReviewViewModel? DeployReview { get; set; }
}

public class TfsReleaseDeployReviewViewModel
{
    public int ReleaseId { get; set; }
    public int ReleaseDefinitionId { get; set; }
    public int DefinitionEnvironmentId { get; set; }
    public int EnvironmentId { get; set; }
    public string? ReleaseName { get; set; }
    public string? EnvironmentName { get; set; }
    public string? EnvironmentStatus { get; set; }
    public string? LatestDeploymentStatus { get; set; }
    public string? LatestOperationStatus { get; set; }
    public int? LatestAttempt { get; set; }
    public string? ConfirmationToken { get; set; }
    public string? Error { get; set; }
}

public enum TfsReleaseEnvironmentAction
{
    None,
    Deploy,
    Redeploy
}

public static class TfsReleaseDeploymentEligibility
{
    private static readonly HashSet<string> ActiveEnvironmentStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "active", "inProgress", "queued", "scheduled"
    };

    private static readonly HashSet<string> ActiveDeploymentStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "active", "inProgress", "queued"
    };

    private static readonly HashSet<string> RedeployDeploymentStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "failed", "canceled", "rejected", "succeeded"
    };

    private static readonly HashSet<string> ActiveOperationStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "queued", "scheduled", "pending", "approved", "deferred", "queuedForAgent", "phaseInProgress",
        "manualInterventionPending", "queuedForPipeline", "cancelling", "evaluatingGates"
    };

    public static bool CanDeploy(TfsReleaseEnvironmentViewModel environment) => GetAction(environment) is not TfsReleaseEnvironmentAction.None;

    public static TfsReleaseEnvironmentAction GetAction(TfsReleaseEnvironmentViewModel environment)
    {
        if (environment.Id <= 0 || environment.DefinitionEnvironmentId <= 0 || IsProduction(environment.Name) || environment.PendingApprovals.Count > 0)
            return TfsReleaseEnvironmentAction.None;
        if (environment.Status is { } environmentStatus && ActiveEnvironmentStatuses.Contains(environmentStatus))
            return TfsReleaseEnvironmentAction.None;
        if (environment.Status is { } terminalEnvironmentStatus && RedeployDeploymentStatuses.Contains(terminalEnvironmentStatus))
            return TfsReleaseEnvironmentAction.Redeploy;

        var latest = environment.LatestDeployment;
        if (latest is null)
            return string.Equals(environment.Status, "notStarted", StringComparison.OrdinalIgnoreCase)
                ? TfsReleaseEnvironmentAction.Deploy : TfsReleaseEnvironmentAction.None;

        if (latest.Status is { } deploymentStatus)
        {
            if (ActiveDeploymentStatuses.Contains(deploymentStatus))
                return TfsReleaseEnvironmentAction.None;
            if (string.Equals(deploymentStatus, "notDeployed", StringComparison.OrdinalIgnoreCase))
                return string.Equals(environment.Status, "notStarted", StringComparison.OrdinalIgnoreCase)
                    ? TfsReleaseEnvironmentAction.Deploy : TfsReleaseEnvironmentAction.None;
            if (RedeployDeploymentStatuses.Contains(deploymentStatus))
                return TfsReleaseEnvironmentAction.Redeploy;
            return TfsReleaseEnvironmentAction.None;
        }

        return string.Equals(environment.Status, "notStarted", StringComparison.OrdinalIgnoreCase)
            ? TfsReleaseEnvironmentAction.Deploy : TfsReleaseEnvironmentAction.None;
    }

    public static string GetEffectiveDeploymentStatus(TfsReleaseEnvironmentViewModel environment)
    {
        if (environment.Status is { } terminalEnvironmentStatus && RedeployDeploymentStatuses.Contains(terminalEnvironmentStatus))
            return terminalEnvironmentStatus;

        var latest = environment.LatestDeployment;
        if (latest?.OperationStatus is { } operationStatus)
        {
            var effectiveOperationStatus = GetEffectiveOperationStatus(operationStatus);
            if (effectiveOperationStatus is not null)
                return effectiveOperationStatus;
        }

        if (environment.Status is { } activeEnvironmentStatus && ActiveEnvironmentStatuses.Contains(activeEnvironmentStatus))
            return activeEnvironmentStatus;
        if (string.Equals(environment.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
            return "succeeded";

        if (latest?.Status is { } latestStatus && !string.Equals(latestStatus, "notDeployed", StringComparison.OrdinalIgnoreCase))
            return latestStatus;
        if (string.Equals(environment.Status, "notStarted", StringComparison.OrdinalIgnoreCase))
            return "notDeployed";
        return environment.Status ?? latest?.Status ?? "unknown";
    }

    public static bool HasConflictingWorkflow(TfsReleaseEnvironmentViewModel environment)
    {
        if (environment.PendingApprovals.Count > 0)
            return true;
        if (environment.Status is { } status && ActiveEnvironmentStatuses.Contains(status))
            return true;
        var latest = environment.LatestDeployment;
        return latest is not null
            && ((latest.Status is { } deploymentStatus && ActiveDeploymentStatuses.Contains(deploymentStatus))
                || (latest.OperationStatus is { } operationStatus && ActiveOperationStatuses.Contains(operationStatus)));
    }

    public static bool IsProduction(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && (name.Contains("production", StringComparison.OrdinalIgnoreCase)
               || name.Contains("prod", StringComparison.OrdinalIgnoreCase));

    private static string? GetEffectiveOperationStatus(string operationStatus)
    {
        if (string.Equals(operationStatus, "queuedForAgent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operationStatus, "queued", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operationStatus, "queuedForPipeline", StringComparison.OrdinalIgnoreCase))
            return "queued";
        if (string.Equals(operationStatus, "phaseInProgress", StringComparison.OrdinalIgnoreCase)
            || string.Equals(operationStatus, "inProgress", StringComparison.OrdinalIgnoreCase))
            return "inProgress";
        return null;
    }
}
