using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    public Task SaveTaskIntegrationPreparationAsync(TaskIntegrationPreparation preparation) => RunAsync(connection =>
    {
        preparation.ValidateDefinition();
        if (preparation.State != TaskIntegrationPreparationState.Preparing) throw new ArgumentException("Registre a intenção antes da combinação.");
        using var transaction = connection.BeginTransaction(); EnsureActiveReservation(connection, transaction, preparation.Reservation);
        using (var occupied = connection.CreateCommand())
        {
            occupied.Transaction = transaction; occupied.CommandText = "SELECT COUNT(*) FROM task_integration_validations WHERE id=$id";
            occupied.Parameters.AddWithValue("$id", preparation.Reservation.Id);
            if (Convert.ToInt32(occupied.ExecuteScalar()) != 0) throw new InvalidOperationException("Esta reserva já pertence a uma validação.");
        }
        Execute(connection, transaction, "INSERT INTO task_integration_preparations(id,definition,checkout_key,state) VALUES($id,$definition,$checkout,0)",
            ("$id", preparation.Reservation.Id), ("$definition", JsonSerializer.Serialize(preparation)), ("$checkout", PathKey(preparation.CheckoutDirectory)));
        transaction.Commit(); return true;
    });
    public Task FinishTaskIntegrationPreparationAsync(TaskIntegrationPreparation preparation) => RunAsync(connection =>
    {
        preparation.ValidateDefinition();
        if (preparation.State == TaskIntegrationPreparationState.Preparing) throw new ArgumentException("Informe o resultado da combinação.");
        using var transaction = connection.BeginTransaction(); EnsureActiveReservation(connection, transaction, preparation.Reservation);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "SELECT definition,state,error FROM task_integration_preparations WHERE id=$id";
            command.Parameters.AddWithValue("$id", preparation.Reservation.Id); using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("A intenção da combinação não foi registrada.");
            var intent = ReadPreparation(reader);
            if (intent.State != TaskIntegrationPreparationState.Preparing || intent.Reservation != preparation.Reservation || intent.CheckoutDirectory != preparation.CheckoutDirectory)
                throw new InvalidOperationException("Esta preparação já terminou ou mudou. A pasta e os registros existentes foram preservados.");
        }
        Execute(connection, transaction, "UPDATE task_integration_preparations SET definition=$definition,state=$state,error=$error WHERE id=$id AND state=0",
            ("$id", preparation.Reservation.Id), ("$definition", JsonSerializer.Serialize(preparation)), ("$state", (int)preparation.State), ("$error", preparation.Error));
        Execute(connection, transaction, "UPDATE task_integrations SET state=$state,error=$error WHERE id=$id AND state=0",
            ("$id", preparation.Reservation.Id), ("$state", (int)(preparation.State == TaskIntegrationPreparationState.NeedsAttention ? TaskIntegrationState.NeedsAttention : TaskIntegrationState.Released)),
            ("$error", preparation.Error));
        transaction.Commit(); return true;
    });
    public Task<IReadOnlyList<TaskIntegrationPreparation>> GetTaskIntegrationPreparationsAsync(string projectId) => RunAsync<IReadOnlyList<TaskIntegrationPreparation>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.definition,p.state,p.error FROM task_integration_preparations p JOIN task_integrations i ON i.id=p.id
            JOIN work_tasks t ON t.id=i.task_id JOIN task_batches b ON b.id=t.batch_id WHERE b.project_id=$project ORDER BY p.rowid;
            """;
        command.Parameters.AddWithValue("$project", projectId); using var reader = command.ExecuteReader(); var result = new List<TaskIntegrationPreparation>();
        while (reader.Read()) result.Add(ReadPreparation(reader)); return result;
    });
    private static TaskIntegrationPreparation ReadPreparation(SqliteDataReader reader)
    {
        var result = JsonSerializer.Deserialize<TaskIntegrationPreparation>(reader.GetString(0))!
            with { State = (TaskIntegrationPreparationState)reader.GetInt32(1), Error = Optional(reader, 2) };
        result.ValidateDefinition(); return result;
    }
    private static void EnsureActiveReservation(SqliteConnection connection, SqliteTransaction transaction, TaskIntegrationReservation expected)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT definition,state,error FROM task_integrations WHERE id=$id"; command.Parameters.AddWithValue("$id", expected.Id);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || ReadIntegration(reader) != expected || expected.State != TaskIntegrationState.Reserved)
            throw new InvalidOperationException("A reserva da integração já terminou ou mudou. Nenhuma operação foi iniciada.");
    }
}
