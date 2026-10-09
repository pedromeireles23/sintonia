namespace Sintonia.Core;

public enum TaskWorktreeCleanupState { Archiving, Archived, Cancelled, Interrupted, NeedsAttention }
public sealed record TaskWorktreeCleanupPreview(string Id, TaskPublication Publication, string ArchiveDirectory, string? PreviousId)
{
    public void ValidateDefinition()
    {
        Publication.ValidateDefinition();
        if (!Guid.TryParse(Id, out var id) || Publication.State != TaskPublicationState.Published
            || PreviousId is not null && !Guid.TryParse(PreviousId, out _)
            || !Path.IsPathFullyQualified(ArchiveDirectory) || Path.GetFileName(Path.TrimEndingDirectorySeparator(ArchiveDirectory)) != id.ToString("N")
            || Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(ArchiveDirectory))) != Guid.Parse(Publication.Reservation.Delivery.TaskId).ToString("N"))
            throw new ArgumentException("Prévia de arquivamento inválida.");
    }
}
public sealed record TaskWorktreeCleanup(TaskWorktreeCleanupPreview Preview, TaskIntegrationReservation Reservation,
    DateTimeOffset StartedAt, TaskWorktreeCleanupState State = TaskWorktreeCleanupState.Archiving, DateTimeOffset? FinishedAt = null, string? Error = null)
{
    public void ValidateDefinition()
    {
        Preview.ValidateDefinition(); Reservation.Target.ValidateFor(Reservation.Delivery);
        if (Reservation.Id != Preview.Id || Reservation.State != TaskIntegrationState.Reserved || Reservation.Delivery != Preview.Publication.Reservation.Delivery
            || Reservation.Target != Preview.Publication.Reservation.Target || !Enum.IsDefined(State) || Error?.Length > 4000
            || State == TaskWorktreeCleanupState.Archiving && (FinishedAt is not null || Error is not null)
            || State != TaskWorktreeCleanupState.Archiving && (FinishedAt is null || FinishedAt < StartedAt)
            || State == TaskWorktreeCleanupState.Archived && Error is not null
            || State is TaskWorktreeCleanupState.Cancelled or TaskWorktreeCleanupState.Interrupted or TaskWorktreeCleanupState.NeedsAttention && string.IsNullOrWhiteSpace(Error))
            throw new ArgumentException("Registro de arquivamento inválido.");
    }
    public bool BlocksCheckout => State is not TaskWorktreeCleanupState.Cancelled;
}
public interface IGitTaskWorktreeArchiver
{
    string GetArchiveDirectory(TaskPublication publication, string operationId);
    Task VerifyArchiveAsync(TaskWorktreeCleanupPreview preview, CancellationToken token);
    /// <summary>Move files intact, then retire only the missing checkout's Git registration, within a bounded operation.</summary>
    Task ArchiveAsync(TaskWorktreeCleanupPreview preview);
}
public sealed class TaskWorktreeCleanupService(IWorkspaceStore store, IRepositoryIntegrationLock repositoryLock, IGitTaskWorktreeArchiver archiver)
{
    public async Task<TaskWorktreeCleanupPreview> PreviewAsync(string projectId, string taskId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var task = (await store.GetTaskBatchesAsync(projectId).ConfigureAwait(false)).SelectMany(b => b.Tasks).Single(t => t.Id == taskId);
        if (task.State != WorkspaceTaskState.Approved || task.Publication is not { State: TaskPublicationState.Published } publication
            || task.Delivery is not { } delivery || publication.Reservation.Delivery != delivery || task.LastRunId != delivery.SourceRunId || task.Worktree != delivery.Worktree)
            throw new InvalidOperationException("Arquive somente a pasta de uma entrega aprovada e publicada no projeto.");
        if (task.Cleanup is { State: TaskWorktreeCleanupState.Archiving or TaskWorktreeCleanupState.Archived })
            throw new InvalidOperationException("A worktree já está sendo arquivada ou tem um arquivo preservado. Atualize a fila.");
        if (task.Cleanup is { } previous && (Directory.Exists(previous.Preview.ArchiveDirectory) || File.Exists(previous.Preview.ArchiveDirectory)))
            throw new InvalidOperationException("Há um arquivo preservado de uma operação anterior. Confira a recuperação antes de arquivar novamente.");
        var id = Guid.NewGuid().ToString(); var preview = new TaskWorktreeCleanupPreview(id, publication, archiver.GetArchiveDirectory(publication, id), task.Cleanup?.Preview.Id);
        preview.ValidateDefinition(); await archiver.VerifyArchiveAsync(preview, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); return preview;
    }
    public async Task<TaskWorktreeCleanup> ArchiveAsync(string projectId, TaskWorktreeCleanupPreview preview, CancellationToken token)
    {
        preview.ValidateDefinition(); token.ThrowIfCancellationRequested();
        if (projectId != preview.Publication.ProjectId) throw new ArgumentException("Confira o projeto da prévia.");
        using var held = repositoryLock.Acquire(preview.Publication.Reservation.Target.CommonGitDirectory);
        var intent = await store.ReserveTaskWorktreeCleanupAsync(projectId, preview).ConfigureAwait(false); var applying = false;
        TaskWorktreeCleanup result;
        try
        {
            await archiver.VerifyArchiveAsync(preview, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); applying = true;
            await archiver.ArchiveAsync(preview).ConfigureAwait(false);
            result = intent with { State = TaskWorktreeCleanupState.Archived, FinishedAt = DateTimeOffset.UtcNow };
        }
        catch (Exception error)
        {
            var cancelled = !applying && error is OperationCanceledException && token.IsCancellationRequested;
            var message = cancelled ? "Arquivamento cancelado antes de mover a pasta." : "Confira a pasta original, o arquivo preservado e o registro Git antes de retomar. Nenhum efeito será repetido automaticamente. " + error.Message;
            result = intent with { State = cancelled ? TaskWorktreeCleanupState.Cancelled : TaskWorktreeCleanupState.NeedsAttention,
                FinishedAt = DateTimeOffset.UtcNow, Error = message[..Math.Min(message.Length, 4000)] };
        }
        // A persistence failure leaves the original intent for recovery; never attempt a second terminal transition.
        await store.FinishTaskWorktreeCleanupAsync(result).ConfigureAwait(false); return result;
    }
}
