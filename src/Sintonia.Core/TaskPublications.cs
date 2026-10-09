namespace Sintonia.Core;

public enum TaskPublicationState { Publishing, Published, Cancelled, Interrupted, NeedsAttention }
public sealed record TaskPublication(string ProjectId, TaskIntegrationReservation Reservation, TaskIntegrationValidation Validation,
    DateTimeOffset StartedAt, TaskPublicationState State = TaskPublicationState.Publishing, string? Commit = null,
    DateTimeOffset? FinishedAt = null, string? Error = null)
{
    public void ValidateDefinition()
    {
        Validation.ValidateDefinition(); Reservation.Target.ValidateFor(Reservation.Delivery);
        if (!Guid.TryParse(ProjectId, out _) || ProjectId != Validation.ProjectId || !Guid.TryParse(Reservation.Id, out _)
            || Reservation.State != TaskIntegrationState.Reserved || Validation.State != ValidationState.Passed
            || Reservation.Delivery != Validation.Reservation.Delivery || Reservation.Target != Validation.Reservation.Target
            || !Enum.IsDefined(State) || Commit is not null && (!TaskDelivery.IsObjectId(Commit) || Commit.Length != Reservation.Target.Commit.Length)
            || Error?.Length > 4000 || State == TaskPublicationState.Publishing && (FinishedAt is not null || Error is not null)
            || State != TaskPublicationState.Publishing && (FinishedAt is null || FinishedAt < StartedAt)
            || State == TaskPublicationState.Published && (Commit is null || Error is not null)
            || State is TaskPublicationState.Cancelled or TaskPublicationState.Interrupted or TaskPublicationState.NeedsAttention && string.IsNullOrWhiteSpace(Error))
            throw new ArgumentException("Registro de publicação inválido.");
    }
}

public interface IGitTaskPublisher
{
    Task VerifyAsync(TaskIntegrationValidation validation, CancellationToken token);
    Task<string> CreateCommitAsync(TaskPublication intent, CancellationToken token);
    /// <summary>Once applying starts, finish verification within a bounded native operation instead of interrupting checkout.</summary>
    Task ApplyAsync(TaskPublication intent);
}

public interface IGitTaskRevisionInspector
{
    Task VerifyRevisionAsync(string directory, string commit, CancellationToken token);
}

public sealed class TaskPublicationService(IWorkspaceStore store, TaskDeliveryService deliveries,
    IRepositoryIntegrationLock repositoryLock, IGitTaskPublisher publisher)
{
    public Task<IReadOnlyList<TaskPublication>> GetAsync(string projectId) => store.GetTaskPublicationsAsync(projectId);
    public async Task<TaskIntegrationValidation> PreviewAsync(string projectId, string validationId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var validation = (await store.GetTaskIntegrationValidationsAsync(projectId).ConfigureAwait(false)).Single(v => v.Reservation.Id == validationId);
        if (validation.State != ValidationState.Passed) throw new InvalidOperationException("A publicação exige todos os critérios aprovados para esta combinação.");
        if ((await store.GetTaskPublicationsAsync(projectId).ConfigureAwait(false)).Any(p => p.State == TaskPublicationState.Published && p.Reservation.Delivery.TaskId == validation.Reservation.Delivery.TaskId))
            throw new InvalidOperationException("Esta entrega já foi integrada. Atualize a fila para consultar o commit publicado.");
        await VerifyCriteriaAsync(validation).ConfigureAwait(false);
        await publisher.VerifyAsync(validation, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); return validation;
    }
    private async Task VerifyCriteriaAsync(TaskIntegrationValidation validation)
    {
        var configuration = await store.GetProjectValidationAsync(validation.ProjectId).ConfigureAwait(false);
        if (configuration.Revision != validation.Configuration.Revision)
            throw new InvalidOperationException("Os critérios mudaram. Valide a combinação novamente antes de publicar.");
    }
    public async Task<TaskPublication> PublishAsync(string projectId, TaskIntegrationValidation preview, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); preview.ValidateDefinition();
        if (projectId != preview.ProjectId || preview.State != ValidationState.Passed) throw new ArgumentException("Confira a validação aprovada antes de publicar.");
        using var held = repositoryLock.Acquire(preview.Reservation.Target.CommonGitDirectory);
        TaskIntegrationReservation? reservation = null; TaskPublication? intent = null; var applying = false;
        try
        {
            await VerifyCriteriaAsync(preview).ConfigureAwait(false);
            reservation = await deliveries.ReserveIntegrationAsync(projectId, preview.Reservation.Delivery.TaskId, preview.Reservation.Target, token).ConfigureAwait(false);
            var planned = new TaskPublication(projectId, reservation, preview, DateTimeOffset.UtcNow);
            await store.SaveTaskPublicationAsync(planned).ConfigureAwait(false); intent = planned;
            await publisher.VerifyAsync(preview, token).ConfigureAwait(false);
            var commit = await publisher.CreateCommitAsync(intent, token).ConfigureAwait(false);
            var candidate = intent with { Commit = commit }; candidate.ValidateDefinition();
            await store.RecordTaskPublicationCommitAsync(candidate).ConfigureAwait(false); intent = candidate;
            await publisher.VerifyAsync(preview, token).ConfigureAwait(false);
            await VerifyCriteriaAsync(preview).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); applying = true;
            await publisher.ApplyAsync(intent).ConfigureAwait(false);
            var result = intent with { State = TaskPublicationState.Published, FinishedAt = DateTimeOffset.UtcNow };
            await store.FinishTaskPublicationAsync(result).ConfigureAwait(false); return result;
        }
        catch (Exception error)
        {
            var cancelled = !applying && token.IsCancellationRequested && error is OperationCanceledException;
            var message = applying ? "A aplicação pode ter produzido efeitos no destino. Confira o commit registrado e os arquivos; nada será repetido automaticamente. " + error.Message
                : cancelled ? "Publicação cancelada antes de aplicar arquivos no destino." : error.Message;
            message = message[..Math.Min(message.Length, 4000)];
            if (intent is not null)
            {
                var result = intent with { State = cancelled ? TaskPublicationState.Cancelled : TaskPublicationState.NeedsAttention, FinishedAt = DateTimeOffset.UtcNow, Error = message };
                await store.FinishTaskPublicationAsync(result).ConfigureAwait(false); return result;
            }
            if (reservation is not null) await store.ReleaseTaskIntegrationAsync(reservation.Id, message).ConfigureAwait(false);
            throw;
        }
    }
}
