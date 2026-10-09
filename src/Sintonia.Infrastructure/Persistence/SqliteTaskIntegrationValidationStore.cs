using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    public Task SaveTaskIntegrationValidationAsync(TaskIntegrationValidation validation)
    {
        validation = validation with { Configuration = validation.Configuration.Snapshot() }; validation.ValidateDefinition();
        if (validation.State != ValidationState.Running) throw new ArgumentException("Registre a intenção antes dos comandos.");
        return RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(); EnsureActiveReservation(connection, transaction, validation.Reservation);
            if (JsonSerializer.Serialize(ReadValidationConfiguration(connection, transaction, validation.ProjectId)) != JsonSerializer.Serialize(validation.Configuration))
                throw new InvalidOperationException("A configuração mudou após a prévia. Confira os comandos novamente.");
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = """
                    SELECT p.definition,p.state,p.error FROM task_integration_preparations p JOIN task_integrations i ON i.id=p.id
                    JOIN work_tasks t ON t.id=i.task_id JOIN task_batches b ON b.id=t.batch_id WHERE p.id=$id AND b.project_id=$project;
                    """;
                command.Parameters.AddWithValue("$id", validation.Preparation.Reservation.Id); command.Parameters.AddWithValue("$project", validation.ProjectId);
                using var reader = command.ExecuteReader();
                if (!reader.Read() || JsonSerializer.Serialize(ReadPreparation(reader)) != JsonSerializer.Serialize(validation.Preparation))
                    throw new InvalidOperationException("A combinação mudou ou pertence a outro projeto. Atualize a consulta.");
            }
            using (var occupied = connection.CreateCommand())
            {
                occupied.Transaction = transaction; occupied.CommandText = "SELECT COUNT(*) FROM task_integration_preparations WHERE id=$id";
                occupied.Parameters.AddWithValue("$id", validation.Reservation.Id);
                if (Convert.ToInt32(occupied.ExecuteScalar()) != 0) throw new InvalidOperationException("Esta reserva já pertence a uma preparação.");
            }
            Execute(connection, transaction, """
                INSERT INTO task_integration_validations(id,preparation_id,project_id,definition,state)
                VALUES($id,$preparation,$project,$definition,0);
                """, ("$id", validation.Reservation.Id), ("$preparation", validation.Preparation.Reservation.Id),
                ("$project", validation.ProjectId), ("$definition", JsonSerializer.Serialize(validation)));
            transaction.Commit(); return true;
        });
    }

    public Task FinishTaskIntegrationValidationAsync(TaskIntegrationValidation validation) => RunAsync(connection =>
    {
        validation.ValidateDefinition();
        if (validation.State is ValidationState.Running or ValidationState.Interrupted) throw new ArgumentException("Informe o resultado da validação executada.");
        using var transaction = connection.BeginTransaction(); EnsureActiveReservation(connection, transaction, validation.Reservation);
        if (validation.State == ValidationState.Passed
            && JsonSerializer.Serialize(ReadValidationConfiguration(connection, transaction, validation.ProjectId)) != JsonSerializer.Serialize(validation.Configuration))
            throw new InvalidOperationException("Os critérios mudaram durante a validação. Confira os comandos e valide novamente.");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "SELECT definition,state,finished_at,error FROM task_integration_validations WHERE id=$id";
            command.Parameters.AddWithValue("$id", validation.Reservation.Id); using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("A intenção de validação não foi registrada.");
            var intent = ReadIntegrationValidation(reader);
            if (intent.State != ValidationState.Running
                || JsonSerializer.Serialize(intent) != JsonSerializer.Serialize(validation with { State = ValidationState.Running, FinishedAt = null, Results = null, Error = null }))
                throw new InvalidOperationException("Esta validação já terminou ou mudou. Registros anteriores foram preservados.");
        }
        Execute(connection, transaction, "UPDATE task_integration_validations SET definition=$definition,state=$state,finished_at=$finished,error=$error WHERE id=$id AND state=0",
            ("$id", validation.Reservation.Id), ("$definition", JsonSerializer.Serialize(validation)), ("$state", (int)validation.State),
            ("$finished", validation.FinishedAt!.Value.ToString("O")), ("$error", validation.Error));
        Execute(connection, transaction, "UPDATE task_integrations SET state=$state,error=$error WHERE id=$id AND state=0",
            ("$id", validation.Reservation.Id), ("$state", (int)(validation.State == ValidationState.Passed ? TaskIntegrationState.Released : TaskIntegrationState.NeedsAttention)), ("$error", validation.Error));
        transaction.Commit(); return true;
    });

    public Task<IReadOnlyList<TaskIntegrationValidation>> GetTaskIntegrationValidationsAsync(string projectId) => RunAsync<IReadOnlyList<TaskIntegrationValidation>>(connection =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT definition,state,finished_at,error FROM task_integration_validations WHERE project_id=$project ORDER BY rowid";
        command.Parameters.AddWithValue("$project", projectId); using var reader = command.ExecuteReader(); var list = new List<TaskIntegrationValidation>();
        while (reader.Read()) list.Add(ReadIntegrationValidation(reader)); return list;
    });

    private static TaskIntegrationValidation ReadIntegrationValidation(SqliteDataReader reader)
    {
        var result = JsonSerializer.Deserialize<TaskIntegrationValidation>(reader.GetString(0))! with
        { State = (ValidationState)reader.GetInt32(1), FinishedAt = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)), Error = Optional(reader, 3) };
        result.ValidateDefinition(); return result;
    }
}
