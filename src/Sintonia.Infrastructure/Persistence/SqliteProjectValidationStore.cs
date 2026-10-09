using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    public Task<ProjectValidationConfiguration> GetProjectValidationAsync(string projectId) => RunAsync(connection => ReadValidationConfiguration(connection, null, projectId));

    public Task<ProjectValidationConfiguration> SaveProjectValidationAsync(ProjectValidationConfiguration configuration)
    {
        configuration = configuration.Snapshot();
        return RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var current = ReadValidationConfiguration(connection, transaction, configuration.ProjectId);
            if (current.Revision != configuration.Revision)
                throw new InvalidOperationException("A configuração mudou. Atualize antes de salvar seus comandos.");
            var saved = configuration with { Revision = checked(configuration.Revision + 1) };
            Execute(connection, transaction, """
                INSERT INTO project_validations(project_id,definition) VALUES($project,$definition)
                ON CONFLICT(project_id) DO UPDATE SET definition=$definition;
                """, ("$project", saved.ProjectId), ("$definition", JsonSerializer.Serialize(saved)));
            transaction.Commit(); return saved;
        });
    }

    private static ProjectValidationConfiguration ReadValidationConfiguration(SqliteConnection connection, SqliteTransaction? transaction, string projectId)
    {
        using var project = connection.CreateCommand(); project.Transaction = transaction;
        project.CommandText = "SELECT COUNT(*) FROM projects WHERE id=$id"; project.Parameters.AddWithValue("$id", projectId);
        if (Convert.ToInt32(project.ExecuteScalar()) != 1) throw new InvalidOperationException("Projeto não encontrado.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT definition FROM project_validations WHERE project_id=$id"; command.Parameters.AddWithValue("$id", projectId);
        var result = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ProjectValidationConfiguration>(json)!
            : new(projectId, 0, []);
        result.ValidateDefinition();
        if (result.ProjectId != projectId) throw new InvalidOperationException("Configuração vinculada a outro projeto.");
        return result;
    }
}
