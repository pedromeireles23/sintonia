using System.Collections.Concurrent;
using System.Text;

namespace Sintonia.Core;

/// <summary>Reserves capacity before persistence/inference; parallel writes require distinct validated checkouts.</summary>
public sealed class WorkspaceChatService(IWorkspaceStore store, IEnumerable<IConversationProvider> providers,
    IGitTaskWorktreeManager? worktrees = null)
{
    private readonly IReadOnlyDictionary<ProviderKind, IConversationProvider> _providers = providers.ToDictionary(p => p.Kind);
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkspaceExecutionSlot> _active = [];

    public Task<ChatRun> SendAsync(WorkspaceProject project, WorkspaceConversation conversation, string prompt,
        IProgress<ConversationEvent> progress, CancellationToken cancellationToken,
        Func<ConversationPermission, CancellationToken, Task<bool>>? permissionHandler = null)
    {
        if (conversation.IsTask) throw new InvalidOperationException("Inicie ou ajuste esta conversa pela fila de tarefas.");
        return SendCoreAsync(project, conversation, prompt, progress, cancellationToken, permissionHandler, null);
    }

    public async Task<ChatRun> SendTaskAsync(string projectId, string taskId, IProgress<ConversationEvent> progress,
        CancellationToken cancellationToken, Func<ConversationPermission, CancellationToken, Task<bool>>? permissionHandler = null)
    {
        var project = (await store.GetProjectsAsync().ConfigureAwait(false)).Single(p => p.Id == projectId);
        var batch = (await store.GetTaskBatchesAsync(projectId).ConfigureAwait(false)).Single(b => b.Tasks.Any(t => t.Id == taskId));
        var task = batch.Tasks.Single(t => t.Id == taskId);
        var dependencies = new List<ChatRun>();
        foreach (var id in task.Definition.Dependencies)
        {
            var dependency = batch.Tasks.Single(t => t.Definition.Id == id);
            dependencies.AddRange(await store.GetRunsAsync(dependency.ConversationId).ConfigureAwait(false));
        }
        var prompt = WorkspaceTaskPolicy.BuildPrompt(batch, task, dependencies);
        if (task.Worktree is { } worktree)
        {
            if (worktrees is null) throw new InvalidOperationException("O gerenciador de worktrees não está disponível nesta instalação.");
            await worktrees.ValidateAsync(worktree, cancellationToken).ConfigureAwait(false);
            project = project with { Directory = worktree.WorkingDirectory };
        }
        foreach (var dependency in batch.Tasks.Where(t => task.Definition.Dependencies.Contains(t.Definition.Id) && t.Publication is not null))
        {
            if (worktrees is not IGitTaskRevisionInspector revisions)
                throw new InvalidOperationException("A conferência da revisão integrada não está disponível nesta instalação.");
            await revisions.VerifyRevisionAsync(project.Directory, dependency.Publication!.Commit!, cancellationToken).ConfigureAwait(false);
        }
        var conversation = (await store.GetConversationsAsync(projectId).ConfigureAwait(false)).Single(c => c.Id == task.ConversationId);
        return await SendCoreAsync(project, conversation, prompt, progress, cancellationToken, permissionHandler, taskId, task.Worktree).ConfigureAwait(false);
    }

    private async Task<ChatRun> SendCoreAsync(WorkspaceProject project, WorkspaceConversation conversation, string prompt,
        IProgress<ConversationEvent> progress, CancellationToken cancellationToken,
        Func<ConversationPermission, CancellationToken, Task<bool>>? permissionHandler, string? taskId, TaskWorktree? expectedWorktree = null)
    {
        if (project.Id != conversation.ProjectId || string.IsNullOrWhiteSpace(prompt) || prompt.Length > 200_000)
            throw new ArgumentException("Projeto incompatível ou pedido vazio/extenso.");
        var instructions = conversation.Instructions;
        if (conversation.FunctionName == PlanProposalFormat.ChiefFunctionName)
        {
            conversation = conversation with { Access = ConversationAccess.ReadOnly };
            permissionHandler = null;
            instructions = PlanProposalFormat.ComposeChiefInstructions(instructions);
        }
        var settings = await store.GetProjectExecutionSettingsAsync(project.Id).ConfigureAwait(false);
        var slot = WorkspaceExecutionSlot.Create(project, conversation, expectedWorktree);
        lock (_gate)
        {
            if (!WorkspaceExecutionPolicy.CanAdmit(slot, _active.Values, settings.MaxConcurrentSessions, out var reason))
                throw new InvalidOperationException(reason);
            _active.Add(conversation.Id, slot);
        }
        try
        {
            var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, prompt, null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
            await store.BeginRunAsync(run, taskId, expectedWorktree, slot).ConfigureAwait(false);
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
