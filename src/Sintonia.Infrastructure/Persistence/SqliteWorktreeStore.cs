using Microsoft.Data.Sqlite;
using Sintonia.Core;
using System.Text.Json;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    private static TaskWorktree? ReadWorktree(SqliteConnection connection, SqliteTransaction? transaction, string taskId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT definition,state,error FROM task_worktrees WHERE task_id=$task";
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var worktree = JsonSerializer.Deserialize<TaskWorktree>(reader.GetString(0))!
            with { State = (TaskWorktreeState)reader.GetInt32(1), Error = Optional(reader, 2) };
        worktree.ValidateDefinition();
        if (worktree.TaskId != taskId) throw new InvalidOperationException("Vínculo de worktree incompatível com a tarefa.");
        return worktree;
    }

    public Task<TaskWorktree> ReserveTaskWorktreeAsync(string projectId, TaskWorktree worktree) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var batch = ReadBatches(connection, transaction, projectId).Single(b => b.Tasks.Any(t => t.Id == worktree.TaskId));
        var task = batch.Tasks.Single(t => t.Id == worktree.TaskId);
        if (!WorkspaceTaskPolicy.CanPrepareWorktree(task, batch.Tasks))
            throw new InvalidOperationException("A tarefa mudou ou já iniciou. Atualize a fila antes de preparar a pasta.");
        using var project = connection.CreateCommand(); project.Transaction = transaction;
        project.CommandText = "SELECT directory FROM projects WHERE id=$project"; project.Parameters.AddWithValue("$project", projectId);
        worktree.ValidateDefinition((string)project.ExecuteScalar()!);
        var reserved = worktree with { State = TaskWorktreeState.Preparing, Error = null };
        EnsureCommonNotReserved(connection, transaction, worktree.CommonGitDirectory);
        if (task.Worktree is { } existing && existing with { State = TaskWorktreeState.Preparing, Error = null } != reserved)
            throw new InvalidOperationException("Preserve o vínculo original da tarefa ao retomar a preparação.");
        try
        {
            Execute(connection, transaction, """
                INSERT INTO task_worktrees(task_id,definition,state,error,checkout_key,common_key)
                VALUES($task,$definition,0,NULL,$checkout,$common)
                ON CONFLICT(task_id) DO UPDATE SET state=0,error=NULL WHERE task_worktrees.state=2;
                """, ("$task", reserved.TaskId), ("$definition", JsonSerializer.Serialize(reserved)),
                ("$checkout", Path.TrimEndingDirectorySeparator(Path.GetFullPath(reserved.CheckoutDirectory)).ToUpperInvariant()),
                ("$common", Path.TrimEndingDirectorySeparator(Path.GetFullPath(reserved.CommonGitDirectory)).ToUpperInvariant()));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        { throw new InvalidOperationException("Outra preparação usa este repositório ou esta pasta. Aguarde e atualize a fila.", exception); }
        transaction.Commit(); return reserved;
    });

    public Task FinishTaskWorktreeAsync(string taskId, bool ready, string? error) => RunAsync(connection =>
    {
        if (error?.Length > 4000) throw new ArgumentException("Mensagem de preparação muito longa.");
        if (Execute(connection, null, "UPDATE task_worktrees SET state=$state,error=$error WHERE task_id=$task AND state=0",
            ("$state", (int)(ready ? TaskWorktreeState.Ready : TaskWorktreeState.NeedsAttention)),
            ("$error", ready ? null : error), ("$task", taskId)) != 1)
            throw new InvalidOperationException("A preparação já foi encerrada. Atualize a fila e confira a pasta.");
        return true;
    });
}
