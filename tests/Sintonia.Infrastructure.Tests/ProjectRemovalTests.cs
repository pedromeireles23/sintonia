using Sintonia.Core;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Infrastructure.Tests;

public sealed class ProjectRemovalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Sintonia-project-removal-" + Guid.NewGuid());
    private string Database => Path.Combine(_directory, "history.db");
    public ProjectRemovalTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task RemovalPreservesFilesAndHistoryAndReaddingRestoresTheSameProject()
    {
        var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync();
        var project = await store.AddProjectAsync(_directory);
        var otherDirectory = Path.Combine(_directory, "outro"); Directory.CreateDirectory(otherDirectory);
        var other = await store.AddProjectAsync(otherDirectory);
        var file = Path.Combine(_directory, "entrega.txt"); await File.WriteAllTextAsync(file, "preservar");
        var conversation = Conversation(project); await store.SaveConversationAsync(conversation);
        var run = Run(conversation); await store.BeginRunAsync(run);
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "Entrega salva", FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        await store.RemoveProjectAsync(project.Id);
        var reopened = new SqliteWorkspaceStore(Database); await reopened.InitializeAsync();
        Assert.Equal(other.Id, Assert.Single(await reopened.GetProjectsAsync()).Id);
        Assert.Equal("preservar", await File.ReadAllTextAsync(file));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.BeginRunAsync(Run(conversation)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SaveConversationAsync(conversation));
        Assert.Equal(project, await reopened.AddProjectAsync(_directory));
        Assert.Equal(conversation, Assert.Single(await reopened.GetConversationsAsync(project.Id)));
        Assert.Equal("Entrega salva", Assert.Single(await reopened.GetRunsAsync(conversation.Id)).Response);
        Assert.Equal(2, (await reopened.GetProjectsAsync()).Count);
    }

    [Fact]
    public async Task ActiveRunPreventsRemovalAcrossStoreInstancesUntilItFinishes()
    {
        var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync();
        var project = await store.AddProjectAsync(_directory); var conversation = Conversation(project);
        await store.SaveConversationAsync(conversation); var run = Run(conversation); await store.BeginRunAsync(run);
        var other = new SqliteWorkspaceStore(Database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.RemoveProjectAsync(project.Id));
        Assert.Single(await store.GetProjectsAsync());
        await store.FinishRunAsync(run with { State = ChatRunState.Cancelled, FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        await other.RemoveProjectAsync(project.Id); Assert.Empty(await store.GetProjectsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveProjectAsync(project.Id));
    }

    private static WorkspaceConversation Conversation(WorkspaceProject project) => new(Guid.NewGuid().ToString(), project.Id,
        "Histórico preservado", ProviderKind.Codex, null, null, "Conversa", "", ConversationAccess.ReadOnly);
    private static ChatRun Run(WorkspaceConversation conversation) => new(Guid.NewGuid().ToString(), conversation.Id,
        "Pedido", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
    public void Dispose()
    {
        if (!Path.GetFileName(_directory).StartsWith("Sintonia-project-removal-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
