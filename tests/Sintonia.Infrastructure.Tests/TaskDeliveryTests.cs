using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Sintonia.Core;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Infrastructure.Tests;

[Collection("Git environment")]
public sealed class TaskDeliveryTests
{
    private static async Task<(SqliteWorkspaceStore Store, WorkspaceProject Project, WorkspaceTask Task, TaskDiffReview Review)> PrepareAsync(
        TaskWorktreeTests.Fixture fixture, bool independentWrites = false)
    {
        await fixture.InitializeAsync(); var (store, project, batch) = await fixture.SeedAsync(independentWrites);
        var preparation = new TaskWorktreeService(store, fixture.Manager);
        await preparation.PrepareAsync(project.Id, await preparation.PreviewAsync(project.Id, batch.Tasks[0].Id, CancellationToken.None), CancellationToken.None);
        var task = await fixture.CurrentAsync(store, project, batch.Tasks[0].Id);
        var conversation = (await store.GetConversationsAsync(project.Id)).Single(c => c.Id == task.ConversationId);
        var run = NewRun(task); await store.BeginRunAsync(run, task.Id, task.Worktree);
        await File.WriteAllTextAsync(Path.Combine(task.Worktree!.WorkingDirectory, "portal.txt"), "entrega salva\n");
        await fixture.GitAsync(task.Worktree.CheckoutDirectory, "add", "."); await fixture.GitAsync(task.Worktree.CheckoutDirectory, "commit", "-m", "entrega de teste");
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "SIMULAÇÃO: entrega de teste", FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        await store.ReviewTaskAsync(project.Id, task.Id, run.Id, true, "Conferido no teste");
        var review = await new TaskDiffService(store, new GitTaskDiffReader(fixture.Manager)).ScanAsync(project.Id, task.Id, CancellationToken.None);
        return (store, project, review.Task, review);
    }
    private static ChatRun NewRun(WorkspaceTask task) => new(Guid.NewGuid().ToString(), task.ConversationId, "teste", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
    private static TaskDeliveryService Service(SqliteWorkspaceStore store, TaskWorktreeTests.Fixture fixture) => new(store, new GitTaskDeliveryInspector(fixture.Manager));

    [Fact]
    public async Task RecoveryLeavesLiveIntegrationAloneAndRecoversAfterOwnerProcessExits()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, review) = await PrepareAsync(fixture);
        var service = Service(store, fixture); var delivery = await service.RegisterAsync(project.Id, review, CancellationToken.None);
        var target = await service.PreviewIntegrationAsync(project.Id, task.Id, CancellationToken.None);
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.CommonGitDirectory)).ToUpperInvariant();
        var lockPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sintonia", "integration-locks",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var owner = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "hold-file", lockPath } })!;
        try
        {
            Assert.Equal("LOCKED", await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var reservation = await store.ReserveTaskIntegrationAsync(project.Id, delivery, target);
            var other = new SqliteWorkspaceStore(fixture.Database); await other.InitializeAsync();
            await other.RecoverInterruptedRunsAsync();
            Assert.Equal(TaskIntegrationState.Reserved, (await other.GetTaskIntegrationsAsync(project.Id)).Single().State);
            Assert.Throws<InvalidOperationException>(() => new RepositoryIntegrationLock().Acquire(target.CommonGitDirectory));
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.ReserveTaskIntegrationAsync(project.Id, delivery, target));
            owner.Kill(entireProcessTree: true); await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await other.RecoverInterruptedRunsAsync();
            Assert.Equal(TaskIntegrationState.NeedsAttention, (await other.GetTaskIntegrationsAsync(project.Id)).Single().State);
            using var nextOwner = new RepositoryIntegrationLock().Acquire(target.CommonGitDirectory);
            Assert.Throws<InvalidOperationException>(() => new RepositoryIntegrationLock().Acquire(target.CommonGitDirectory));
            Assert.Equal(reservation.Id, (await store.GetTaskIntegrationsAsync(project.Id)).Single().Id);
        }
        finally { if (!owner.HasExited) { owner.Kill(entireProcessTree: true); await owner.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task RegistersExactReviewedCommitWithoutChangingGitAndKeepsItOnReopen()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, review) = await PrepareAsync(fixture);
        var gitDirectory = (await fixture.GitAsync(task.Worktree!.CheckoutDirectory, "rev-parse", "--absolute-git-dir")).StandardOutput.Trim();
        var index = Path.Combine(gitDirectory, "index"); var before = SHA256.HashData(await File.ReadAllBytesAsync(index)); var modified = File.GetLastWriteTimeUtc(index);
        await File.WriteAllTextAsync(index + ".lock", "lock externo"); await File.WriteAllTextAsync(Path.Combine(task.Worktree.WorkingDirectory, "config.local"), "ignorado");
        var delivery = await Service(store, fixture).RegisterAsync(project.Id, review, CancellationToken.None);
        Assert.Equal(review.Snapshot.HeadCommit, delivery.Commit); Assert.Equal(task.LastRunId, delivery.SourceRunId);
        Assert.Equal((await fixture.GitAsync(task.Worktree.CheckoutDirectory, "rev-parse", "HEAD^{tree}")).StandardOutput.Trim(), delivery.Tree);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(index))); Assert.Equal(modified, File.GetLastWriteTimeUtc(index));
        Assert.Equal("lock externo", await File.ReadAllTextAsync(index + ".lock")); Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt")));
        Assert.Equal(delivery, await store.SaveTaskDeliveryAsync(project.Id, delivery with { RegisteredAt = DateTimeOffset.UtcNow.AddMinutes(1) }));
        var reopened = new SqliteWorkspaceStore(fixture.Database); await reopened.InitializeAsync();
        Assert.Equal(delivery, (await fixture.CurrentAsync(reopened, project, task.Id)).Delivery);
        var batch = (await reopened.GetTaskBatchesAsync(project.Id)).Single(); Assert.False(WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveTaskDeliveryAsync(project.Id, delivery with { Commit = new('b', 40) }));
    }
    [Theory]
    [InlineData("tracked")]
    [InlineData("staged")]
    [InlineData("new")]
    public async Task UncommittedFilesCannotBecomeAPartialDelivery(string kind)
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, _) = await PrepareAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(task.Worktree!.WorkingDirectory, kind == "new" ? "novo.txt" : "portal.txt"), "mudança\n");
        if (kind == "staged") await fixture.GitAsync(task.Worktree.CheckoutDirectory, "add", ".");
        var review = await new TaskDiffService(store, new GitTaskDiffReader(fixture.Manager)).ScanAsync(project.Id, task.Id, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(store, fixture).RegisterAsync(project.Id, review, CancellationToken.None));
        Assert.Null((await fixture.CurrentAsync(store, project, task.Id)).Delivery);
    }
    [Fact]
    public async Task ReplacementRefsCannotRedirectTheRegisteredCommitOrTree()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, review) = await PrepareAsync(fixture);
        var directory = task.Worktree!.CheckoutDirectory;
        var actualTree = (await fixture.GitAsync(directory, "rev-parse", "HEAD^{tree}")).StandardOutput.Trim();
        var baseTree = (await fixture.GitAsync(directory, "rev-parse", task.Worktree.BaseCommit + "^{tree}")).StandardOutput.Trim();
        var replacement = (await fixture.GitAsync(directory, "commit-tree", baseTree, "-p", task.Worktree.BaseCommit, "-m", "substituição de teste")).StandardOutput.Trim();
        await fixture.GitAsync(directory, "replace", review.Snapshot.HeadCommit, replacement);
        Assert.Equal(baseTree, (await fixture.GitAsync(directory, "rev-parse", "HEAD^{tree}")).StandardOutput.Trim());
        var refreshed = await new TaskDiffService(store, new GitTaskDiffReader(fixture.Manager)).ScanAsync(project.Id, task.Id, CancellationToken.None);
        var delivery = await Service(store, fixture).RegisterAsync(project.Id, refreshed, CancellationToken.None);
        Assert.Equal(review.Snapshot.HeadCommit, delivery.Commit); Assert.Equal(actualTree, delivery.Tree);
        Assert.Equal(baseTree, (await fixture.GitAsync(directory, "rev-parse", "HEAD^{tree}")).StandardOutput.Trim());
        Assert.Equal(replacement, (await fixture.GitAsync(directory, "rev-parse", "refs/replace/" + delivery.Commit)).StandardOutput.Trim());
    }
    [Fact]
    public async Task ANewCommitAfterReviewAndWrongRunOrProjectCannotBeRegistered()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, review) = await PrepareAsync(fixture);
        var inspector = new GitTaskDeliveryInspector(fixture.Manager); var captured = await inspector.CaptureAsync(review.Snapshot, CancellationToken.None);
        var delivery = new TaskDelivery(task.Id, task.LastRunId!, task.Worktree!, captured.Commit, captured.Tree, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveTaskDeliveryAsync(project.Id, delivery with { SourceRunId = Guid.NewGuid().ToString() }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveTaskDeliveryAsync("other-project", delivery));
        await File.WriteAllTextAsync(Path.Combine(task.Worktree!.WorkingDirectory, "portal.txt"), "posterior\n");
        await fixture.GitAsync(task.Worktree.CheckoutDirectory, "add", "."); await fixture.GitAsync(task.Worktree.CheckoutDirectory, "commit", "-m", "posterior");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(store, fixture).RegisterAsync(project.Id, review, CancellationToken.None));
        Assert.Null((await fixture.CurrentAsync(store, project, task.Id)).Delivery);
    }
    [Theory]
    [InlineData("tracked")]
    [InlineData("new")]
    [InlineData("detached")]
    public async Task DirtyOrDetachedDestinationIsRefusedWithoutChangingOriginal(string kind)
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, review) = await PrepareAsync(fixture);
        var service = Service(store, fixture); await service.RegisterAsync(project.Id, review, CancellationToken.None);
        if (kind == "detached") await fixture.GitAsync(fixture.Repository, "checkout", "--detach");
        else await File.WriteAllTextAsync(Path.Combine(fixture.Project, kind == "new" ? "novo.txt" : "portal.txt"), "trabalho original preservado");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewIntegrationAsync(project.Id, task.Id, CancellationToken.None));
        Assert.Empty(await store.GetTaskIntegrationsAsync(project.Id));
        if (kind != "detached") Assert.Equal("trabalho original preservado", await File.ReadAllTextAsync(Path.Combine(fixture.Project, kind == "new" ? "novo.txt" : "portal.txt")));
    }
    [Fact]
    public async Task ChangedSourceOrDestinationInvalidatesTheIntegrationPreview()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, review) = await PrepareAsync(fixture);
        var service = Service(store, fixture); await service.RegisterAsync(project.Id, review, CancellationToken.None);
        var preview = await service.PreviewIntegrationAsync(project.Id, task.Id, CancellationToken.None);
        await fixture.GitAsync(fixture.Repository, "commit", "--allow-empty", "-m", "destino posterior");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReserveIntegrationAsync(project.Id, task.Id, preview, CancellationToken.None));
        await File.WriteAllTextAsync(Path.Combine(task.Worktree!.WorkingDirectory, "portal.txt"), "conteúdo posterior");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewIntegrationAsync(project.Id, task.Id, CancellationToken.None));
        Assert.Empty(await store.GetTaskIntegrationsAsync(project.Id));
    }
    [Fact]
    public async Task ReservationIsExclusiveAcrossStoresAndOldReleaseCannotUnlockANewerOne()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, review) = await PrepareAsync(fixture);
        var service = Service(store, fixture); var delivery = await service.RegisterAsync(project.Id, review, CancellationToken.None);
        var target = await service.PreviewIntegrationAsync(project.Id, task.Id, CancellationToken.None);
        var other = new SqliteWorkspaceStore(fixture.Database); await other.InitializeAsync();
        async Task<bool> Attempt(SqliteWorkspaceStore candidate)
        { try { await candidate.ReserveTaskIntegrationAsync(project.Id, delivery, target); return true; } catch (InvalidOperationException) { return false; } }
        var outcomes = await Task.WhenAll(Attempt(store), Attempt(other)); Assert.Single(outcomes, b => b);
        var first = (await store.GetTaskIntegrationsAsync(project.Id)).Single();
        await store.ReleaseTaskIntegrationAsync(first.Id);
        var second = await other.ReserveTaskIntegrationAsync(project.Id, delivery, target);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReleaseTaskIntegrationAsync(first.Id));
        Assert.Equal(TaskIntegrationState.Reserved, (await other.GetTaskIntegrationsAsync(project.Id)).Last().State);
        await other.RecoverInterruptedRunsAsync(); var recovered = (await store.GetTaskIntegrationsAsync(project.Id)).Last();
        Assert.Equal(second.Id, recovered.Id); Assert.Equal(TaskIntegrationState.NeedsAttention, recovered.State); Assert.NotNull(recovered.Error);
        Assert.Equal(delivery, (await fixture.CurrentAsync(store, project, task.Id)).Delivery);
        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt")));
    }
    [Fact]
    public async Task ReservationBlocksAttemptsInAnotherSubprojectAndWorktreePreparation()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, review) = await PrepareAsync(fixture, true);
        var service = Service(store, fixture); var delivery = await service.RegisterAsync(project.Id, review, CancellationToken.None);
        var target = await service.PreviewIntegrationAsync(project.Id, task.Id, CancellationToken.None);
        var batch = (await store.GetTaskBatchesAsync(project.Id)).Single(); var second = batch.Tasks[1];
        var plan = await new TaskWorktreeService(store, fixture.Manager).PreviewAsync(project.Id, second.Id, CancellationToken.None);
        var anotherPath = Path.Combine(fixture.Repository, "outro"); Directory.CreateDirectory(anotherPath); var another = await store.AddProjectAsync(anotherPath);
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), another.Id, "Outra sessão", ProviderKind.Claude, null, null, "Análise", "", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(conversation);
        var run = NewRun(second) with { ConversationId = conversation.Id };
        await store.BeginRunAsync(run);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveTaskIntegrationAsync(project.Id, delivery, target));
        await store.FinishRunAsync(run with { State = ChatRunState.Completed }, conversation, []);
        var reserved = await store.ReserveTaskIntegrationAsync(project.Id, delivery, target);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(run with { Id = Guid.NewGuid().ToString() }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(NewRun(second), second.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveTaskWorktreeAsync(project.Id, plan));
        Assert.Equal(0, (await fixture.CurrentAsync(store, project, second.Id)).Attempts);
        var parent = await store.AddProjectAsync(fixture.Root); var parentConversation = conversation with { Id = Guid.NewGuid().ToString(), ProjectId = parent.Id };
        await store.SaveConversationAsync(parentConversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(run with { Id = Guid.NewGuid().ToString(), ConversationId = parentConversation.Id }));
        var checkoutProject = await store.AddProjectAsync(task.Worktree!.WorkingDirectory); var checkoutConversation = conversation with { Id = Guid.NewGuid().ToString(), ProjectId = checkoutProject.Id };
        await store.SaveConversationAsync(checkoutConversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(run with { Id = Guid.NewGuid().ToString(), ConversationId = checkoutConversation.Id }));
        var unrelatedPath = Path.Combine(fixture.Root, "sem vínculo"); Directory.CreateDirectory(unrelatedPath);
        var unrelated = await store.AddProjectAsync(unrelatedPath); var free = conversation with { Id = Guid.NewGuid().ToString(), ProjectId = unrelated.Id };
        await store.SaveConversationAsync(free); await store.BeginRunAsync(run with { Id = Guid.NewGuid().ToString(), ConversationId = free.Id });
        await store.ReleaseTaskIntegrationAsync(reserved.Id, "SIMULAÇÃO: cancelamento preservando arquivos");
        await store.ReserveTaskWorktreeAsync(project.Id, plan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveTaskIntegrationAsync(project.Id, delivery, target));
    }
    [Fact]
    public async Task MigrationFromSchema5PreservesWorktreesTasksRunsAndFutureSchemaIsRefused()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, _) = await PrepareAsync(fixture);
        using (var connection = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TABLE task_integrations; DROP TABLE task_deliveries; PRAGMA user_version=5;"; command.ExecuteNonQuery(); }
        var migrated = new SqliteWorkspaceStore(fixture.Database); await migrated.InitializeAsync();
        var restored = await fixture.CurrentAsync(migrated, project, task.Id);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(task), System.Text.Json.JsonSerializer.Serialize(restored));
        Assert.Single(await migrated.GetRunsAsync(task.ConversationId));
        using (var connection = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=7;"; command.ExecuteNonQuery(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => migrated.InitializeAsync());
    }
    [Fact]
    public async Task LateCancelledCaptureCannotPersistADelivery()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, _, review) = await PrepareAsync(fixture);
        var inspector = new LateInspector(); var service = new TaskDeliveryService(store, inspector); using var stop = new CancellationTokenSource();
        var register = service.RegisterAsync(project.Id, review, stop.Token); await inspector.Started.Task; stop.Cancel(); inspector.Release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => register);
        Assert.Null((await fixture.CurrentAsync(store, project, review.Task.Id)).Delivery);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(store, fixture).RegisterAsync(project.Id, review, stop.Token));
    }
    private sealed class LateInspector : IGitTaskDeliveryInspector
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TaskDeliveryCommit> CaptureAsync(TaskDiffSnapshot snapshot, CancellationToken token)
        { Started.SetResult(); await Release.Task; return new(snapshot.HeadCommit, new('a', snapshot.HeadCommit.Length)); }
        public Task<TaskIntegrationTarget> InspectTargetAsync(TaskDelivery delivery, CancellationToken token) => throw new NotSupportedException();
    }
}
