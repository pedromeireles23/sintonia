namespace Sintonia.Core;

public enum ConversationAccess { ReadOnly, WorkspaceWrite }
public enum ConversationEventKind { Session, TextDelta, Message, Tool, PermissionDenied, Diagnostic, TokenUsage }
public enum ConversationOutcome { Completed, Blocked }

public sealed record ConversationRequest(string WorkingDirectory, string Prompt, string? Model = null,
    string? NativeSessionId = null, string Instructions = "", ConversationAccess Access = ConversationAccess.ReadOnly,
    Func<ConversationPermission, CancellationToken, Task<bool>>? PermissionHandler = null,
    Func<ConversationQuestionRequest, CancellationToken, Task<ConversationQuestionResponse?>>? QuestionHandler = null,
    string? ReasoningEffort = null);
public sealed record ConversationPermission(ProviderKind Provider, string Kind, string Description, string WorkingDirectory);
public sealed record ConversationEvent(ConversationEventKind Kind, string Text, string? NativeSessionId = null,
    string? Model = null, RunTokenUsage? TokenUsage = null, string? ReasoningEffort = null);
public sealed record ConversationResult(string NativeSessionId, string Model, string Text,
    ConversationOutcome Outcome, IReadOnlyList<string> PermissionDenials, RunTokenUsage? TokenUsage = null, string? ReasoningEffort = null);
public sealed record ProviderReasoningEffort(string Id, string Description);
public sealed record ProviderModel(string Id, string Name, bool IsDefault, string Description = "",
    string? DefaultReasoningEffort = null, IReadOnlyList<ProviderReasoningEffort>? ReasoningEfforts = null);
public sealed record ProviderModelDefaults(string? Model, string? ReasoningEffort, string ModelSource, string ReasoningSource);
public sealed record ProviderCapabilities(ProviderKind Provider, IReadOnlyList<ProviderModel> Models,
    IReadOnlyList<string> Extensions, IReadOnlyList<string> Warnings, ProviderModelDefaults? Defaults = null);

public interface IConversationProvider
{
    ProviderKind Kind { get; }
    Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress,
        CancellationToken cancellationToken);
}

public enum ProviderFailureKind { Unspecified, AuthenticationRefreshFailed }
public sealed class ProviderException(string message, ProviderFailureKind failureKind = ProviderFailureKind.Unspecified) : Exception(message)
{
    public ProviderFailureKind FailureKind { get; } = failureKind;
}
