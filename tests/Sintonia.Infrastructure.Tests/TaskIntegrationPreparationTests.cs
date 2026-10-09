using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Infrastructure.Tests;

[Collection("Git environment")]
public sealed class TaskIntegrationPreparationTests
{
    private static TaskDeliveryService Deliveries(SqliteWorkspaceStore store, TaskWorktreeTests.Fixture fixture) =>
        new(store, new GitTaskDeliveryInspector(fixture.Manager));
    private static GitTaskIntegrationPreparer Native(TaskWorktreeTests.Fixture fixture) => new(fixture.Manager, Path.Combine(fixture.Root, "combinações"));
    private static TaskIntegrationPreparationService Service(SqliteWorkspaceStore store, TaskWorktreeTests.Fixture fixture, IGitTaskIntegrationPreparer? preparer = null) =>
        new(store, Deliveries(store, fixture), new RepositoryIntegrationLock(), preparer ?? Native(fixture));
    private static async Task<(SqliteWorkspaceStore Store, WorkspaceProject Project, WorkspaceTask Task)> SeedAsync(TaskWorktreeTests.Fixture fixture)
    {
        var (store, project, task, review) = await TaskDeliveryTests.PrepareAsync(fixture);
        await Deliveries(store, fixture).RegisterAsync(project.Id, review, CancellationToken.None);
        return (store, project, await fixture.CurrentAsync(store, project, task.Id));
    }

