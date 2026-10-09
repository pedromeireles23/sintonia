using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Sintonia.Core;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Infrastructure.Tests;

public sealed class WorkspaceConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sintonia-concurrency-" + Guid.NewGuid());
    private string Database => Path.Combine(_root, "workspace.db");
    private async Task<(SqliteWorkspaceStore Store, WorkspaceProject Project)> SetupAsync()
    {
        Directory.CreateDirectory(_root); var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync();
        return (store, await store.AddProjectAsync(_root));
    }
    private static WorkspaceConversation Conversation(WorkspaceProject project, int index, ConversationAccess access = ConversationAccess.ReadOnly) =>
        new(Guid.NewGuid().ToString(), project.Id, "Sessão " + index, index % 2 == 0 ? ProviderKind.Codex : ProviderKind.Claude,
            null, null, "Conversa", "", access);
    private static ChatRun Run(WorkspaceConversation conversation) =>
        new(Guid.NewGuid().ToString(), conversation.Id, "pedido", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);

    [Fact]
    public async Task SettingsArePerProjectRevisionedAndPreservedBySchemaNineMigration()
    {
        var (store, project) = await SetupAsync();
        var original = await store.GetProjectExecutionSettingsAsync(project.Id); Assert.Equal(3, original.MaxConcurrentSessions);
        foreach (var limit in new[] { 2, 8 })
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveProjectExecutionSettingsAsync(original with { MaxConcurrentSessions = limit }));
        var saved = await store.SaveProjectExecutionSettingsAsync(original with { MaxConcurrentSessions = 7 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteWorkspaceStore(Database).SaveProjectExecutionSettingsAsync(original));
        var secondFolder = Path.Combine(_root, "second"); Directory.CreateDirectory(secondFolder);
        var second = await store.AddProjectAsync(secondFolder);
        Assert.Equal(3, (await store.GetProjectExecutionSettingsAsync(second.Id)).MaxConcurrentSessions);
        Assert.Equal(saved, await new SqliteWorkspaceStore(Database).GetProjectExecutionSettingsAsync(project.Id));
        var conversation = Conversation(project, 0); await store.SaveConversationAsync(conversation);
        await store.BeginRunAsync(Run(conversation));
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE run_execution_scopes; DROP TABLE project_execution_settings; PRAGMA user_version=9;";
            command.ExecuteNonQuery();
        }
        var migrated = new SqliteWorkspaceStore(Database); await migrated.InitializeAsync();
        Assert.Equal(3, (await migrated.GetProjectExecutionSettingsAsync(project.Id)).MaxConcurrentSessions);
        Assert.Single(await migrated.GetRunsAsync(conversation.Id));
        // Existing live runs still occupy a slot when they have no snapshot in the old schema.
        for (var i = 1; i <= 3; i++)
        {
            var next = Conversation(project, i); await migrated.SaveConversationAsync(next);
            if (i < 3) await migrated.BeginRunAsync(Run(next));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => migrated.BeginRunAsync(Run(next)));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public async Task MixedSessionsUseAllSlotsAndCancellationHoldsCapacityUntilProviderStops(int limit)
    {
        var (store, project) = await SetupAsync();
        await store.SaveProjectExecutionSettingsAsync(new(project.Id, MaxConcurrentSessions: limit));
        var conversations = Enumerable.Range(0, limit + 1).Select(i => Conversation(project, i)).ToArray();
        foreach (var conversation in conversations) await store.SaveConversationAsync(conversation);
        var codex = new BlockingProvider(ProviderKind.Codex); var claude = new BlockingProvider(ProviderKind.Claude);
        var chat = new WorkspaceChatService(store, [codex, claude]); using var stop = new CancellationTokenSource();
        var executions = conversations.Take(limit).Select(c => chat.SendAsync(project, c, "pedido", new Progress(), stop.Token)).ToArray();
        try
        {
            await UntilAsync(() => codex.Requests.Count + claude.Requests.Count == limit);
            Assert.True(codex.Requests.Count >= 2); // Multiple sessions of the same provider are now allowed.
            await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendAsync(project, conversations[^1], "extra", new Progress(), CancellationToken.None));
            stop.Cancel();
            await UntilAsync(() => codex.Cancellations + claude.Cancellations == limit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendAsync(project, conversations[^1], "extra", new Progress(), CancellationToken.None));
            Assert.All(executions, task => Assert.False(task.IsCompleted));
        }
        finally { stop.Cancel(); codex.Release.TrySetResult(); claude.Release.TrySetResult(); await Task.WhenAll(executions); }
        Assert.All(executions, task => Assert.Equal(ChatRunState.Cancelled, task.Result.State));
        var reopened = new SqliteWorkspaceStore(Database);
        foreach (var conversation in conversations.Take(limit))
            Assert.Equal(ChatRunState.Cancelled, Assert.Single(await reopened.GetRunsAsync(conversation.Id)).State);
        Assert.Equal(ChatRunState.Completed, (await chat.SendAsync(project, conversations[^1], "retomar", new Progress(), CancellationToken.None)).State);
    }

    [Fact]
    public async Task ConcurrentStoresReserveAtMostSevenAndLoweringLimitDoesNotInterruptLiveRuns()
    {
        var (store, project) = await SetupAsync();
        var settings = await store.SaveProjectExecutionSettingsAsync(new(project.Id, MaxConcurrentSessions: 7));
        var conversations = Enumerable.Range(0, 10).Select(i => Conversation(project, i)).ToArray();
        foreach (var conversation in conversations) await store.SaveConversationAsync(conversation);
        var admitted = await Task.WhenAll(conversations.Select(async conversation =>
        {
            try { await new SqliteWorkspaceStore(Database).BeginRunAsync(Run(conversation)); return true; }
            catch (InvalidOperationException) { return false; }
        }));
        Assert.Equal(7, admitted.Count(value => value));
        await store.SaveProjectExecutionSettingsAsync(settings with { MaxConcurrentSessions = 3 });
        Assert.Equal(7, (await Task.WhenAll(conversations.Select(c => store.GetRunsAsync(c.Id)))).SelectMany(r => r).Count(r => r.State == ChatRunState.Running));
        var extra = Conversation(project, 10); await store.SaveConversationAsync(extra);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(Run(extra)));
        var otherFolder = Path.Combine(_root, "other"); Directory.CreateDirectory(otherFolder);
        var other = await store.AddProjectAsync(otherFolder); var otherConversation = Conversation(other, 0);
        await store.SaveConversationAsync(otherConversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(Run(otherConversation))); // Global ceiling.
        await store.RecoverInterruptedRunsAsync(); await store.BeginRunAsync(Run(extra));
    }

    [Fact]
    public async Task ActiveScopeSurvivesConversationEditsAndRejectsOverlappingProjectsAndForgedScope()
    {
        var (store, project) = await SetupAsync(); var writer = Conversation(project, 0, ConversationAccess.WorkspaceWrite);
        await store.SaveConversationAsync(writer); var run = Run(writer); await store.BeginRunAsync(run);
        await store.SaveConversationAsync(writer with { Access = ConversationAccess.ReadOnly });
        var nestedFolder = Path.Combine(_root, "nested"); Directory.CreateDirectory(nestedFolder);
        var nested = await store.AddProjectAsync(nestedFolder); var reader = Conversation(nested, 1);
        await store.SaveConversationAsync(reader);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteWorkspaceStore(Database).BeginRunAsync(Run(reader)));
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, FinishedAt = DateTimeOffset.UtcNow }, writer, []);
        var forged = WorkspaceExecutionSlot.Create(nested, reader) with { Directory = Path.Combine(_root, "different") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(Run(reader), expectedSlot: forged));
        Assert.Empty(await store.GetRunsAsync(reader.Id));
    }

    internal sealed class BlockingProvider(ProviderKind kind) : IConversationProvider
    {
        public ProviderKind Kind => kind;
        public ConcurrentBag<ConversationRequest> Requests { get; } = [];
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancellations;
        public int Cancellations => Volatile.Read(ref _cancellations);
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Requests.Add(request); var id = Guid.NewGuid().ToString();
            progress.Report(new(ConversationEventKind.Session, "Sessão de teste", id, "teste"));
            using var cancellation = token.Register(() => Interlocked.Increment(ref _cancellations));
            await Release.Task; token.ThrowIfCancellationRequested();
            return new(id, "teste", "Resultado de teste", ConversationOutcome.Completed, []);
        }
    }
    internal sealed class Progress : IProgress<ConversationEvent> { public void Report(ConversationEvent value) { } }
    internal static async Task UntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
    public void Dispose()
    {
        var path = Path.GetFullPath(_root); var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("sintonia-concurrency-")) throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
