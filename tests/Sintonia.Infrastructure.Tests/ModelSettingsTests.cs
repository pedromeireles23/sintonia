using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class ModelSettingsTests
{
    private static ExecutableLaunch Fixture(string name) => new(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), [name], "Teste");

    [Fact]
    public async Task CatalogPreservesNamesEffortsAndSafeConfigurationOrigins()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await new CodexConversationProvider(Fixture("rpc-effort")).InspectAsync(AppContext.BaseDirectory, stop.Token);
        var model = Assert.Single(result.Models);
        Assert.Equal("Modelo de teste", model.Name);
        Assert.Equal(new[] { "low", "high" }, model.ReasoningEfforts!.Select(e => e.Id));
        Assert.Equal("low", model.DefaultReasoningEffort);
        Assert.Equal("fixture-model", result.Defaults!.Model);
        Assert.Equal("high", result.Defaults.ReasoningEffort);
        Assert.Equal("Configuração do usuário", result.Defaults.ModelSource);
        Assert.Equal("Configuração do projeto", result.Defaults.ReasoningSource);
        Assert.DoesNotContain("SEGREDO", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ExplicitEffortIsAppliedToNewAndResumedTurns()
    {
        var provider = new CodexConversationProvider(Fixture("rpc-effort"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = new ConversationRequest(AppContext.BaseDirectory, "Teste simulado", Model: "fixture-model", ReasoningEffort: "high");
        var first = await provider.SendAsync(request, new Progress<ConversationEvent>(), stop.Token);
        var resumed = await provider.SendAsync(request with { NativeSessionId = first.NativeSessionId }, new Progress<ConversationEvent>(), stop.Token);
        Assert.Equal("high", first.ReasoningEffort);
        Assert.Equal(first.NativeSessionId, resumed.NativeSessionId);
        Assert.Equal("high", resumed.ReasoningEffort);
    }

    [Theory]
    [InlineData("rpc-effort", "max")]
    [InlineData("rpc-effort-mismatch", "high")]
    public async Task UnsupportedOrUnconfirmedEffortNeverStartsATurn(string scenario, string effort)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<ProviderException>(() => new CodexConversationProvider(Fixture(scenario))
            .SendAsync(new(AppContext.BaseDirectory, "Teste", ReasoningEffort: effort), new Progress<ConversationEvent>(), stop.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("high \"--config\"")]
    [InlineData("High")]
    [InlineData("high\n")]
    public void EffortRejectsInvalidIdentifiers(string effort) => Assert.Throws<ArgumentException>(() => ModelSettings.ValidateEffort(effort));

    [Fact]
    public void FutureBackendEffortsRemainSelectable() => ModelSettings.ValidateEffort("future-effort");

    [Fact]
    public async Task ModelSettingsSurviveMigrationRestartAndCannotChangeDuringLiveRun()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sintonia-models-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workspace.db");
            var store = new SqliteWorkspaceStore(path); await store.InitializeAsync();
            var project = await store.AddProjectAsync(directory);
            var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Teste", ProviderKind.Codex, "fixture-model", null,
                "Revisão", "", ConversationAccess.ReadOnly, ReasoningEffort: "high", UsesInstallationDefault: false);
            await store.SaveConversationAsync(conversation);
            var reopened = new SqliteWorkspaceStore(path); await reopened.InitializeAsync();
            Assert.Equal(conversation, Assert.Single(await reopened.GetConversationsAsync(project.Id)));
            var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "Teste", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
            await reopened.BeginRunAsync(run);
            await Assert.ThrowsAsync<ArgumentException>(() => reopened.SaveConversationAsync(conversation with { ReasoningEffort = "low" }));
            await reopened.CheckpointRunAsync(run, conversation with { EffectiveReasoningEffort = "high" });
            Assert.Equal("high", Assert.Single(await reopened.GetConversationsAsync(project.Id)).EffectiveReasoningEffort);
            await Assert.ThrowsAsync<ArgumentException>(() => reopened.SaveConversationAsync(conversation with { SelectedModel = "outro-modelo" }));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ClaudeEffortRequiresAnnouncedSupportForAnExplicitModel()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var provider = new ClaudeConversationProvider(Fixture("claude-effort"));
        var request = new ConversationRequest(AppContext.BaseDirectory, "Teste simulado", Model: "fixture-model", ReasoningEffort: "high");
        Assert.Equal("Teste simulado", (await provider.SendAsync(request, new Progress<ConversationEvent>(), stop.Token)).Text);
        var missing = await Assert.ThrowsAsync<ProviderException>(() => provider.SendAsync(request with { Model = null }, new Progress<ConversationEvent>(), stop.Token));
        Assert.Contains("explícito", missing.Message);
        var unsupported = await Assert.ThrowsAsync<ProviderException>(() => provider.SendAsync(request with { ReasoningEffort = "max" }, new Progress<ConversationEvent>(), stop.Token));
        Assert.Contains("não anuncia suporte", unsupported.Message);
    }

    [Fact]
    public async Task ModelChoiceChangesBetweenTurnsWithoutReplacingItByTheConfirmedVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sintonia-model-choice-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workspace.db");
            var store = new SqliteWorkspaceStore(path); await store.InitializeAsync();
            var project = await store.AddProjectAsync(directory);
            var provider = new RecordingProvider();
            var chat = new WorkspaceChatService(store, [provider]);
            var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Teste", ProviderKind.Claude, "opus", null,
                "Conversa", "", ConversationAccess.ReadOnly, ReasoningEffort: "high", UsesInstallationDefault: false, SelectedModel: "opus");
            await store.SaveConversationAsync(conversation);
            await chat.SendAsync(project, conversation, "primeiro", new Progress<ConversationEvent>(), CancellationToken.None);
            var saved = Assert.Single(await store.GetConversationsAsync(project.Id));
            Assert.Equal(("opus", "high"), (provider.Requests[0].Model, provider.Requests[0].ReasoningEffort));
            Assert.Equal(("versao-confirmada", "opus"), (saved.Model, saved.SelectedModel));
            await chat.SendAsync(project, saved, "segundo", new Progress<ConversationEvent>(), CancellationToken.None);
            Assert.Equal("opus", provider.Requests[1].Model);
            var installationDefault = Assert.Single(await store.GetConversationsAsync(project.Id))
                with { SelectedModel = null, UsesInstallationDefault = true, ReasoningEffort = null };
            await store.SaveConversationAsync(installationDefault);
            await chat.SendAsync(project, installationDefault, "terceiro", new Progress<ConversationEvent>(), CancellationToken.None);
            Assert.Equal((null, null, saved.NativeSessionId), (provider.Requests[2].Model, provider.Requests[2].ReasoningEffort, provider.Requests[2].NativeSessionId));
            var reopened = new SqliteWorkspaceStore(path); await reopened.InitializeAsync();
            var final = Assert.Single(await reopened.GetConversationsAsync(project.Id));
            Assert.Equal((true, null, null, "versao-confirmada"), (final.UsesInstallationDefault, final.SelectedModel, final.ReasoningEffort, final.Model));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Schema16HistoryGainsSelectedModelWithoutLosingSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sintonia-model-schema-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workspace.db");
            var store = new SqliteWorkspaceStore(path); await store.InitializeAsync();
            var project = await store.AddProjectAsync(directory);
            var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Teste", ProviderKind.Codex, "fixture-model", null,
                "Conversa", "", ConversationAccess.ReadOnly, ReasoningEffort: "high", UsesInstallationDefault: false);
            await store.SaveConversationAsync(conversation);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE conversation_model_settings DROP COLUMN selected_model; PRAGMA user_version=16;"; command.ExecuteNonQuery();
            }
            var migrated = new SqliteWorkspaceStore(path); await migrated.InitializeAsync();
            Assert.Equal(conversation, Assert.Single(await migrated.GetConversationsAsync(project.Id)));
            await migrated.SaveConversationAsync(conversation with { SelectedModel = "fixture-model" });
            Assert.Equal("fixture-model", Assert.Single(await migrated.GetConversationsAsync(project.Id)).SelectedModel);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class RecordingProvider : IConversationProvider
    {
        public ProviderKind Kind => ProviderKind.Claude;
        public List<ConversationRequest> Requests { get; } = [];
        public Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new ConversationResult(request.NativeSessionId ?? Guid.NewGuid().ToString(), "versao-confirmada", "SIMULAÇÃO",
                ConversationOutcome.Completed, []));
        }
    }
}
