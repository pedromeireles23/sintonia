using System.Collections.Concurrent;
using System.Text;

namespace Sintonia.Core;

/// <summary>Reserves capacity before persistence/inference. Writing conversations run alone in their project.</summary>
public sealed class WorkspaceChatService(IWorkspaceStore store, IEnumerable<IConversationProvider> providers)
{
    private readonly IReadOnlyDictionary<ProviderKind, IConversationProvider> _providers = providers.ToDictionary(p => p.Kind);
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkspaceConversation> _active = [];

    public async Task<ChatRun> SendAsync(WorkspaceProject project, WorkspaceConversation conversation, string prompt,
        IProgress<ConversationEvent> progress, CancellationToken cancellationToken,
        Func<ConversationPermission, CancellationToken, Task<bool>>? permissionHandler = null)
    {
        if (project.Id != conversation.ProjectId || string.IsNullOrWhiteSpace(prompt) || prompt.Length > 200_000)
            throw new ArgumentException("Projeto incompatível ou pedido vazio/extenso.");
        var instructions = conversation.Instructions;
        if (conversation.FunctionName == PlanProposalFormat.ChiefFunctionName)
        {
            conversation = conversation with { Access = ConversationAccess.ReadOnly };
            permissionHandler = null;
            instructions = PlanProposalFormat.ChiefInstructions + "\n\nInstruções adicionais do projeto:\n" + instructions;
            if (instructions.Length > 8000) throw new ArgumentException("Reduza as instruções adicionais do chefe: o formato do plano também ocupa parte do limite de 8.000 caracteres.");
        }
        lock (_gate)
        {
            if (_active.Count >= 2 || _active.ContainsKey(conversation.Id) || _active.Values.Any(c => c.Provider == conversation.Provider))
                throw new InvalidOperationException("Aguarde a execução ativa deste provedor. Limite: duas conversas, uma por IA.");
            if (_active.Values.Any(c => c.ProjectId == project.Id && (c.Access == ConversationAccess.WorkspaceWrite || conversation.Access == ConversationAccess.WorkspaceWrite)))
                throw new InvalidOperationException("Uma conversa com escrita trabalha sozinha neste projeto. Aguarde ou use outra pasta.");
            _active.Add(conversation.Id, conversation);
        }
        try
        {
            var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, prompt, null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
            await store.BeginRunAsync(run).ConfigureAwait(false);
            var text = new StringBuilder();
            var events = new ConcurrentQueue<ChatEvent>();
            var checkpoint = Task.CompletedTask;
            var dataGate = new object();
            var lastCheckpoint = DateTimeOffset.UtcNow;
            var live = new InlineProgress(ev =>
            {
                lock (dataGate)
                {
                    if (ev.Kind == ConversationEventKind.Session)
                        conversation = conversation with { NativeSessionId = ev.NativeSessionId, Model = ev.Model };
                    if (ev.Kind == ConversationEventKind.TextDelta)
                        text.Append(ev.Text.AsSpan(0, Math.Min(ev.Text.Length, 256_000 - text.Length)));
                    else
                    {
                        events.Enqueue(new(run.Id, ev.Kind, ev.Text));
                        while (events.Count > 500) events.TryDequeue(out _);
                    }
                    if (ev.Kind == ConversationEventKind.Session || DateTimeOffset.UtcNow - lastCheckpoint > TimeSpan.FromSeconds(2))
                    {
                        lastCheckpoint = DateTimeOffset.UtcNow;
                        var saved = run with { Response = text.ToString() };
                        var session = conversation;
                        checkpoint = ChainCheckpointAsync(checkpoint, saved, session);
                    }
                }
                progress.Report(ev);
            });
            try
            {
                var result = await _providers[conversation.Provider].SendAsync(new(project.Directory, prompt, conversation.Model,
                    conversation.NativeSessionId, instructions, conversation.Access, permissionHandler), live, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                conversation = conversation with { NativeSessionId = result.NativeSessionId, Model = result.Model };
                run = run with { Response = result.Text, State = result.Outcome == ConversationOutcome.Completed ? ChatRunState.Completed : ChatRunState.Blocked,
                    Error = result.PermissionDenials.Count > 0 ? "Permissões recusadas: " + string.Join(", ", result.PermissionDenials) : null };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                run = run with { Response = text.ToString(), State = ChatRunState.Cancelled, Error = "Execução cancelada. Confira possíveis efeitos antes de reenviar." };
            }
            catch (Exception exception)
            {
                run = run with { Response = text.ToString(), State = ChatRunState.Failed, Error = exception.Message };
            }
            await checkpoint.ConfigureAwait(false);
            run = run with { FinishedAt = DateTimeOffset.UtcNow };
            await store.FinishRunAsync(run, conversation, events.ToArray()).ConfigureAwait(false);
            return run;
        }
        finally { lock (_gate) _active.Remove(conversation.Id); }
    }

    private async Task ChainCheckpointAsync(Task previous, ChatRun run, WorkspaceConversation conversation)
    {
        await previous.ConfigureAwait(false);
        await store.CheckpointRunAsync(run, conversation).ConfigureAwait(false);
    }
    private sealed class InlineProgress(Action<ConversationEvent> report) : IProgress<ConversationEvent>
    {
        public void Report(ConversationEvent value) => report(value);
    }
}
