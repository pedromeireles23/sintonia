namespace Sintonia.Core;

public enum TaskWorktreeState { Preparing, Ready, NeedsAttention }

public sealed record TaskWorktree(string TaskId, string RepositoryDirectory, string CommonGitDirectory,
    string CheckoutDirectory, string ProjectRelativeDirectory, string BaseCommit, string Branch,
    TaskWorktreeState State = TaskWorktreeState.Preparing, string? Error = null)
{
    public string WorkingDirectory => Path.GetFullPath(Path.Combine(CheckoutDirectory, ProjectRelativeDirectory));

    public void ValidateDefinition(string? projectDirectory = null)
    {
        if (!Guid.TryParse(TaskId, out var id) || Branch != "codex/sintonia/" + id.ToString("N")
            || BaseCommit.Length is not (40 or 64) || !BaseCommit.All(Uri.IsHexDigit)
            || !Enum.IsDefined(State) || Error?.Length > 4000
            || new[] { RepositoryDirectory, CommonGitDirectory, CheckoutDirectory }.Any(p => !Path.IsPathFullyQualified(p))
            || Path.GetFileName(Path.TrimEndingDirectorySeparator(CheckoutDirectory)) != id.ToString("N")
            || Path.IsPathRooted(ProjectRelativeDirectory) || string.IsNullOrWhiteSpace(ProjectRelativeDirectory))
            throw new ArgumentException("Vínculo de worktree inválido.");
        var relative = Path.GetRelativePath(CheckoutDirectory, WorkingDirectory);
        var targetRelative = Path.GetRelativePath(RepositoryDirectory, CheckoutDirectory);
        if (Escapes(relative) || !Escapes(targetRelative)) throw new ArgumentException("A worktree precisa ficar fora do repositório e conter a pasta do projeto.");
        if (projectDirectory is not null && !string.Equals(Path.GetFullPath(Path.Combine(RepositoryDirectory, ProjectRelativeDirectory)),
            Path.GetFullPath(projectDirectory), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A worktree pertence a outra pasta de projeto.");
    }
    private static bool Escapes(string path) => Path.IsPathRooted(path) || path == ".."
        || path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

public interface IGitTaskWorktreeManager
{
    Task<TaskWorktree> PlanAsync(WorkspaceProject project, string taskId, CancellationToken cancellationToken);
    Task PrepareAsync(TaskWorktree worktree, CancellationToken cancellationToken);
    Task ValidateAsync(TaskWorktree worktree, CancellationToken cancellationToken);
}

/// <summary>Record the intent before touching Git; uncertain effects always require an explicit retry.</summary>
public sealed class TaskWorktreeService(IWorkspaceStore store, IGitTaskWorktreeManager manager)
{
    public async Task<TaskWorktree> PreviewAsync(string projectId, string taskId, CancellationToken cancellationToken)
    {
        var project = (await store.GetProjectsAsync().ConfigureAwait(false)).Single(p => p.Id == projectId);
        var batch = (await store.GetTaskBatchesAsync(projectId).ConfigureAwait(false)).Single(b => b.Tasks.Any(t => t.Id == taskId));
        var task = batch.Tasks.Single(t => t.Id == taskId);
        if (!WorkspaceTaskPolicy.CanPrepareWorktree(task, batch.Tasks))
            throw new InvalidOperationException("Prepare a worktree antes da primeira tentativa de uma tarefa com escrita e com dependências disponíveis.");
        return task.Worktree ?? await manager.PlanAsync(project, taskId, cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareAsync(string projectId, TaskWorktree preview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reserved = await store.ReserveTaskWorktreeAsync(projectId, preview).ConfigureAwait(false);
        try
        {
            await manager.PrepareAsync(reserved, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await store.FinishTaskWorktreeAsync(reserved.TaskId, true, null).ConfigureAwait(false);
        }
        catch
        {
            await store.FinishTaskWorktreeAsync(reserved.TaskId, false,
                "Preparação interrompida ou recusada. Arquivos e branches foram preservados; confira a pasta e tente preparar novamente.").ConfigureAwait(false);
            throw;
        }
    }
}
