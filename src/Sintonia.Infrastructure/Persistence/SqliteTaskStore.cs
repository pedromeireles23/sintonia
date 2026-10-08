using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    public Task<IReadOnlyList<WorkspaceTaskBatch>> GetTaskBatchesAsync(string projectId) =>
        RunAsync<IReadOnlyList<WorkspaceTaskBatch>>(connection => ReadBatches(connection, null, projectId));

    private static List<WorkspaceTaskBatch> ReadBatches(SqliteConnection connection, SqliteTransaction? transaction, string projectId)
    {
        var batches = new List<WorkspaceTaskBatch>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,project_id,proposal_id,proposal_revision,definition FROM task_batches WHERE project_id=$project ORDER BY rowid DESC";
            command.Parameters.AddWithValue("$project", projectId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) batches.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                PlanProposalFormat.ParseJson(reader.GetString(4)), []));
        }
        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i]; var tasks = new List<WorkspaceTask>();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT id,plan_task_id,conversation_id,state,attempts,last_run_id,review_note FROM work_tasks WHERE batch_id=$batch ORDER BY rowid";
            command.Parameters.AddWithValue("$batch", batch.Id);
            using (var reader = command.ExecuteReader())
                while (reader.Read()) tasks.Add(new(reader.GetString(0), batch.Id, batch.Definition.Tasks.Single(t => t.Id == reader.GetString(1)),
                    reader.GetString(2), (WorkspaceTaskState)reader.GetInt32(3), reader.GetInt32(4), Optional(reader, 5), Optional(reader, 6)));
            for (var j = 0; j < tasks.Count; j++) tasks[j] = tasks[j] with { Worktree = ReadWorktree(connection, transaction, tasks[j].Id) };
            batches[i] = batch with { Tasks = tasks };
        }
        return batches;
    }

    public Task<WorkspaceTaskBatch> EnqueueProposalAsync(string projectId, string proposalId, int revision) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        WorkspaceProposal proposal;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,project_id,source_run_id,definition,state,revision FROM proposals WHERE id=$id AND project_id=$project AND revision=$revision AND state=1";
            command.Parameters.AddWithValue("$id", proposalId); command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$revision", revision);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Confirme a revisão atual deste plano antes de encaminhar à fila.");
            proposal = ReadProposal(reader);
        }
        var existing = ReadBatches(connection, transaction, projectId).SingleOrDefault(b => b.ProposalId == proposalId);
        if (existing is not null) { transaction.Commit(); return existing; }
        var batchId = Guid.NewGuid().ToString();
        Execute(connection, transaction, "INSERT INTO task_batches(id,project_id,proposal_id,proposal_revision,definition) VALUES($id,$project,$proposal,$revision,$definition)",
            ("$id", batchId), ("$project", projectId), ("$proposal", proposalId), ("$revision", revision), ("$definition", PlanProposalFormat.Serialize(proposal.Definition)));
        foreach (var task in proposal.Definition.Tasks)
        {
            var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), projectId, task.Title, task.Provider, task.Model, null,
                task.FunctionName, task.Instructions, task.FunctionName == PlanProposalFormat.ChiefFunctionName ? ConversationAccess.ReadOnly : task.Access);
            SaveConversation(connection, transaction, conversation);
            Execute(connection, transaction, "INSERT INTO work_tasks(id,batch_id,plan_task_id,conversation_id,state,attempts) VALUES($id,$batch,$local,$conversation,0,0)",
                ("$id", Guid.NewGuid().ToString()), ("$batch", batchId), ("$local", task.Id), ("$conversation", conversation.Id));
        }
        var result = ReadBatches(connection, transaction, projectId).Single(b => b.Id == batchId);
        transaction.Commit(); return result;
    });

    private static void ReserveTask(SqliteConnection connection, SqliteTransaction transaction, ChatRun run, string? taskId, TaskWorktree? expectedWorktree)
    {
        using var owner = connection.CreateCommand(); owner.Transaction = transaction;
        owner.CommandText = "SELECT t.id,b.project_id FROM work_tasks t JOIN task_batches b ON b.id=t.batch_id WHERE t.conversation_id=$conversation";
        owner.Parameters.AddWithValue("$conversation", run.ConversationId);
        string? ownedTask = null, projectId = null;
        using (var reader = owner.ExecuteReader())
            if (reader.Read()) { ownedTask = reader.GetString(0); projectId = reader.GetString(1); }
        if (taskId != ownedTask) throw new InvalidOperationException("Esta conversa deve ser executada pela tarefa correspondente na fila.");
        if (taskId is null)
        {
            if (expectedWorktree is not null) throw new InvalidOperationException("Worktrees pertencem a tarefas da fila.");
            return;
        }
        var batch = ReadBatches(connection, transaction, projectId!).Single(b => b.Tasks.Any(t => t.Id == taskId));
        var task = batch.Tasks.Single(t => t.Id == taskId);
        if (task.Worktree != expectedWorktree)
            throw new InvalidOperationException("A pasta de trabalho da tarefa mudou. Atualize a fila antes de executar.");
        if (!WorkspaceTaskPolicy.CanStart(task, batch.Tasks))
            throw new InvalidOperationException("Tarefa indisponível: confira dependências aprovadas, estado e limite de três tentativas.");
        Execute(connection, transaction, "UPDATE work_tasks SET state=1,attempts=attempts+1 WHERE id=$task", ("$task", taskId));
    }

    public Task ReviewTaskAsync(string projectId, string taskId, string runId, bool approve, string note)
    {
        if (note.Length > 4000 || (!approve && string.IsNullOrWhiteSpace(note))) throw new ArgumentException("Descreva o ajuste necessário, com até 4.000 caracteres.");
        return RunAsync(connection =>
        {
            if (Execute(connection, null, """
                UPDATE work_tasks SET state=$state,review_note=$note WHERE id=$task AND state=2 AND last_run_id=$run
                    AND EXISTS(SELECT 1 FROM task_batches b WHERE b.id=work_tasks.batch_id AND b.project_id=$project)
                    AND EXISTS(SELECT 1 FROM runs r WHERE r.id=$run AND r.state=$completed);
                """, ("$state", (int)(approve ? WorkspaceTaskState.Approved : WorkspaceTaskState.ChangesRequested)), ("$note", note.Trim()),
                ("$task", taskId), ("$run", runId), ("$project", projectId), ("$completed", (int)ChatRunState.Completed)) != 1)
                throw new InvalidOperationException("A entrega mudou ou não aguarda revisão neste projeto. Atualize a fila.");
            return true;
        });
    }
}
