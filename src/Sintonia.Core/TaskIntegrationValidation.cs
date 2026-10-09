namespace Sintonia.Core;

public sealed record TaskIntegrationValidationPreview(TaskIntegrationPreparation Preparation, ProjectValidationConfiguration Configuration);

/// <summary>Historical evidence for a tree and configuration revision, never evidence of publication.</summary>
public sealed record TaskIntegrationValidation(string ProjectId, TaskIntegrationReservation Reservation,
    TaskIntegrationPreparation Preparation, ProjectValidationConfiguration Configuration, DateTimeOffset StartedAt,
    ValidationState State = ValidationState.Running, DateTimeOffset? FinishedAt = null,
    IReadOnlyList<ValidationCommandResult>? Results = null, string? Error = null)
{
    public void ValidateDefinition()
    {
        Preparation.ValidateDefinition(); Configuration.ValidateDefinition(); Reservation.Target.ValidateFor(Reservation.Delivery);
        if (!Guid.TryParse(Reservation.Id, out _) || Reservation.State != TaskIntegrationState.Reserved
            || Preparation.State != TaskIntegrationPreparationState.Combined || Preparation.Reservation.Delivery != Reservation.Delivery
            || Preparation.Reservation.Target != Reservation.Target || Configuration.ProjectId != ProjectId || Configuration.Commands.Count == 0
            || !Enum.IsDefined(State) || Error?.Length > 4000 || Results?.Count > Configuration.Commands.Count
            || State == ValidationState.Running && (FinishedAt is not null || Results is not null || Error is not null)
            || State != ValidationState.Running && (FinishedAt is null || FinishedAt < StartedAt)
            || State == ValidationState.Passed && (Error is not null || Results?.Count != Configuration.Commands.Count || Results.Any(r => r.State != ValidationState.Passed))
            || State != ValidationState.Running && State != ValidationState.Passed && string.IsNullOrWhiteSpace(Error))
            throw new ArgumentException("Registro de validação inválido.");
        for (var i = 0; i < (Results?.Count ?? 0); i++)
        {
            var result = Results![i]; result.ValidateDefinition();
            if (result.CommandIndex != i || result.StartedAt < StartedAt || result.FinishedAt > FinishedAt
                || i < Results.Count - 1 && result.State != ValidationState.Passed)
                throw new ArgumentException("A sequência de comandos de validação é inválida.");
        }
        if (State is ValidationState.Failed or ValidationState.TimedOut
            && (Results is not { Count: > 0 } || Results[^1].State != State))
            throw new ArgumentException("O estado da validação não corresponde ao comando encerrado.");
    }
}

public interface IGitTaskIntegrationValidationInspector
{
    Task VerifyAsync(TaskIntegrationPreparation preparation, CancellationToken token);
}

