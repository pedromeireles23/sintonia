namespace Sintonia.Core;

public enum TaskDiffView { SinceBase, Index, WorkingTree }
public enum TaskDiffContentState { Text, Binary, TooLarge, NoChanges }
public sealed record TaskDiffFile(string Path, string? OriginalPath, char BaseStatus, GitFileChange? LocalChange, bool HasUntrackedContent = false);
public sealed record TaskDiffSnapshot(TaskWorktree Worktree, string HeadCommit, string LocalStatus,
    IReadOnlyList<TaskDiffFile> Files, DateTimeOffset CheckedAt);
public sealed record TaskFileDiff(TaskDiffContentState State, string Text, string Message, DateTimeOffset CheckedAt);
public sealed record TaskDiffReview(WorkspaceTask Task, TaskDiffSnapshot Snapshot);

public interface IGitTaskDiffReader
{
    Task<TaskDiffSnapshot> ScanAsync(TaskWorktree worktree, CancellationToken cancellationToken);
    Task<TaskFileDiff> ReadAsync(TaskDiffSnapshot snapshot, TaskDiffFile file, TaskDiffView view, CancellationToken cancellationToken);
}

/// <summary>Only inspect the currently persisted, idle task; reading does not approve or integrate its delivery.</summary>
public sealed class TaskDiffService(IWorkspaceStore store, IGitTaskDiffReader reader)
{
    public async Task<TaskDiffReview> ScanAsync(string projectId, string taskId, CancellationToken cancellationToken)
    {
        var task = await CurrentAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        var snapshot = await reader.ScanAsync(task.Worktree!, cancellationToken).ConfigureAwait(false);
        EnsureSame(task, await CurrentAsync(projectId, taskId, cancellationToken).ConfigureAwait(false));
        return new(task, snapshot);
    }
    public async Task<TaskFileDiff> ReadAsync(string projectId, TaskDiffReview review, TaskDiffFile file, TaskDiffView view, CancellationToken cancellationToken)
    {
        EnsureSame(review.Task, await CurrentAsync(projectId, review.Task.Id, cancellationToken).ConfigureAwait(false));
        if (review.Snapshot.Worktree != review.Task.Worktree) throw new InvalidOperationException("A consulta pertence a outra pasta da tarefa.");
        var result = await reader.ReadAsync(review.Snapshot, file, view, cancellationToken).ConfigureAwait(false);
        EnsureSame(review.Task, await CurrentAsync(projectId, review.Task.Id, cancellationToken).ConfigureAwait(false));
        return result;
    }
    private async Task<WorkspaceTask> CurrentAsync(string projectId, string taskId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var task = (await store.GetTaskBatchesAsync(projectId).ConfigureAwait(false)).SelectMany(b => b.Tasks).Single(t => t.Id == taskId);
        token.ThrowIfCancellationRequested();
        if (task.State == WorkspaceTaskState.Running || task.Worktree?.State != TaskWorktreeState.Ready)
            throw new InvalidOperationException("Aguarde a tentativa terminar e confira a worktree preparada antes de consultar os diffs.");
        return task;
    }
    private static void EnsureSame(WorkspaceTask expected, WorkspaceTask current)
    {
        if (expected.Worktree != current.Worktree || expected.State != current.State || expected.Attempts != current.Attempts || expected.LastRunId != current.LastRunId)
            throw new InvalidOperationException("A tarefa mudou durante a consulta. Atualize os diffs antes de continuar.");
    }
}
