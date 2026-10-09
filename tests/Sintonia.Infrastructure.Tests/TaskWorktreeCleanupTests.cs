using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Infrastructure.Tests;

[Collection("Git environment")]
public sealed class TaskWorktreeCleanupTests
{
    private static TaskWorktreeCleanupService Service(SqliteWorkspaceStore store, TaskWorktreeTests.Fixture fixture, IGitTaskWorktreeArchiver? archiver = null) =>
        new(store, new RepositoryIntegrationLock(), archiver ?? fixture.Manager);
    private static async Task<(SqliteWorkspaceStore Store, WorkspaceProject Project, TaskPublication Publication)> SeedAsync(TaskWorktreeTests.Fixture fixture)
    {
        var (store, project, validation) = await TaskPublicationTests.SeedAsync(fixture);
        var publication = await new TaskPublicationService(store, new(store, new GitTaskDeliveryInspector(fixture.Manager)),
            new RepositoryIntegrationLock(), new GitTaskPublisher(fixture.Manager)).PublishAsync(project.Id, validation, CancellationToken.None);
        Assert.Equal(TaskPublicationState.Published, publication.State); return (store, project, publication);
    }
    [Fact]
    public async Task ArchivePreservesEveryFileBranchPublicationHistoryAndDependencyWithoutKeepingActiveCheckout()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, publication) = await SeedAsync(fixture);
        var delivery = publication.Reservation.Delivery; var source = delivery.Worktree.CheckoutDirectory;
        await File.WriteAllTextAsync(Path.Combine(source, "config.local"), "configuração ignorada");
        await File.WriteAllTextAsync(Path.Combine(source, "ação não rastreada.txt"), "rascunho preservado");
        Directory.CreateDirectory(Path.Combine(source, "vazia"));
        var savedFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(source, p), File.ReadAllBytes);
        var ignored = Path.Combine(project.Directory, "config.local"); await File.WriteAllTextAsync(ignored, "original preservado");
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, delivery.TaskId, CancellationToken.None);
        var cleanup = await service.ArchiveAsync(project.Id, preview, CancellationToken.None);
        Assert.Equal(TaskWorktreeCleanupState.Archived, cleanup.State); Assert.Null(cleanup.Error); Assert.False(Directory.Exists(source));
        Assert.True(Directory.Exists(Path.Combine(preview.ArchiveDirectory, "vazia")));
        foreach (var (path, content) in savedFiles) Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(preview.ArchiveDirectory, path)));
        var registrations = (await fixture.GitAsync(fixture.Repository, "worktree", "list", "--porcelain")).StandardOutput;
        Assert.DoesNotContain(source.Replace('\\', '/'), registrations.Replace('\\', '/'));
        Assert.Equal(delivery.Commit, (await fixture.GitAsync(fixture.Repository, "rev-parse", "refs/heads/" + delivery.Worktree.Branch)).StandardOutput.Trim());
        Assert.Equal(publication.Commit, (await fixture.GitAsync(fixture.Repository, "rev-parse", "HEAD")).StandardOutput.Trim());
        Assert.Equal("original preservado", await File.ReadAllTextAsync(ignored)); Assert.True(Directory.Exists(publication.Validation.Preparation.CheckoutDirectory));
        var reopened = new SqliteWorkspaceStore(fixture.Database); await reopened.InitializeAsync(); await reopened.RecoverInterruptedRunsAsync();
        Assert.Equal(JsonSerializer.Serialize(cleanup), JsonSerializer.Serialize((await reopened.GetTaskWorktreeCleanupsAsync(project.Id)).Single()));
        var batch = (await reopened.GetTaskBatchesAsync(project.Id)).Single(); Assert.Equal(cleanup.Preview.Id, batch.Tasks[0].Cleanup!.Preview.Id);
        Assert.Equal(delivery, batch.Tasks[0].Delivery); Assert.Equal(TaskPublicationState.Published, batch.Tasks[0].Publication!.State);
        Assert.Single(await reopened.GetRunsAsync(batch.Tasks[0].ConversationId)); Assert.True(WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks));
        Assert.Contains(publication.Commit!, WorkspaceTaskPolicy.BuildPrompt(batch, batch.Tasks[1], await store.GetRunsAsync(batch.Tasks[0].ConversationId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(project.Id, delivery.TaskId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.FinishTaskWorktreeCleanupAsync(cleanup));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TaskDiffService(reopened, new GitTaskDiffReader(fixture.Manager)).ScanAsync(project.Id, delivery.TaskId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TaskDeliveryService(reopened, new GitTaskDeliveryInspector(fixture.Manager)).PreviewIntegrationAsync(project.Id, delivery.TaskId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReserveTaskIntegrationAsync(project.Id, delivery, publication.Reservation.Target));
    }
    [Theory]
    [InlineData("hidden")]
    [InlineData("staged")]
    [InlineData("commit")]
    public async Task ChangedDeliveryAfterPreviewIsNeverMovedOrOverwritten(string change)
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, publication) = await SeedAsync(fixture);
        var delivery = publication.Reservation.Delivery; var source = delivery.Worktree.CheckoutDirectory;
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, delivery.TaskId, CancellationToken.None);
        if (change == "hidden") await fixture.GitAsync(source, "update-index", "--assume-unchanged", "portal/portal.txt");
        await File.WriteAllTextAsync(Path.Combine(delivery.Worktree.WorkingDirectory, "portal.txt"), "mudança posterior preservada");
        if (change is "staged" or "commit") await fixture.GitAsync(source, "add", ".");
        if (change == "commit") await fixture.GitAsync(source, "commit", "-m", "trabalho posterior");
        var result = await service.ArchiveAsync(project.Id, preview, CancellationToken.None);
        Assert.Equal(TaskWorktreeCleanupState.NeedsAttention, result.State); Assert.True(Directory.Exists(source)); Assert.False(Directory.Exists(preview.ArchiveDirectory));
        Assert.Equal("mudança posterior preservada", await File.ReadAllTextAsync(Path.Combine(delivery.Worktree.WorkingDirectory, "portal.txt")));
        Assert.Equal(TaskPublicationState.Published, (await store.GetTaskPublicationsAsync(project.Id)).Single().State);
    }
    [Fact]
    public async Task OwnedPathsCollisionAndExistingIndexLockRefuseWithoutMovingFiles()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, publication) = await SeedAsync(fixture);
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, publication.Reservation.Delivery.TaskId, CancellationToken.None);
        var outside = Path.Combine(fixture.Root, "outside", Guid.Parse(publication.Reservation.Delivery.TaskId).ToString("N"), Guid.Parse(preview.Id).ToString("N"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.VerifyArchiveAsync(preview with { ArchiveDirectory = outside }, CancellationToken.None));
        Directory.CreateDirectory(preview.ArchiveDirectory); await File.WriteAllTextAsync(Path.Combine(preview.ArchiveDirectory, "sentinel"), "não sobrescrever");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.VerifyArchiveAsync(preview, CancellationToken.None));
        Assert.Equal("não sobrescrever", await File.ReadAllTextAsync(Path.Combine(preview.ArchiveDirectory, "sentinel")));
        preview = await service.PreviewAsync(project.Id, publication.Reservation.Delivery.TaskId, CancellationToken.None);
        var source = publication.Reservation.Delivery.Worktree.CheckoutDirectory;
        var index = (await fixture.GitAsync(source, "rev-parse", "--path-format=absolute", "--git-path", "index")).StandardOutput.Trim();
        using (var held = new FileStream(index + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            var result = await service.ArchiveAsync(project.Id, preview, CancellationToken.None);
            Assert.Equal(TaskWorktreeCleanupState.NeedsAttention, result.State); Assert.True(Directory.Exists(source)); Assert.False(Directory.Exists(preview.ArchiveDirectory));
            Assert.True(File.Exists(index + ".lock"));
        }
        File.Delete(index + ".lock");
    }
    [Fact]
    public async Task CancellationBeforeNativeMoveKeepsLiveGateUntilStoppedAndAllowsExplicitRetry()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, publication) = await SeedAsync(fixture);
        var preview = await Service(store, fixture).PreviewAsync(project.Id, publication.Reservation.Delivery.TaskId, CancellationToken.None);
        var archiver = new ControlledArchiver(fixture.Manager) { BlockVerify = true }; using var stop = new CancellationTokenSource();
        var operation = Service(store, fixture, archiver).ArchiveAsync(project.Id, preview, stop.Token);
        await archiver.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var reopened = new SqliteWorkspaceStore(fixture.Database); await reopened.InitializeAsync(); await reopened.RecoverInterruptedRunsAsync();
        Assert.Equal(TaskWorktreeCleanupState.Archiving, (await reopened.GetTaskWorktreeCleanupsAsync(project.Id)).Single().State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReleaseTaskIntegrationAsync(preview.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReserveTaskIntegrationAsync(project.Id, publication.Reservation.Delivery, publication.Reservation.Target));
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Gate de teste", ProviderKind.Codex, null, null, "Conversa", "", ConversationAccess.ReadOnly);
        await reopened.SaveConversationAsync(conversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.BeginRunAsync(new(Guid.NewGuid().ToString(), conversation.Id, "SIMULAÇÃO", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null)));
        stop.Cancel(); Assert.False(operation.IsCompleted); archiver.Release.TrySetResult(); var cancelled = await operation;
        Assert.Equal(TaskWorktreeCleanupState.Cancelled, cancelled.State); Assert.Equal(0, archiver.Applies); Assert.False(cancelled.BlocksCheckout);
        Assert.True(Directory.Exists(publication.Reservation.Delivery.Worktree.CheckoutDirectory));
        var retry = await Service(store, fixture).PreviewAsync(project.Id, publication.Reservation.Delivery.TaskId, CancellationToken.None);
        Assert.Equal(preview.Id, retry.PreviousId); Assert.NotEqual(preview.Id, retry.Id);
        Assert.Equal(TaskWorktreeCleanupState.Archived, (await Service(store, fixture).ArchiveAsync(project.Id, retry, CancellationToken.None)).State);
        Assert.Equal(2, (await store.GetTaskWorktreeCleanupsAsync(project.Id)).Count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCancellationWaitsForNativeOutcomeAndAmbiguousFailureKeepsArchiveWithoutReplay(bool failAfterMove)
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, publication) = await SeedAsync(fixture);
        var preview = await Service(store, fixture).PreviewAsync(project.Id, publication.Reservation.Delivery.TaskId, CancellationToken.None);
        var archiver = new ControlledArchiver(fixture.Manager) { BlockApply = true, FailAfterApply = failAfterMove }; using var stop = new CancellationTokenSource();
        var service = Service(store, fixture, archiver); var operation = service.ArchiveAsync(project.Id, preview, stop.Token);
        await archiver.Started.Task.WaitAsync(TimeSpan.FromSeconds(60)); stop.Cancel(); Assert.False(operation.IsCompleted);
        archiver.Release.TrySetResult(); var result = await operation;
        Assert.Equal(failAfterMove ? TaskWorktreeCleanupState.NeedsAttention : TaskWorktreeCleanupState.Archived, result.State);
        Assert.True(Directory.Exists(preview.ArchiveDirectory)); Assert.False(Directory.Exists(publication.Reservation.Delivery.Worktree.CheckoutDirectory));
        await store.RecoverInterruptedRunsAsync(); Assert.Equal(1, archiver.Applies);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(project.Id, publication.Reservation.Delivery.TaskId, CancellationToken.None));
        var batch = (await store.GetTaskBatchesAsync(project.Id)).Single(); Assert.True(WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks));
        Assert.Equal(publication.Commit, batch.Tasks[0].Publication!.Commit); Assert.True(batch.Tasks[0].Cleanup!.BlocksCheckout);
    }
    [Fact]
    public async Task Schema12MigratesAndAbandonedIntentBecomesInterruptedWithoutMovingOrReplaying()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, publication) = await SeedAsync(fixture);
        using (var connection = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False"))
        { connection.Open(); using var sql = connection.CreateCommand(); sql.CommandText = "DROP TABLE task_worktree_cleanups; PRAGMA user_version=12;"; sql.ExecuteNonQuery(); }
        await store.InitializeAsync(); Assert.Empty(await store.GetTaskWorktreeCleanupsAsync(project.Id));
        Assert.Equal(JsonSerializer.Serialize(publication), JsonSerializer.Serialize((await store.GetTaskPublicationsAsync(project.Id)).Single()));
        var preview = await Service(store, fixture).PreviewAsync(project.Id, publication.Reservation.Delivery.TaskId, CancellationToken.None);
        TaskWorktreeCleanup intent;
        using (new RepositoryIntegrationLock().Acquire(publication.Reservation.Target.CommonGitDirectory))
        {
            intent = await store.ReserveTaskWorktreeCleanupAsync(project.Id, preview);
            await store.RecoverInterruptedRunsAsync(); Assert.Equal(TaskWorktreeCleanupState.Archiving, (await store.GetTaskWorktreeCleanupsAsync(project.Id)).Single().State);
        }
        await store.RecoverInterruptedRunsAsync(); var recovered = (await store.GetTaskWorktreeCleanupsAsync(project.Id)).Single();
        Assert.Equal(TaskWorktreeCleanupState.Interrupted, recovered.State); Assert.NotNull(recovered.FinishedAt);
        Assert.True(Directory.Exists(publication.Reservation.Delivery.Worktree.CheckoutDirectory)); Assert.False(Directory.Exists(preview.ArchiveDirectory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FinishTaskWorktreeCleanupAsync(intent with { State = TaskWorktreeCleanupState.Archived, FinishedAt = DateTimeOffset.UtcNow }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveTaskWorktreeCleanupAsync(project.Id, preview));
        var retry = await Service(store, fixture).PreviewAsync(project.Id, publication.Reservation.Delivery.TaskId, CancellationToken.None); Assert.Equal(preview.Id, retry.PreviousId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveTaskWorktreeCleanupAsync(project.Id, retry with { Publication = publication with { Commit = new string('a', publication.Commit!.Length) } }));
        var batch = (await store.GetTaskBatchesAsync(project.Id)).Single(); Assert.True(WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks));
    }
    private sealed class ControlledArchiver(IGitTaskWorktreeArchiver native) : IGitTaskWorktreeArchiver
    {
        public bool BlockVerify { get; init; }
        public bool BlockApply { get; init; }
        public bool FailAfterApply { get; init; }
        public int Applies { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string GetArchiveDirectory(TaskPublication publication, string id) => native.GetArchiveDirectory(publication, id);
        public async Task VerifyArchiveAsync(TaskWorktreeCleanupPreview preview, CancellationToken token)
        {
            if (BlockVerify) { Started.TrySetResult(); await Release.Task; token.ThrowIfCancellationRequested(); }
            await native.VerifyArchiveAsync(preview, token);
        }
        public async Task ArchiveAsync(TaskWorktreeCleanupPreview preview)
        {
            Applies++; if (BlockApply) { Started.TrySetResult(); await Release.Task; }
            await native.ArchiveAsync(preview); if (FailAfterApply) throw new IOException("SIMULAÇÃO: falha após arquivamento nativo");
        }
    }
}
