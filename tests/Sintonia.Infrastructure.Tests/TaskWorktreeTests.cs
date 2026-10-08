using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;
using System.Security.Cryptography;

namespace Sintonia.Infrastructure.Tests;

[Collection("Git environment")]
public sealed class TaskWorktreeTests
{
    [Fact]
    public async Task PreparationPinsBasePreservesOriginalChangesAndMapsSubproject()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var (store, project, batch) = await fixture.SeedAsync();
        var tracked = Path.Combine(project.Directory, "portal.txt");
        await File.WriteAllTextAsync(tracked, "alteração local"); await fixture.GitAsync(fixture.Repository, "add", ".");
        await File.AppendAllTextAsync(tracked, " e outra alteração");
        await File.WriteAllTextAsync(Path.Combine(project.Directory, "novo.txt"), "não rastreado");
        await File.WriteAllTextAsync(Path.Combine(project.Directory, "segredo.local"), "ignorado");
        var index = Path.Combine(fixture.Repository, ".git", "index"); var before = SHA256.HashData(await File.ReadAllBytesAsync(index));
        var service = new TaskWorktreeService(store, fixture.Manager);
        var preview = await service.PreviewAsync(project.Id, batch.Tasks[0].Id, CancellationToken.None);
        Assert.False(Directory.Exists(preview.CheckoutDirectory));
        // A later source commit must not silently replace the commit the user reviewed.
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "later.txt"), "depois");
        await fixture.GitAsync(fixture.Repository, "add", "later.txt"); await fixture.GitAsync(fixture.Repository, "commit", "-m", "later", "--", "later.txt");
        before = SHA256.HashData(await File.ReadAllBytesAsync(index));
        await service.PrepareAsync(project.Id, preview, CancellationToken.None);
        var saved = (await fixture.CurrentAsync(store, project, batch.Tasks[0].Id)).Worktree!;
        Assert.Equal(TaskWorktreeState.Ready, saved.State); Assert.Equal(preview.BaseCommit, saved.BaseCommit);
        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(saved.WorkingDirectory, "portal.txt")));
        Assert.False(File.Exists(Path.Combine(saved.WorkingDirectory, "novo.txt")));
        Assert.False(File.Exists(Path.Combine(saved.WorkingDirectory, "segredo.local")));
        Assert.False(File.Exists(Path.Combine(saved.CheckoutDirectory, "later.txt")));
        Assert.Equal("alteração local e outra alteração", await File.ReadAllTextAsync(tracked));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(index)));
        Assert.Contains("Sintonia: tarefa " + saved.TaskId, (await fixture.GitAsync(fixture.Repository, "worktree", "list", "--porcelain")).StandardOutput);
        await fixture.Manager.PrepareAsync(saved, CancellationToken.None); // validation only; no duplicate checkout or branch
        var reopened = new SqliteWorkspaceStore(fixture.Database); await reopened.InitializeAsync();
        Assert.Equal(saved, (await fixture.CurrentAsync(reopened, project, batch.Tasks[0].Id)).Worktree);
    }

    [Fact]
    public async Task DispatchUsesStableWorktreeSessionAndApprovalDoesNotReleaseUnintegratedDependencies()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var (store, project, batch) = await fixture.SeedAsync();
        var first = batch.Tasks[0]; var second = batch.Tasks[1]; var service = new TaskWorktreeService(store, fixture.Manager);
        var preview = await service.PreviewAsync(project.Id, first.Id, CancellationToken.None);
        await service.PrepareAsync(project.Id, preview, CancellationToken.None);
        var worker = new Worker(); var chat = new WorkspaceChatService(store, [worker], fixture.Manager);
        var run = await chat.SendTaskAsync(project.Id, first.Id, new Progress(), CancellationToken.None);
        Assert.Equal(preview.WorkingDirectory, worker.Requests[0].WorkingDirectory);
        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(project.Directory, "portal.txt")));
        await store.ReviewTaskAsync(project.Id, first.Id, run.Id, false, "Acrescentar instruções.");
        var next = await chat.SendTaskAsync(project.Id, first.Id, new Progress(), CancellationToken.None);
        Assert.Equal(worker.Requests[0].WorkingDirectory, worker.Requests[1].WorkingDirectory);
        Assert.Equal("fixture-session", worker.Requests[1].NativeSessionId);
        await store.ReviewTaskAsync(project.Id, first.Id, next.Id, true, "Conferido");
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendTaskAsync(project.Id, second.Id, new Progress(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(NewRun(second), second.Id));
        Assert.Equal(0, (await fixture.CurrentAsync(store, project, second.Id)).Attempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(project.Id, first.Id, CancellationToken.None));
        Assert.Equal(2, worker.Requests.Count);
    }

    [Fact]
    public async Task RecoveryAndRetryReuseExactIntentAndRejectStaleDispatch()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var (store, project, batch) = await fixture.SeedAsync(); var task = batch.Tasks[0];
        var preview = await fixture.Manager.PlanAsync(project, task.Id, CancellationToken.None);
        await store.ReserveTaskWorktreeAsync(project.Id, preview);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(NewRun(task), task.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveTaskWorktreeAsync(project.Id, preview));
        // Git completed, but the application died before saving Ready.
        await fixture.Manager.PrepareAsync(preview, CancellationToken.None);
        var reopened = new SqliteWorkspaceStore(fixture.Database); await reopened.InitializeAsync(); await reopened.RecoverInterruptedRunsAsync();
        var attention = (await fixture.CurrentAsync(reopened, project, task.Id)).Worktree!;
        Assert.Equal(TaskWorktreeState.NeedsAttention, attention.State); Assert.True(Directory.Exists(attention.CheckoutDirectory));
        var wrongBase = attention with { BaseCommit = new string('a', 40) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReserveTaskWorktreeAsync(project.Id, wrongBase));
        var service = new TaskWorktreeService(reopened, fixture.Manager);
        Assert.Equal(attention, await service.PreviewAsync(project.Id, task.Id, CancellationToken.None));
        await service.PrepareAsync(project.Id, attention, CancellationToken.None);
        var ready = (await fixture.CurrentAsync(reopened, project, task.Id)).Worktree!;
        // A caller that read the task before preparation must not execute in the original directory.
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.BeginRunAsync(NewRun(task), task.Id));
        await reopened.BeginRunAsync(NewRun(task), task.Id, ready);
    }

    [Fact]
    public async Task CancellationPreservesPartialEffectsAndDoesNotConsumeModelAttempt()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var (store, project, batch) = await fixture.SeedAsync(); var task = batch.Tasks[0];
        var preview = await fixture.Manager.PlanAsync(project, task.Id, CancellationToken.None);
        var waiting = new WaitingManager(preview); var service = new TaskWorktreeService(store, waiting);
        using var stop = new CancellationTokenSource(); var preparation = service.PrepareAsync(project.Id, preview, stop.Token);
        await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        var saved = await fixture.CurrentAsync(store, project, task.Id);
        Assert.Equal(0, saved.Attempts); Assert.Equal(TaskWorktreeState.NeedsAttention, saved.Worktree!.State);
        Assert.Equal("parcial", await File.ReadAllTextAsync(Path.Combine(preview.CheckoutDirectory, "partial.txt")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TaskWorktreeService(store, fixture.Manager).PrepareAsync(project.Id, saved.Worktree, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(preview.CheckoutDirectory, "partial.txt")));
    }

    [Fact]
    public async Task PreparationsInSameRepositoryAreSerializedAcrossStores()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var (store, project, batch) = await fixture.SeedAsync(independentWrites: true);
        var first = await fixture.Manager.PlanAsync(project, batch.Tasks[0].Id, CancellationToken.None);
        var second = await fixture.Manager.PlanAsync(project, batch.Tasks[1].Id, CancellationToken.None);
        await store.ReserveTaskWorktreeAsync(project.Id, first);
        var other = new SqliteWorkspaceStore(fixture.Database); await other.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ReserveTaskWorktreeAsync(project.Id, second));
        await store.FinishTaskWorktreeAsync(first.TaskId, false, "interrompido");
        await other.ReserveTaskWorktreeAsync(project.Id, second);
    }

    [Fact]
    public async Task SeparateCheckoutsDoNotEnableConcurrentWritersInSameProject()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var (store, project, batch) = await fixture.SeedAsync(independentWrites: true);
        var service = new TaskWorktreeService(store, fixture.Manager);
        foreach (var task in batch.Tasks)
            await service.PrepareAsync(project.Id, await service.PreviewAsync(project.Id, task.Id, CancellationToken.None), CancellationToken.None);
        var worker = new Worker(wait: true); var other = new Worker(ProviderKind.Claude); var chat = new WorkspaceChatService(store, [worker, other], fixture.Manager);
        using var stop = new CancellationTokenSource(); var active = chat.SendTaskAsync(project.Id, batch.Tasks[0].Id, new Progress(), stop.Token);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendTaskAsync(project.Id, batch.Tasks[1].Id, new Progress(), CancellationToken.None));
        Assert.Empty(other.Requests); stop.Cancel(); Assert.Equal(ChatRunState.Cancelled, (await active).State);
    }

    [Fact]
    public async Task ChangedBranchOrMissingCheckoutBlocksProviderWithoutConsumingAttempt()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var (store, project, batch) = await fixture.SeedAsync(); var task = batch.Tasks[0];
        var service = new TaskWorktreeService(store, fixture.Manager); var preview = await service.PreviewAsync(project.Id, task.Id, CancellationToken.None);
        await service.PrepareAsync(project.Id, preview, CancellationToken.None);
        await fixture.GitAsync(preview.CheckoutDirectory, "checkout", "--detach");
        var worker = new Worker(); var chat = new WorkspaceChatService(store, [worker], fixture.Manager);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendTaskAsync(project.Id, task.Id, new Progress(), CancellationToken.None));
        Assert.Empty(worker.Requests); Assert.Equal(0, (await fixture.CurrentAsync(store, project, task.Id)).Attempts);
        Directory.Move(preview.CheckoutDirectory, preview.CheckoutDirectory + "-moved");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.ValidateAsync(preview, CancellationToken.None));
    }

    [Fact]
    public async Task CollidingBranchAndDirectoryArePreserved()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var project = new WorkspaceProject("id", "Portal", fixture.Project);
        var plan = await fixture.Manager.PlanAsync(project, Guid.NewGuid().ToString(), CancellationToken.None);
        await fixture.GitAsync(fixture.Repository, "branch", plan.Branch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.PrepareAsync(plan, CancellationToken.None));
        Assert.False(Directory.Exists(plan.CheckoutDirectory));
        plan = await fixture.Manager.PlanAsync(project, Guid.NewGuid().ToString(), CancellationToken.None);
        Directory.CreateDirectory(plan.CheckoutDirectory); await File.WriteAllTextAsync(Path.Combine(plan.CheckoutDirectory, "owned.txt"), "preservar");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.PrepareAsync(plan, CancellationToken.None));
        Assert.Equal("preservar", await File.ReadAllTextAsync(Path.Combine(plan.CheckoutDirectory, "owned.txt")));
    }

    [Fact]
    public async Task HooksAreDisabledOnlyForPreparationAndUnsupportedFiltersAreExplicit()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var hooks = Path.Combine(fixture.Root, "hooks"); Directory.CreateDirectory(hooks);
        var marker = Path.Combine(fixture.Root, "hook-ran.txt").Replace('\\', '/');
        await File.WriteAllTextAsync(Path.Combine(hooks, "post-checkout"), "#!/bin/sh\necho executed > '" + marker + "'\n");
        await fixture.GitAsync(fixture.Repository, "config", "core.hooksPath", hooks);
        var project = new WorkspaceProject("id", "Portal", fixture.Project);
        var plan = await fixture.Manager.PlanAsync(project, Guid.NewGuid().ToString(), CancellationToken.None);
        await fixture.Manager.PrepareAsync(plan, CancellationToken.None);
        Assert.False(File.Exists(marker));
        Assert.Equal(hooks, (await fixture.GitAsync(fixture.Repository, "config", "--get", "core.hooksPath")).StandardOutput.Trim());
        await fixture.GitAsync(fixture.Repository, "config", "filter.fixture.smudge", "must-never-run");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, ".gitattributes"), "portal/* filter=fixture\n");
        await fixture.GitAsync(fixture.Repository, "add", ".gitattributes"); await fixture.GitAsync(fixture.Repository, "commit", "-m", "attributes");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.PlanAsync(project, Guid.NewGuid().ToString(), CancellationToken.None));
        Assert.Contains("filtros", error.Message);
    }

    [Fact]
    public async Task PreviewAndInterruptedCheckoutInspectionDoNotRunCleanFilters()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var project = new WorkspaceProject("id", "Portal", fixture.Project);
        var marker = Path.Combine(fixture.Root, "clean-filter-ran"); var script = Path.Combine(fixture.Root, "clean-filter.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho ran > '" + marker.Replace('\\', '/') + "'\ncat\n");
        await fixture.GitAsync(fixture.Repository, "config", "filter.fixture.clean", "\"" + script.Replace('\\', '/') + "\"");
        // Dirty source attributes must not affect the committed checkout or invoke a filter during preview.
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, ".gitattributes"), "portal/* filter=fixture\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, "portal.txt"), "local");
        var plan = await fixture.Manager.PlanAsync(project, Guid.NewGuid().ToString(), CancellationToken.None);
        Assert.False(File.Exists(marker)); await fixture.Manager.PrepareAsync(plan, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(plan.CheckoutDirectory, ".gitattributes"), "portal/* filter=fixture\n");
        await File.WriteAllTextAsync(Path.Combine(plan.WorkingDirectory, "portal.txt"), "partial");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.PrepareAsync(plan, CancellationToken.None));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task MissingGitUnbornRepositoryAndUncommittedProjectCannotPrepare()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.Repository);
        var project = new WorkspaceProject("id", "Portal", fixture.Repository);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.PlanAsync(project, Guid.NewGuid().ToString(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitTaskWorktreeManager(fixture.Managed, "").PlanAsync(project, Guid.NewGuid().ToString(), CancellationToken.None));
        await fixture.GitAsync(fixture.Repository, "init", "--template=", "--initial-branch=main");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.PlanAsync(project, Guid.NewGuid().ToString(), CancellationToken.None));
        await fixture.InitializeAsync(); var missing = Path.Combine(fixture.Repository, "uncommitted"); Directory.CreateDirectory(missing);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.PlanAsync(project with { Directory = missing }, Guid.NewGuid().ToString(), CancellationToken.None));
    }

    [Fact]
    public async Task SchemaFourMigrationPreservesProfilesAndTaskHistory()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var (store, project, batch) = await fixture.SeedAsync();
        var profile = await store.CreateFunctionProfileAsync("Portal", "Desenvolvimento", ProviderKind.Codex, null, "Conferir acessibilidade");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE task_worktrees; PRAGMA user_version=4;"; command.ExecuteNonQuery();
        }
        var migrated = new SqliteWorkspaceStore(fixture.Database); await migrated.InitializeAsync();
        Assert.Equal(profile, Assert.Single(await migrated.GetFunctionProfilesAsync()));
        Assert.Equal(batch.Id, Assert.Single(await migrated.GetTaskBatchesAsync(project.Id)).Id);
        Assert.All((await migrated.GetTaskBatchesAsync(project.Id)).SelectMany(b => b.Tasks), t => Assert.Null(t.Worktree));
    }

    [Fact]
    public void WorktreeParserPreservesSpacesAndRejectsTruncationOrDuplicateMetadata()
    {
        var head = new string('a', 40); var output = $"worktree C:/test ação/root\0HEAD {head}\0branch refs/heads/main\0\0worktree C:/test ação/task\0HEAD {head}\0branch refs/heads/codex/task\0locked Sintonia: tarefa id\0\0";
        Assert.Equal(2, GitWorktreeListParser.Parse(output).Count);
        Assert.Equal("Sintonia: tarefa id", GitWorktreeListParser.Parse(output)[1].LockReason);
        Assert.Throws<FormatException>(() => GitWorktreeListParser.Parse(output[..^1]));
        Assert.Throws<FormatException>(() => GitWorktreeListParser.Parse($"worktree C:/test\0HEAD {head}\0HEAD {head}\0\0"));
    }

    private static ChatRun NewRun(WorkspaceTask task) => new(Guid.NewGuid().ToString(), task.ConversationId, "pedido", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
    private sealed class Progress : IProgress<ConversationEvent> { public void Report(ConversationEvent value) { } }
    private sealed class Worker(ProviderKind kind = ProviderKind.Codex, bool wait = false) : IConversationProvider
    {
        public ProviderKind Kind => kind;
        public List<ConversationRequest> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken cancellationToken)
        {
            Requests.Add(request); Started.TrySetResult();
            if (wait) await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(request.WorkingDirectory, "portal.txt"), "entrega simulada", cancellationToken);
            return new("fixture-session", "modelo-teste", "Entrega simulada", ConversationOutcome.Completed, []);
        }
    }
    private sealed class WaitingManager(TaskWorktree preview) : IGitTaskWorktreeManager
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TaskWorktree> PlanAsync(WorkspaceProject project, string taskId, CancellationToken cancellationToken) => Task.FromResult(preview);
        public async Task PrepareAsync(TaskWorktree worktree, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(worktree.CheckoutDirectory); await File.WriteAllTextAsync(Path.Combine(worktree.CheckoutDirectory, "partial.txt"), "parcial", cancellationToken);
            Started.TrySetResult(); await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public Task ValidateAsync(TaskWorktree worktree, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sintonia-worktree-ação " + Guid.NewGuid());
        public string Repository => Path.Combine(Root, "original");
        public string Project => Path.Combine(Repository, "portal");
        public string Managed => Path.Combine(Root, "managed");
        public string Database => Path.Combine(Root, "workspace.db");
        public GitTaskWorktreeManager Manager => new(Managed);
        public Fixture() => Directory.CreateDirectory(Root);
        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(Project); await GitAsync(Repository, "init", "--template=", "--initial-branch=main");
            await File.WriteAllTextAsync(Path.Combine(Project, "portal.txt"), "base");
            await File.WriteAllTextAsync(Path.Combine(Project, "ação com espaços.txt"), "conteúdo");
            await File.WriteAllTextAsync(Path.Combine(Repository, ".gitignore"), "*.local\n");
            await GitAsync(Repository, "add", "-f", "."); await GitAsync(Repository, "commit", "-m", "base");
        }
        public async Task<ProcessProbeResult> GitAsync(string directory, params string[] arguments)
        {
            var result = await ProcessProbe.RunAsync(new("git.exe", [], "Git teste"),
                new[] { "-c", "user.name=Sintonia Test", "-c", "user.email=sintonia@example.invalid", "-c", "commit.gpgSign=false", "-c", "core.fsmonitor=false" }.Concat(arguments).ToArray(),
                directory, TimeSpan.FromSeconds(10));
            Assert.Equal(0, result.ExitCode); return result;
        }
        public async Task<(SqliteWorkspaceStore Store, WorkspaceProject Project, WorkspaceTaskBatch Batch)> SeedAsync(bool independentWrites = false)
        {
            var store = new SqliteWorkspaceStore(Database); await store.InitializeAsync(); var project = await store.AddProjectAsync(Project);
            var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Plano", ProviderKind.Codex, null, null, PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.ReadOnly);
            await store.SaveConversationAsync(conversation);
            var definition = new PlanProposal(1, "Portal", "Criar um portal acessível", [
                new("implement", "Criar portal", "Desenvolvimento", ProviderKind.Codex, null, ConversationAccess.WorkspaceWrite, "Criar portal", ["portal.txt"], [], ["Conferir"]),
                new("review", "Conferir portal", "Revisão", ProviderKind.Claude, null, independentWrites ? ConversationAccess.WorkspaceWrite : ConversationAccess.ReadOnly,
                    "Conferir portal", ["portal.txt"], independentWrites ? [] : ["implement"], ["Conferir"])]);
            var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "Plano", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
            await store.BeginRunAsync(run); await store.FinishRunAsync(run with { State = ChatRunState.Completed, FinishedAt = DateTimeOffset.UtcNow,
                Response = "```sintonia-plan\n" + PlanProposalFormat.Serialize(definition) + "\n```" }, conversation, []);
            var proposal = await store.CreateProposalAsync(project.Id, run.Id); proposal = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
            return (store, project, await store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision));
        }
        public async Task<WorkspaceTask> CurrentAsync(SqliteWorkspaceStore store, WorkspaceProject project, string taskId) =>
            (await store.GetTaskBatchesAsync(project.Id)).SelectMany(b => b.Tasks).Single(t => t.Id == taskId);
        public void Dispose()
        {
            var full = Path.GetFullPath(Root); var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("sintonia-worktree-ação ")) throw new InvalidOperationException();
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(full, recursive: true);
        }
    }
}
