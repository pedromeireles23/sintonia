using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class RunTokenUsageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sintonia-tokens-" + Guid.NewGuid());
    private string Database => Path.Combine(_directory, "workspace.db");
    public RunTokenUsageTests() => Directory.CreateDirectory(_directory);
    private static ExecutableLaunch Fixture(string scenario) => new(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), [scenario], "Teste de tokens");
    [Theory]
    [InlineData("rpc-tokens-new", false, 40)]
    [InlineData("rpc-tokens-baseline", true, 40)]
    [InlineData("rpc-tokens-no-baseline", true, 27)]
    [InlineData("rpc-tokens-reset", false, 18)]
    public async Task CodexUsesObservedDifferencesAndNeverSumsRepeatedLastOrPreviousTurn(string scenario, bool resume, long expected)
    {
        var collector = new Collector(); var result = await new CodexConversationProvider(Fixture(scenario)).SendAsync(
            new(_directory, "SIMULAÇÃO", NativeSessionId: resume ? "c9511c7e-3bc3-4b0d-bfff-a602c04e99a1" : null), collector, CancellationToken.None);
        Assert.NotNull(result.TokenUsage); Assert.Equal(expected, result.TokenUsage.TotalTokens); Assert.True(result.TokenUsage.IsPartial);
        Assert.Equal(result.TokenUsage, collector.Events.Last(e => e.TokenUsage is not null).TokenUsage);
        Assert.All(collector.Events.Where(e => e.TokenUsage is not null), e => Assert.True(e.TokenUsage!.TotalTokens <= expected));
    }
    [Fact]
    public async Task InvalidOrMissingUsageDoesNotFailSuccessfulTurnOrInventZero()
    {
        var result = await new CodexConversationProvider(Fixture("rpc-tokens-malformed")).SendAsync(new(_directory, "SIMULAÇÃO"), new Collector(), CancellationToken.None);
        Assert.Equal(ConversationOutcome.Completed, result.Outcome); Assert.Null(result.TokenUsage);
        result = await new ClaudeConversationProvider(Fixture("claude-success")).SendAsync(new(_directory, "SIMULAÇÃO"), new Collector(), CancellationToken.None);
        Assert.Null(result.TokenUsage);
    }
    [Theory]
    [InlineData("rpc-tokens-failed", ChatRunState.Failed)]
    [InlineData("rpc-tokens-wait", ChatRunState.Cancelled)]
    [InlineData("claude-tokens-failed", ChatRunState.Failed)]
    public async Task FailureAndCancellationKeepAlreadyReportedUsage(string scenario, ChatRunState expectedState)
    {
        var kind = scenario.StartsWith("rpc", StringComparison.Ordinal) ? ProviderKind.Codex : ProviderKind.Claude;
        IConversationProvider provider = kind == ProviderKind.Codex ? new CodexConversationProvider(Fixture(scenario)) : new ClaudeConversationProvider(Fixture(scenario));
        var (store, project, conversation) = await SeedAsync(kind); using var cancel = new CancellationTokenSource();
        var run = await new WorkspaceChatService(store, [provider]).SendAsync(project, conversation, "SIMULAÇÃO", new Collector(e =>
        { if (scenario.EndsWith("wait", StringComparison.Ordinal) && e.TokenUsage is not null) cancel.CancelAfter(100); }), cancel.Token);
        Assert.Equal(expectedState, run.State); Assert.Equal(kind == ProviderKind.Codex ? 40 : 38, run.TokenUsage!.TotalTokens);
        Assert.True(run.TokenUsage.IsPartial); Assert.Equal(run.TokenUsage, (await store.GetRunsAsync(conversation.Id)).Single().TokenUsage);
    }
    [Fact]
    public async Task ClaudeMainTurnUsageIncludesCacheExactlyOnceAndExcludesRestoredOrSubagentModelTotals()
    {
        var provider = new ClaudeConversationProvider(Fixture("claude-tokens-success")); var (store, project, conversation) = await SeedAsync(ProviderKind.Claude);
        var service = new WorkspaceChatService(store, [provider]);
        var first = await service.SendAsync(project, conversation, "SIMULAÇÃO", new Collector(), CancellationToken.None);
        var resumed = (await store.GetConversationsAsync(project.Id)).Single();
        var second = await service.SendAsync(project, resumed, "SIMULAÇÃO: retomada", new Collector(), CancellationToken.None);
        Assert.Equal(ChatRunState.Completed, second.State); Assert.Equal(38, first.TokenUsage!.TotalTokens); Assert.Equal(first.TokenUsage, second.TokenUsage);
        Assert.Equal(33, second.TokenUsage!.InputTokens); Assert.Equal(5, second.TokenUsage.OutputTokens); Assert.False(second.TokenUsage.IsPartial);
        Assert.Equal(20, second.TokenUsage.CacheReadTokens); Assert.Equal(3, second.TokenUsage.CacheWriteTokens);
        var reopened = new SqliteWorkspaceStore(Database); await reopened.InitializeAsync();
        Assert.All(await reopened.GetRunsAsync(conversation.Id), run => Assert.Equal(38, run.TokenUsage!.TotalTokens));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"usage\":{\"input_tokens\":1,\"output_tokens\":2}}")]
    [InlineData("{\"usage\":{\"input_tokens\":-1,\"output_tokens\":2,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}")]
    [InlineData("{\"usage\":{\"input_tokens\":9223372036854775807,\"output_tokens\":2,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}")]
    public void IncompatibleOrAbsentCountsStayUnavailable(string json) => Assert.Null(RunTokenUsageParser.ClaudeResult(JsonSerializer.Deserialize<JsonElement>(json)));
    [Theory]
    [InlineData(5, 2, 8, 1, 1)]
    [InlineData(5, 2, 7, 6, 1)]
    [InlineData(5, 2, 7, 1, 3)]
    [InlineData(-1, 2, 1, 0, 0)]
    [InlineData(long.MaxValue, 2, long.MaxValue, 0, 0)]
    public void InvalidTotalsSubsetsAndOverflowAreNotAccepted(long input, long output, long total, long cached, long reasoning)
    {
        var data = JsonSerializer.SerializeToElement(new { inputTokens = input, outputTokens = output, totalTokens = total, cachedInputTokens = cached, reasoningOutputTokens = reasoning });
        Assert.Null(RunTokenUsageParser.Codex(data));
    }
    [Fact]
    public async Task CheckpointSurvivesRecoveryMigrationAndNullFinishAndInvalidDefinitionRollsBack()
    {
        var (store, project, conversation) = await SeedAsync(ProviderKind.Codex);
        var old = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "antigo", "histórico", ChatRunState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        await store.BeginRunAsync(old with { State = ChatRunState.Running }); await store.FinishRunAsync(old, conversation, []);
        using (var connection = new SqliteConnection("Data Source=" + Database + ";Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TABLE run_token_usage; PRAGMA user_version=13;"; command.ExecuteNonQuery(); }
        await store.InitializeAsync(); Assert.Null((await store.GetRunsAsync(conversation.Id)).Single().TokenUsage);
        var running = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "SIMULAÇÃO", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(running);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CheckpointRunAsync(running with { TokenUsage = new(ProviderKind.Claude, 3, 2, 5) }, conversation));
        Assert.Null((await store.GetRunsAsync(conversation.Id)).Last().TokenUsage);
        running = running with { TokenUsage = new(ProviderKind.Codex, 3, 2, 5) }; await store.CheckpointRunAsync(running, conversation);
        await Assert.ThrowsAsync<ArgumentException>(() => store.FinishRunAsync(running with { State = ChatRunState.Completed, TokenUsage = new(ProviderKind.Codex, 1, 1, 2) }, conversation, []));
        Assert.Equal(ChatRunState.Running, (await store.GetRunsAsync(conversation.Id)).Last().State);
        await store.RecoverInterruptedRunsAsync();
        var recovered = (await store.GetRunsAsync(conversation.Id)).Last(); Assert.Equal(ChatRunState.Interrupted, recovered.State); Assert.Equal(running.TokenUsage, recovered.TokenUsage);
        running = running with { Id = Guid.NewGuid().ToString(), TokenUsage = null }; await store.BeginRunAsync(running);
        await store.CheckpointRunAsync(running with { TokenUsage = recovered.TokenUsage }, conversation);
        await store.FinishRunAsync(running with { State = ChatRunState.Cancelled, FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        Assert.Equal(recovered.TokenUsage, (await store.GetRunsAsync(conversation.Id)).Last().TokenUsage);
        await Assert.ThrowsAsync<ArgumentException>(() => store.BeginRunAsync(running with { Id = Guid.NewGuid().ToString(), TokenUsage = recovered.TokenUsage }));
    }
    private async Task<(SqliteWorkspaceStore Store, WorkspaceProject Project, WorkspaceConversation Conversation)> SeedAsync(ProviderKind kind)
    {
        var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync(); var project = await store.AddProjectAsync(_directory);
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Teste de tokens", kind, null, null, "Teste", "", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(conversation); return (store, project, conversation);
    }
    private sealed class Collector(Action<ConversationEvent>? action = null) : IProgress<ConversationEvent>
    {
        public ConcurrentQueue<ConversationEvent> Events { get; } = new();
        public void Report(ConversationEvent value) { Events.Enqueue(value); action?.Invoke(value); }
    }
    public void Dispose()
    {
        var path = Path.GetFullPath(_directory); var parent = Path.GetFullPath(Path.GetTempPath());
        if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("sintonia-tokens-")) throw new InvalidOperationException();
        Directory.Delete(path, true);
    }
}
