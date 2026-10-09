using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Infrastructure.Tests;

public sealed class TaskPlanUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sintonia-plan-update-" + Guid.NewGuid());
    private string Database => Path.Combine(_root, "test.db");
    private static PlanProposal Plan() => new(1, "Plano geral", "Entregar relatório e revisão", [
        new("write", "Escrever relatório", "Documentação", ProviderKind.Codex, null, ConversationAccess.ReadOnly, "Produzir texto", ["."], [], ["Texto revisável"]),
        new("review", "Revisar relatório", "Revisão", ProviderKind.Claude, null, ConversationAccess.ReadOnly, "Revisar texto", ["."], ["write"], ["Revisão verificável"])]);
    private async Task<(SqliteWorkspaceStore Store, WorkspaceProject Project, WorkspaceTaskBatch Batch)> SeedAsync()
    {
        Directory.CreateDirectory(_root); var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync(); var project = await store.AddProjectAsync(_root);
        var proposal = await ProposeAsync(store, project, Plan()); return (store, project, await store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision));
    }
    private static async Task<WorkspaceProposal> ProposeAsync(SqliteWorkspaceStore store, WorkspaceProject project, PlanProposal plan)
    {
        var chief = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Chefia", ProviderKind.Codex, null, null, PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(chief); var now = DateTimeOffset.UtcNow;
        var run = new ChatRun(Guid.NewGuid().ToString(), chief.Id, "planejar", null, ChatRunState.Running, now, null, null); await store.BeginRunAsync(run);
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```", FinishedAt = DateTimeOffset.UtcNow }, chief, []);
        var proposal = await store.CreateProposalAsync(project.Id, run.Id); return await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
    }
    private static ChatRun Run(WorkspaceTask task) => new(Guid.NewGuid().ToString(), task.ConversationId, "executar", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
    [Fact]
    public async Task RevisionPreservesStartedHistoryAndChangesPendingContractsWithoutExecutionOrDuplicateEnqueue()
    {
        var (store, project, batch) = await SeedAsync(); var first = batch.Tasks[0]; var run = Run(first); await store.BeginRunAsync(run, first.Id);
        var conversation = (await store.GetConversationsAsync(project.Id)).Single(c => c.Id == first.ConversationId);
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "resultado preservado", FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        var revised = batch.Definition with { Tasks = [batch.Definition.Tasks[0], batch.Definition.Tasks[1] with { Provider = ProviderKind.Codex, Title = "Revisão atualizada", Instructions = "Revisar também a conclusão" },
            batch.Definition.Tasks[1] with { Id = "extra", Title = "Conferência final", Dependencies = ["review"] }] };
        var proposal = await ProposeAsync(store, project, revised);
        var updated = await store.ReviseTaskBatchAsync(project.Id, batch.Id, batch.Revision, proposal.Id, proposal.Revision);
        Assert.Equal(1, updated.Revision); Assert.Equal(3, updated.Tasks.Count); Assert.Equal(first.Id, updated.Tasks[0].Id);
        Assert.Equal(WorkspaceTaskState.AwaitingReview, updated.Tasks[0].State); Assert.Equal(run.Id, updated.Tasks[0].LastRunId);
        Assert.Equal(batch.Tasks[1].ConversationId, updated.Tasks[1].ConversationId); Assert.Equal(0, updated.Tasks[2].Attempts);
        Assert.Equal(ProviderKind.Codex, (await store.GetConversationsAsync(project.Id)).Single(c => c.Id == batch.Tasks[1].ConversationId).Provider);
        Assert.Equal("resultado preservado", (await store.GetRunsAsync(first.ConversationId)).Single().Response);
        Assert.Contains(proposal.Id, await store.GetAppliedProposalIdsAsync(project.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveProposalAsync(proposal with { State = ProposalReviewState.Draft }));
        var reopened = new SqliteWorkspaceStore(Database); await reopened.InitializeAsync(); Assert.Equal(1, (await reopened.GetTaskBatchesAsync(project.Id)).Single().Revision);
    }
    [Fact]
    public async Task FrozenTasksAndStaleProposalOrBatchRefuseUpdateAndPendingRemovalRetainsConversation()
    {
        var (store, project, batch) = await SeedAsync(); var run = Run(batch.Tasks[0]); await store.BeginRunAsync(run, batch.Tasks[0].Id);
        var invalid = await ProposeAsync(store, project, batch.Definition with { Tasks = [batch.Definition.Tasks[0] with { Instructions = "alteração proibida" }, batch.Definition.Tasks[1]] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReviseTaskBatchAsync(project.Id, batch.Id, 0, invalid.Id, invalid.Revision));
        var proposal = await ProposeAsync(store, project, batch.Definition with { Tasks = [batch.Definition.Tasks[0]] });
        var stale = proposal; proposal = await store.SaveProposalAsync(proposal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReviseTaskBatchAsync(project.Id, batch.Id, 0, stale.Id, stale.Revision));
        var updated = await store.ReviseTaskBatchAsync(project.Id, batch.Id, 0, proposal.Id, proposal.Revision); Assert.Single(updated.Tasks);
        Assert.Contains(await store.GetConversationsAsync(project.Id), c => c.Id == batch.Tasks[1].ConversationId && !c.IsTask);
        var next = await ProposeAsync(store, project, updated.Definition);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReviseTaskBatchAsync(project.Id, batch.Id, 0, next.Id, next.Revision));
        await store.RecoverInterruptedRunsAsync(); Assert.Equal(WorkspaceTaskState.Interrupted, (await store.GetTaskBatchesAsync(project.Id)).Single().Tasks[0].State);
    }
    [Fact]
    public async Task StaleTaskDefinitionAndExecutionSettingsRefuseReservationWithoutConsumingAttempt()
    {
        var (store, project, batch) = await SeedAsync(); var task = batch.Tasks[0]; var settings = await store.GetProjectExecutionSettingsAsync(project.Id);
        var proposal = await ProposeAsync(store, project, batch.Definition with { Tasks = [task.Definition with { Instructions = "contrato atualizado" }, batch.Definition.Tasks[1]] });
        await store.ReviseTaskBatchAsync(project.Id, batch.Id, 0, proposal.Id, proposal.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(Run(task), task.Id, expectedDefinition: task.Definition));
        await store.SaveProjectExecutionSettingsAsync(settings with { MaxAttempts = 1, MaxExecutionSeconds = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(Run(task), task.Id, expectedSettings: settings));
        var current = (await store.GetTaskBatchesAsync(project.Id)).Single().Tasks[0]; Assert.Equal(0, current.Attempts); Assert.Empty(await store.GetRunsAsync(task.ConversationId));
    }
    [Fact]
    public async Task TimeoutAndAttemptLimitsApplyToChatAndQueueAndLateSuccessCannotPass()
    {
        var (store, project, batch) = await SeedAsync(); await store.SaveProjectExecutionSettingsAsync(new(project.Id, MaxAttempts: 1, MaxExecutionSeconds: 1));
        var worker = new ExpiringWorker(); var service = new WorkspaceChatService(store, [worker]);
        var result = await service.SendTaskAsync(project.Id, batch.Tasks[0].Id, new Progress(), CancellationToken.None);
        Assert.Equal(ChatRunState.Failed, result.State); Assert.Contains("prazo", result.Error); Assert.Contains("parcial", result.Response);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendTaskAsync(project.Id, batch.Tasks[0].Id, new Progress(), CancellationToken.None));
        var conversation = (await store.GetConversationsAsync(project.Id)).First(c => !c.IsTask);
        result = await service.SendAsync(project, conversation, "pedido", new Progress(), CancellationToken.None); Assert.Equal(ChatRunState.Failed, result.State); Assert.Equal(2, worker.Calls);
        Assert.Equal(1, (await store.GetTaskBatchesAsync(project.Id)).Single().Tasks[0].Attempts);
    }
    [Fact]
    public async Task ConcurrentRevisionsHaveOneWinnerAndPreserveTheLosingProposalForReview()
    {
        var (store, project, batch) = await SeedAsync(); var other = new SqliteWorkspaceStore(Database); await other.InitializeAsync();
        var first = await ProposeAsync(store, project, batch.Definition with { Title = "Primeira revisão", Tasks = [batch.Definition.Tasks[0], batch.Definition.Tasks[1] with { Title = "Revisão A" }] });
        var second = await ProposeAsync(store, project, batch.Definition with { Title = "Segunda revisão", Tasks = [batch.Definition.Tasks[0], batch.Definition.Tasks[1] with { Title = "Revisão B" }] });
        async Task<bool> Apply(SqliteWorkspaceStore writer, WorkspaceProposal proposal)
        {
            try { await writer.ReviseTaskBatchAsync(project.Id, batch.Id, 0, proposal.Id, proposal.Revision); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(Apply(store, first), Apply(other, second)); Assert.Single(outcomes, success => success);
        var winner = outcomes[0] ? first : second; var loser = outcomes[0] ? second : first;
        var updated = (await store.GetTaskBatchesAsync(project.Id)).Single(); Assert.Equal(1, updated.Revision);
        Assert.Equal(winner.Definition.Title, updated.Definition.Title); Assert.Equal(batch.Tasks[1].ConversationId, updated.Tasks[1].ConversationId);
        Assert.Equal(winner.Definition.Tasks[1].Title, (await store.GetConversationsAsync(project.Id)).Single(c => c.Id == updated.Tasks[1].ConversationId).Title);
        Assert.Contains(winner.Id, await store.GetAppliedProposalIdsAsync(project.Id)); Assert.DoesNotContain(loser.Id, await store.GetAppliedProposalIdsAsync(project.Id));
        Assert.All(updated.Tasks, task => { Assert.Equal(0, task.Attempts); Assert.Equal(WorkspaceTaskState.Pending, task.State); });
        await store.SaveProposalAsync(loser with { State = ProposalReviewState.Draft });
    }
    [Fact]
    public async Task Schema11MigrationKeepsPlansAndSessionsAndDefaultsNewLimits()
    {
        var (store, project, batch) = await SeedAsync();
        using (var connection = new SqliteConnection("Data Source=" + Database + ";Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TABLE project_execution_limits; DROP TABLE task_plan_revisions; PRAGMA user_version=11;"; command.ExecuteNonQuery(); }
        await store.InitializeAsync(); var settings = await store.GetProjectExecutionSettingsAsync(project.Id); Assert.Equal(3, settings.MaxAttempts); Assert.Equal(300, settings.MaxExecutionSeconds);
        Assert.Equal(JsonSerializer.Serialize(batch), JsonSerializer.Serialize((await store.GetTaskBatchesAsync(project.Id)).Single()));
    }
    private sealed class ExpiringWorker : IConversationProvider
    {
        public ProviderKind Kind => ProviderKind.Codex;
        public int Calls { get; private set; }
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Calls++; progress.Report(new(ConversationEventKind.TextDelta, "parcial"));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } catch (OperationCanceledException) { }
            await Task.Delay(20); return new("sessão-teste", "modelo-teste", "resposta tardia", ConversationOutcome.Completed, []);
        }
    }
    private sealed class Progress : IProgress<ConversationEvent> { public void Report(ConversationEvent value) { } }
    public void Dispose()
    {
        if (Directory.Exists(_root) && Path.GetFileName(_root).StartsWith("Sintonia-plan-update-", StringComparison.Ordinal)) Directory.Delete(_root, true);
    }
}
