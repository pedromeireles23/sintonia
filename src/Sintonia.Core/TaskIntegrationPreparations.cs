namespace Sintonia.Core;

public enum TaskIntegrationPreparationState { Preparing, Combined, Conflicted, NeedsAttention }

/// <summary>A separate checkout of the combined files. Never evidence of validation or publication.</summary>
public sealed record TaskIntegrationPreparation(TaskIntegrationReservation Reservation, string CheckoutDirectory,
    TaskIntegrationPreparationState State = TaskIntegrationPreparationState.Preparing, string? Tree = null,
    IReadOnlyList<string>? Conflicts = null, string? Error = null)
{
    public void ValidateDefinition()
    {
        Reservation.Target.ValidateFor(Reservation.Delivery);
        if (!Guid.TryParse(Reservation.Id, out var id) || Reservation.State != TaskIntegrationState.Reserved
            || !Path.IsPathFullyQualified(CheckoutDirectory) || Path.GetFileName(CheckoutDirectory) != id.ToString("N")
            || !Enum.IsDefined(State) || Error?.Length > 4000
            || Tree is not null && (!TaskDelivery.IsObjectId(Tree) || Tree.Length != Reservation.Delivery.Commit.Length)
            || Conflicts?.Count > 1000 || Conflicts?.Any(p => string.IsNullOrEmpty(p) || p.Length > 4096) == true
            || State == TaskIntegrationPreparationState.Combined && (Tree is null || Conflicts?.Count > 0 || Error is not null)
            || State == TaskIntegrationPreparationState.Conflicted && (Tree is not null || Conflicts is not { Count: > 0 } || Error is not null)
            || State == TaskIntegrationPreparationState.Preparing && (Tree is not null || Conflicts is not null || Error is not null)
            || State == TaskIntegrationPreparationState.NeedsAttention && (Tree is not null || string.IsNullOrWhiteSpace(Error)))
            throw new ArgumentException("Preparação de integração inválida.");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(CheckoutDirectory));
        var original = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Reservation.Target.RepositoryDirectory));
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Reservation.Delivery.Worktree.CheckoutDirectory));
        var common = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Reservation.Target.CommonGitDirectory));
        if (Overlaps(path, original) || Overlaps(path, source) || Overlaps(path, common))
            throw new ArgumentException("A combinação deve usar uma pasta separada da origem e do destino.");
    }
    private static bool Overlaps(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
        || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public interface IGitTaskIntegrationPreparer
{
    string GetCheckoutDirectory(TaskIntegrationReservation reservation);
    Task<TaskIntegrationPreparation> PrepareAsync(TaskIntegrationPreparation intent, CancellationToken token);
}

public sealed class TaskIntegrationPreparationService(IWorkspaceStore store, TaskDeliveryService deliveries,
    IRepositoryIntegrationLock repositoryLock, IGitTaskIntegrationPreparer preparer)
{
    public Task<TaskIntegrationTarget> PreviewAsync(string projectId, string taskId, CancellationToken token) =>
        deliveries.PreviewIntegrationAsync(projectId, taskId, token);

    public Task<IReadOnlyList<TaskIntegrationPreparation>> GetAsync(string projectId) => store.GetTaskIntegrationPreparationsAsync(projectId);

    public async Task<TaskIntegrationPreparation> PrepareAsync(string projectId, string taskId, TaskIntegrationTarget preview, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var held = repositoryLock.Acquire(preview.CommonGitDirectory);
        TaskIntegrationReservation? reservation = null; TaskIntegrationPreparation? intent = null;
        try
        {
            reservation = await deliveries.ReserveIntegrationAsync(projectId, taskId, preview, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var planned = new TaskIntegrationPreparation(reservation, preparer.GetCheckoutDirectory(reservation));
            planned.ValidateDefinition();
            await store.SaveTaskIntegrationPreparationAsync(planned).ConfigureAwait(false); intent = planned;
            token.ThrowIfCancellationRequested();
            var result = await preparer.PrepareAsync(intent, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); result.ValidateDefinition();
            if (result.Reservation != intent.Reservation || result.CheckoutDirectory != intent.CheckoutDirectory
                || result.State is not (TaskIntegrationPreparationState.Combined or TaskIntegrationPreparationState.Conflicted))
                throw new InvalidOperationException("O executor retornou uma combinação incompatível com a intenção registrada.");
            // Result and release are atomic. A cancellation after this transaction must not hide a saved result.
            await store.FinishTaskIntegrationPreparationAsync(result).ConfigureAwait(false);
            return result;
        }
        catch (Exception error)
        {
            var message = error is OperationCanceledException ? "Preparação cancelada. Confira a pasta preservada antes de tentar novamente."
                : error.Message.Length > 4000 ? error.Message[..4000] : error.Message;
            if (string.IsNullOrWhiteSpace(message)) message = "A preparação falhou. Confira a pasta preservada antes de tentar novamente.";
            if (intent is not null)
                await store.FinishTaskIntegrationPreparationAsync(intent with { State = TaskIntegrationPreparationState.NeedsAttention, Error = message }).ConfigureAwait(false);
            else if (reservation is not null) await store.ReleaseTaskIntegrationAsync(reservation.Id, message).ConfigureAwait(false);
            throw;
        }
    }
}
