using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    public Task<ProjectTokenBudget> GetProjectTokenBudgetAsync(string projectId) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var result = ReadProjectTokenBudget(connection, transaction, projectId);
        transaction.Commit(); return result;
    });

    private static ProjectTokenBudget ReadProjectTokenBudget(SqliteConnection connection, SqliteTransaction transaction, string projectId)
    {
        var settings = ReadExecutionSettings(connection, transaction, projectId);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT r.state,u.definition,COALESCE(b.tokens,0) FROM runs r
            JOIN conversations c ON c.id=r.conversation_id LEFT JOIN run_token_usage u ON u.run_id=r.id
            LEFT JOIN run_token_reservations b ON b.run_id=r.id WHERE c.project_id=$project
            """;
        command.Parameters.AddWithValue("$project", projectId);
        using var reader = command.ExecuteReader();
        long reported = 0, reserved = 0; var missing = 0; var partial = 0; var active = 0; var overflow = false;
        while (reader.Read())
        {
            var running = (ChatRunState)reader.GetInt32(0) == ChatRunState.Running;
            var usage = ReadTokenUsage(reader, 1);
            if (usage is not null)
            {
                reported = Add(reported, usage.TotalTokens, ref overflow);
                if (usage.IsPartial) partial++;
            }
            else if (!running) missing++;
            if (!running) continue;
            active++;
            reserved = Add(reserved, Math.Max(0, reader.GetInt64(2) - (usage?.TotalTokens ?? 0)), ref overflow);
        }
        return new(settings, reported, reserved, missing, partial, active, overflow);
    }

    private static long Add(long total, long value, ref bool overflow)
    {
        if (value <= long.MaxValue - total) return total + value;
        overflow = true; return long.MaxValue;
    }
}
