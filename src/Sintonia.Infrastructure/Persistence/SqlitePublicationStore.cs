using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    public Task<IReadOnlyList<TaskPublication>> GetTaskPublicationsAsync(string projectId) => RunAsync<IReadOnlyList<TaskPublication>>(connection =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT definition,state,finished_at,error FROM task_publications WHERE project_id=$project ORDER BY rowid";
        command.Parameters.AddWithValue("$project", projectId); using var reader = command.ExecuteReader(); var list = new List<TaskPublication>();
        while (reader.Read()) list.Add(ReadPublication(reader)); return list;
    });
    private static TaskPublication ReadPublication(SqliteDataReader reader)
    {
        var record = JsonSerializer.Deserialize<TaskPublication>(reader.GetString(0))! with { State = (TaskPublicationState)reader.GetInt32(1),
            FinishedAt = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)), Error = Optional(reader, 3) };
        record.ValidateDefinition(); return record;
    }
    private static TaskPublication? ReadTaskPublication(SqliteConnection connection, SqliteTransaction? transaction, string taskId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT definition,state,finished_at,error FROM task_publications WHERE task_id=$task ORDER BY CASE WHEN state=1 THEN 0 ELSE 1 END,rowid DESC LIMIT 1";
        command.Parameters.AddWithValue("$task", taskId); using var reader = command.ExecuteReader(); return reader.Read() ? ReadPublication(reader) : null;
    }
    private static void CheckPublicationCriteria(SqliteConnection connection, SqliteTransaction transaction, TaskPublication publication)
    {
        if (JsonSerializer.Serialize(ReadValidationConfiguration(connection, transaction, publication.ProjectId)) != JsonSerializer.Serialize(publication.Validation.Configuration))
            throw new InvalidOperationException("Os critérios mudaram. Valide novamente antes de publicar.");
    }
    public Task SaveTaskPublicationAsync(TaskPublication publication) => RunAsync(connection =>
    {
        publication.ValidateDefinition();
        if (publication.State != TaskPublicationState.Publishing || publication.Commit is not null) throw new ArgumentException("Registre a intenção antes de criar o commit.");
        using var transaction = connection.BeginTransaction(); EnsureActiveReservation(connection, transaction, publication.Reservation);
        CheckPublicationCriteria(connection, transaction, publication);
        if (ReadTaskPublication(connection, transaction, publication.Reservation.Delivery.TaskId) is { State: TaskPublicationState.Published })
            throw new InvalidOperationException("Esta entrega já foi integrada.");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "SELECT definition,state,finished_at,error FROM task_integration_validations WHERE id=$id AND project_id=$project";
            command.Parameters.AddWithValue("$id", publication.Validation.Reservation.Id); command.Parameters.AddWithValue("$project", publication.ProjectId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || JsonSerializer.Serialize(ReadIntegrationValidation(reader)) != JsonSerializer.Serialize(publication.Validation))
                throw new InvalidOperationException("A validação não corresponde ao registro salvo. Atualize a consulta.");
        }
        Execute(connection, transaction, "INSERT INTO task_publications(id,project_id,task_id,definition,state) VALUES($id,$project,$task,$definition,0)",
            ("$id", publication.Reservation.Id), ("$project", publication.ProjectId), ("$task", publication.Reservation.Delivery.TaskId), ("$definition", JsonSerializer.Serialize(publication)));
        transaction.Commit(); return true;
    });
    private static TaskPublication CurrentPublication(SqliteConnection connection, SqliteTransaction transaction, TaskPublication publication)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT definition,state,finished_at,error FROM task_publications WHERE id=$id"; command.Parameters.AddWithValue("$id", publication.Reservation.Id);
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw new InvalidOperationException("A intenção de publicação não foi registrada.");
        var current = ReadPublication(reader);
        if (current.State != TaskPublicationState.Publishing) throw new InvalidOperationException("Esta publicação já terminou. O resultado anterior foi preservado.");
        return current;
    }
    public Task RecordTaskPublicationCommitAsync(TaskPublication publication) => RunAsync(connection =>
    {
        publication.ValidateDefinition();
        if (publication.State != TaskPublicationState.Publishing || publication.Commit is null) throw new ArgumentException("Informe o commit criado antes de aplicar.");
        using var transaction = connection.BeginTransaction(); EnsureActiveReservation(connection, transaction, publication.Reservation);
        var current = CurrentPublication(connection, transaction, publication);
        if (current.Commit is not null || JsonSerializer.Serialize(current) != JsonSerializer.Serialize(publication with { Commit = null }))
            throw new InvalidOperationException("O commit desta publicação já foi registrado ou a intenção mudou.");
        CheckPublicationCriteria(connection, transaction, publication);
        Execute(connection, transaction, "UPDATE task_publications SET definition=$definition WHERE id=$id AND state=0", ("$id", publication.Reservation.Id), ("$definition", JsonSerializer.Serialize(publication)));
        transaction.Commit(); return true;
    });
    public Task FinishTaskPublicationAsync(TaskPublication publication) => RunAsync(connection =>
    {
        publication.ValidateDefinition();
        if (publication.State is TaskPublicationState.Publishing or TaskPublicationState.Interrupted) throw new ArgumentException("Informe o resultado da publicação executada.");
        using var transaction = connection.BeginTransaction(); EnsureActiveReservation(connection, transaction, publication.Reservation);
        var current = CurrentPublication(connection, transaction, publication);
        if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(publication with { State = TaskPublicationState.Publishing, FinishedAt = null, Error = null }))
            throw new InvalidOperationException("A intenção de publicação mudou. O registro anterior foi preservado.");
        if (publication.State == TaskPublicationState.Published)
        {
            CheckPublicationCriteria(connection, transaction, publication);
            var task = ApprovedDeliveryTask(connection, transaction, publication.ProjectId, publication.Reservation.Delivery);
            if (task.Delivery != publication.Reservation.Delivery) throw new InvalidOperationException("O registro da entrega mudou.");
        }
        Execute(connection, transaction, "UPDATE task_publications SET definition=$definition,state=$state,finished_at=$finished,error=$error WHERE id=$id AND state=0",
            ("$id", publication.Reservation.Id), ("$definition", JsonSerializer.Serialize(publication)), ("$state", (int)publication.State), ("$finished", publication.FinishedAt!.Value.ToString("O")), ("$error", publication.Error));
        Execute(connection, transaction, "UPDATE task_integrations SET state=$state,error=$error WHERE id=$id AND state=0", ("$id", publication.Reservation.Id),
            ("$state", (int)(publication.State == TaskPublicationState.Published ? TaskIntegrationState.Released : TaskIntegrationState.NeedsAttention)), ("$error", publication.Error));
        transaction.Commit(); return true;
    });
}
