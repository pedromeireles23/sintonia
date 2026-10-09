using Microsoft.Data.Sqlite;
using Sintonia.Core;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Infrastructure.Tests;

public sealed class TokenBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sintonia-budget-" + Guid.NewGuid());
    private string Database => Path.Combine(_root, "workspace.db");
    private async Task<(SqliteWorkspaceStore Store, WorkspaceProject Project, WorkspaceConversation Conversation)> SeedAsync()
    {
        Directory.CreateDirectory(_root); var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync();
        var project = await store.AddProjectAsync(_root); var conversation = Conversation(project);
        await store.SaveConversationAsync(conversation); return (store, project, conversation);
    }
    private static WorkspaceConversation Conversation(WorkspaceProject project, ProviderKind kind = ProviderKind.Codex) =>
        new(Guid.NewGuid().ToString(), project.Id, "Teste de orçamento", kind, null, null, "Conversa", "", ConversationAccess.ReadOnly);
    private static ChatRun Run(WorkspaceConversation conversation) =>
        new(Guid.NewGuid().ToString(), conversation.Id, "SIMULAÇÃO", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);

    [Fact]
    public async Task ConcurrentStoresShareAdmissionBudgetBeforeCreatingRuns()
    {
        var (store, project, _) = await SeedAsync();
        await store.SaveProjectExecutionSettingsAsync(new(project.Id, MaxConcurrentSessions: 7, MaxReportedTokens: 200, TokenReservation: 100));
        var conversations = Enumerable.Range(0, 5).Select(_ => Conversation(project)).ToArray();
        foreach (var conversation in conversations) await store.SaveConversationAsync(conversation);
        var admitted = await Task.WhenAll(conversations.Select(async conversation =>
        {
            try { await new SqliteWorkspaceStore(Database).BeginRunAsync(Run(conversation)); return true; }
            catch (InvalidOperationException) { return false; }
        }));
        Assert.Equal(2, admitted.Count(value => value));
        var budget = await store.GetProjectTokenBudgetAsync(project.Id);
        Assert.Equal(200, budget.ReservedTokens); Assert.Equal(0, budget.ReportedTokens); Assert.Equal(2, budget.ActiveRuns);
        Assert.False(budget.CanAdmit(out _));
        Assert.Equal(2, (await Task.WhenAll(conversations.Select(c => store.GetRunsAsync(c.Id)))).Sum(r => r.Count));
    }

    [Fact]
    public async Task CheckpointsReplaceCountsReservationsDoNotDoubleCountAndOverflowBlocksNewStarts()
    {
        var (store, project, conversation) = await SeedAsync();
        await store.SaveProjectExecutionSettingsAsync(new(project.Id, MaxReportedTokens: 200, TokenReservation: 100));
        var run = Run(conversation); await store.BeginRunAsync(run);
        run = run with { TokenUsage = new(ProviderKind.Codex, 50, 10, 60) };
        await store.CheckpointRunAsync(run, conversation); await store.CheckpointRunAsync(run, conversation);
        var budget = await store.GetProjectTokenBudgetAsync(project.Id);
        Assert.Equal(60, budget.ReportedTokens); Assert.Equal(40, budget.ReservedTokens); Assert.True(budget.CanAdmit(out _));
        run = run with { TokenUsage = new(ProviderKind.Codex, 190, 20, 210) }; await store.CheckpointRunAsync(run, conversation);
        budget = await store.GetProjectTokenBudgetAsync(project.Id);
        Assert.Equal(210, budget.ReportedTokens); Assert.Equal(0, budget.ReservedTokens); Assert.False(budget.CanAdmit(out _));
        Assert.Equal(ChatRunState.Running, (await store.GetRunsAsync(conversation.Id)).Single().State);
        await store.FinishRunAsync(run with { State = ChatRunState.Cancelled, TokenUsage = null, FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        budget = await store.GetProjectTokenBudgetAsync(project.Id);
        Assert.Equal(210, budget.ReportedTokens); Assert.Equal(0, budget.ActiveRuns); Assert.Equal(1, budget.PartialRuns);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(Run(conversation)));
    }

    [Theory]
    [InlineData(ChatRunState.Completed)]
    [InlineData(ChatRunState.Failed)]
    [InlineData(ChatRunState.Cancelled)]
    [InlineData(ChatRunState.Blocked)]
    public async Task TerminalStatesReleaseOnlyUnobservedReservationAndRetainReportedConsumption(ChatRunState state)
    {
        var (store, project, conversation) = await SeedAsync();
        await store.SaveProjectExecutionSettingsAsync(new(project.Id, MaxReportedTokens: 100, TokenReservation: 100));
        var run = Run(conversation); await store.BeginRunAsync(run);
        await store.FinishRunAsync(run with { State = state, TokenUsage = new(ProviderKind.Codex, 20, 10, 30), FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        var budget = await store.GetProjectTokenBudgetAsync(project.Id);
        Assert.Equal(30, budget.ReportedTokens); Assert.Equal(0, budget.ReservedTokens); Assert.False(budget.CanAdmit(out _));
        var saved = await store.GetProjectExecutionSettingsAsync(project.Id);
        await store.SaveProjectExecutionSettingsAsync(saved with { TokenReservation = 70 });
        await store.BeginRunAsync(Run(conversation));
        Assert.Equal(70, (await store.GetProjectTokenBudgetAsync(project.Id)).ReservedTokens);
    }

    [Fact]
    public async Task UnknownUsageAndRecoveryStayExplicitAndPreserveReservationsAcrossConfigurationChanges()
    {
        var (store, project, conversation) = await SeedAsync();
        var settings = await store.SaveProjectExecutionSettingsAsync(new(project.Id, MaxReportedTokens: 200, TokenReservation: 100));
        var run = Run(conversation); await store.BeginRunAsync(run);
        await store.SaveProjectExecutionSettingsAsync(settings with { MaxReportedTokens = 150, TokenReservation = 25 });
        Assert.Equal(100, (await store.GetProjectTokenBudgetAsync(project.Id)).ReservedTokens);
        var reopened = new SqliteWorkspaceStore(Database); await reopened.InitializeAsync(); await reopened.RecoverInterruptedRunsAsync();
        var budget = await reopened.GetProjectTokenBudgetAsync(project.Id);
        Assert.Equal(0, budget.ReservedTokens); Assert.Equal(1, budget.MissingRuns); Assert.Equal(0, budget.ReportedTokens);
        Assert.Contains("sem medição", budget.Describe()); Assert.True(budget.CanAdmit(out _));
    }

    [Fact]
    public async Task LimitsAreScopedRevisionedAndMigrationPreservesHistoricMeasurements()
    {
        var (store, project, conversation) = await SeedAsync(); var run = Run(conversation);
        await store.BeginRunAsync(run); await store.FinishRunAsync(run with { State = ChatRunState.Completed, TokenUsage = new(ProviderKind.Codex, 70, 30, 100) }, conversation, []);
        using (var connection = new SqliteConnection("Data Source=" + Database + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE run_token_reservations; DROP TABLE project_token_limits; PRAGMA user_version=14;"; command.ExecuteNonQuery();
        }
        await store.InitializeAsync(); var settings = await store.GetProjectExecutionSettingsAsync(project.Id);
        Assert.Null(settings.MaxReportedTokens); Assert.Equal(100, (await store.GetProjectTokenBudgetAsync(project.Id)).ReportedTokens);
        await store.SaveProjectExecutionSettingsAsync(settings with { MaxReportedTokens = 100, TokenReservation = 50 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveProjectExecutionSettingsAsync(settings));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(Run(conversation)));
        var path = Path.Combine(_root, "other"); Directory.CreateDirectory(path); var other = await store.AddProjectAsync(path);
        var second = Conversation(other, ProviderKind.Claude); await store.SaveConversationAsync(second); await store.BeginRunAsync(Run(second));
        Assert.Null((await store.GetProjectTokenBudgetAsync(other.Id)).Settings.MaxReportedTokens);
        Assert.Equal(0, (await store.GetProjectTokenBudgetAsync(other.Id)).ReportedTokens);
    }

    [Fact]
    public async Task AggregateOverflowNeverWrapsToAnAvailableBalance()
    {
        var (store, project, conversation) = await SeedAsync();
        foreach (var total in new[] { long.MaxValue, 1L })
        {
            var run = Run(conversation); await store.BeginRunAsync(run);
            await store.FinishRunAsync(run with { State = ChatRunState.Completed, TokenUsage = new(ProviderKind.Codex, total, 0, total) }, conversation, []);
        }
        await store.SaveProjectExecutionSettingsAsync(new(project.Id, MaxReportedTokens: 100, TokenReservation: 10));
        var budget = await store.GetProjectTokenBudgetAsync(project.Id);
        Assert.True(budget.Overflow); Assert.Equal(long.MaxValue, budget.ReportedTokens); Assert.False(budget.CanAdmit(out _));
    }

    [Fact]
    public async Task RejectedChatAndTaskNeverCallProviderOrConsumeAttemptAndSettingsCannotBeStale()
    {
        var (store, project, conversation) = await SeedAsync();
        var definition = new PlanProposal(1, "Teste", "Verificar admissão", [new("one", "Tarefa", "Teste", ProviderKind.Codex, null,
            ConversationAccess.ReadOnly, "SIMULAÇÃO", ["."], [], ["Resultado"]) ]);
        var source = Run(conversation); await store.BeginRunAsync(source);
        await store.FinishRunAsync(source with { State = ChatRunState.Completed, Response = "```sintonia-plan\n" + PlanProposalFormat.Serialize(definition) + "\n```",
            TokenUsage = new(ProviderKind.Codex, 100, 0, 100) }, conversation, []);
        var proposal = await store.CreateProposalAsync(project.Id, source.Id);
        proposal = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
        var batch = await store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision);
        var settings = await store.SaveProjectExecutionSettingsAsync(new(project.Id, MaxReportedTokens: 100, TokenReservation: 10));
        var provider = new NeverProvider(); var service = new WorkspaceChatService(store, [provider]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(project, conversation, "SIMULAÇÃO", new Progress(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendTaskAsync(project.Id, batch.Tasks[0].Id, new Progress(), CancellationToken.None));
        var task = (await store.GetTaskBatchesAsync(project.Id)).Single().Tasks.Single();
        Assert.Equal(0, task.Attempts); Assert.Empty(await store.GetRunsAsync(task.ConversationId)); Assert.Equal(0, provider.Calls);
        await store.SaveProjectExecutionSettingsAsync(settings with { MaxReportedTokens = null });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(Run(conversation), expectedSettings: settings));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(10, 0)]
    [InlineData(10, 11)]
    [InlineData(1000000000001, 1)]
    public async Task InvalidBudgetNeverChangesRevision(long limit, long reservation)
    {
        var (store, project, _) = await SeedAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveProjectExecutionSettingsAsync(new(project.Id, MaxReportedTokens: limit, TokenReservation: reservation)));
        Assert.Equal(0, (await store.GetProjectExecutionSettingsAsync(project.Id)).Revision);
    }
    private sealed class NeverProvider : IConversationProvider
    {
        public int Calls { get; private set; }
        public ProviderKind Kind => ProviderKind.Codex;
        public Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Não deveria chamar o provedor."); }
    }
    private sealed class Progress : IProgress<ConversationEvent> { public void Report(ConversationEvent value) { } }
    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root); var parent = Path.GetFullPath(Path.GetTempPath());
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("sintonia-budget-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
