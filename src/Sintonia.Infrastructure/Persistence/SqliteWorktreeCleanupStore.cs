using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    public Task<IReadOnlyList<TaskWorktreeCleanup>> GetTaskWorktreeCleanupsAsync(string projectId) => RunAsync<IReadOnlyList<TaskWorktreeCleanup>>(connection =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT definition,state,finished_at,error FROM task_worktree_cleanups WHERE project_id=$project ORDER BY rowid";
        command.Parameters.AddWithValue("$project", projectId); using var reader = command.ExecuteReader(); var records = new List<TaskWorktreeCleanup>();
        while (reader.Read()) records.Add(ReadCleanup(reader)); return records;
    });
    private static TaskWorktreeCleanup ReadCleanup(SqliteDataReader reader)
    {
        var record = JsonSerializer.Deserialize<TaskWorktreeCleanup>(reader.GetString(0))! with { State = (TaskWorktreeCleanupState)reader.GetInt32(1),
            FinishedAt = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)), Error = Optional(reader, 3) };
        record.ValidateDefinition(); return record;
    }
    private static TaskWorktreeCleanup? ReadTaskCleanup(SqliteConnection connection, SqliteTransaction? transaction, string taskId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT definition,state,finished_at,error FROM task_worktree_cleanups WHERE task_id=$task ORDER BY CASE WHEN state=1 THEN 0 ELSE 1 END,rowid DESC LIMIT 1";
        command.Parameters.AddWithValue("$task", taskId); using var reader = command.ExecuteReader(); return reader.Read() ? ReadCleanup(reader) : null;
    }
    public Task<TaskWorktreeCleanup> ReserveTaskWorktreeCleanupAsync(string projectId, TaskWorktreeCleanupPreview preview) => RunAsync(connection =>
    {
        preview.ValidateDefinition(); if (projectId != preview.Publication.ProjectId) throw new ArgumentException("Projeto de arquivamento incompatível.");
        using var transaction = connection.BeginTransaction();
        var task = ApprovedDeliveryTask(connection, transaction, projectId, preview.Publication.Reservation.Delivery);
        if (JsonSerializer.Serialize(task.Publication) != JsonSerializer.Serialize(preview.Publication) || task.Publication?.State != TaskPublicationState.Published
            || task.Delivery != preview.Publication.Reservation.Delivery || task.Cleanup?.Preview.Id != preview.PreviousId
            || task.Cleanup?.State is TaskWorktreeCleanupState.Archiving or TaskWorktreeCleanupState.Archived)
            throw new InvalidOperationException("A entrega, publicação ou prévia de arquivamento mudou. Atualize a fila.");
        // Reuse the same repository gate as preparation/validation/publication; reserve and intent are atomic.
        var reservation = ReserveIntegration(connection, transaction, projectId, preview.Publication.Reservation.Delivery, preview.Publication.Reservation.Target, preview.Id, archiving: true);
        var intent = new TaskWorktreeCleanup(preview, reservation, DateTimeOffset.UtcNow); intent.ValidateDefinition();
        Execute(connection, transaction, "INSERT INTO task_worktree_cleanups(id,project_id,task_id,publication_id,definition,state) VALUES($id,$project,$task,$publication,$definition,0)",
            ("$id", preview.Id), ("$project", projectId), ("$task", reservation.Delivery.TaskId), ("$publication", preview.Publication.Reservation.Id), ("$definition", JsonSerializer.Serialize(intent)));
        transaction.Commit(); return intent;
    });
    public Task FinishTaskWorktreeCleanupAsync(TaskWorktreeCleanup cleanup) => RunAsync(connection =>
    {
        cleanup.ValidateDefinition();
        if (cleanup.State is TaskWorktreeCleanupState.Archiving or TaskWorktreeCleanupState.Interrupted) throw new ArgumentException("Informe o resultado da operação executada.");
        using var transaction = connection.BeginTransaction(); EnsureActiveReservation(connection, transaction, cleanup.Reservation);
        TaskWorktreeCleanup current;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "SELECT definition,state,finished_at,error FROM task_worktree_cleanups WHERE id=$id";
            command.Parameters.AddWithValue("$id", cleanup.Preview.Id); using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Intenção de arquivamento ausente."); current = ReadCleanup(reader);
        }
        if (current.State != TaskWorktreeCleanupState.Archiving || JsonSerializer.Serialize(current) != JsonSerializer.Serialize(cleanup with { State = TaskWorktreeCleanupState.Archiving, FinishedAt = null, Error = null }))
            throw new InvalidOperationException("Este arquivamento já terminou ou a intenção mudou. O histórico foi preservado.");
        Execute(connection, transaction, "UPDATE task_worktree_cleanups SET definition=$definition,state=$state,finished_at=$finished,error=$error WHERE id=$id AND state=0",
            ("$id", cleanup.Preview.Id), ("$definition", JsonSerializer.Serialize(cleanup)), ("$state", (int)cleanup.State), ("$finished", cleanup.FinishedAt!.Value.ToString("O")), ("$error", cleanup.Error));
        Execute(connection, transaction, "UPDATE task_integrations SET state=$state,error=$error WHERE id=$id AND state=0", ("$id", cleanup.Preview.Id),
            ("$state", (int)(cleanup.State is TaskWorktreeCleanupState.Archived or TaskWorktreeCleanupState.Cancelled ? TaskIntegrationState.Released : TaskIntegrationState.NeedsAttention)), ("$error", cleanup.Error));
        transaction.Commit(); return true;
    });
}
