namespace Sintonia.Core;

public sealed record WorkspaceProject(string Id, string Name, string Directory);
public enum ChatRunState { Running, Completed, Blocked, Failed, Cancelled, Interrupted }
public sealed record WorkspaceConversation(string Id, string ProjectId, string Title, ProviderKind Provider,
    string? Model, string? NativeSessionId, string FunctionName, string Instructions, ConversationAccess Access);
public sealed record ChatRun(string Id, string ConversationId, string Prompt, string? Response, ChatRunState State,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error);
public sealed record ChatEvent(string RunId, ConversationEventKind Kind, string Text);

public interface IWorkspaceStore
{
    Task InitializeAsync();
    Task<IReadOnlyList<WorkspaceProject>> GetProjectsAsync();
    Task<WorkspaceProject> AddProjectAsync(string directory);
    Task<IReadOnlyList<WorkspaceConversation>> GetConversationsAsync(string projectId);
    Task SaveConversationAsync(WorkspaceConversation conversation);
    Task<IReadOnlyList<ChatRun>> GetRunsAsync(string conversationId);
    Task<IReadOnlyList<ChatEvent>> GetEventsAsync(string conversationId);
    Task BeginRunAsync(ChatRun run);
    Task CheckpointRunAsync(ChatRun run, WorkspaceConversation conversation);
    Task FinishRunAsync(ChatRun run, WorkspaceConversation conversation, IReadOnlyList<ChatEvent> events);
    Task RecoverInterruptedRunsAsync();
}