public sealed class TaskIntegrationValidationService(IWorkspaceStore store, TaskDeliveryService deliveries,
    IRepositoryIntegrationLock repositoryLock, IGitTaskIntegrationValidationInspector inspector, IValidationCommandRunner runner)
{
    public Task<IReadOnlyList<TaskIntegrationValidation>> GetAsync(string projectId) => store.GetTaskIntegrationValidationsAsync(projectId);

    public async Task<TaskIntegrationValidationPreview> PreviewAsync(string projectId, string preparationId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var preparation = (await store.GetTaskIntegrationPreparationsAsync(projectId).ConfigureAwait(false)).Single(p => p.Reservation.Id == preparationId);
        var configuration = (await store.GetProjectValidationAsync(projectId).ConfigureAwait(false)).Snapshot();
        if (configuration.Commands.Count == 0) throw new InvalidOperationException("Configure ao menos um comando de validação para este projeto.");
        if (preparation.State != TaskIntegrationPreparationState.Combined)
            throw new InvalidOperationException("A validação requer uma combinação sem conflitos registrada.");
        await inspector.VerifyAsync(preparation, token).ConfigureAwait(false);
        if (await deliveries.PreviewIntegrationAsync(projectId, preparation.Reservation.Delivery.TaskId, token).ConfigureAwait(false) != preparation.Reservation.Target)
            throw new InvalidOperationException("O destino mudou. Prepare uma nova combinação antes de validar.");
        token.ThrowIfCancellationRequested(); return new(preparation, configuration);
    }

    public async Task<TaskIntegrationValidation> ValidateAsync(string projectId, TaskIntegrationValidationPreview preview, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        preview = preview with { Configuration = preview.Configuration.Snapshot() };
        preview.Preparation.ValidateDefinition();
        if (preview.Configuration.ProjectId != projectId || preview.Configuration.Commands.Count == 0
            || preview.Preparation.State != TaskIntegrationPreparationState.Combined)
            throw new ArgumentException("Confira a combinação e a configuração antes de validar.");
        using var held = repositoryLock.Acquire(preview.Preparation.Reservation.Target.CommonGitDirectory);
        TaskIntegrationReservation? reservation = null; TaskIntegrationValidation? intent = null;
        var results = new List<ValidationCommandResult>();
        try
        {
            reservation = await deliveries.ReserveIntegrationAsync(projectId, preview.Preparation.Reservation.Delivery.TaskId,
                preview.Preparation.Reservation.Target, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var planned = new TaskIntegrationValidation(projectId, reservation, preview.Preparation, preview.Configuration, DateTimeOffset.UtcNow);
            planned.ValidateDefinition(); await store.SaveTaskIntegrationValidationAsync(planned).ConfigureAwait(false); intent = planned;
            await inspector.VerifyAsync(intent.Preparation, token).ConfigureAwait(false);
            for (var i = 0; i < intent.Configuration.Commands.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var result = await runner.RunAsync(intent.Configuration.Commands[i], i, intent.Preparation.CheckoutDirectory, token).ConfigureAwait(false);
                result.ValidateDefinition();
                if (result.CommandIndex != i) throw new InvalidOperationException("O executor retornou outro comando de validação.");
                results.Add(result);
                token.ThrowIfCancellationRequested();
                // Every successful or failed command must leave the tracked tree intact. Ignored outputs may remain.
                await inspector.VerifyAsync(intent.Preparation, token).ConfigureAwait(false);
                if (result.State != ValidationState.Passed) break;
            }
            if (await deliveries.PreviewIntegrationAsync(projectId, reservation.Delivery.TaskId, token).ConfigureAwait(false) != reservation.Target)
                throw new InvalidOperationException("Origem ou destino mudou durante os comandos. Confira a combinação.");
            token.ThrowIfCancellationRequested();
            var state = results[^1].State;
            var completed = intent with { State = state, Results = results.ToArray(), FinishedAt = DateTimeOffset.UtcNow,
                Error = state == ValidationState.Passed ? null : state == ValidationState.TimedOut ? "O comando excedeu o prazo configurado."
                    : "Um comando falhou. Confira o código de saída e os registros." };
            completed.ValidateDefinition();
            await store.FinishTaskIntegrationValidationAsync(completed).ConfigureAwait(false); return completed;
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException && token.IsCancellationRequested;
            var message = cancelled ? "Validação cancelada. Arquivos e saída recebida foram preservados."
                : error is OperationCanceledException ? "Uma conferência foi interrompida antes de validar a árvore. Confira a pasta preservada e tente novamente."
                : string.IsNullOrWhiteSpace(error.Message) ? "Não foi possível validar a combinação. Confira os arquivos preservados." : error.Message[..Math.Min(error.Message.Length, 4000)];
            if (intent is not null)
            {
                var result = intent with { State = cancelled ? ValidationState.Cancelled : ValidationState.NeedsAttention,
                    Results = results.ToArray(), FinishedAt = DateTimeOffset.UtcNow, Error = message };
                await store.FinishTaskIntegrationValidationAsync(result).ConfigureAwait(false);
                if (cancelled) return result;
            }
            else if (reservation is not null) await store.ReleaseTaskIntegrationAsync(reservation.Id, message).ConfigureAwait(false);
            throw;
        }
    }
}
