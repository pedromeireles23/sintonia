namespace Sintonia.Core;

public sealed record ProjectExecutionSettings(string ProjectId, int Revision = 0, int MaxConcurrentSessions = 3, int MaxAttempts = 3, int MaxExecutionSeconds = 300,
    long? MaxReportedTokens = null, long TokenReservation = 1000)
{
    public const int Minimum = 3;
    public const int Maximum = 7;
    public void Validate()
    {
        if (!Guid.TryParse(ProjectId, out _) || Revision < 0 || MaxConcurrentSessions is < Minimum or > Maximum || MaxAttempts is < 1 or > 3 || MaxExecutionSeconds is < 1 or > 300)
            throw new ArgumentException("Escolha 3–7 sessões, 1–3 tentativas por tarefa e prazo de 1–300 segundos por execução.");
        if (MaxReportedTokens is < 1 or > 1_000_000_000_000 || TokenReservation is < 1 or > 1_000_000_000_000
            || (MaxReportedTokens is { } limit && TokenReservation > limit))
            throw new ArgumentException("Use limite e reserva de tokens entre 1 e 1.000.000.000.000; a reserva não pode superar o limite. Deixe o limite vazio para desativar.");
    }
}

/// <summary>Frozen execution scope; checkout roots, rather than task subfolders, define overlap.</summary>
public sealed record WorkspaceExecutionSlot(string ConversationId, string ProjectId, ConversationAccess Access,
    string Directory, bool Isolated)
{
    public static WorkspaceExecutionSlot Create(WorkspaceProject project, WorkspaceConversation conversation, TaskWorktree? worktree = null) =>
        new(conversation.Id, project.Id, conversation.FunctionName == PlanProposalFormat.ChiefFunctionName
                ? ConversationAccess.ReadOnly : conversation.Access,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree?.CheckoutDirectory ?? project.Directory)),
            worktree?.State == TaskWorktreeState.Ready);
}

public static class WorkspaceExecutionPolicy
{
    // Shared by all projects, chats and task queues in this workspace database.
    public const int GlobalMaximum = 7;

    public static bool CanAdmit(WorkspaceExecutionSlot candidate, IReadOnlyCollection<WorkspaceExecutionSlot> active,
        int projectLimit, out string? reason)
    {
        if (active.Any(s => s.ConversationId == candidate.ConversationId))
            reason = "Esta sessão já está executando.";
        else if (active.Count >= GlobalMaximum)
            reason = "As 7 vagas globais estão ocupadas. Aguarde uma sessão encerrar.";
        else if (active.Count(s => s.ProjectId == candidate.ProjectId) >= projectLimit)
            reason = $"As {projectLimit} vagas deste projeto estão ocupadas. Aguarde ou ajuste o limite.";
        else if (active.Any(s => Conflicts(candidate, s)))
            reason = "Escrita na pasta original é exclusiva. Para trabalhar em paralelo, prepare worktrees distintas para as tarefas.";
        else { reason = null; return true; }
        return false;
    }

    private static bool Conflicts(WorkspaceExecutionSlot a, WorkspaceExecutionSlot b)
    {
        if (a.Access != ConversationAccess.WorkspaceWrite && b.Access != ConversationAccess.WorkspaceWrite) return false;
        if (a.ProjectId == b.ProjectId && ((a.Access == ConversationAccess.WorkspaceWrite && !a.Isolated)
            || (b.Access == ConversationAccess.WorkspaceWrite && !b.Isolated))) return true;
        return Within(a.Directory, b.Directory) || Within(b.Directory, a.Directory);
    }
    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
