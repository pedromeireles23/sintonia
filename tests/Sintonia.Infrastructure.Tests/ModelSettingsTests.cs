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
        }
        finally { Directory.Delete(directory, true); }
    }
}
