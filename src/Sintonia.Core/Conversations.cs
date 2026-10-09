namespace Sintonia.Core;

public enum ConversationAccess { ReadOnly, WorkspaceWrite }
public enum ConversationEventKind { Session, TextDelta, Message, Tool, PermissionDenied, Diagnostic, TokenUsage }
public enum ConversationOutcome { Completed, Blocked }

public sealed record ConversationRequest(string WorkingDirectory, string Prompt, string? Model = null,
    string? NativeSessionId = null, string Instructions = "", ConversationAccess Access = ConversationAccess.ReadOnly,
    Func<ConversationPermission, CancellationToken, Task<bool>>? PermissionHandler = null);
public sealed record ConversationPermission(ProviderKind Provider, string Kind, string Description, string WorkingDirectory);
public sealed record ConversationEvent(ConversationEventKind Kind, string Text, string? NativeSessionId = null,
    string? Model = null, RunTokenUsage? TokenUsage = null);
public sealed record ConversationResult(string NativeSessionId, string Model, string Text,
    ConversationOutcome Outcome, IReadOnlyList<string> PermissionDenials, RunTokenUsage? TokenUsage = null);
public sealed record ProviderModel(string Id, string Name, bool IsDefault);
public sealed record ProviderCapabilities(ProviderKind Provider, IReadOnlyList<ProviderModel> Models,
    IReadOnlyList<string> Extensions, IReadOnlyList<string> Warnings);

public interface IConversationProvider
{
    ProviderKind Kind { get; }
    Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress,
        CancellationToken cancellationToken);
}

public sealed class ProviderException(string message) : Exception(message);
