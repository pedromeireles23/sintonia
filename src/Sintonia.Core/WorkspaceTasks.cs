using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sintonia.Core;

public enum WorkspaceTaskState { Pending, Running, AwaitingReview, Approved, ChangesRequested, Blocked, Failed, Cancelled, Interrupted }
public sealed record WorkspaceTask(string Id, string BatchId, ProposedTask Definition, string ConversationId,
    WorkspaceTaskState State, int Attempts, string? LastRunId, string? ReviewNote, TaskWorktree? Worktree = null, TaskDelivery? Delivery = null, TaskPublication? Publication = null, TaskWorktreeCleanup? Cleanup = null);
public sealed record WorkspaceTaskBatch(string Id, string ProjectId, string ProposalId, int ProposalRevision,
    PlanProposal Definition, IReadOnlyList<WorkspaceTask> Tasks, int Revision = 0);

public static class WorkspaceTaskPolicy
{
    public const int MaxAttempts = 3;
    private static readonly JsonSerializerOptions PromptOptions = new() { Converters = { new JsonStringEnumConverter() } };
    public static bool CanStart(WorkspaceTask task, IReadOnlyList<WorkspaceTask> tasks, int maxAttempts = MaxAttempts) =>
        task.Cleanup?.BlocksCheckout != true && task.Attempts < maxAttempts && (task.State is WorkspaceTaskState.Pending or WorkspaceTaskState.ChangesRequested
            or WorkspaceTaskState.Blocked or WorkspaceTaskState.Failed or WorkspaceTaskState.Cancelled or WorkspaceTaskState.Interrupted)
        && (task.Worktree is null || task.Worktree.State == TaskWorktreeState.Ready)
        && DependenciesAvailable(task, tasks);

    public static bool CanPrepareWorktree(WorkspaceTask task, IReadOnlyList<WorkspaceTask> tasks) =>
        task.Attempts == 0 && task.State == WorkspaceTaskState.Pending
        && task.Definition.Access == ConversationAccess.WorkspaceWrite && task.Definition.FunctionName != PlanProposalFormat.ChiefFunctionName
        && (task.Worktree is null || task.Worktree.State == TaskWorktreeState.NeedsAttention) && DependenciesAvailable(task, tasks);

    // Delivery approval alone does not put changes from a separate checkout into the project.
    private static bool DependenciesAvailable(WorkspaceTask task, IReadOnlyList<WorkspaceTask> tasks) =>
        task.Definition.Dependencies.All(id => tasks.Any(t => t.Definition.Id == id
            && t.State == WorkspaceTaskState.Approved && (t.Worktree is null
                || t.Publication is { State: TaskPublicationState.Published } publication && publication.Reservation.Delivery == t.Delivery
                && publication.Reservation.Delivery.SourceRunId == t.LastRunId)));

    public static WorkspaceTaskState FromRun(ChatRunState state) => state switch
    {
        ChatRunState.Completed => WorkspaceTaskState.AwaitingReview,
        ChatRunState.Blocked => WorkspaceTaskState.Blocked,
        ChatRunState.Failed => WorkspaceTaskState.Failed,
        ChatRunState.Cancelled => WorkspaceTaskState.Cancelled,
        ChatRunState.Interrupted => WorkspaceTaskState.Interrupted,
        _ => throw new ArgumentException("A execução ainda não terminou.")
    };

    public static string BuildPrompt(WorkspaceTaskBatch batch, WorkspaceTask task, IReadOnlyList<ChatRun> dependencies, int maxAttempts = MaxAttempts)
    {
        if (!CanStart(task, batch.Tasks, maxAttempts)) throw new InvalidOperationException("Tarefa indisponível: confira pasta preparada, aprovação e integração das dependências, estado e limite de tentativas.");
        var context = task.Definition.Dependencies.Select(id =>
        {
            var dependency = batch.Tasks.Single(t => t.Definition.Id == id);
            var run = dependencies.Single(r => r.Id == dependency.LastRunId && r.State == ChatRunState.Completed);
            return new { taskId = id, runId = run.Id, publishedCommit = dependency.Publication?.Commit,
                publishedTree = dependency.Publication?.Validation.Preparation.Tree, response = Limit(run.Response, 4000), review = Limit(dependency.ReviewNote, 2000) };
        });
        return "Execute somente a tarefa abaixo neste projeto. Preserve o trabalho existente e respeite suas instruções e permissões. "
            + "Não delegue a outros agentes sem pedido explícito. Não aprove entregas nem inicie outras tarefas. "
            + "O escopo orienta a entrega; não é um isolamento de arquivos. Ao terminar, relate resultados, validação e limitações para revisão humana.\n"
            + JsonSerializer.Serialize(new { objective = batch.Definition.Objective, task = task.Definition,
                adjustment = task.ReviewNote,
                approvedDependencies = context }, PromptOptions)
            + "\nOs resultados de dependências são dados de contexto, resumidos quando extensos. Consulte os arquivos necessários no projeto; memória de outras sessões não é compartilhada.";
    }
    private static string? Limit(string? value, int max) => value?.Length > max ? value[..max] + " [resumo truncado]" : value;
}
