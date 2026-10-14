using Microsoft.Data.Sqlite;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    // Removing a registration never touches the project folder or its managed checkouts.
    public Task RemoveProjectAsync(string projectId) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        EnsureProjectActive(connection, transaction, projectId);
        using var active = connection.CreateCommand();
        active.Transaction = transaction;
        active.CommandText = """
            SELECT EXISTS(SELECT 1 FROM runs r JOIN conversations c ON c.id=r.conversation_id WHERE c.project_id=$project AND r.state=0)
                OR EXISTS(SELECT 1 FROM task_worktrees w JOIN work_tasks t ON t.id=w.task_id
                    JOIN task_batches b ON b.id=t.batch_id WHERE b.project_id=$project AND w.state=0)
                OR EXISTS(SELECT 1 FROM task_integrations i JOIN work_tasks t ON t.id=i.task_id
                    JOIN task_batches b ON b.id=t.batch_id WHERE b.project_id=$project AND i.state=0);
            """;
        active.Parameters.AddWithValue("$project", projectId);
        if (Convert.ToBoolean(active.ExecuteScalar()))
            throw new InvalidOperationException("Aguarde ou cancele as execuções e operações Git do projeto antes de removê-lo.");
        Execute(connection, transaction, "INSERT INTO removed_projects(project_id) VALUES($project)", ("$project", projectId));
        transaction.Commit();
        return true;
    });

    private static void EnsureProjectActive(SqliteConnection connection, SqliteTransaction transaction, string projectId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM projects WHERE id=$project AND id NOT IN (SELECT project_id FROM removed_projects))";
        command.Parameters.AddWithValue("$project", projectId);
        if (!Convert.ToBoolean(command.ExecuteScalar()))
            throw new InvalidOperationException("O projeto foi removido do Sintonia. Adicione a pasta novamente para continuar.");
    }
}
