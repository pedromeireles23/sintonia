using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    private static TaskDelivery? ReadDelivery(SqliteConnection connection, SqliteTransaction? transaction, string taskId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT definition,run_id FROM task_deliveries WHERE task_id=$task"; command.Parameters.AddWithValue("$task", taskId);
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var delivery = JsonSerializer.Deserialize<TaskDelivery>(reader.GetString(0))!; delivery.ValidateDefinition();
        if (delivery.TaskId != taskId || delivery.SourceRunId != reader.GetString(1)) throw new InvalidOperationException("Registro de entrega incompatível com a tarefa.");
        return delivery;
    }
    public Task<TaskDelivery> SaveTaskDeliveryAsync(string projectId, TaskDelivery delivery) => RunAsync(connection =>
    {
        delivery.ValidateDefinition(); using var transaction = connection.BeginTransaction();
        var task = ApprovedDeliveryTask(connection, transaction, projectId, delivery);
        if (task.Delivery is { } existing)
        {
            if (existing with { RegisteredAt = delivery.RegisteredAt } != delivery)
                throw new InvalidOperationException("Esta tarefa já tem um commit registrado. A entrega original foi preservada.");
            transaction.Commit(); return existing;
        }
        Execute(connection, transaction, "INSERT INTO task_deliveries(task_id,run_id,definition) VALUES($task,$run,$definition)",
            ("$task", delivery.TaskId), ("$run", delivery.SourceRunId), ("$definition", JsonSerializer.Serialize(delivery)));
        transaction.Commit(); return delivery;
    });
    public Task<TaskIntegrationReservation> ReserveTaskIntegrationAsync(string projectId, TaskDelivery delivery, TaskIntegrationTarget target) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var result = ReserveIntegration(connection, transaction, projectId, delivery, target, Guid.NewGuid().ToString()); transaction.Commit(); return result;
    });
    private static TaskIntegrationReservation ReserveIntegration(SqliteConnection connection, SqliteTransaction transaction, string projectId,
        TaskDelivery delivery, TaskIntegrationTarget target, string id, bool archiving = false)
    {
        target.ValidateFor(delivery);
        var task = ApprovedDeliveryTask(connection, transaction, projectId, delivery);
        if (!archiving && task.Cleanup?.BlocksCheckout == true) throw new InvalidOperationException("A worktree tem um arquivamento registrado. Confira a fila antes de integrar.");
        if (task.Delivery != delivery) throw new InvalidOperationException("Registre esta entrega antes de reservar a integração.");
        EnsureCommonNotReserved(connection, transaction, target.CommonGitDirectory);
        using (var preparing = connection.CreateCommand())
        {
            preparing.Transaction = transaction; preparing.CommandText = "SELECT COUNT(*) FROM task_worktrees WHERE common_key=$common AND state=0";
            preparing.Parameters.AddWithValue("$common", PathKey(target.CommonGitDirectory));
            if (Convert.ToInt32(preparing.ExecuteScalar()) != 0) throw new InvalidOperationException("Aguarde a preparação de worktrees deste repositório antes de integrar.");
        }
        using (var live = connection.CreateCommand())
        {
            live.Transaction = transaction; live.CommandText = """
                SELECT p.directory,w.common_key FROM runs r JOIN conversations c ON c.id=r.conversation_id
                JOIN projects p ON p.id=c.project_id LEFT JOIN work_tasks t ON t.conversation_id=c.id
                LEFT JOIN task_worktrees w ON w.task_id=t.id WHERE r.state=0;
                """;
            using var reader = live.ExecuteReader();
            while (reader.Read())
                if (UsesRepository(connection, transaction, reader.GetString(0), target) || Optional(reader, 1) == PathKey(target.CommonGitDirectory))
                    throw new InvalidOperationException("Aguarde as execuções deste repositório antes de reservar a integração.");
        }
        var reservation = new TaskIntegrationReservation(id, delivery, target, DateTimeOffset.UtcNow);
        Execute(connection, transaction, "INSERT INTO task_integrations(id,task_id,definition,common_key,state) VALUES($id,$task,$definition,$common,0)",
            ("$id", reservation.Id), ("$task", delivery.TaskId), ("$definition", JsonSerializer.Serialize(reservation)), ("$common", PathKey(target.CommonGitDirectory)));
        return reservation;
    }
    public Task<IReadOnlyList<TaskIntegrationReservation>> GetTaskIntegrationsAsync(string projectId) => RunAsync<IReadOnlyList<TaskIntegrationReservation>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.definition,i.state,i.error FROM task_integrations i JOIN work_tasks t ON t.id=i.task_id
            JOIN task_batches b ON b.id=t.batch_id WHERE b.project_id=$project ORDER BY i.rowid;
            """;
        command.Parameters.AddWithValue("$project", projectId); using var reader = command.ExecuteReader(); var list = new List<TaskIntegrationReservation>();
        while (reader.Read()) list.Add(ReadIntegration(reader)); return list;
    });
    public Task ReleaseTaskIntegrationAsync(string reservationId, string? error = null) => RunAsync(connection =>
    {
        if (error?.Length > 4000) throw new ArgumentException("Mensagem de integração muito longa.");
        if (Execute(connection, null, """
            UPDATE task_integrations SET state=$state,error=$error WHERE id=$id AND state=0
            AND NOT EXISTS(SELECT 1 FROM task_integration_preparations p WHERE p.id=task_integrations.id AND p.state=0)
            AND NOT EXISTS(SELECT 1 FROM task_integration_validations v WHERE v.id=task_integrations.id AND v.state=0)
            AND NOT EXISTS(SELECT 1 FROM task_publications p WHERE p.id=task_integrations.id AND p.state=0)
            AND NOT EXISTS(SELECT 1 FROM task_worktree_cleanups c WHERE c.id=task_integrations.id AND c.state=0)
            """,
            ("$state", (int)(error is null ? TaskIntegrationState.Released : TaskIntegrationState.NeedsAttention)), ("$error", error), ("$id", reservationId)) != 1)
            throw new InvalidOperationException("Esta reserva já terminou ou tem uma combinação em andamento. Nenhuma reserva mais nova foi alterada.");
        return true;
    });
    private static TaskIntegrationReservation ReadIntegration(SqliteDataReader reader)
    {
        var result = JsonSerializer.Deserialize<TaskIntegrationReservation>(reader.GetString(0))!
            with { State = (TaskIntegrationState)reader.GetInt32(1), Error = Optional(reader, 2) };
        result.Target.ValidateFor(result.Delivery);
        if (!Guid.TryParse(result.Id, out _) || !Enum.IsDefined(result.State)) throw new InvalidOperationException("Reserva de integração inválida.");
        return result;
    }
    private static WorkspaceTask ApprovedDeliveryTask(SqliteConnection connection, SqliteTransaction transaction, string projectId, TaskDelivery delivery)
    {
        var task = ReadBatches(connection, transaction, projectId).SelectMany(b => b.Tasks).Single(t => t.Id == delivery.TaskId);
        if (task.State != WorkspaceTaskState.Approved || task.LastRunId != delivery.SourceRunId || task.Worktree != delivery.Worktree)
            throw new InvalidOperationException("A entrega ou a aprovação mudou. Atualize a fila antes de registrar ou integrar.");
        using var run = connection.CreateCommand(); run.Transaction = transaction;
        run.CommandText = "SELECT COUNT(*) FROM runs WHERE id=$run AND conversation_id=$conversation AND state=$completed";
        run.Parameters.AddWithValue("$run", delivery.SourceRunId); run.Parameters.AddWithValue("$conversation", task.ConversationId);
        run.Parameters.AddWithValue("$completed", (int)ChatRunState.Completed);
        if (Convert.ToInt32(run.ExecuteScalar()) != 1) throw new InvalidOperationException("A tentativa aprovada não está concluída nesta tarefa.");
        return task;
    }
    private static void EnsureCommonNotReserved(SqliteConnection connection, SqliteTransaction transaction, string commonDirectory)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM task_integrations WHERE common_key=$common AND state=0";
        command.Parameters.AddWithValue("$common", PathKey(commonDirectory));
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) throw new InvalidOperationException("Este repositório tem uma integração reservada. Aguarde e confira o estado.");
    }
    private static void EnsureConversationNotReserved(SqliteConnection connection, SqliteTransaction transaction, string conversationId, TaskWorktree? worktree)
    {
        using var project = connection.CreateCommand(); project.Transaction = transaction;
        project.CommandText = "SELECT p.directory FROM conversations c JOIN projects p ON p.id=c.project_id WHERE c.id=$conversation";
        project.Parameters.AddWithValue("$conversation", conversationId); var directory = project.ExecuteScalar() as string;
        if (directory is null) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT definition,state,error FROM task_integrations WHERE state=0"; using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var reserved = ReadIntegration(reader);
            if (UsesRepository(connection, transaction, directory, reserved.Target) || (worktree is not null && PathKey(worktree.CommonGitDirectory) == PathKey(reserved.Target.CommonGitDirectory)))
                throw new InvalidOperationException("A integração deste repositório está reservada. Nenhuma tentativa foi iniciada.");
        }
    }
    private static string PathKey(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
    private static bool UsesRepository(SqliteConnection connection, SqliteTransaction transaction, string directory, TaskIntegrationTarget target)
    {
        if (Within(directory, target.RepositoryDirectory) || Within(target.RepositoryDirectory, directory)) return true;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT definition,0 FROM task_worktrees WHERE common_key=$common
            UNION ALL SELECT p.definition,1 FROM task_integration_preparations p
            JOIN task_integrations i ON i.id=p.id WHERE i.common_key=$common
            """;
        command.Parameters.AddWithValue("$common", PathKey(target.CommonGitDirectory)); using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string checkout;
            if (reader.GetInt32(1) == 0)
            {
                var worktree = JsonSerializer.Deserialize<TaskWorktree>(reader.GetString(0))!; worktree.ValidateDefinition(); checkout = worktree.CheckoutDirectory;
            }
            else
            {
                var preparation = JsonSerializer.Deserialize<TaskIntegrationPreparation>(reader.GetString(0))!;
                preparation.ValidateDefinition(); checkout = preparation.CheckoutDirectory;
            }
            if (Within(directory, checkout) || Within(checkout, directory)) return true;
        }
        return false;
    }
    private static bool Within(string path, string root) => PathKey(path) == PathKey(root)
        || PathKey(path).StartsWith(PathKey(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
