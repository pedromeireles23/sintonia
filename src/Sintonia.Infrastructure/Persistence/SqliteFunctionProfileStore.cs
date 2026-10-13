using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

public sealed partial class SqliteWorkspaceStore
{
    public Task<IReadOnlyList<WorkspaceFunctionProfile>> GetFunctionProfilesAsync() => RunAsync<IReadOnlyList<WorkspaceFunctionProfile>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,function_name,provider,model,instructions,revision FROM function_profiles ORDER BY name_key,id";
        using var reader = command.ExecuteReader(); var profiles = new List<WorkspaceFunctionProfile>();
        while (reader.Read()) profiles.Add(WorkspaceFunctionProfiles.Normalize(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            (ProviderKind)reader.GetInt32(3), Optional(reader, 4), reader.GetString(5), reader.GetInt32(6))));
        return profiles;
    });

    public Task<WorkspaceFunctionProfile> CreateFunctionProfileAsync(string name, string functionName, ProviderKind provider, string? model, string instructions)
    {
        var profile = WorkspaceFunctionProfiles.Normalize(new(Guid.NewGuid().ToString(), name, functionName, provider, model, instructions, 0));
        return RunAsync(connection =>
        {
            try
            {
                Execute(connection, null, """
                    INSERT INTO function_profiles(id,name,name_key,function_name,provider,model,instructions,revision)
                    VALUES($id,$name,$key,$function,$provider,$model,$instructions,0);
                    """, Parameters(profile));
            }
            catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == 2067)
            { throw new ArgumentException("Já existe um perfil com este nome.", exception); }
            return profile;
        });
    }

    public Task<WorkspaceFunctionProfile> SaveFunctionProfileAsync(WorkspaceFunctionProfile profile)
    {
        profile = WorkspaceFunctionProfiles.Normalize(profile);
        return RunAsync(connection =>
        {
            try
            {
                if (Execute(connection, null, """
                    UPDATE function_profiles SET name=$name,name_key=$key,function_name=$function,provider=$provider,
                        model=$model,instructions=$instructions,revision=revision+1 WHERE id=$id AND revision=$revision;
                    """, Parameters(profile)) != 1)
                    throw new InvalidOperationException("O perfil mudou em outra janela ou foi excluído. Reverta as alterações e atualize a lista antes de salvar.");
            }
            catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == 2067)
            { throw new ArgumentException("Já existe um perfil com este nome.", exception); }
            return profile with { Revision = profile.Revision + 1 };
        });
    }

    /// <summary>Creates starter profiles once per database. Later deletions or renames are kept; existing names are not touched.</summary>
    public Task<int> EnsureStarterFunctionProfilesAsync(IReadOnlyList<WorkspaceFunctionProfile> profiles)
    {
        var normalized = profiles.Select(WorkspaceFunctionProfiles.Normalize).ToArray();
        return RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            if (Execute(connection, transaction, "INSERT OR IGNORE INTO workspace_markers(key) VALUES('starter-function-profiles-v1')") == 0) return 0;
            var created = 0;
            foreach (var profile in normalized)
                created += Execute(connection, transaction, """
                    INSERT OR IGNORE INTO function_profiles(id,name,name_key,function_name,provider,model,instructions,revision)
                    VALUES($id,$name,$key,$function,$provider,$model,$instructions,0);
                    """, Parameters(profile));
            transaction.Commit();
            return created;
        });
    }

    public Task DeleteFunctionProfileAsync(string id, int revision) => RunAsync(connection =>
    {
        if (Execute(connection, null, "DELETE FROM function_profiles WHERE id=$id AND revision=$revision", ("$id", id), ("$revision", revision)) != 1)
            throw new InvalidOperationException("O perfil mudou em outra janela ou foi excluído. Atualize a lista antes de excluir.");
        return true;
    });

    private static (string, object?)[] Parameters(WorkspaceFunctionProfile profile) =>
        [("$id", profile.Id), ("$name", profile.Name), ("$key", WorkspaceFunctionProfiles.NameKey(profile.Name)),
         ("$function", profile.FunctionName), ("$provider", (int)profile.Provider), ("$model", profile.Model),
         ("$instructions", profile.Instructions), ("$revision", profile.Revision)];
}
