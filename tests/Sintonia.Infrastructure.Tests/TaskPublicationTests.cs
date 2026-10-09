using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Infrastructure.Tests;

[Collection("Git environment")]
public sealed class TaskPublicationTests
{
    private static TaskDeliveryService Deliveries(SqliteWorkspaceStore store, TaskWorktreeTests.Fixture fixture) => new(store, new GitTaskDeliveryInspector(fixture.Manager));
    private static TaskPublicationService Service(SqliteWorkspaceStore store, TaskWorktreeTests.Fixture fixture, IGitTaskPublisher? publisher = null) =>
        new(store, Deliveries(store, fixture), new RepositoryIntegrationLock(), publisher ?? new GitTaskPublisher(fixture.Manager));
    internal static async Task<(SqliteWorkspaceStore Store, WorkspaceProject Project, TaskIntegrationValidation Validation)> SeedAsync(TaskWorktreeTests.Fixture fixture, bool ignoredCollision = false)
    {
        var (store, project, _, preparation) = await TaskIntegrationValidationTests.SeedAsync(fixture, ignoredCollision);
        var validations = new TaskIntegrationValidationService(store, Deliveries(store, fixture), new RepositoryIntegrationLock(), new GitTaskIntegrationValidationInspector(fixture.Manager), new PassingRunner());
        var validation = await validations.ValidateAsync(project.Id, await validations.PreviewAsync(project.Id, preparation.Reservation.Id, CancellationToken.None), CancellationToken.None);
        return (store, project, validation);
    }
    [Fact]
    public async Task PublicationAppliesExactTestedTreePreservesIgnoredAndUnlocksCorrectDependencyRevision()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, validation) = await SeedAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(validation.Reservation.Target.RepositoryDirectory, "config.local"), "ignorado preservado");
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, validation.Reservation.Id, CancellationToken.None);
        var published = await service.PublishAsync(project.Id, preview, CancellationToken.None);
        Assert.Equal(TaskPublicationState.Published, published.State); Assert.NotNull(published.Commit);
        Assert.Equal(published.Commit, (await fixture.GitAsync(fixture.Project, "rev-parse", "HEAD")).StandardOutput.Trim());
        Assert.Equal(validation.Preparation.Tree, (await fixture.GitAsync(fixture.Project, "rev-parse", "HEAD^{tree}")).StandardOutput.Trim());
        var parents = (await fixture.GitAsync(fixture.Project, "rev-list", "--parents", "-n", "1", "HEAD")).StandardOutput.Trim().Split(' ');
        Assert.Equal(new[] { published.Commit, validation.Reservation.Target.Commit, validation.Reservation.Delivery.Commit }, parents);
        Assert.Equal("ignorado preservado", await File.ReadAllTextAsync(Path.Combine(validation.Reservation.Target.RepositoryDirectory, "config.local")));
        var batch = (await store.GetTaskBatchesAsync(project.Id)).Single(); Assert.True(WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks));
        var prompt = WorkspaceTaskPolicy.BuildPrompt(batch, batch.Tasks[1], await store.GetRunsAsync(batch.Tasks[0].ConversationId)); Assert.Contains(published.Commit, prompt);
        await fixture.Manager.VerifyRevisionAsync(project.Directory, published.Commit, CancellationToken.None);
        await fixture.GitAsync(fixture.Project, "checkout", "--detach", validation.Reservation.Target.Commit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.VerifyRevisionAsync(project.Directory, published.Commit, CancellationToken.None));
        var reopened = new SqliteWorkspaceStore(fixture.Database); await reopened.InitializeAsync(); await reopened.RecoverInterruptedRunsAsync();
        Assert.Equal(JsonSerializer.Serialize(published), JsonSerializer.Serialize((await reopened.GetTaskPublicationsAsync(project.Id)).Single()));
        Assert.Equal(TaskPublicationState.Published, (await reopened.GetTaskBatchesAsync(project.Id)).Single().Tasks[0].Publication!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(project.Id, validation.Reservation.Id, CancellationToken.None));
    }
    [Fact]
    public async Task ChangedCriteriaAndHiddenLocalChangesRefusePublicationWithoutChangingBranch()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, validation) = await SeedAsync(fixture); var service = Service(store, fixture);
        var preview = await service.PreviewAsync(project.Id, validation.Reservation.Id, CancellationToken.None);
        await fixture.GitAsync(fixture.Project, "update-index", "--assume-unchanged", "portal.txt");
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, "portal.txt"), "mudança local oculta");
        var result = await service.PublishAsync(project.Id, preview, CancellationToken.None);
        Assert.Equal(TaskPublicationState.NeedsAttention, result.State); Assert.Null(result.Commit);
        Assert.Equal(validation.Reservation.Target.Commit, (await fixture.GitAsync(fixture.Project, "rev-parse", "HEAD")).StandardOutput.Trim());
        Assert.Equal("mudança local oculta", await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt")));
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, "portal.txt"), original);
        await store.SaveProjectValidationAsync(validation.Configuration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(project.Id, preview, CancellationToken.None));
        Assert.Single(await store.GetTaskPublicationsAsync(project.Id));
    }
    [Fact]
    public async Task CancellationBeforeCommitWaitsForVerifierAndDoesNotApply()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, validation) = await SeedAsync(fixture);
        var publisher = new ControlledPublisher(new GitTaskPublisher(fixture.Manager)) { BlockVerify = true };
        using var stop = new CancellationTokenSource(); var service = Service(store, fixture, publisher);
        var operation = service.PublishAsync(project.Id, validation, stop.Token); await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var other = new SqliteWorkspaceStore(fixture.Database); await other.InitializeAsync(); await other.RecoverInterruptedRunsAsync();
        Assert.Equal(TaskPublicationState.Publishing, (await other.GetTaskPublicationsAsync(project.Id)).Single().State);
        var reservationId = (await other.GetTaskPublicationsAsync(project.Id)).Single().Reservation.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ReleaseTaskIntegrationAsync(reservationId));
        stop.Cancel(); Assert.False(operation.IsCompleted); publisher.Release.SetResult(); var result = await operation;
        Assert.Equal(TaskPublicationState.Cancelled, result.State); Assert.Equal(0, publisher.Applies);
        Assert.Equal(validation.Reservation.Target.Commit, (await fixture.GitAsync(fixture.Project, "rev-parse", "HEAD")).StandardOutput.Trim());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyingFinishesDespiteLateCancellationAndFailurePreservesCommitWithoutUnlocking(bool failAfterApply)
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, validation) = await SeedAsync(fixture);
        var publisher = new ControlledPublisher(new GitTaskPublisher(fixture.Manager)) { BlockApply = true, FailAfterApply = failAfterApply };
        using var stop = new CancellationTokenSource(); var operation = Service(store, fixture, publisher).PublishAsync(project.Id, validation, stop.Token);
        await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(90)); var live = (await store.GetTaskPublicationsAsync(project.Id)).Single(); Assert.NotNull(live.Commit);
        stop.Cancel(); publisher.Release.SetResult(); var result = await operation;
        Assert.Equal(failAfterApply ? TaskPublicationState.NeedsAttention : TaskPublicationState.Published, result.State);
        Assert.Equal(result.Commit, (await fixture.GitAsync(fixture.Project, "rev-parse", "HEAD")).StandardOutput.Trim());
        var batch = (await store.GetTaskBatchesAsync(project.Id)).Single(); Assert.Equal(!failAfterApply, WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks));
    }
    [Fact]
    public async Task RecoveryPreservesCandidateAndNeverReplaysCheckoutAndSchema10Migrates()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, validation) = await SeedAsync(fixture);
        using (var connection = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TABLE task_publications; PRAGMA user_version=10;"; command.ExecuteNonQuery(); }
        await store.InitializeAsync(); Assert.Single(await store.GetTaskIntegrationValidationsAsync(project.Id));
        var reservation = await Deliveries(store, fixture).ReserveIntegrationAsync(project.Id, validation.Reservation.Delivery.TaskId, validation.Reservation.Target, CancellationToken.None);
        var intent = new TaskPublication(project.Id, reservation, validation, DateTimeOffset.UtcNow); await store.SaveTaskPublicationAsync(intent);
        intent = intent with { Commit = await new GitTaskPublisher(fixture.Manager).CreateCommitAsync(intent, CancellationToken.None) }; await store.RecordTaskPublicationCommitAsync(intent);
        await store.RecoverInterruptedRunsAsync(); var recovered = (await store.GetTaskPublicationsAsync(project.Id)).Single();
        Assert.Equal(TaskPublicationState.Interrupted, recovered.State); Assert.Equal(intent.Commit, recovered.Commit);
        Assert.Equal(validation.Reservation.Target.Commit, (await fixture.GitAsync(fixture.Project, "rev-parse", "HEAD")).StandardOutput.Trim());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FinishTaskPublicationAsync(intent with { State = TaskPublicationState.Published, FinishedAt = DateTimeOffset.UtcNow }));
        var batch = (await store.GetTaskBatchesAsync(project.Id)).Single(); Assert.False(WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks));
    }
    private sealed class PassingRunner : IValidationCommandRunner
    {
        public Task<ValidationCommandResult> RunAsync(ProjectValidationCommand command, int index, string directory, CancellationToken token)
        { var now = DateTimeOffset.UtcNow; return Task.FromResult(new ValidationCommandResult(index, ValidationState.Passed, 0, "SIMULAÇÃO", "", false, now, now)); }
    }
    [Fact]
    public async Task IncomingTrackedPathNeverOverwritesAnExistingIgnoredFile()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, validation) = await SeedAsync(fixture, ignoredCollision: true);
        var path = Path.Combine(validation.Reservation.Target.RepositoryDirectory, "config.local"); await File.WriteAllTextAsync(path, "configuração local preservada");
        var result = await Service(store, fixture).PublishAsync(project.Id, validation, CancellationToken.None);
        Assert.Equal(TaskPublicationState.NeedsAttention, result.State); Assert.NotNull(result.Commit);
        Assert.Equal("configuração local preservada", await File.ReadAllTextAsync(path));
        Assert.Equal(validation.Reservation.Target.Commit, (await fixture.GitAsync(fixture.Project, "rev-parse", "HEAD")).StandardOutput.Trim());
    }
    private sealed class ControlledPublisher(IGitTaskPublisher native) : IGitTaskPublisher
    {
        public bool BlockVerify { get; init; }
        public bool BlockApply { get; init; }
        public bool FailAfterApply { get; init; }
        public int Applies { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task VerifyAsync(TaskIntegrationValidation validation, CancellationToken token)
        {
            if (BlockVerify) { Started.TrySetResult(); await Release.Task; token.ThrowIfCancellationRequested(); }
            await native.VerifyAsync(validation, token);
        }
        public Task<string> CreateCommitAsync(TaskPublication intent, CancellationToken token) => native.CreateCommitAsync(intent, token);
        public async Task ApplyAsync(TaskPublication intent)
        {
            Applies++; if (BlockApply) { Started.TrySetResult(); await Release.Task; }
            await native.ApplyAsync(intent); if (FailAfterApply) throw new IOException("SIMULAÇÃO: falha após checkout");
        }
    }
}
