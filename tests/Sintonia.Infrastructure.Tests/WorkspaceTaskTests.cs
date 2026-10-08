using Sintonia.Core;
using Sintonia.Infrastructure.Persistence;
using System.Text.Json;

namespace Sintonia.Infrastructure.Tests;

public sealed class WorkspaceTaskTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Sintonia-queue-tests-" + Guid.NewGuid());
    private string Database => Path.Combine(_directory, "queue.db");
    private async Task<(SqliteWorkspaceStore Store, WorkspaceProject Project, WorkspaceProposal Proposal)> SetupAsync(bool independent = false)
    {
        Directory.CreateDirectory(_directory); var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync();
        var project = await store.AddProjectAsync(_directory);
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Plano", ProviderKind.Codex, null, null,
            PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(conversation);
        var plan = new PlanProposal(1, "Portal de atendimento", "Atender solicitações pelo portal.",
            [new("implement", "Criar formulário", "Desenvolvimento", ProviderKind.Codex, null, ConversationAccess.WorkspaceWrite,
                "Criar formulário acessível.", ["src/"], [], ["Envio pelo teclado."]),
             new("review", "Revisar formulário", "Revisão", ProviderKind.Claude, "modelo-configurado", ConversationAccess.ReadOnly,
                "Conferir os critérios.", ["src/"], independent ? [] : ["implement"], ["Relatório verificável."])]);
        var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "Planejar", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run);
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```",
            FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        return (store, project, await store.CreateProposalAsync(project.Id, run.Id));
    }
    private static async Task<WorkspaceTaskBatch> EnqueueAsync(SqliteWorkspaceStore store, WorkspaceProject project, WorkspaceProposal proposal)
    {
        proposal = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
        return await store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision);
    }
    private static async Task<WorkspaceTask> CurrentAsync(SqliteWorkspaceStore store, WorkspaceProject project, string id) =>
        (await store.GetTaskBatchesAsync(project.Id)).SelectMany(b => b.Tasks).Single(t => t.Id == id);

    [Fact]
    public async Task EnqueueRequiresCurrentApprovalAndFreezesSnapshotWithoutExecuting()
    {
        var (store, project, proposal) = await SetupAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision));
        var approved = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnqueueProposalAsync(project.Id, approved.Id, proposal.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnqueueProposalAsync("wrong", approved.Id, approved.Revision));
        var batch = await store.EnqueueProposalAsync(project.Id, approved.Id, approved.Revision);
        var duplicate = await store.EnqueueProposalAsync(project.Id, approved.Id, approved.Revision);
        Assert.Equal(batch.Id, duplicate.Id); Assert.Equal(2, batch.Tasks.Count);
        Assert.All(batch.Tasks, t => { Assert.Equal(WorkspaceTaskState.Pending, t.State); Assert.Equal(0, t.Attempts); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveProposalAsync(approved with { State = ProposalReviewState.Draft }));
        var reopened = new SqliteWorkspaceStore(Database); await reopened.InitializeAsync();
        Assert.Equal(batch.Id, Assert.Single(await reopened.GetTaskBatchesAsync(project.Id)).Id);
        Assert.Empty(await reopened.GetTaskBatchesAsync("wrong"));
        Assert.All((await reopened.GetConversationsAsync(project.Id)).Where(c => c.IsTask), c => Assert.Null(c.NativeSessionId));
    }

    [Fact]
    public async Task DeliveryReviewAdjustmentsAndExactApprovedRunControlDependencies()
    {
        var (store, project, proposal) = await SetupAsync(); var batch = await EnqueueAsync(store, project, proposal);
        var first = batch.Tasks[0]; var second = batch.Tasks[1];
        var codex = new Provider(ProviderKind.Codex); var claude = new Provider(ProviderKind.Claude);
        var service = new WorkspaceChatService(store, [codex, claude]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendTaskAsync(project.Id, second.Id, new Progress(), CancellationToken.None));
        var run = await service.SendTaskAsync(project.Id, first.Id, new Progress(), CancellationToken.None);
        Assert.Equal(WorkspaceTaskState.AwaitingReview, (await CurrentAsync(store, project, first.Id)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendTaskAsync(project.Id, second.Id, new Progress(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReviewTaskAsync("wrong", first.Id, run.Id, true, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReviewTaskAsync(project.Id, first.Id, run.Id, false, " "));
        await store.ReviewTaskAsync(project.Id, first.Id, run.Id, false, "Acrescentar rótulos acessíveis.");
        Assert.Equal(WorkspaceTaskState.ChangesRequested, (await CurrentAsync(store, project, first.Id)).State);
        var adjusted = await service.SendTaskAsync(project.Id, first.Id, new Progress(), CancellationToken.None);
        Assert.Contains("Acrescentar rótulos", PromptData(codex.Requests[1]).GetProperty("adjustment").GetString()); Assert.Equal(run.ConversationId, adjusted.ConversationId);
        Assert.NotNull(codex.Requests[1].NativeSessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReviewTaskAsync(project.Id, first.Id, run.Id, true, ""));
        await store.ReviewTaskAsync(project.Id, first.Id, adjusted.Id, true, "Critérios conferidos.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReviewTaskAsync(project.Id, first.Id, adjusted.Id, false, "revogar"));
        await service.SendTaskAsync(project.Id, second.Id, new Progress(), CancellationToken.None);
        Assert.Contains(adjusted.Id, claude.Requests[0].Prompt);
        Assert.Contains("Critérios conferidos", PromptData(claude.Requests[0]).GetProperty("approvedDependencies")[0].GetProperty("review").GetString());
        Assert.Equal(ConversationAccess.ReadOnly, claude.Requests[0].Access); Assert.Equal("modelo-configurado", claude.Requests[0].Model);
        Assert.Equal(2, (await store.GetRunsAsync(first.ConversationId)).Count);
    }

    [Fact]
    public async Task QueueConversationsCannotBypassDispatchOrChangeTheirTaskContract()
    {
        var (store, project, proposal) = await SetupAsync(); var batch = await EnqueueAsync(store, project, proposal);
        var task = batch.Tasks[1]; var conversation = (await store.GetConversationsAsync(project.Id)).Single(c => c.Id == task.ConversationId);
        var provider = new Provider(ProviderKind.Claude); var service = new WorkspaceChatService(store, [provider]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(project, conversation, "bypass", new Progress(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(project, conversation with { IsTask = false }, "bypass", new Progress(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveConversationAsync(conversation with { Access = ConversationAccess.WorkspaceWrite }));
        Assert.Empty(provider.Requests); Assert.Empty(await store.GetRunsAsync(conversation.Id));
        Assert.Equal(0, (await CurrentAsync(store, project, task.Id)).Attempts);
    }

    [Theory]
    [InlineData("failed", WorkspaceTaskState.Failed)]
    [InlineData("blocked", WorkspaceTaskState.Blocked)]
    public async Task FailedOrBlockedAttemptsRemainUnapprovedAndStopAtLimit(string mode, WorkspaceTaskState state)
    {
        var (store, project, proposal) = await SetupAsync(); var batch = await EnqueueAsync(store, project, proposal);
        var task = batch.Tasks[0]; var provider = new Provider(ProviderKind.Codex, mode);
        var service = new WorkspaceChatService(store, [provider]);
        for (var i = 0; i < WorkspaceTaskPolicy.MaxAttempts; i++)
        {
            var run = await service.SendTaskAsync(project.Id, task.Id, new Progress(), CancellationToken.None);
            Assert.Equal(state, (await CurrentAsync(store, project, task.Id)).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReviewTaskAsync(project.Id, task.Id, run.Id, true, ""));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendTaskAsync(project.Id, task.Id, new Progress(), CancellationToken.None));
        Assert.Equal(3, provider.Requests.Count);
    }

    [Fact]
    public async Task ChatAndQueueShareReservationsAndCancellationKeepsAttemptHistory()
    {
        var (store, project, proposal) = await SetupAsync(independent: true); var batch = await EnqueueAsync(store, project, proposal);
        var provider = new Provider(ProviderKind.Codex, "wait"); var other = new Provider(ProviderKind.Claude);
        var service = new WorkspaceChatService(store, [provider, other]); using var stop = new CancellationTokenSource();
        var active = service.SendTaskAsync(project.Id, batch.Tasks[0].Id, new Progress(), stop.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendTaskAsync(project.Id, batch.Tasks[0].Id, new Progress(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendTaskAsync(project.Id, batch.Tasks[1].Id, new Progress(), CancellationToken.None));
        Assert.Equal(0, (await CurrentAsync(store, project, batch.Tasks[1].Id)).Attempts);
        var chat = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Chat", ProviderKind.Claude, null, null, "Conversa", "", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(chat);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(project, chat, "pedido", new Progress(), CancellationToken.None));
        stop.Cancel(); Assert.Equal(ChatRunState.Cancelled, (await active).State);
        Assert.Equal(WorkspaceTaskState.Cancelled, (await CurrentAsync(store, project, batch.Tasks[0].Id)).State);
        await service.SendTaskAsync(project.Id, batch.Tasks[1].Id, new Progress(), CancellationToken.None);
        Assert.Single(other.Requests);
    }

    [Fact]
    public async Task RecoveryMarksRunAndTaskInterruptedWithoutRepeatingEffects()
    {
        var (store, project, proposal) = await SetupAsync(); var batch = await EnqueueAsync(store, project, proposal); var task = batch.Tasks[0];
        var run = new ChatRun(Guid.NewGuid().ToString(), task.ConversationId, "pedido", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run, task.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(run with { Id = Guid.NewGuid().ToString() }, task.Id));
        var reopened = new SqliteWorkspaceStore(Database); await reopened.InitializeAsync(); await reopened.RecoverInterruptedRunsAsync();
        var interrupted = await CurrentAsync(reopened, project, task.Id);
        Assert.Equal(WorkspaceTaskState.Interrupted, interrupted.State); Assert.Equal(1, interrupted.Attempts); Assert.Equal(run.Id, interrupted.LastRunId);
        Assert.Equal(ChatRunState.Interrupted, Assert.Single(await reopened.GetRunsAsync(task.ConversationId)).State);
        await reopened.RecoverInterruptedRunsAsync(); Assert.Equal(1, (await CurrentAsync(reopened, project, task.Id)).Attempts);
    }

    [Fact]
    public async Task SchemaTwoMigrationPreservesApprovedProposalAndSource()
    {
        var (store, project, proposal) = await SetupAsync();
        var approved = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE work_tasks; DROP TABLE task_batches; PRAGMA user_version=2;"; command.ExecuteNonQuery();
        }
        var migrated = new SqliteWorkspaceStore(Database); await migrated.InitializeAsync();
        var saved = Assert.Single(await migrated.GetProposalsAsync(project.Id));
        Assert.Equal(approved.Revision, saved.Revision); Assert.Equal(ProposalReviewState.Approved, saved.State);
        Assert.Equal(2, (await migrated.EnqueueProposalAsync(project.Id, saved.Id, saved.Revision)).Tasks.Count);
    }

    private static JsonElement PromptData(ConversationRequest request)
    {
        using var data = JsonDocument.Parse(request.Prompt.Split('\n')[1]); return data.RootElement.Clone();
    }

    private sealed class Progress : IProgress<ConversationEvent> { public void Report(ConversationEvent value) { } }
    private sealed class Provider(ProviderKind kind, string mode = "completed") : IConversationProvider
    {
        public ProviderKind Kind => kind;
        public List<ConversationRequest> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Requests.Add(request); var native = request.NativeSessionId ?? Guid.NewGuid().ToString();
            progress.Report(new(ConversationEventKind.Session, "Provedor de teste", native, "modelo-teste")); Started.TrySetResult();
            if (mode == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (mode == "failed") throw new ProviderException("Falha de teste");
            return new(native, "modelo-teste", "Entrega simulada para revisão", mode == "blocked" ? ConversationOutcome.Blocked : ConversationOutcome.Completed,
                mode == "blocked" ? ["Write"] : []);
        }
    }
    public void Dispose()
    {
        if (!Path.GetFileName(_directory).StartsWith("Sintonia-queue-tests-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
