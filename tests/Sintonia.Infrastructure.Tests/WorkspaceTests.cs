using Sintonia.Core;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Infrastructure.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Sintonia-tests-" + Guid.NewGuid(), "projeto ação ' com espaços");
    private string Database => Path.Combine(_directory, "history.db");
    public WorkspaceTests() => Directory.CreateDirectory(_directory);
    private async Task<SqliteWorkspaceStore> StoreAsync()
    {
        var store = new SqliteWorkspaceStore(Database);
        await store.InitializeAsync();
        return store;
    }
    private static WorkspaceConversation Conversation(WorkspaceProject project, ProviderKind provider = ProviderKind.Codex,
        ConversationAccess access = ConversationAccess.ReadOnly) => new(Guid.NewGuid().ToString(), project.Id, "Conversa", provider, null, null, "Revisão", "Confira critérios.", access);

    private static PlanProposal Plan() => new(1, "Menu do jogo", "Entrar pelo menu.",
        [new("task-1", "Criar menu", "Interface", ProviderKind.Codex, null, ConversationAccess.WorkspaceWrite,
            "Criar tela inicial.", ["src/"], [], ["Botão inicia o jogo."]),
         new("task-2", "Revisar menu", "Revisão", ProviderKind.Claude, null, ConversationAccess.ReadOnly,
            "Revisar entrega.", ["src/"], ["task-1"], ["Conferir acessibilidade."])]);
    private static async Task<ChatRun> ProposalSourceAsync(SqliteWorkspaceStore store, WorkspaceProject project,
        ChatRunState state = ChatRunState.Completed, string? response = null)
    {
        var conversation = Conversation(project) with { FunctionName = PlanProposalFormat.ChiefFunctionName };
        await store.SaveConversationAsync(conversation);
        var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "Planejar menu.", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run);
        run = run with { Response = response ?? "```sintonia-plan\n" + PlanProposalFormat.Serialize(Plan()) + "\n```", State = state, FinishedAt = DateTimeOffset.UtcNow };
        await store.FinishRunAsync(run, conversation, []);
        return run;
    }

    [Fact]
    public async Task ProposalReviewSurvivesRestartAndReimportPreservesApproval()
    {
        var store = await StoreAsync(); var project = await store.AddProjectAsync(_directory);
        var run = await ProposalSourceAsync(store, project);
        var proposal = await store.CreateProposalAsync(project.Id, run.Id);
        Assert.Equal(ProposalReviewState.Draft, proposal.State);
        var approved = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved,
            Definition = proposal.Definition with { Title = "Plano revisado pelo usuário" } });
        var reopened = await StoreAsync();
        var saved = await reopened.CreateProposalAsync(project.Id, run.Id);
        Assert.Equal(approved.Id, saved.Id); Assert.Equal(approved.Revision, saved.Revision);
        Assert.Equal(ProposalReviewState.Approved, saved.State);
        Assert.Equal("Plano revisado pelo usuário", saved.Definition.Title);
        Assert.Single(await reopened.GetProposalsAsync(project.Id));
        Assert.Equal(run.Response, Assert.Single(await reopened.GetRunsAsync(run.ConversationId)).Response);
    }

    [Theory]
    [InlineData(ChatRunState.Blocked)]
    [InlineData(ChatRunState.Failed)]
    [InlineData(ChatRunState.Interrupted)]
    public async Task OnlyCompletedResponsesCanBecomeProposals(ChatRunState state)
    {
        var store = await StoreAsync(); var project = await store.AddProjectAsync(_directory);
        var run = await ProposalSourceAsync(store, project, state);
        await Assert.ThrowsAsync<PlanValidationException>(() => store.CreateProposalAsync(project.Id, run.Id));
        Assert.Empty(await store.GetProposalsAsync(project.Id));
    }

    [Fact]
    public async Task ProposalSourceMustBelongToProjectAndContainValidPlan()
    {
        var store = await StoreAsync(); var project = await store.AddProjectAsync(_directory);
        var secondPath = Path.Combine(_directory, "outro"); Directory.CreateDirectory(secondPath);
        var second = await store.AddProjectAsync(secondPath);
        var run = await ProposalSourceAsync(store, project);
        await Assert.ThrowsAsync<PlanValidationException>(() => store.CreateProposalAsync(second.Id, run.Id));
        var invalid = await ProposalSourceAsync(store, project, response: "Texto livre sem proposta.");
        await Assert.ThrowsAsync<PlanValidationException>(() => store.CreateProposalAsync(project.Id, invalid.Id));
        Assert.Empty(await store.GetProposalsAsync(project.Id)); Assert.Empty(await store.GetProposalsAsync(second.Id));
    }

    [Fact]
    public async Task InvalidEditsStaleRevisionsAndChangedOriginCannotOverwriteProposal()
    {
        var store = await StoreAsync(); var project = await store.AddProjectAsync(_directory);
        var run = await ProposalSourceAsync(store, project);
        var original = await store.CreateProposalAsync(project.Id, run.Id);
        var saved = await store.SaveProposalAsync(original with { State = ProposalReviewState.Approved });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveProposalAsync(original));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveProposalAsync(saved with { ProjectId = "another-project" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveProposalAsync(saved with { SourceRunId = "another-run" }));
        await Assert.ThrowsAsync<PlanValidationException>(() => store.SaveProposalAsync(saved with
        {
            Definition = saved.Definition with { Tasks = [saved.Definition.Tasks[0] with { Dependencies = ["missing"] }] }
        }));
        Assert.Equal(saved.Revision, Assert.Single(await store.GetProposalsAsync(project.Id)).Revision);
        Assert.Equal(ProposalReviewState.Approved, Assert.Single(await store.GetProposalsAsync(project.Id)).State);
        var draft = await store.SaveProposalAsync(saved with { State = ProposalReviewState.Draft, Definition = saved.Definition with { Title = "Em ajuste" } });
        Assert.Equal(ProposalReviewState.Draft, draft.State);
    }

    [Fact]
    public async Task VersionOneMigrationPreservesConversationAndRunHistory()
    {
        var store = await StoreAsync(); var project = await store.AddProjectAsync(_directory);
        var run = await ProposalSourceAsync(store, project);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE proposals; PRAGMA user_version=1;"; command.ExecuteNonQuery();
        }
        var migrated = await StoreAsync();
        Assert.Equal(run.Response, Assert.Single(await migrated.GetRunsAsync(run.ConversationId)).Response);
        Assert.Empty(await migrated.GetProposalsAsync(project.Id));
        Assert.Equal(project.Id, (await migrated.CreateProposalAsync(project.Id, run.Id)).ProjectId);
    }

    [Fact]
    public async Task ChiefUsesReadOnlyRequestWithoutPermissionHostAndDoesNotPersistProtocolInstructions()
    {
        var store = await StoreAsync(); var project = await store.AddProjectAsync(_directory);
        var conversation = Conversation(project, access: ConversationAccess.WorkspaceWrite) with
            { FunctionName = PlanProposalFormat.ChiefFunctionName, Instructions = "Contexto adicional." };
        await store.SaveConversationAsync(conversation);
        var provider = new TestProvider(ProviderKind.Codex); var service = new WorkspaceChatService(store, [provider]);
        await service.SendAsync(project, conversation, "Planejar.", new InlineProgress(), CancellationToken.None, (_, _) => Task.FromResult(true));
        Assert.Equal(ConversationAccess.ReadOnly, provider.LastRequest!.Access); Assert.Null(provider.LastRequest.PermissionHandler);
        Assert.Contains("sintonia-plan", provider.LastRequest.Instructions);
        var saved = Assert.Single(await store.GetConversationsAsync(project.Id));
        Assert.Equal("Contexto adicional.", saved.Instructions); Assert.Equal(ConversationAccess.ReadOnly, saved.Access);
        await service.SendAsync(project, saved, "Replanejar.", new InlineProgress(), CancellationToken.None);
        Assert.Equal(1, provider.LastRequest!.Instructions.Split("Atue como chefe do projeto.", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task DuplicatePathReturnsSameProjectAndConversationsStaySeparatedAcrossRestart()
    {
        var store = await StoreAsync();
        var first = await store.AddProjectAsync(_directory);
        var same = await store.AddProjectAsync(_directory + Path.DirectorySeparatorChar);
        Assert.Equal(first.Id, same.Id);
        var secondPath = Path.Combine(_directory, "segundo");
        Directory.CreateDirectory(secondPath);
        var second = await store.AddProjectAsync(secondPath);
        var conversation = Conversation(first);
        await store.SaveConversationAsync(conversation);
        var reopened = await StoreAsync();
        Assert.Equal(2, (await reopened.GetProjectsAsync()).Count);
        Assert.Equal(conversation, Assert.Single(await reopened.GetConversationsAsync(first.Id)));
        Assert.Empty(await reopened.GetConversationsAsync(second.Id));
    }

    [Fact]
    public async Task RecoveryKeepsNativeSessionAndPartialResponseWithoutResubmitting()
    {
        var store = await StoreAsync();
        var project = await store.AddProjectAsync(_directory);
        var conversation = Conversation(project);
        await store.SaveConversationAsync(conversation);
        var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "pedido", "parcial", ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run);
        conversation = conversation with { NativeSessionId = Guid.NewGuid().ToString(), Model = "modelo" };
        await store.CheckpointRunAsync(run, conversation);
        var reopened = await StoreAsync();
        await reopened.RecoverInterruptedRunsAsync();
        var recovered = Assert.Single(await reopened.GetRunsAsync(conversation.Id));
        Assert.Equal(ChatRunState.Interrupted, recovered.State);
        Assert.Equal("parcial", recovered.Response);
        Assert.Equal(conversation.NativeSessionId, Assert.Single(await reopened.GetConversationsAsync(project.Id)).NativeSessionId);
        Assert.NotNull(recovered.FinishedAt);
    }

    [Fact]
    public async Task OneLiveRunAndImmutableProjectProviderPreventDuplicateOrCrossProjectWrites()
    {
        var store = await StoreAsync();
        var project = await store.AddProjectAsync(_directory);
        var conversation = Conversation(project);
        await store.SaveConversationAsync(conversation);
        var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "pedido", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.BeginRunAsync(run with { Id = Guid.NewGuid().ToString() }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveConversationAsync(conversation with { Provider = ProviderKind.Claude }));
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "entrega", FinishedAt = DateTimeOffset.UtcNow }, conversation, [new(run.Id, ConversationEventKind.Tool, "Read")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FinishRunAsync(run with { State = ChatRunState.Failed }, conversation, []));
        Assert.Single(await store.GetEventsAsync(conversation.Id));
        Assert.Equal(ChatRunState.Completed, Assert.Single(await store.GetRunsAsync(conversation.Id)).State);
    }

    [Theory]
    [InlineData(false, false, ChatRunState.Completed)]
    [InlineData(true, false, ChatRunState.Blocked)]
    [InlineData(false, true, ChatRunState.Failed)]
    public async Task ServicePersistsNativeOutcomeInsteadOfTreatingEverythingAsSuccess(bool denied, bool failed, ChatRunState expected)
    {
        var store = await StoreAsync();
        var project = await store.AddProjectAsync(_directory);
        var conversation = Conversation(project);
        await store.SaveConversationAsync(conversation);
        var provider = new TestProvider(ProviderKind.Codex, denied, failed);
        var service = new WorkspaceChatService(store, [provider]);
        var result = await service.SendAsync(project, conversation, "pedido", new InlineProgress(), CancellationToken.None);
        Assert.Equal(expected, result.State);
        Assert.Equal(result, Assert.Single(await store.GetRunsAsync(conversation.Id)));
        Assert.NotNull(Assert.Single(await store.GetConversationsAsync(project.Id)).NativeSessionId);
    }

    [Fact]
    public async Task ReservationsLimitProviderAndSerializeWritingUntilStopped()
    {
        var store = await StoreAsync();
        var project = await store.AddProjectAsync(_directory);
        var writing = Conversation(project, access: ConversationAccess.WorkspaceWrite);
        var other = Conversation(project, ProviderKind.Claude);
        var sameProvider = Conversation(project);
        foreach (var conversation in new[] { writing, other, sameProvider }) await store.SaveConversationAsync(conversation);
        var codex = new TestProvider(ProviderKind.Codex, wait: true);
        var claude = new TestProvider(ProviderKind.Claude);
        var service = new WorkspaceChatService(store, [codex, claude]);
        using var cancel = new CancellationTokenSource();
        var active = service.SendAsync(project, writing, "pedido", new InlineProgress(), cancel.Token);
        await codex.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(project, other, "pedido", new InlineProgress(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(project, sameProvider, "pedido", new InlineProgress(), CancellationToken.None));
        cancel.Cancel();
        Assert.Equal(ChatRunState.Cancelled, (await active).State);
        Assert.Equal(ChatRunState.Completed, (await service.SendAsync(project, other, "pedido", new InlineProgress(), CancellationToken.None)).State);
    }

    [Fact]
    public async Task TwoReadOnlyProvidersCanWorkAtOnce()
    {
        var store = await StoreAsync();
        var project = await store.AddProjectAsync(_directory);
        var a = Conversation(project);
        var b = Conversation(project, ProviderKind.Claude);
        await store.SaveConversationAsync(a);
        await store.SaveConversationAsync(b);
        var codex = new TestProvider(ProviderKind.Codex, wait: true);
        var claude = new TestProvider(ProviderKind.Claude, wait: true);
        var service = new WorkspaceChatService(store, [codex, claude]);
        using var cancel = new CancellationTokenSource();
        var first = service.SendAsync(project, a, "pedido", new InlineProgress(), cancel.Token);
        var second = service.SendAsync(project, b, "pedido", new InlineProgress(), cancel.Token);
        await Task.WhenAll(codex.Started.Task, claude.Started.Task).WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        Assert.All(await Task.WhenAll(first, second), r => Assert.Equal(ChatRunState.Cancelled, r.State));
    }

    private sealed class TestProvider(ProviderKind kind, bool denied = false, bool failed = false, bool wait = false) : IConversationProvider
    {
        public ProviderKind Kind => kind;
        public ConversationRequest? LastRequest { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var id = request.NativeSessionId ?? Guid.NewGuid().ToString();
            progress.Report(new(ConversationEventKind.Session, "Sessão de teste", id, "modelo"));
            progress.Report(new(ConversationEventKind.TextDelta, "parcial"));
            Started.TrySetResult();
            if (wait) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (failed) throw new ProviderException("falha de teste");
            return new(id, "modelo", "resposta", denied ? ConversationOutcome.Blocked : ConversationOutcome.Completed, denied ? ["Write"] : []);
        }
    }
    private sealed class InlineProgress : IProgress<ConversationEvent> { public void Report(ConversationEvent value) { } }
    public void Dispose()
    {
        // Only remove the uniquely owned test directory; never a project or computed parent.
        var root = new DirectoryInfo(_directory).Parent!.FullName;
        if (!Path.GetFileName(root).StartsWith("Sintonia-tests-", StringComparison.Ordinal)) throw new InvalidOperationException();
        Directory.Delete(root, recursive: true);
    }
}
