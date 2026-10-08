using Sintonia.Core;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Providers;

internal static class WorkspaceQueueProbe
{
    public static async Task RunAsync(CancellationToken token)
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "provider-probes", "Fila geral " + Guid.NewGuid()));
        Directory.CreateDirectory(directory); var database = Path.Combine(directory, "queue.db");
        var store = new SqliteWorkspaceStore(database); await store.InitializeAsync(); var project = await store.AddProjectAsync(directory);
        var marker = "CHECK-" + Guid.NewGuid().ToString("N");
        var plan = new PlanProposal(1, "Diagnóstico finito da fila", "Conferir a transferência explícita de uma entrega entre provedores.",
            [new("answer", "Produzir resposta verificável", "Análise", ProviderKind.Codex, null, ConversationAccess.ReadOnly,
                "Sem ferramentas, arquivos ou outros agentes. Responda somente o marcador literal " + marker + " e a soma de 11 com 14.", ["."], [], ["Marcador literal e soma correta."]),
             new("check", "Conferir entrega anterior", "Revisão", ProviderKind.Claude, null, ConversationAccess.ReadOnly,
                "Sem ferramentas, arquivos ou outros agentes. Confira a resposta aprovada da dependência fornecida neste pedido. "
                + "Responda somente o marcador que ela contém e o número informado, para comprovar recebimento do contexto.", ["."], ["answer"], ["Repetir o marcador e o número da entrega anterior."])]);
        // Diagnostic seed only. This is not presented as a model-generated proposal or a user project.
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Plano de diagnóstico", ProviderKind.Codex,
            null, null, PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(conversation);
        var source = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "Plano fornecido pelo diagnóstico", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(source);
        await store.FinishRunAsync(source with { State = ChatRunState.Completed, Response = "DIAGNÓSTICO: plano fornecido, sem inferência de planejamento.\n```sintonia-plan\n"
            + PlanProposalFormat.Serialize(plan) + "\n```", FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        var proposal = await store.CreateProposalAsync(project.Id, source.Id);
        proposal = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
        var batch = await store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision);
        var service = new WorkspaceChatService(store, [new CodexConversationProvider(), new ClaudeConversationProvider()]);
        var first = await service.SendTaskAsync(project.Id, batch.Tasks[0].Id, new SilentProgress(), token);
        Check(first, marker);
        if ((await store.GetTaskBatchesAsync(project.Id)).Single().Tasks[0].State != WorkspaceTaskState.AwaitingReview)
            throw new ProviderException("A tarefa não aguardou revisão.");
        await store.ReviewTaskAsync(project.Id, batch.Tasks[0].Id, first.Id, true, "Critérios conferidos pelo diagnóstico local na pasta exclusiva de teste.");
        var second = await service.SendTaskAsync(project.Id, batch.Tasks[1].Id, new SilentProgress(), token);
        Check(second, marker);
        var reopened = new SqliteWorkspaceStore(database); await reopened.InitializeAsync();
        var saved = (await reopened.GetTaskBatchesAsync(project.Id)).Single();
        if (saved.Tasks[0].State != WorkspaceTaskState.Approved || saved.Tasks[1].State != WorkspaceTaskState.AwaitingReview
            || saved.Tasks.Any(t => t.Attempts != 1)) throw new ProviderException("Histórico ou revisão da fila não foi preservado.");
        var sessions = (await reopened.GetConversationsAsync(project.Id)).Where(c => c.IsTask).ToArray();
        Console.WriteLine("PASS: dois turnos reais em leitura, um por provedor, dependência liberada após revisão do diagnóstico, contexto transferido e SQLite reaberto. "
            + string.Join("; ", sessions.Select(c => c.Provider + " " + c.Model)) + ". Evidência local: " + directory);
    }
    private static void Check(ChatRun run, string marker)
    {
        if (run.State != ChatRunState.Completed || run.Response?.Contains(marker, StringComparison.Ordinal) != true || !run.Response.Contains("25", StringComparison.Ordinal))
            throw new ProviderException("A resposta da fila não cumpriu os critérios; nenhuma nova tentativa será iniciada. " + run.Error);
    }
    private sealed class SilentProgress : IProgress<ConversationEvent> { public void Report(ConversationEvent value) { } }
}