    [Fact]
    public async Task CombinesPinnedCommitsInDetachedCheckoutAndPreservesOriginalSourceAndIgnoredFiles()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task) = await SeedAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, "outra entrega.txt"), "alteração do destino\n");
        await fixture.GitAsync(fixture.Repository, "add", "."); await fixture.GitAsync(fixture.Repository, "commit", "-m", "outro trabalho aprovado");
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, "config.local"), "original ignorado");
        await File.WriteAllTextAsync(Path.Combine(task.Worktree!.WorkingDirectory, "config.local"), "origem ignorada");
        var index = Path.Combine(task.Worktree.CommonGitDirectory, "index"); var hash = SHA256.HashData(await File.ReadAllBytesAsync(index));
        await File.WriteAllTextAsync(index + ".lock", "trava do usuário");
        var sourceIndex = Path.Combine((await fixture.GitAsync(task.Worktree.CheckoutDirectory, "rev-parse", "--absolute-git-dir")).StandardOutput.Trim(), "index");
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceIndex));
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, task.Id, CancellationToken.None);
        var result = await service.PrepareAsync(project.Id, task.Id, preview, CancellationToken.None);
        Assert.Equal(TaskIntegrationPreparationState.Combined, result.State); Assert.NotNull(result.Tree);
        Assert.Equal("entrega salva\n", (await File.ReadAllTextAsync(Path.Combine(result.CheckoutDirectory, "portal", "portal.txt"))).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("alteração do destino\n", (await File.ReadAllTextAsync(Path.Combine(result.CheckoutDirectory, "portal", "outra entrega.txt"))).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(result.CheckoutDirectory, "portal", "config.local")));
        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt")));
        Assert.Equal("original ignorado", await File.ReadAllTextAsync(Path.Combine(fixture.Project, "config.local")));
        Assert.Equal("origem ignorada", await File.ReadAllTextAsync(Path.Combine(task.Worktree.WorkingDirectory, "config.local")));
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(index))); Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(sourceIndex)));
        Assert.Equal("trava do usuário", await File.ReadAllTextAsync(index + ".lock"));
        Assert.Equal(preview.Commit, (await fixture.GitAsync(result.CheckoutDirectory, "rev-parse", "HEAD")).StandardOutput.Trim());
        Assert.Equal(preview.Commit, (await fixture.GitAsync(fixture.Repository, "rev-parse", "HEAD")).StandardOutput.Trim());
        Assert.Equal(task.Delivery!.Commit, (await fixture.GitAsync(task.Worktree.CheckoutDirectory, "rev-parse", "HEAD")).StandardOutput.Trim());
        Assert.Equal(result.Tree, (await fixture.GitAsync(result.CheckoutDirectory, "write-tree")).StandardOutput.Trim());
        var reopened = new SqliteWorkspaceStore(fixture.Database); await reopened.InitializeAsync(); await reopened.RecoverInterruptedRunsAsync();
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize((await reopened.GetTaskIntegrationPreparationsAsync(project.Id)).Single()));
        Assert.Equal(TaskIntegrationState.Released, (await reopened.GetTaskIntegrationsAsync(project.Id)).Single().State);
        var batch = (await reopened.GetTaskBatchesAsync(project.Id)).Single(); Assert.False(WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FinishTaskIntegrationPreparationAsync(result));
    }

    [Fact]
    public async Task RepositoryHooksAndDefaultExternalDriverCannotExecuteDuringCombination()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task) = await SeedAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, "portal.txt"), "mudança concorrente\n");
        await fixture.GitAsync(fixture.Repository, "add", "."); await fixture.GitAsync(fixture.Repository, "commit", "-m", "conflito para testar driver");
        var hooks = Path.Combine(fixture.Root, "hooks"); Directory.CreateDirectory(hooks);
        var marker = Path.Combine(fixture.Root, "executou.txt").Replace('\\', '/');
        foreach (var name in new[] { "post-checkout", "pre-merge-commit", "post-merge" })
            await File.WriteAllTextAsync(Path.Combine(hooks, name), "#!/bin/sh\nprintf executou > '" + marker + "'\n");
        await fixture.GitAsync(fixture.Repository, "config", "core.hooksPath", hooks);
        await fixture.GitAsync(fixture.Repository, "config", "merge.default", "external");
        await fixture.GitAsync(fixture.Repository, "config", "merge.external.driver", "printf executou > '" + marker + "'");
        await fixture.GitAsync(fixture.Repository, "config", "commit.gpgSign", "true");
        await fixture.GitAsync(fixture.Repository, "config", "merge.autoStash", "true");
        await fixture.GitAsync(fixture.Repository, "config", "rerere.enabled", "true");
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, task.Id, CancellationToken.None);
        Assert.Equal(TaskIntegrationPreparationState.Conflicted, (await service.PrepareAsync(project.Id, task.Id, preview, CancellationToken.None)).State);
        Assert.False(File.Exists(marker));
        Assert.Equal("external", (await fixture.GitAsync(fixture.Repository, "config", "merge.default")).StandardOutput.Trim());
        Assert.Equal("true", (await fixture.GitAsync(fixture.Repository, "config", "--local", "--get", "commit.gpgSign")).StandardOutput.Trim());
    }

    [Fact]
    public async Task AnExistingIntegrationDirectoryIsNeverOverwritten()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task) = await SeedAsync(fixture);
        var preview = await Service(store, fixture).PreviewAsync(project.Id, task.Id, CancellationToken.None);
        var reservation = await Deliveries(store, fixture).ReserveIntegrationAsync(project.Id, task.Id, preview, CancellationToken.None);
        var native = Native(fixture); var intent = new TaskIntegrationPreparation(reservation, native.GetCheckoutDirectory(reservation));
        await store.SaveTaskIntegrationPreparationAsync(intent); Directory.CreateDirectory(intent.CheckoutDirectory);
        await File.WriteAllTextAsync(Path.Combine(intent.CheckoutDirectory, "arquivo.txt"), "arquivos do usuário");
        await Assert.ThrowsAsync<InvalidOperationException>(() => native.PrepareAsync(intent, CancellationToken.None));
        Assert.Equal("arquivos do usuário", await File.ReadAllTextAsync(Path.Combine(intent.CheckoutDirectory, "arquivo.txt")));
        var other = intent with { CheckoutDirectory = Path.Combine(fixture.Root, "fora da pasta gerenciada", Guid.Parse(reservation.Id).ToString("N")) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => native.PrepareAsync(other, CancellationToken.None));
        Assert.False(Directory.Exists(other.CheckoutDirectory)); await store.RecoverInterruptedRunsAsync();
    }

    [Fact]
    public async Task ConflictsRemainInSeparateCheckoutWithoutACombinedTreeOrPublication()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task) = await SeedAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, "portal.txt"), "mudança concorrente\n");
        await fixture.GitAsync(fixture.Repository, "add", "."); await fixture.GitAsync(fixture.Repository, "commit", "-m", "conflito de teste");
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, task.Id, CancellationToken.None);
        var result = await service.PrepareAsync(project.Id, task.Id, preview, CancellationToken.None);
        Assert.Equal(TaskIntegrationPreparationState.Conflicted, result.State); Assert.Null(result.Tree); Assert.Equal(["portal/portal.txt"], result.Conflicts);
        Assert.Contains("<<<<<<<", await File.ReadAllTextAsync(Path.Combine(result.CheckoutDirectory, "portal", "portal.txt")));
        Assert.Equal("mudança concorrente\n", await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt")));
        Assert.Equal("entrega salva\n", await File.ReadAllTextAsync(Path.Combine(task.Worktree!.WorkingDirectory, "portal.txt")));
        Assert.Contains("portal/portal.txt", (await fixture.GitAsync(result.CheckoutDirectory, "ls-files", "--unmerged")).StandardOutput);
        await store.RecoverInterruptedRunsAsync(); Assert.Equal(TaskIntegrationPreparationState.Conflicted, (await service.GetAsync(project.Id)).Single().State);
    }

    [Theory]
    [InlineData("destination")]
    [InlineData("source")]
    public async Task AStalePreviewCannotCreateACheckout(string changed)
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task) = await SeedAsync(fixture);
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, task.Id, CancellationToken.None);
        await fixture.GitAsync(changed == "destination" ? fixture.Repository : task.Worktree!.CheckoutDirectory, "commit", "--allow-empty", "-m", "posterior");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(project.Id, task.Id, preview, CancellationToken.None));
        Assert.Empty(await store.GetTaskIntegrationsAsync(project.Id)); Assert.Empty(await service.GetAsync(project.Id));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "combinações")));
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("merge")]
    [InlineData("submodule")]
    public async Task UnsupportedAttributesAndSubmodulesAreRefusedBeforeCheckoutAndDoNotRunCommands(string kind)
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, _) = await TaskDeliveryTests.PrepareAsync(fixture);
        var directory = task.Worktree!.CheckoutDirectory;
        if (kind == "submodule")
        {
            await fixture.GitAsync(directory, "worktree", "add", "--detach", "--lock", Path.Combine(directory, "module"), task.Worktree.BaseCommit);
            await fixture.GitAsync(directory, "update-index", "--add", "--cacheinfo", "160000," + task.Worktree.BaseCommit + ",module");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(directory, ".gitattributes"), "portal/portal.txt " + kind + "=external\n");
            await fixture.GitAsync(directory, "add", ".");
        }
        await fixture.GitAsync(directory, "commit", "-m", "entrega com requisito não suportado");
        var review = await new TaskDiffService(store, new GitTaskDiffReader(fixture.Manager)).ScanAsync(project.Id, task.Id, CancellationToken.None);
        await Deliveries(store, fixture).RegisterAsync(project.Id, review, CancellationToken.None);
        await fixture.GitAsync(directory, "config", "merge.external.driver", "echo executou > driver-marker");
        await fixture.GitAsync(directory, "config", "filter.external.smudge", "echo executou > filter-marker");
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, task.Id, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(project.Id, task.Id, preview, CancellationToken.None));
        var failed = (await service.GetAsync(project.Id)).Single(); Assert.Equal(TaskIntegrationPreparationState.NeedsAttention, failed.State);
        Assert.False(Directory.Exists(failed.CheckoutDirectory)); Assert.False(File.Exists(Path.Combine(fixture.Repository, "driver-marker")));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "filter-marker")));
    }

    [Fact]
    public async Task CancellationPreservesPartialFilesAndLiveRecoveryCannotReleaseTheReservation()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task) = await SeedAsync(fixture);
        var blocked = new BlockingPreparer(Native(fixture)); var service = Service(store, fixture, blocked);
        var preview = await service.PreviewAsync(project.Id, task.Id, CancellationToken.None); using var stop = new CancellationTokenSource();
        var operation = service.PrepareAsync(project.Id, task.Id, preview, stop.Token); await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var other = new SqliteWorkspaceStore(fixture.Database); await other.InitializeAsync(); await other.RecoverInterruptedRunsAsync();
        var live = (await other.GetTaskIntegrationsAsync(project.Id)).Single(); Assert.Equal(TaskIntegrationState.Reserved, live.State);
        Assert.Equal(TaskIntegrationPreparationState.Preparing, (await other.GetTaskIntegrationPreparationsAsync(project.Id)).Single().State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ReleaseTaskIntegrationAsync(live.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(other, fixture).PrepareAsync(project.Id, task.Id, preview, CancellationToken.None));
        var checkoutProject = await store.AddProjectAsync(blocked.Intent!.CheckoutDirectory);
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), checkoutProject.Id, "Teste", ProviderKind.Codex, null, null, "Revisão", "", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(conversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(new(Guid.NewGuid().ToString(), conversation.Id, "Teste", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null)));
        stop.Cancel(); blocked.Release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        var failed = (await service.GetAsync(project.Id)).Single(); Assert.Equal(TaskIntegrationPreparationState.NeedsAttention, failed.State); Assert.Null(failed.Tree);
        Assert.Equal("efeito parcial preservado", await File.ReadAllTextAsync(Path.Combine(failed.CheckoutDirectory, "partial.txt")));
        Assert.Equal(TaskIntegrationState.NeedsAttention, (await store.GetTaskIntegrationsAsync(project.Id)).Single().State);
        using var next = new RepositoryIntegrationLock().Acquire(preview.CommonGitDirectory);
    }

    [Fact]
    public async Task RecoveryKeepsAbandonedFilesAndANewAttemptUsesANewDirectory()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task) = await SeedAsync(fixture);
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, task.Id, CancellationToken.None);
        var reservation = await Deliveries(store, fixture).ReserveIntegrationAsync(project.Id, task.Id, preview, CancellationToken.None);
        var intent = new TaskIntegrationPreparation(reservation, Native(fixture).GetCheckoutDirectory(reservation));
        await store.SaveTaskIntegrationPreparationAsync(intent); Directory.CreateDirectory(intent.CheckoutDirectory);
        await File.WriteAllTextAsync(Path.Combine(intent.CheckoutDirectory, "arquivo.local"), "não descartar");
        await store.RecoverInterruptedRunsAsync();
        Assert.Equal(TaskIntegrationPreparationState.NeedsAttention, (await service.GetAsync(project.Id)).Single().State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FinishTaskIntegrationPreparationAsync(intent with { State = TaskIntegrationPreparationState.Combined, Tree = task.Delivery!.Tree }));
        var next = await service.PrepareAsync(project.Id, task.Id, preview, CancellationToken.None);
        Assert.NotEqual(intent.CheckoutDirectory, next.CheckoutDirectory); Assert.Equal(TaskIntegrationPreparationState.Combined, next.State);
        Assert.Equal("não descartar", await File.ReadAllTextAsync(Path.Combine(intent.CheckoutDirectory, "arquivo.local")));
    }

    [Fact]
    public async Task SchemaSixMigrationKeepsTheRegisteredDeliveryAndReservation()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task) = await SeedAsync(fixture);
        var preview = await Service(store, fixture).PreviewAsync(project.Id, task.Id, CancellationToken.None);
        var reservation = await Deliveries(store, fixture).ReserveIntegrationAsync(project.Id, task.Id, preview, CancellationToken.None);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Database, Pooling = false }.ToString()))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TABLE task_integration_preparations; PRAGMA user_version=6;"; command.ExecuteNonQuery(); }
        var reopened = new SqliteWorkspaceStore(fixture.Database); await reopened.InitializeAsync();
        Assert.Equal(task.Delivery, (await fixture.CurrentAsync(reopened, project, task.Id)).Delivery);
        Assert.Equal(reservation, (await reopened.GetTaskIntegrationsAsync(project.Id)).Single()); Assert.Empty(await reopened.GetTaskIntegrationPreparationsAsync(project.Id));
    }

    private sealed class BlockingPreparer(IGitTaskIntegrationPreparer native) : IGitTaskIntegrationPreparer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskIntegrationPreparation? Intent { get; private set; }
        public string GetCheckoutDirectory(TaskIntegrationReservation reservation) => native.GetCheckoutDirectory(reservation);
        public async Task<TaskIntegrationPreparation> PrepareAsync(TaskIntegrationPreparation intent, CancellationToken token)
        {
            Intent = intent; Directory.CreateDirectory(intent.CheckoutDirectory);
            await File.WriteAllTextAsync(Path.Combine(intent.CheckoutDirectory, "partial.txt"), "efeito parcial preservado");
            Started.SetResult(); await Release.Task; return intent with { State = TaskIntegrationPreparationState.Combined, Tree = new('a', intent.Reservation.Delivery.Tree.Length) };
        }
    }
}
