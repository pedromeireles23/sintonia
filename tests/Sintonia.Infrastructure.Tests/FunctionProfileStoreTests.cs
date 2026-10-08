using Sintonia.Core;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Infrastructure.Tests;

public sealed class FunctionProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Sintonia-profile-tests-" + Guid.NewGuid());
    private string Database => Path.Combine(_directory, "workspace.db");
    private async Task<SqliteWorkspaceStore> StoreAsync()
    {
        Directory.CreateDirectory(_directory); var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync(); return store;
    }

    [Fact]
    public async Task ProfilesPersistProviderModelInstructionsAndEditsAcrossRestarts()
    {
        var store = await StoreAsync(); Assert.Empty(await store.GetFunctionProfilesAsync());
        var created = await store.CreateFunctionProfileAsync(" Documentação técnica ", "Documentação", ProviderKind.Claude, " modelo-configurado ", "Texto literal\ncom espaços  ");
        Assert.Equal(0, created.Revision); Assert.Equal("Documentação técnica", created.Name);
        var edited = await store.SaveFunctionProfileAsync(created with { Provider = ProviderKind.Codex, Model = null, Instructions = "Revisar referências." });
        var reopened = await StoreAsync(); var saved = Assert.Single(await reopened.GetFunctionProfilesAsync());
        Assert.Equal(edited, saved); Assert.Equal(1, saved.Revision);
        await reopened.DeleteFunctionProfileAsync(saved.Id, saved.Revision);
        Assert.Empty(await (await StoreAsync()).GetFunctionProfilesAsync());
    }

    [Fact]
    public async Task NamesAreUniqueAfterCaseUnicodeAndWhitespaceNormalization()
    {
        var store = await StoreAsync(); var first = await store.CreateFunctionProfileAsync("Documentação", "Documentação", ProviderKind.Codex, null, "");
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateFunctionProfileAsync(" DOCUMENTAC\u0327A\u0303O ", "Revisão", ProviderKind.Claude, null, ""));
        var second = await store.CreateFunctionProfileAsync("Pesquisa", "Análise", ProviderKind.Claude, null, "");
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveFunctionProfileAsync(second with { Name = first.Name }));
        Assert.Equal("Pesquisa", (await store.GetFunctionProfilesAsync()).Single(p => p.Id == second.Id).Name);
        Assert.Equal(0, (await store.GetFunctionProfilesAsync()).Single(p => p.Id == second.Id).Revision);
    }

    [Fact]
    public async Task StaleEditsAndDeletesCannotOverwriteANewerProfile()
    {
        var store = await StoreAsync(); var first = await store.CreateFunctionProfileAsync("Revisão", "Revisão", ProviderKind.Codex, null, "Original.");
        var changed = await store.SaveFunctionProfileAsync(first with { Instructions = "Atualizado." });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveFunctionProfileAsync(first with { Instructions = "Obsoleto." }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFunctionProfileAsync(first.Id, first.Revision));
        Assert.Equal(changed, Assert.Single(await store.GetFunctionProfilesAsync()));
        await store.DeleteFunctionProfileAsync(changed.Id, changed.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveFunctionProfileAsync(changed));
    }

    [Fact]
    public async Task ProfileChangesAndDeletionPreserveConversationSnapshots()
    {
        var store = await StoreAsync(); var project = await store.AddProjectAsync(_directory);
        var profile = await store.CreateFunctionProfileAsync("Análise de fontes", "Análise", ProviderKind.Claude, "modelo-inicial", "Conferir fontes.");
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Conversa", profile.Provider, profile.Model,
            "sessao-nativa", profile.FunctionName, profile.Instructions, ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(conversation);
        var changed = await store.SaveFunctionProfileAsync(profile with { FunctionName = "Documentação", Provider = ProviderKind.Codex, Model = null, Instructions = "Outras instruções." });
        await store.DeleteFunctionProfileAsync(changed.Id, changed.Revision);
        Assert.Equal(conversation, Assert.Single(await store.GetConversationsAsync(project.Id)));
    }

    [Fact]
    public async Task SchemaThreeMigrationPreservesQueueProposalAndRunHistory()
    {
        var store = await StoreAsync(); var project = await store.AddProjectAsync(_directory);
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Plano", ProviderKind.Codex, null, null,
            PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(conversation);
        var plan = new PlanProposal(1, "Relatório", "Registrar evidências.",
            [new("report", "Preparar relatório", "Documentação", ProviderKind.Claude, null, ConversationAccess.ReadOnly, "Organizar as evidências.", ["docs/"], [], ["Fontes identificadas."])]);
        var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "Planejar", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run);
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```", FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        var proposal = await store.CreateProposalAsync(project.Id, run.Id);
        proposal = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
        var batch = await store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE function_profiles; PRAGMA user_version=3;"; command.ExecuteNonQuery();
        }
        var migrated = await StoreAsync(); Assert.Empty(await migrated.GetFunctionProfilesAsync());
        Assert.Equal(batch.Id, Assert.Single(await migrated.GetTaskBatchesAsync(project.Id)).Id);
        Assert.Equal(proposal.Revision, Assert.Single(await migrated.GetProposalsAsync(project.Id)).Revision);
        Assert.Equal(ChatRunState.Completed, Assert.Single(await migrated.GetRunsAsync(conversation.Id)).State);
        await migrated.CreateFunctionProfileAsync("Análise", "Análise", ProviderKind.Codex, null, "Conferir fontes.");
        Assert.Single(await migrated.GetFunctionProfilesAsync());
    }

    public void Dispose()
    {
        if (!Path.GetFileName(_directory).StartsWith("Sintonia-profile-tests-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
