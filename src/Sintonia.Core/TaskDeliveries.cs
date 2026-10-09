namespace Sintonia.Core;

public sealed record TaskDeliveryCommit(string Commit, string Tree);
public sealed record TaskDelivery(string TaskId, string SourceRunId, TaskWorktree Worktree,
    string Commit, string Tree, DateTimeOffset RegisteredAt)
{
    public void ValidateDefinition()
    {
        Worktree.ValidateDefinition();
        if (TaskId != Worktree.TaskId || Worktree.State != TaskWorktreeState.Ready || !Guid.TryParse(SourceRunId, out _)
            || !IsObjectId(Commit) || !IsObjectId(Tree) || Commit.Length != Tree.Length || Commit.Length != Worktree.BaseCommit.Length)
            throw new ArgumentException("Registro de entrega inválido.");
    }
    public static bool IsObjectId(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}

public sealed record TaskIntegrationTarget(string RepositoryDirectory, string CommonGitDirectory, string Branch, string Commit)
{
    public void ValidateFor(TaskDelivery delivery)
    {
        delivery.ValidateDefinition();
        if (!SamePath(RepositoryDirectory, delivery.Worktree.RepositoryDirectory) || !SamePath(CommonGitDirectory, delivery.Worktree.CommonGitDirectory)
            || !TaskDelivery.IsObjectId(Commit) || Commit.Length != delivery.Commit.Length || !Branch.StartsWith("refs/heads/", StringComparison.Ordinal)
            || Branch.Length <= 11 || Branch.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "~^:?*[\\".Contains(c))
            || Branch.Contains("..", StringComparison.Ordinal) || Branch.Contains("@{", StringComparison.Ordinal)
            || Branch.Split('/').Any(s => s.Length == 0 || s.StartsWith('.') || s.EndsWith('.') || s.EndsWith(".lock", StringComparison.Ordinal)))
            throw new ArgumentException("Destino de integração inválido ou incompatível com a entrega.");
    }
    private static bool SamePath(string first, string second) => Path.IsPathFullyQualified(first)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
}
public enum TaskIntegrationState { Reserved, Released, NeedsAttention }
public sealed record TaskIntegrationReservation(string Id, TaskDelivery Delivery, TaskIntegrationTarget Target,
    DateTimeOffset ReservedAt, TaskIntegrationState State = TaskIntegrationState.Reserved, string? Error = null);

/// <summary>Held throughout native integration work, including persistence of its final state.</summary>
public interface IRepositoryIntegrationLock
{
    IDisposable Acquire(string commonGitDirectory);
}

public interface IGitTaskDeliveryInspector
{
    Task<TaskDeliveryCommit> CaptureAsync(TaskDiffSnapshot snapshot, CancellationToken cancellationToken);
    Task<TaskIntegrationTarget> InspectTargetAsync(TaskDelivery delivery, CancellationToken cancellationToken);
}

/// <summary>Register the exact reviewed commit; reservation is preparation, never proof of integration.</summary>
public sealed class TaskDeliveryService(IWorkspaceStore store, IGitTaskDeliveryInspector inspector)
{
    public async Task<TaskDelivery> RegisterAsync(string projectId, TaskDiffReview review, CancellationToken token)
    {
        var current = await CurrentAsync(projectId, review.Task.Id, token).ConfigureAwait(false);
        if (current.Worktree != review.Task.Worktree || current.State != review.Task.State || current.LastRunId != review.Task.LastRunId
            || current.Attempts != review.Task.Attempts || review.Snapshot.Worktree != current.Worktree)
            throw new InvalidOperationException("A entrega mudou. Atualize os diffs da tarefa aprovada antes de registrar.");
        var content = await inspector.CaptureAsync(review.Snapshot, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (content.Commit != review.Snapshot.HeadCommit) throw new InvalidOperationException("O commit mudou após a revisão. Atualize os diffs.");
        var delivery = new TaskDelivery(current.Id, current.LastRunId!, current.Worktree!, content.Commit, content.Tree, DateTimeOffset.UtcNow);
        // The store compares the approved run/worktree again in the same transaction as registration.
        return await store.SaveTaskDeliveryAsync(projectId, delivery).ConfigureAwait(false);
    }
    public async Task<TaskIntegrationTarget> PreviewIntegrationAsync(string projectId, string taskId, CancellationToken token)
    {
        var task = await CurrentAsync(projectId, taskId, token).ConfigureAwait(false);
        var delivery = task.Delivery ?? throw new InvalidOperationException("Registre o commit revisado antes de preparar a integração.");
        var target = await inspector.InspectTargetAsync(delivery, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if ((await CurrentAsync(projectId, taskId, token).ConfigureAwait(false)).Delivery != delivery)
            throw new InvalidOperationException("A entrega mudou durante a consulta.");
        target.ValidateFor(delivery); return target;
    }
    public async Task<TaskIntegrationReservation> ReserveIntegrationAsync(string projectId, string taskId, TaskIntegrationTarget preview, CancellationToken token)
    {
        var task = await CurrentAsync(projectId, taskId, token).ConfigureAwait(false);
        var delivery = task.Delivery ?? throw new InvalidOperationException("Registre a entrega antes de reservar a integração.");
        var actual = await inspector.InspectTargetAsync(delivery, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (actual != preview) throw new InvalidOperationException("O destino mudou após a prévia. Confira novamente antes de integrar.");
        return await store.ReserveTaskIntegrationAsync(projectId, delivery, preview).ConfigureAwait(false);
    }
    private async Task<WorkspaceTask> CurrentAsync(string projectId, string taskId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var task = (await store.GetTaskBatchesAsync(projectId).ConfigureAwait(false)).SelectMany(b => b.Tasks).Single(t => t.Id == taskId);
        token.ThrowIfCancellationRequested();
        if (task.State != WorkspaceTaskState.Approved || task.Worktree?.State != TaskWorktreeState.Ready || task.LastRunId is null)
            throw new InvalidOperationException("A entrega precisa estar aprovada e ter uma worktree pronta para registrar seu commit.");
        return task;
    }
}
