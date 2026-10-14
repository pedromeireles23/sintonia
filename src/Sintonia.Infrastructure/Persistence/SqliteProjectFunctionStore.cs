using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    private static void EnsureProjectProfiles(SqliteConnection connection, SqliteTransaction transaction, string projectId)
    {
        EnsureProjectActive(connection, transaction, projectId);
        if (Execute(connection, transaction, "INSERT OR IGNORE INTO project_profile_libraries(project_id) VALUES($project)", ("$project", projectId)) == 0) return;
        Execute(connection, transaction, """
            INSERT INTO project_function_profiles(id,project_id,name,name_key,function_name,provider,model,instructions,revision)
                SELECT lower(hex(randomblob(16))),$project,name,name_key,function_name,provider,model,instructions,0 FROM function_profiles;
            """, ("$project", projectId));
        // A chief is always available even if the user removed the template from the shared library.
        if (ReadProjectProfiles(connection, transaction, projectId).Any(p => p.FunctionName == PlanProposalFormat.ChiefFunctionName)) return;
        var chief = WorkspaceFunctionCatalog.Find(PlanProposalFormat.ChiefFunctionName)!;
        var name = chief.Name;
        var names = ReadProjectProfiles(connection, transaction, projectId).Select(p => WorkspaceFunctionProfiles.NameKey(p.Name)).ToHashSet();
        for (var suffix = 2; names.Contains(WorkspaceFunctionProfiles.NameKey(name)); suffix++) name = chief.Name + " " + suffix;
        InsertProjectProfile(connection, transaction, new(Guid.NewGuid().ToString(), name, chief.Name, chief.SuggestedProvider, null, chief.Instructions, 0, projectId));
    }

    private Task<IReadOnlyList<WorkspaceFunctionProfile>> GetProjectFunctionProfilesAsync(string projectId) => RunAsync<IReadOnlyList<WorkspaceFunctionProfile>>(connection =>
    {
        using var transaction = connection.BeginTransaction(); EnsureProjectProfiles(connection, transaction, projectId);
        var profiles = ReadProjectProfiles(connection, transaction, projectId); transaction.Commit(); return profiles;
    });

    private static List<WorkspaceFunctionProfile> ReadProjectProfiles(SqliteConnection connection, SqliteTransaction transaction, string projectId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,name,function_name,provider,model,instructions,revision FROM project_function_profiles WHERE project_id=$project ORDER BY name_key,id";
        command.Parameters.AddWithValue("$project", projectId); using var reader = command.ExecuteReader();
        var profiles = new List<WorkspaceFunctionProfile>();
        while (reader.Read()) profiles.Add(WorkspaceFunctionProfiles.Normalize(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            (ProviderKind)reader.GetInt32(3), Optional(reader, 4), reader.GetString(5), reader.GetInt32(6), projectId)));
        return profiles;
    }

    private static void InsertProjectProfile(SqliteConnection connection, SqliteTransaction transaction, WorkspaceFunctionProfile profile) =>
        Execute(connection, transaction, """
            INSERT INTO project_function_profiles(id,project_id,name,name_key,function_name,provider,model,instructions,revision)
                VALUES($id,$project,$name,$key,$function,$provider,$model,$instructions,0);
            """, Parameters(profile).Append(("$project", (object?)profile.ProjectId)).ToArray());

    private Task<WorkspaceFunctionProfile> SaveProjectFunctionProfileAsync(WorkspaceFunctionProfile profile, bool create) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction(); EnsureProjectProfiles(connection, transaction, profile.ProjectId!);
        try
        {
            if (create) InsertProjectProfile(connection, transaction, profile);
            else if (Execute(connection, transaction, """
                UPDATE project_function_profiles SET name=$name,name_key=$key,function_name=$function,provider=$provider,model=$model,
                    instructions=$instructions,revision=revision+1 WHERE id=$id AND project_id=$project AND revision=$revision;
                """, Parameters(profile).Append(("$project", (object?)profile.ProjectId)).ToArray()) != 1)
                throw new InvalidOperationException("O perfil mudou em outra janela ou foi excluído. Reverta as alterações e atualize a lista antes de salvar.");
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == 2067)
        { throw new ArgumentException("Já existe um perfil com este nome neste projeto.", exception); }
        transaction.Commit(); return create ? profile : profile with { Revision = profile.Revision + 1 };
    });

    public Task<WorkspaceConversation> GetProjectChiefAsync(string projectId) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction(); EnsureProjectProfiles(connection, transaction, projectId);
        var chief = ReadProjectChief(connection, transaction, projectId);
        if (chief is null)
        {
            // Reuse the most recent chief from before this feature, keeping its native session and history.
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                SELECT id FROM conversations c WHERE project_id=$project AND function_name=$chief
                    AND NOT EXISTS(SELECT 1 FROM work_tasks t WHERE t.conversation_id=c.id) ORDER BY rowid DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$chief", PlanProposalFormat.ChiefFunctionName);
            if (command.ExecuteScalar() is string existing)
            {
                Execute(connection, transaction, "INSERT INTO project_chiefs(project_id,conversation_id) VALUES($project,$conversation)", ("$project", projectId), ("$conversation", existing));
                chief = ReadProjectChief(connection, transaction, projectId)!;
            }
            else chief = AssignProjectChief(connection, transaction, projectId, ReadProjectProfiles(connection, transaction, projectId)
                .First(p => p.FunctionName == PlanProposalFormat.ChiefFunctionName));
        }
        transaction.Commit(); return chief;
    });

    public Task<WorkspaceConversation> SetProjectChiefAsync(string projectId, string profileId, int profileRevision) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction(); EnsureProjectProfiles(connection, transaction, projectId);
        var profile = ReadProjectProfiles(connection, transaction, projectId).SingleOrDefault(p => p.Id == profileId);
        if (profile is null || profile.Revision != profileRevision || profile.FunctionName != PlanProposalFormat.ChiefFunctionName)
            throw new InvalidOperationException("Selecione um perfil de Chefe do projeto salvo e atualizado neste projeto.");
        var chief = AssignProjectChief(connection, transaction, projectId, profile); transaction.Commit(); return chief;
    });

    private static WorkspaceConversation AssignProjectChief(SqliteConnection connection, SqliteTransaction transaction, string projectId, WorkspaceFunctionProfile profile)
    {
        var previous = ReadProjectChief(connection, transaction, projectId);
        using var active = connection.CreateCommand(); active.Transaction = transaction;
        active.CommandText = "SELECT EXISTS(SELECT 1 FROM runs WHERE conversation_id=$id AND state=0)";
        active.Parameters.AddWithValue("$id", previous?.Id ?? "");
        if (Convert.ToBoolean(active.ExecuteScalar())) throw new InvalidOperationException("Aguarde a resposta do chefe antes de mudar sua configuração.");
        var chief = previous is not null && previous.Provider == profile.Provider
            ? previous with { Instructions = profile.Instructions, SelectedModel = profile.Model, UsesInstallationDefault = profile.Model is null,
                ReasoningEffort = null, EffectiveReasoningEffort = null, Access = ConversationAccess.ReadOnly, FunctionName = PlanProposalFormat.ChiefFunctionName }
            : new WorkspaceConversation(Guid.NewGuid().ToString(), projectId, "Chefe do projeto", profile.Provider, null, null,
                PlanProposalFormat.ChiefFunctionName, profile.Instructions, ConversationAccess.ReadOnly, UsesInstallationDefault: profile.Model is null, SelectedModel: profile.Model);
        SaveConversation(connection, transaction, chief);
        Execute(connection, transaction, """
            INSERT INTO project_chiefs(project_id,conversation_id) VALUES($project,$conversation)
                ON CONFLICT(project_id) DO UPDATE SET conversation_id=excluded.conversation_id;
            """, ("$project", projectId), ("$conversation", chief.Id));
        return chief;
    }

    private static WorkspaceConversation? ReadProjectChief(SqliteConnection connection, SqliteTransaction transaction, string projectId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT c.id,c.title,c.provider,c.model,c.native_session_id,c.instructions,s.reasoning_effort,s.effective_reasoning_effort,s.uses_installation_default,s.selected_model
                FROM project_chiefs p JOIN conversations c ON c.id=p.conversation_id
                LEFT JOIN conversation_model_settings s ON s.conversation_id=c.id WHERE p.project_id=$project;
            """;
        command.Parameters.AddWithValue("$project", projectId); using var reader = command.ExecuteReader();
        return !reader.Read() ? null : new(reader.GetString(0), projectId, reader.GetString(1), (ProviderKind)reader.GetInt32(2), Optional(reader, 3),
            Optional(reader, 4), PlanProposalFormat.ChiefFunctionName, reader.GetString(5), ConversationAccess.ReadOnly, ReasoningEffort: Optional(reader, 6),
            EffectiveReasoningEffort: Optional(reader, 7), UsesInstallationDefault: reader.IsDBNull(8) ? null : reader.GetBoolean(8), SelectedModel: Optional(reader, 9));
    }
}
