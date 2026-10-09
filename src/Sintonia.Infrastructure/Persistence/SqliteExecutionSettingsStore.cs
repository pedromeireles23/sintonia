using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    public Task<ProjectExecutionSettings> GetProjectExecutionSettingsAsync(string projectId) =>
        RunAsync(connection => ReadExecutionSettings(connection, null, projectId));

    public Task<ProjectExecutionSettings> SaveProjectExecutionSettingsAsync(ProjectExecutionSettings settings)
    {
        settings.Validate();
        return RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var current = ReadExecutionSettings(connection, transaction, settings.ProjectId);
            if (current.Revision != settings.Revision)
                throw new InvalidOperationException("O limite mudou em outra janela. Atualize antes de salvar.");
            var saved = settings with { Revision = checked(settings.Revision + 1) };
            Execute(connection, transaction, """
                INSERT INTO project_execution_settings(project_id,session_limit,revision) VALUES($project,$limit,$revision)
                ON CONFLICT(project_id) DO UPDATE SET session_limit=$limit,revision=$revision;
                """, ("$project", saved.ProjectId), ("$limit", saved.MaxConcurrentSessions), ("$revision", saved.Revision));
            transaction.Commit(); return saved;
        });
    }

    private static ProjectExecutionSettings ReadExecutionSettings(SqliteConnection connection, SqliteTransaction? transaction, string projectId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(s.revision,0),COALESCE(s.session_limit,3) FROM projects p
            LEFT JOIN project_execution_settings s ON s.project_id=p.id WHERE p.id=$project
            """;
        command.Parameters.AddWithValue("$project", projectId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Projeto não encontrado.");
        var result = new ProjectExecutionSettings(projectId, reader.GetInt32(0), reader.GetInt32(1)); result.Validate(); return result;
    }

    private static WorkspaceExecutionSlot ReadExecutionScope(SqliteConnection connection, SqliteTransaction transaction,
        string conversationId, TaskWorktree? worktree)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT c.project_id,c.access,c.function_name,p.directory FROM conversations c JOIN projects p ON p.id=c.project_id WHERE c.id=$id";
        command.Parameters.AddWithValue("$id", conversationId); using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Sessão não encontrada.");
        var project = new WorkspaceProject(reader.GetString(0), "", reader.GetString(3));
        var conversation = new WorkspaceConversation(conversationId, project.Id, "", ProviderKind.Codex, null, null,
            reader.GetString(2), "", (ConversationAccess)reader.GetInt32(1));
        return WorkspaceExecutionSlot.Create(project, conversation, worktree);
    }

    private static WorkspaceExecutionSlot ReserveExecutionScope(SqliteConnection connection, SqliteTransaction transaction,
        ChatRun run, TaskWorktree? worktree, WorkspaceExecutionSlot? expected)
    {
        var slot = ReadExecutionScope(connection, transaction, run.ConversationId, worktree);
        if (expected is not null && slot != expected)
            throw new InvalidOperationException("O projeto, acesso ou pasta desta sessão mudou. Atualize antes de executar.");
        var active = new List<WorkspaceExecutionSlot>();
        var legacy = new List<(string ConversationId, string? Worktree, int? State, string? Error)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT r.conversation_id,s.definition,w.definition,w.state,w.error FROM runs r
                LEFT JOIN run_execution_scopes s ON s.run_id=r.id
                LEFT JOIN work_tasks t ON t.conversation_id=r.conversation_id
                LEFT JOIN task_worktrees w ON w.task_id=t.id WHERE r.state=0
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(1)) active.Add(JsonSerializer.Deserialize<WorkspaceExecutionSlot>(reader.GetString(1))!);
                else legacy.Add((reader.GetString(0), Optional(reader, 2), reader.IsDBNull(3) ? null : reader.GetInt32(3), Optional(reader, 4)));
        }
        foreach (var old in legacy)
        {
            var checkout = old.Worktree is null ? null : JsonSerializer.Deserialize<TaskWorktree>(old.Worktree)!
                with { State = (TaskWorktreeState)old.State!, Error = old.Error };
            active.Add(ReadExecutionScope(connection, transaction, old.ConversationId, checkout));
        }
        var settings = ReadExecutionSettings(connection, transaction, slot.ProjectId);
        if (!WorkspaceExecutionPolicy.CanAdmit(slot, active, settings.MaxConcurrentSessions, out var reason))
            throw new InvalidOperationException(reason);
        return slot;
    }
}
