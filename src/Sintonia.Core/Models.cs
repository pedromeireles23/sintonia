namespace Sintonia.Core;

public enum ProviderKind { Codex, Claude }
public enum WorkTaskState { Queued, Running, InReview, Completed, Failed, Cancelled }
public enum RunState { Running, Succeeded, Failed, Cancelled }
public enum ProviderEventKind { Message, Result, Error }

public sealed record FunctionProfile(string Id, string Name, string Instructions, ProviderKind Provider);

public sealed record WorkTask(string Id, string Title, string Description, string FunctionId,
    IReadOnlyList<string> DependencyIds);

public sealed record ProviderSession(string Id, ProviderKind Provider, string FunctionId, bool IsSimulated);

public sealed record ProviderEvent(ProviderEventKind Kind, string Text);
public sealed record ProviderResult(string Summary);
public sealed record ProviderRequest(WorkTask Task, FunctionProfile Function, ProviderSession Session,
    IReadOnlyList<ApprovedDelivery> Dependencies);
public sealed record ApprovedDelivery(string TaskId, string RunId, string Summary);

public sealed record TaskRun(string Id, ProviderSession Session, int Attempt, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, RunState State, string? Result, string? Error,
    IReadOnlyList<ProviderEvent> Events);

public sealed record TaskSnapshot(WorkTask Definition, ProviderKind Provider, WorkTaskState State,
    IReadOnlyList<TaskRun> Runs)
{
    public TaskRun? LatestRun => Runs.LastOrDefault();
    public bool IsExecuting => LatestRun?.State == RunState.Running;
}

public sealed record CoordinatorSnapshot(IReadOnlyList<FunctionProfile> Functions,
    IReadOnlyList<TaskSnapshot> Tasks);

public interface IProviderAdapter
{
    ProviderKind Kind { get; }
    bool IsSimulated { get; }
    Task<ProviderResult> ExecuteAsync(ProviderRequest request, IProgress<ProviderEvent> progress,
        CancellationToken cancellationToken);
}
