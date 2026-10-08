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
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken cancellationToken)
        {
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
