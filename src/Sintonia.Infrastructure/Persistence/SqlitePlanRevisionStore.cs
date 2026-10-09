using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    public Task<IReadOnlyList<string>> GetAppliedProposalIdsAsync(string projectId) => RunAsync<IReadOnlyList<string>>(connection =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT proposal_id FROM task_batches WHERE project_id=$project UNION SELECT u.proposal_id FROM task_plan_revisions u JOIN task_batches b ON b.id=u.batch_id WHERE b.project_id=$project";
        command.Parameters.AddWithValue("$project", projectId); using var reader = command.ExecuteReader(); var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0)); return ids;
    });
    public Task<WorkspaceTaskBatch> ReviseTaskBatchAsync(string projectId, string batchId, int batchRevision, string proposalId, int proposalRevision) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var batch = ReadBatches(connection, transaction, projectId).Single(b => b.Id == batchId);
        if (batch.Revision != batchRevision) throw new InvalidOperationException("O plano mudou após a prévia. Atualize a fila antes de aplicar.");
        WorkspaceProposal proposal;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,project_id,source_run_id,definition,state,revision FROM proposals WHERE project_id=$project AND id=$id AND revision=$revision AND state=1";
            command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$id", proposalId); command.Parameters.AddWithValue("$revision", proposalRevision);
            using var reader = command.ExecuteReader(); if (!reader.Read()) throw new InvalidOperationException("Confirme a revisão atual da proposta antes de atualizar a fila.");
            proposal = ReadProposal(reader);
        }
        using (var used = connection.CreateCommand())
        {
            used.Transaction = transaction; used.CommandText = "SELECT (SELECT COUNT(*) FROM task_batches WHERE proposal_id=$id) + (SELECT COUNT(*) FROM task_plan_revisions WHERE proposal_id=$id)";
            used.Parameters.AddWithValue("$id", proposalId);
            if (Convert.ToInt32(used.ExecuteScalar()) != 0) throw new InvalidOperationException("Esta proposta já foi encaminhada ou aplicada. Nenhuma tarefa será duplicada.");
        }
        TaskPlanUpdates.ValidateRevision(batch, proposal.Definition);
        foreach (var previous in batch.Tasks.Where(t => !TaskPlanUpdates.IsFrozen(t)))
        {
            var revised = proposal.Definition.Tasks.SingleOrDefault(t => t.Id == previous.Definition.Id);
            if (revised is null)
            {
                if (Execute(connection, transaction, "DELETE FROM work_tasks WHERE id=$id AND state=0 AND attempts=0 AND EXISTS(SELECT 1 FROM conversations c WHERE c.id=work_tasks.conversation_id AND c.native_session_id IS NULL AND NOT EXISTS(SELECT 1 FROM runs WHERE conversation_id=c.id))", ("$id", previous.Id)) != 1)
                    throw new InvalidOperationException("A sessão da tarefa mudou. Atualize a fila; nenhum histórico foi removido.");
                // The unused conversation remains available; no saved transcript or provider session is deleted.
            }
            else if (Execute(connection, transaction, "UPDATE conversations SET title=$title,provider=$provider,model=$model,function_name=$function,instructions=$instructions,access=$access WHERE id=$id AND native_session_id IS NULL AND NOT EXISTS(SELECT 1 FROM runs WHERE conversation_id=$id)",
                ("$id", previous.ConversationId), ("$title", revised.Title), ("$provider", (int)revised.Provider), ("$model", revised.Model),
                ("$function", revised.FunctionName), ("$instructions", revised.Instructions), ("$access", (int)(revised.FunctionName == PlanProposalFormat.ChiefFunctionName ? ConversationAccess.ReadOnly : revised.Access))) != 1)
                    throw new InvalidOperationException("A sessão da tarefa mudou. Atualize a fila antes de revisar o plano.");
        }
        foreach (var added in proposal.Definition.Tasks.Where(t => batch.Tasks.All(previous => previous.Definition.Id != t.Id)))
        {
            var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), projectId, added.Title, added.Provider, added.Model, null,
                added.FunctionName, added.Instructions, added.FunctionName == PlanProposalFormat.ChiefFunctionName ? ConversationAccess.ReadOnly : added.Access);
            SaveConversation(connection, transaction, conversation);
            Execute(connection, transaction, "INSERT INTO work_tasks(id,batch_id,plan_task_id,conversation_id,state,attempts) VALUES($id,$batch,$local,$conversation,0,0)",
                ("$id", Guid.NewGuid().ToString()), ("$batch", batchId), ("$local", added.Id), ("$conversation", conversation.Id));
        }
        Execute(connection, transaction, "INSERT INTO task_plan_revisions(batch_id,revision,proposal_id,definition,created_at) VALUES($batch,$revision,$proposal,$definition,$created)",
            ("$batch", batchId), ("$revision", checked(batchRevision + 1)), ("$proposal", proposalId), ("$definition", PlanProposalFormat.Serialize(batch.Definition)), ("$created", DateTimeOffset.UtcNow.ToString("O")));
        Execute(connection, transaction, "UPDATE task_batches SET definition=$definition WHERE id=$id", ("$id", batchId), ("$definition", PlanProposalFormat.Serialize(proposal.Definition)));
        var result = ReadBatches(connection, transaction, projectId).Single(b => b.Id == batchId); transaction.Commit(); return result;
    });
}
