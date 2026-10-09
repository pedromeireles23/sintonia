namespace Sintonia.Core;

public sealed record WorkspaceProject(string Id, string Name, string Directory);
public enum ChatRunState { Running, Completed, Blocked, Failed, Cancelled, Interrupted }
public sealed record WorkspaceConversation(string Id, string ProjectId, string Title, ProviderKind Provider,
    string? Model, string? NativeSessionId, string FunctionName, string Instructions, ConversationAccess Access, bool IsTask = false);
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
    Task BeginRunAsync(ChatRun run, string? taskId = null, TaskWorktree? expectedWorktree = null);
    Task CheckpointRunAsync(ChatRun run, WorkspaceConversation conversation);
    Task FinishRunAsync(ChatRun run, WorkspaceConversation conversation, IReadOnlyList<ChatEvent> events);
    Task RecoverInterruptedRunsAsync();
    Task<IReadOnlyList<WorkspaceProposal>> GetProposalsAsync(string projectId);
    Task<WorkspaceProposal> CreateProposalAsync(string projectId, string sourceRunId);
    Task<WorkspaceProposal> SaveProposalAsync(WorkspaceProposal proposal);
    Task<IReadOnlyList<WorkspaceTaskBatch>> GetTaskBatchesAsync(string projectId);
    Task<WorkspaceTaskBatch> EnqueueProposalAsync(string projectId, string proposalId, int revision);
    Task ReviewTaskAsync(string projectId, string taskId, string runId, bool approve, string note);
    Task<TaskWorktree> ReserveTaskWorktreeAsync(string projectId, TaskWorktree worktree);
    Task FinishTaskWorktreeAsync(string taskId, bool ready, string? error);
    Task<TaskDelivery> SaveTaskDeliveryAsync(string projectId, TaskDelivery delivery);
    Task<TaskIntegrationReservation> ReserveTaskIntegrationAsync(string projectId, TaskDelivery delivery, TaskIntegrationTarget target);
    Task<IReadOnlyList<TaskIntegrationReservation>> GetTaskIntegrationsAsync(string projectId);
    Task ReleaseTaskIntegrationAsync(string reservationId, string? error = null);
    Task SaveTaskIntegrationPreparationAsync(TaskIntegrationPreparation preparation);
    Task FinishTaskIntegrationPreparationAsync(TaskIntegrationPreparation preparation);
    Task<IReadOnlyList<TaskIntegrationPreparation>> GetTaskIntegrationPreparationsAsync(string projectId);
    Task<ProjectValidationConfiguration> GetProjectValidationAsync(string projectId);
    Task<ProjectValidationConfiguration> SaveProjectValidationAsync(ProjectValidationConfiguration configuration);
    Task SaveTaskIntegrationValidationAsync(TaskIntegrationValidation validation);
    Task FinishTaskIntegrationValidationAsync(TaskIntegrationValidation validation);
    Task<IReadOnlyList<TaskIntegrationValidation>> GetTaskIntegrationValidationsAsync(string projectId);
    Task<IReadOnlyList<WorkspaceFunctionProfile>> GetFunctionProfilesAsync();
    Task<WorkspaceFunctionProfile> CreateFunctionProfileAsync(string name, string functionName, ProviderKind provider, string? model, string instructions);
    Task<WorkspaceFunctionProfile> SaveFunctionProfileAsync(WorkspaceFunctionProfile profile);
    Task DeleteFunctionProfileAsync(string id, int revision);
}
