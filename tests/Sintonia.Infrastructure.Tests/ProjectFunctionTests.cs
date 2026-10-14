using Microsoft.Data.Sqlite;
using Sintonia.Core;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Infrastructure.Tests;

public sealed class ProjectFunctionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Sintonia-project-functions-" + Guid.NewGuid());
    private string Database => Path.Combine(_directory, "history.db");
    public ProjectFunctionTests() => Directory.CreateDirectory(_directory);
    private async Task<SqliteWorkspaceStore> StoreAsync()
    {
        var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync();
        await store.EnsureStarterFunctionProfilesAsync(WorkspaceFunctionCatalog.StarterProfiles()); return store;
    }
    private async Task<WorkspaceProject> ProjectAsync(SqliteWorkspaceStore store, string name)
    {
        var path = Path.Combine(_directory, name); Directory.CreateDirectory(path); return await store.AddProjectAsync(path);
    }

    [Fact]
    public async Task EachProjectHasAnIndependentTeamSeededOnceFromExistingTemplates()
    {
        var store = await StoreAsync(); var first = await ProjectAsync(store, "primeiro"); var second = await ProjectAsync(store, "segundo");
        var profiles = await store.GetFunctionProfilesAsync(first.Id); var copy = await store.GetFunctionProfilesAsync(second.Id);
        Assert.Equal(7, profiles.Count); Assert.All(profiles, p => Assert.Equal(first.Id, p.ProjectId));
        var original = profiles.Single(p => p.FunctionName == "Desenvolvimento");
        var saved = await store.SaveFunctionProfileAsync(original with { Instructions = "Usar C# neste projeto.", Provider = ProviderKind.Claude });
        Assert.Equal(1, saved.Revision);
        Assert.NotEqual(saved.Instructions, copy.Single(p => p.FunctionName == "Desenvolvimento").Instructions);
        Assert.NotEqual(saved.Instructions, (await store.GetFunctionProfilesAsync()).Single(p => p.FunctionName == "Desenvolvimento").Instructions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveFunctionProfileAsync(original));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveFunctionProfileAsync(saved with { ProjectId = second.Id }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFunctionProfileAsync(saved.Id, saved.Revision, second.Id));
        await store.DeleteFunctionProfileAsync(saved.Id, saved.Revision, first.Id);
        var reopened = await StoreAsync(); Assert.DoesNotContain(await reopened.GetFunctionProfilesAsync(first.Id), p => p.Id == saved.Id);
        Assert.Equal(7, (await reopened.GetFunctionProfilesAsync(second.Id)).Count);
        await reopened.CreateFunctionProfileAsync("Especialista", "Análise", ProviderKind.Codex, null, "Primeiro", first.Id);
        await reopened.CreateFunctionProfileAsync("Especialista", "Análise", ProviderKind.Claude, null, "Segundo", second.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.CreateFunctionProfileAsync("ESPECIALISTA", "Análise", ProviderKind.Codex, null, "Duplicado", first.Id));
    }

    [Fact]
    public async Task ChiefPersistsAndCanChangeProfileOrProviderWithoutLosingOldConversations()
    {
        var store = await StoreAsync(); var project = await ProjectAsync(store, "projeto");
        var chief = await store.GetProjectChiefAsync(project.Id);
        Assert.Equal(PlanProposalFormat.ChiefFunctionName, chief.FunctionName); Assert.Equal(ConversationAccess.ReadOnly, chief.Access);
        var run = Run(chief); await store.BeginRunAsync(run);
        var profile = (await store.GetFunctionProfilesAsync(project.Id)).Single(p => p.FunctionName == chief.FunctionName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetProjectChiefAsync(project.Id, profile.Id, profile.Revision));
        chief = chief with { NativeSessionId = "native-chief", Model = "versão-confirmada" };
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "Olá", FinishedAt = DateTimeOffset.UtcNow }, chief, []);
        profile = await store.SaveFunctionProfileAsync(profile with { Instructions = "Priorize entregas verificáveis.", Model = "modelo-escolhido" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetProjectChiefAsync(project.Id, profile.Id, profile.Revision - 1));
        var updated = await store.SetProjectChiefAsync(project.Id, profile.Id, profile.Revision);
        Assert.Equal(chief.Id, updated.Id); Assert.Equal("native-chief", updated.NativeSessionId);
        Assert.Equal(profile.Instructions, updated.Instructions); Assert.Equal(profile.Model, updated.SelectedModel);
        profile = await store.SaveFunctionProfileAsync(profile with { Provider = ProviderKind.Codex });
        var replacement = await store.SetProjectChiefAsync(project.Id, profile.Id, profile.Revision);
        Assert.NotEqual(chief.Id, replacement.Id); Assert.Null(replacement.NativeSessionId);
        Assert.Equal(replacement, await (await StoreAsync()).GetProjectChiefAsync(project.Id));
        Assert.Equal(2, (await store.GetConversationsAsync(project.Id)).Count);
        Assert.Equal("Olá", Assert.Single(await store.GetRunsAsync(chief.Id)).Response);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveConversationAsync(replacement with { FunctionName = "Desenvolvimento" }));
    }

    [Fact]
    public async Task ExistingChiefIsAdoptedAndMissingTemplateGetsAFallback()
    {
        var store = await StoreAsync(); var project = await ProjectAsync(store, "anterior");
        var chief = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Chefia anterior", ProviderKind.Codex, null,
            "sessão-anterior", PlanProposalFormat.ChiefFunctionName, "Minhas instruções", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(chief);
        var template = (await store.GetFunctionProfilesAsync()).Single(p => p.FunctionName == PlanProposalFormat.ChiefFunctionName);
        await store.DeleteFunctionProfileAsync(template.Id, template.Revision);
        Assert.Equal(chief.Id, (await store.GetProjectChiefAsync(project.Id)).Id);
        Assert.Contains(await store.GetFunctionProfilesAsync(project.Id), p => p.FunctionName == PlanProposalFormat.ChiefFunctionName);
        var other = await ProjectAsync(store, "outro"); var otherProfiles = await store.GetFunctionProfilesAsync(other.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetProjectChiefAsync(project.Id, otherProfiles[0].Id, otherProfiles[0].Revision));
        await store.RemoveProjectAsync(project.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetProjectChiefAsync(project.Id));
        await store.AddProjectAsync(project.Directory); Assert.Equal(chief.Id, (await store.GetProjectChiefAsync(project.Id)).Id);
    }

    [Fact]
    public async Task ChiefReceivesOnlyItsProjectTeamAndHistoryKeepsTheOriginalPrompt()
    {
        var store = await StoreAsync(); var project = await ProjectAsync(store, "atual"); var other = await ProjectAsync(store, "outro");
        await store.CreateFunctionProfileAsync("Analista local", "Análise", ProviderKind.Codex, "modelo-local", "REGRA LOCAL", project.Id);
        await store.CreateFunctionProfileAsync("Analista privado", "Análise", ProviderKind.Claude, null, "OUTRO PROJETO", other.Id);
        var chief = await store.GetProjectChiefAsync(project.Id); var provider = new Worker(chief.Provider);
        var service = new WorkspaceChatService(store, [provider]);
        var run = await service.SendAsync(project, chief, "Olá chefe", new SilentProgress(), CancellationToken.None);
        Assert.Equal(ChatRunState.Completed, run.State); Assert.NotNull(provider.Request);
        Assert.Contains("REGRA LOCAL", provider.Request.Prompt); Assert.Contains("modelo-local", provider.Request.Prompt);
        Assert.DoesNotContain("OUTRO PROJETO", provider.Request.Prompt);
        Assert.Equal("Olá chefe", Assert.Single(await store.GetRunsAsync(chief.Id)).Prompt);
        Assert.Equal(ConversationAccess.ReadOnly, provider.Request.Access); Assert.Null(provider.Request.PermissionHandler);
    }

    [Fact]
    public async Task Schema18HistoryMigratesWithoutChangingTheSharedLibraryOrCallingAProvider()
    {
        var store = await StoreAsync(); var project = await ProjectAsync(store, "antigo");
        var templates = await store.GetFunctionProfilesAsync();
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE project_chiefs; DROP TABLE project_function_profiles; DROP TABLE project_profile_libraries; DROP TABLE removed_projects; PRAGMA user_version=18";
            command.ExecuteNonQuery();
        }
        store = await StoreAsync(); var chief = await store.GetProjectChiefAsync(project.Id);
        Assert.Equal(templates, await store.GetFunctionProfilesAsync()); Assert.Equal(project.Id, chief.ProjectId);
        Assert.Equal(7, (await store.GetFunctionProfilesAsync(project.Id)).Count);
    }

    private static ChatRun Run(WorkspaceConversation conversation) => new(Guid.NewGuid().ToString(), conversation.Id, "Pedido", null,
        ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
    private sealed class SilentProgress : IProgress<ConversationEvent> { public void Report(ConversationEvent value) { } }
    private sealed class Worker(ProviderKind kind) : IConversationProvider
    {
        public ProviderKind Kind => kind;
        public ConversationRequest? Request { get; private set; }
        public Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken cancellationToken)
        { Request = request; return Task.FromResult(new ConversationResult("native", "modelo-teste", "Olá!", ConversationOutcome.Completed, [])); }
    }
    public void Dispose()
    {
        if (!Path.GetFileName(_directory).StartsWith("Sintonia-project-functions-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
