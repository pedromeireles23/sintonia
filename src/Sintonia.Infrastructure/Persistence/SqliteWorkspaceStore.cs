using Microsoft.Data.Sqlite;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Persistence;

/// <summary>Small explicit schema. SQLite work runs off the WPF dispatcher and each mutation is transactional.</summary>
public sealed partial class SqliteWorkspaceStore(string databasePath) : IWorkspaceStore
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _path = Path.GetFullPath(databasePath);
    private async Task<T> RunAsync<T>(Func<SqliteConnection, T> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = _path, ForeignKeys = true, DefaultTimeout = 5, Pooling = false }.ToString());
                connection.Open();
                return work(connection);
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task InitializeAsync() => RunAsync(connection =>
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(check.ExecuteScalar()) > 5) throw new InvalidOperationException("Este histórico foi criado por uma versão mais nova do Sintonia.");
        check.CommandText = "PRAGMA journal_mode=WAL";
        check.ExecuteScalar();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY, name TEXT NOT NULL, directory TEXT NOT NULL, directory_key TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS conversations(id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id), title TEXT NOT NULL,
                provider INTEGER NOT NULL, model TEXT, native_session_id TEXT, function_name TEXT NOT NULL, instructions TEXT NOT NULL, access INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversations(id), prompt TEXT NOT NULL,
                response TEXT, state INTEGER NOT NULL, started_at TEXT NOT NULL, finished_at TEXT, error TEXT);
            CREATE UNIQUE INDEX IF NOT EXISTS one_live_run ON runs(conversation_id) WHERE state=0;
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL REFERENCES runs(id), kind INTEGER NOT NULL, text TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS proposals(id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id),
                source_run_id TEXT NOT NULL UNIQUE REFERENCES runs(id), definition TEXT NOT NULL,
                state INTEGER NOT NULL CHECK(state IN (0,1)), revision INTEGER NOT NULL CHECK(revision >= 0));
            CREATE TABLE IF NOT EXISTS task_batches(id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id),
                proposal_id TEXT NOT NULL UNIQUE REFERENCES proposals(id), proposal_revision INTEGER NOT NULL, definition TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS work_tasks(id TEXT PRIMARY KEY, batch_id TEXT NOT NULL REFERENCES task_batches(id),
                plan_task_id TEXT NOT NULL, conversation_id TEXT NOT NULL UNIQUE REFERENCES conversations(id),
                state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 8), attempts INTEGER NOT NULL CHECK(attempts BETWEEN 0 AND 3),
                last_run_id TEXT REFERENCES runs(id), review_note TEXT, UNIQUE(batch_id,plan_task_id));
            CREATE TABLE IF NOT EXISTS function_profiles(id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE,
                function_name TEXT NOT NULL, provider INTEGER NOT NULL CHECK(provider IN (0,1)), model TEXT,
                instructions TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision >= 0));
            CREATE TABLE IF NOT EXISTS task_worktrees(task_id TEXT PRIMARY KEY REFERENCES work_tasks(id),
                definition TEXT NOT NULL, state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 2), error TEXT,
                checkout_key TEXT NOT NULL UNIQUE, common_key TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS one_worktree_preparation ON task_worktrees(common_key) WHERE state=0;
            PRAGMA user_version=5;
            """);
        transaction.Commit();
        return true;
    });

    public Task<IReadOnlyList<WorkspaceProject>> GetProjectsAsync() => RunAsync<IReadOnlyList<WorkspaceProject>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,directory FROM projects ORDER BY name COLLATE NOCASE,id";
        using var reader = command.ExecuteReader();
        var list = new List<WorkspaceProject>();
        while (reader.Read()) list.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    });

    public Task<WorkspaceProject> AddProjectAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new ArgumentException("Escolha uma pasta de projeto existente.");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var key = path.ToUpperInvariant();
        return RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var project = new WorkspaceProject(Guid.NewGuid().ToString(), new DirectoryInfo(path).Name, path);
            Execute(connection, transaction, "INSERT OR IGNORE INTO projects(id,name,directory,directory_key) VALUES($id,$name,$directory,$key)",
                ("$id", project.Id), ("$name", project.Name), ("$directory", path), ("$key", key));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id,name,directory FROM projects WHERE directory_key=$key";
            command.Parameters.AddWithValue("$key", key);
            using (var reader = command.ExecuteReader()) { reader.Read(); project = new(reader.GetString(0), reader.GetString(1), reader.GetString(2)); }
            transaction.Commit();
            return project;
        });
    }

    public Task<IReadOnlyList<WorkspaceConversation>> GetConversationsAsync(string projectId) => RunAsync<IReadOnlyList<WorkspaceConversation>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,project_id,title,provider,model,native_session_id,function_name,instructions,access,EXISTS(SELECT 1 FROM work_tasks t WHERE t.conversation_id=conversations.id) FROM conversations WHERE project_id=$project ORDER BY rowid DESC";
        command.Parameters.AddWithValue("$project", projectId);
        using var reader = command.ExecuteReader();
        var list = new List<WorkspaceConversation>();
        while (reader.Read()) list.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), (ProviderKind)reader.GetInt32(3),
            Optional(reader, 4), Optional(reader, 5), reader.GetString(6), reader.GetString(7), (ConversationAccess)reader.GetInt32(8), reader.GetBoolean(9)));
        return list;
    });

    public Task SaveConversationAsync(WorkspaceConversation conversation) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        SaveConversation(connection, transaction, conversation);
        transaction.Commit();
        return true;
    });

    private static void SaveConversation(SqliteConnection connection, SqliteTransaction transaction, WorkspaceConversation conversation)
    {
        if (Execute(connection, transaction, """
            INSERT INTO conversations(id,project_id,title,provider,model,native_session_id,function_name,instructions,access)
            VALUES($id,$project,$title,$provider,$model,$native,$function,$instructions,$access)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title,model=excluded.model,native_session_id=excluded.native_session_id,
                function_name=excluded.function_name,instructions=excluded.instructions,access=excluded.access
            WHERE conversations.project_id=excluded.project_id AND conversations.provider=excluded.provider
                AND (NOT EXISTS(SELECT 1 FROM work_tasks t WHERE t.conversation_id=conversations.id)
                    OR (conversations.title=excluded.title AND conversations.function_name=excluded.function_name
                        AND conversations.instructions=excluded.instructions AND conversations.access=excluded.access));
            """, ("$id", conversation.Id), ("$project", conversation.ProjectId), ("$title", conversation.Title), ("$provider", (int)conversation.Provider),
            ("$model", conversation.Model), ("$native", conversation.NativeSessionId), ("$function", conversation.FunctionName),
            ("$instructions", conversation.Instructions), ("$access", (int)conversation.Access)) != 1)
            throw new ArgumentException("A conversa pertence a outro projeto/provedor.");
    }

    public Task<IReadOnlyList<ChatRun>> GetRunsAsync(string conversationId) => RunAsync<IReadOnlyList<ChatRun>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,conversation_id,prompt,response,state,started_at,finished_at,error FROM runs WHERE conversation_id=$id ORDER BY rowid";
        command.Parameters.AddWithValue("$id", conversationId);
        using var reader = command.ExecuteReader();
        var list = new List<ChatRun>();
        while (reader.Read()) list.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), Optional(reader, 3), (ChatRunState)reader.GetInt32(4),
            DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture), reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture), Optional(reader, 7)));
        return list;
    });

    public Task<IReadOnlyList<ChatEvent>> GetEventsAsync(string conversationId) => RunAsync<IReadOnlyList<ChatEvent>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.run_id,e.kind,e.text FROM events e JOIN runs r ON r.id=e.run_id WHERE r.conversation_id=$id ORDER BY e.id";
        command.Parameters.AddWithValue("$id", conversationId);
        using var reader = command.ExecuteReader();
        var list = new List<ChatEvent>();
        while (reader.Read()) list.Add(new(reader.GetString(0), (ConversationEventKind)reader.GetInt32(1), reader.GetString(2)));
        return list;
    });

    public Task BeginRunAsync(ChatRun run, string? taskId = null, TaskWorktree? expectedWorktree = null) => RunAsync(connection =>
    {
        if (run.State != ChatRunState.Running) throw new ArgumentException("Uma tentativa deve começar em execução.");
        using var transaction = connection.BeginTransaction();
        ReserveTask(connection, transaction, run, taskId, expectedWorktree);
        Execute(connection, transaction, "INSERT INTO runs(id,conversation_id,prompt,state,started_at) VALUES($id,$conversation,$prompt,$state,$started)",
            ("$id", run.Id), ("$conversation", run.ConversationId), ("$prompt", run.Prompt), ("$state", (int)ChatRunState.Running), ("$started", run.StartedAt.ToString("O")));
        if (taskId is not null) Execute(connection, transaction, "UPDATE work_tasks SET last_run_id=$run WHERE id=$task", ("$run", run.Id), ("$task", taskId));
        transaction.Commit();
        return true;
    });

    public Task CheckpointRunAsync(ChatRun run, WorkspaceConversation conversation) => RunAsync(connection =>
    {
        if (run.ConversationId != conversation.Id) throw new ArgumentException("Execução incompatível com a conversa.");
        using var transaction = connection.BeginTransaction();
        SaveConversation(connection, transaction, conversation);
        if (Execute(connection, transaction, "UPDATE runs SET response=$response WHERE id=$id AND conversation_id=$conversation AND state=0",
            ("$response", run.Response), ("$id", run.Id), ("$conversation", conversation.Id)) != 1)
            throw new InvalidOperationException("A execução do checkpoint não está ativa nesta conversa.");
        transaction.Commit();
        return true;
    });

    public Task FinishRunAsync(ChatRun run, WorkspaceConversation conversation, IReadOnlyList<ChatEvent> events) => RunAsync(connection =>
    {
        if (run.ConversationId != conversation.Id || run.State == ChatRunState.Running || events.Any(e => e.RunId != run.Id))
            throw new ArgumentException("Entrega ou eventos pertencem a outra execução.");
        using var transaction = connection.BeginTransaction();
        SaveConversation(connection, transaction, conversation);
        if (Execute(connection, transaction, "UPDATE runs SET response=$response,state=$state,finished_at=$finished,error=$error WHERE id=$id AND conversation_id=$conversation AND state=0",
            ("$response", run.Response), ("$state", (int)run.State), ("$finished", run.FinishedAt?.ToString("O")), ("$error", run.Error), ("$id", run.Id), ("$conversation", run.ConversationId)) != 1)
            throw new InvalidOperationException("A execução já foi encerrada ou não existe.");
        foreach (var ev in events.TakeLast(500)) Execute(connection, transaction, "INSERT INTO events(run_id,kind,text) VALUES($id,$kind,$text)",
            ("$id", run.Id), ("$kind", (int)ev.Kind), ("$text", ev.Text.Length > 8000 ? ev.Text[..8000] : ev.Text));
        Execute(connection, transaction, "UPDATE work_tasks SET state=$state WHERE conversation_id=$conversation AND last_run_id=$run AND state=1",
            ("$state", (int)WorkspaceTaskPolicy.FromRun(run.State)), ("$conversation", conversation.Id), ("$run", run.Id));
        transaction.Commit();
        return true;
    });

    public Task RecoverInterruptedRunsAsync() => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "UPDATE runs SET state=$state,finished_at=$finished,error=$error WHERE state=0",
            ("$state", (int)ChatRunState.Interrupted), ("$finished", DateTimeOffset.UtcNow.ToString("O")),
            ("$error", "O aplicativo encerrou antes de registrar o resultado. Confira o projeto e a sessão antes de reenviar."));
        Execute(connection, transaction, "UPDATE work_tasks SET state=$state WHERE state=1", ("$state", (int)WorkspaceTaskState.Interrupted));
        Execute(connection, transaction, "UPDATE task_worktrees SET state=2,error=$error WHERE state=0",
            ("$error", "O aplicativo encerrou durante a preparação. Confira a pasta e prepare novamente; nenhum efeito foi repetido."));
        transaction.Commit();
        return true;
    });

    public Task<IReadOnlyList<WorkspaceProposal>> GetProposalsAsync(string projectId) => RunAsync<IReadOnlyList<WorkspaceProposal>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,project_id,source_run_id,definition,state,revision FROM proposals WHERE project_id=$project ORDER BY rowid DESC";
        command.Parameters.AddWithValue("$project", projectId);
        using var reader = command.ExecuteReader();
        var proposals = new List<WorkspaceProposal>();
        while (reader.Read()) proposals.Add(ReadProposal(reader));
        return proposals;
    });

    public Task<WorkspaceProposal> CreateProposalAsync(string projectId, string sourceRunId) => RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using var source = connection.CreateCommand();
        source.Transaction = transaction;
        source.CommandText = "SELECT r.response FROM runs r JOIN conversations c ON c.id=r.conversation_id WHERE r.id=$run AND c.project_id=$project AND r.state=$completed";
        source.Parameters.AddWithValue("$run", sourceRunId); source.Parameters.AddWithValue("$project", projectId);
        source.Parameters.AddWithValue("$completed", (int)ChatRunState.Completed);
        var response = source.ExecuteScalar() as string;
        if (!PlanProposalFormat.TryParseResponse(response, out var definition, out var error))
            throw new PlanValidationException("Não foi possível importar a proposta desta execução: " + error);
        var id = Guid.NewGuid().ToString();
        Execute(connection, transaction, """
            INSERT INTO proposals(id,project_id,source_run_id,definition,state,revision) VALUES($id,$project,$run,$definition,0,0)
            ON CONFLICT(source_run_id) DO NOTHING;
            """, ("$id", id), ("$project", projectId), ("$run", sourceRunId), ("$definition", PlanProposalFormat.Serialize(definition!)));
        using var saved = connection.CreateCommand();
        saved.Transaction = transaction;
        saved.CommandText = "SELECT id,project_id,source_run_id,definition,state,revision FROM proposals WHERE source_run_id=$run AND project_id=$project";
        saved.Parameters.AddWithValue("$run", sourceRunId); saved.Parameters.AddWithValue("$project", projectId);
        WorkspaceProposal result;
        using (var reader = saved.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("A proposta pertence a outro projeto.");
            result = ReadProposal(reader);
        }
        transaction.Commit();
        return result;
    });

    public Task<WorkspaceProposal> SaveProposalAsync(WorkspaceProposal proposal)
    {
        if (!Enum.IsDefined(proposal.State) || proposal.Revision < 0) throw new ArgumentException("Estado ou revisão de proposta inválido.");
        var definition = PlanProposalFormat.Serialize(proposal.Definition);
        return RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            if (Execute(connection, transaction, """
                UPDATE proposals SET definition=$definition,state=$state,revision=revision+1
                WHERE id=$id AND project_id=$project AND source_run_id=$run AND revision=$revision
                    AND NOT EXISTS(SELECT 1 FROM task_batches b WHERE b.proposal_id=proposals.id);
                """, ("$definition", definition), ("$state", (int)proposal.State), ("$id", proposal.Id),
                ("$project", proposal.ProjectId), ("$run", proposal.SourceRunId), ("$revision", proposal.Revision)) != 1)
                throw new InvalidOperationException("A proposta mudou em outra janela, já foi encaminhada à fila ou não pertence a este projeto. Reabra a revisão; para um plano encaminhado, crie uma nova proposta.");
            transaction.Commit();
            return proposal with { Revision = proposal.Revision + 1, Definition = PlanProposalFormat.ParseJson(definition) };
        });
    }

    private static WorkspaceProposal ReadProposal(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        reader.GetString(2), PlanProposalFormat.ParseJson(reader.GetString(3)), (ProposalReviewState)reader.GetInt32(4), reader.GetInt32(5));

    private static string? Optional(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }
}
