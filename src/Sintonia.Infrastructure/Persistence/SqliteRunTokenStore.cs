using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    private static RunTokenUsage? ReadTokenUsage(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        var usage = JsonSerializer.Deserialize<RunTokenUsage>(reader.GetString(index)) ?? throw new InvalidOperationException("Medição de tokens inválida no histórico.");
        usage.ValidateDefinition(); return usage;
    }
    private static void SaveTokenUsage(SqliteConnection connection, SqliteTransaction transaction, ChatRun run, ProviderKind provider)
    {
        if (run.TokenUsage is not { } usage) return; // Absence never becomes a zero or erases a saved checkpoint.
        usage.ValidateDefinition();
        if (usage.Provider != provider) throw new ArgumentException("Medição pertence a outro provedor.");
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction; existing.CommandText = "SELECT definition FROM run_token_usage WHERE run_id=$id";
            existing.Parameters.AddWithValue("$id", run.Id);
            if (existing.ExecuteScalar() is string definition)
            {
                var previous = JsonSerializer.Deserialize<RunTokenUsage>(definition)!;
                if (usage.InputTokens < previous.InputTokens || usage.OutputTokens < previous.OutputTokens)
                    throw new ArgumentException("Uma atualização não pode reduzir tokens já observados nesta execução.");
            }
        }
        Execute(connection, transaction, """
            INSERT INTO run_token_usage(run_id,definition) VALUES($id,$definition)
            ON CONFLICT(run_id) DO UPDATE SET definition=excluded.definition;
            """, ("$id", run.Id), ("$definition", JsonSerializer.Serialize(usage)));
    }
}
