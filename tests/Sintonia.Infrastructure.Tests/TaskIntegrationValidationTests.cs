using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Validation;

namespace Sintonia.Infrastructure.Tests;

[Collection("Git environment")]
public sealed class TaskIntegrationValidationTests
{
    private static TaskDeliveryService Deliveries(SqliteWorkspaceStore store, TaskWorktreeTests.Fixture fixture) => new(store, new GitTaskDeliveryInspector(fixture.Manager));
    private static TaskIntegrationValidationService Service(SqliteWorkspaceStore store, TaskWorktreeTests.Fixture fixture, IValidationCommandRunner? runner = null) =>
        new(store, Deliveries(store, fixture), new RepositoryIntegrationLock(), new GitTaskIntegrationValidationInspector(fixture.Manager), runner ?? new ValidationCommandRunner());
    private static async Task<(SqliteWorkspaceStore Store, WorkspaceProject Project, WorkspaceTask Task, TaskIntegrationPreparation Preparation)> SeedAsync(TaskWorktreeTests.Fixture fixture)
    {
        var (store, project, task, review) = await TaskDeliveryTests.PrepareAsync(fixture);
        await Deliveries(store, fixture).RegisterAsync(project.Id, review, CancellationToken.None);
        task = await fixture.CurrentAsync(store, project, task.Id);
        var service = new TaskIntegrationPreparationService(store, Deliveries(store, fixture), new RepositoryIntegrationLock(),
            new GitTaskIntegrationPreparer(fixture.Manager, Path.Combine(fixture.Root, "combinações")));
        var preparation = await service.PrepareAsync(project.Id, task.Id, await service.PreviewAsync(project.Id, task.Id, CancellationToken.None), CancellationToken.None);
        await store.SaveProjectValidationAsync(new(project.Id, 0, [ProjectValidationTests.Command("validation-context", "ação literal", "$(literal)") with { WorkingDirectory = "portal" }]));
        return (store, project, task, preparation);
    }
    private static async Task<string> IndexAsync(TaskWorktreeTests.Fixture fixture, string directory) =>
        Path.Combine((await fixture.GitAsync(directory, "rev-parse", "--absolute-git-dir")).StandardOutput.Trim(), "index");

    [Fact]
    public async Task PassingValidationPinsCommandsTreeAndLogsWithoutPublishingOrChangingIndexes()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, preparation) = await SeedAsync(fixture);
        var index = await IndexAsync(fixture, preparation.CheckoutDirectory); var hash = SHA256.HashData(await File.ReadAllBytesAsync(index));
        var originalIndex = Path.Combine(task.Worktree!.CommonGitDirectory, "index"); var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(originalIndex));
        await File.WriteAllTextAsync(index + ".lock", "trava preservada");
        await File.WriteAllTextAsync(Path.Combine(preparation.CheckoutDirectory, "portal", "config.local"), "ignorado da combinação");
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, preparation.Reservation.Id, CancellationToken.None);
        var result = await service.ValidateAsync(project.Id, preview, CancellationToken.None);
        Assert.Equal(ValidationState.Passed, result.State); Assert.Equal(preparation.Tree, result.Preparation.Tree); Assert.Equal(1, result.Configuration.Revision);
        var command = Assert.Single(result.Results!); using var output = JsonDocument.Parse(command.StandardOutput);
        Assert.Equal("ação literal", output.RootElement.GetProperty("Arguments")[0].GetString()); Assert.Equal(0, command.ExitCode);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(index))); Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(originalIndex)));
        Assert.Equal("trava preservada", await File.ReadAllTextAsync(index + ".lock")); Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt")));
        Assert.Equal(preparation.Reservation.Target.Commit, (await fixture.GitAsync(fixture.Repository, "rev-parse", "HEAD")).StandardOutput.Trim());
        var reopened = new SqliteWorkspaceStore(fixture.Database); await reopened.InitializeAsync(); await reopened.RecoverInterruptedRunsAsync();
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize((await reopened.GetTaskIntegrationValidationsAsync(project.Id)).Single()));
        Assert.Equal(TaskIntegrationState.Released, (await reopened.GetTaskIntegrationsAsync(project.Id)).Last().State);
        var batch = (await reopened.GetTaskBatchesAsync(project.Id)).Single(); Assert.False(WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FinishTaskIntegrationValidationAsync(result));
        // A later configuration has its own revision; it does not rewrite the historical proof.
        await store.SaveProjectValidationAsync(preview.Configuration with { Commands = [] });
        Assert.Equal(1, (await service.GetAsync(project.Id)).Single().Configuration.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(project.Id, preparation.Reservation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task FailureStopsFollowingCommandsAndRetainsStderrAfterReopening()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, _, preparation) = await SeedAsync(fixture);
        var configuration = await store.GetProjectValidationAsync(project.Id);
        await store.SaveProjectValidationAsync(configuration with { Commands = [ProjectValidationTests.Command("fail"),
            ProjectValidationTests.Command("validation-write", "proibido.local", "não executar") with { Name = "Comando posterior" }] });
        var service = Service(store, fixture); var result = await service.ValidateAsync(project.Id,
            await service.PreviewAsync(project.Id, preparation.Reservation.Id, CancellationToken.None), CancellationToken.None);
        Assert.Equal(ValidationState.Failed, result.State); var command = Assert.Single(result.Results!);
        Assert.Equal(7, command.ExitCode); Assert.Contains("Falha de teste", command.StandardError); Assert.False(File.Exists(Path.Combine(preparation.CheckoutDirectory, "proibido.local")));
        Assert.Equal(ValidationState.Failed, (await new SqliteWorkspaceStore(fixture.Database).GetTaskIntegrationValidationsAsync(project.Id)).Single().State);
    }

    [Fact]
    public async Task TimeoutRecordsCommandAndLeavesCombinationAvailableForExplicitRetry()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, _, preparation) = await SeedAsync(fixture);
        var configuration = await store.GetProjectValidationAsync(project.Id);
        await store.SaveProjectValidationAsync(configuration with { Commands = [ProjectValidationTests.Command("child") with { TimeoutSeconds = 1 }] });
        var service = Service(store, fixture); var result = await service.ValidateAsync(project.Id,
            await service.PreviewAsync(project.Id, preparation.Reservation.Id, CancellationToken.None), CancellationToken.None);
        Assert.Equal(ValidationState.TimedOut, result.State); Assert.Contains("CHILD:", Assert.Single(result.Results!).StandardOutput);
        Assert.Equal(TaskIntegrationState.NeedsAttention, (await store.GetTaskIntegrationsAsync(project.Id)).Last().State);
        Assert.Equal(TaskIntegrationPreparationState.Combined, (await store.GetTaskIntegrationPreparationsAsync(project.Id)).Single().State);
        using var held = new RepositoryIntegrationLock().Acquire(preparation.Reservation.Target.CommonGitDirectory);
    }

    [Theory]
    [InlineData("portal/portal.txt")]
    [InlineData("novo.txt")]
    public async Task SuccessfulCommandThatChangesTrackedOrUntrackedFilesCannotValidateTheTree(string path)
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, _, preparation) = await SeedAsync(fixture);
        var configuration = await store.GetProjectValidationAsync(project.Id);
        await store.SaveProjectValidationAsync(configuration with { Commands = [ProjectValidationTests.Command("validation-write", path, "mudança pelo comando")] });
        var service = Service(store, fixture); var preview = await service.PreviewAsync(project.Id, preparation.Reservation.Id, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateAsync(project.Id, preview, CancellationToken.None));
        var result = (await service.GetAsync(project.Id)).Single(); Assert.Equal(ValidationState.NeedsAttention, result.State);
        Assert.Equal(ValidationState.Passed, Assert.Single(result.Results!).State); Assert.NotNull(result.Error);
        Assert.Equal("mudança pelo comando", await File.ReadAllTextAsync(Path.Combine(preparation.CheckoutDirectory, path)));
        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt")));
    }

    [Fact]
    public async Task ContentCheckBypassesAssumeUnchangedAndStatCacheWithoutWritingTheIndex()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (_, _, _, preparation) = await SeedAsync(fixture);
        await fixture.GitAsync(preparation.CheckoutDirectory, "update-index", "--assume-unchanged", "portal/portal.txt");
        var path = Path.Combine(preparation.CheckoutDirectory, "portal", "portal.txt"); var time = File.GetLastWriteTimeUtc(path);
        var bytes = await File.ReadAllBytesAsync(path); bytes[0] = (byte)'X'; await File.WriteAllBytesAsync(path, bytes); File.SetLastWriteTimeUtc(path, time);
        var status = await fixture.GitAsync(preparation.CheckoutDirectory, "status", "--porcelain=v2", "-z"); Assert.DoesNotContain(".M", status.StandardOutput);
        var index = await IndexAsync(fixture, preparation.CheckoutDirectory); var before = await File.ReadAllBytesAsync(index);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitTaskIntegrationValidationInspector(fixture.Manager).VerifyAsync(preparation, CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(index));
    }

    [Fact]
    public async Task StaleConfigurationDoesNotLaunchAndStaleContentCannotReportPassing()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, _, preparation) = await SeedAsync(fixture);
        var runner = new BlockingRunner(); var service = Service(store, fixture, runner);
        var preview = await service.PreviewAsync(project.Id, preparation.Reservation.Id, CancellationToken.None);
        await store.SaveProjectValidationAsync(preview.Configuration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateAsync(project.Id, preview, CancellationToken.None));
        Assert.Equal(0, runner.Calls); Assert.Empty(await service.GetAsync(project.Id));
        preview = await service.PreviewAsync(project.Id, preparation.Reservation.Id, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(preparation.CheckoutDirectory, "portal", "portal.txt"), "mudança posterior à prévia");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateAsync(project.Id, preview, CancellationToken.None));
        Assert.Equal(0, runner.Calls); Assert.Equal(ValidationState.NeedsAttention, (await service.GetAsync(project.Id)).Single().State);
    }

    [Fact]
    public async Task LiveValidationBlocksRelatedRunsAndRecoveryAndLateCancellationCannotPass()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, _, preparation) = await SeedAsync(fixture);
        var runner = new BlockingRunner(); var service = Service(store, fixture, runner); using var stop = new CancellationTokenSource();
        var preview = await service.PreviewAsync(project.Id, preparation.Reservation.Id, CancellationToken.None);
        var operation = service.ValidateAsync(project.Id, preview, stop.Token); await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var other = new SqliteWorkspaceStore(fixture.Database); await other.InitializeAsync(); await other.RecoverInterruptedRunsAsync();
        var live = (await other.GetTaskIntegrationValidationsAsync(project.Id)).Single(); Assert.Equal(ValidationState.Running, live.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ReleaseTaskIntegrationAsync(live.Reservation.Id));
        Assert.Throws<InvalidOperationException>(() => new RepositoryIntegrationLock().Acquire(live.Reservation.Target.CommonGitDirectory));
        var checkoutProject = await other.AddProjectAsync(preparation.CheckoutDirectory);
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), checkoutProject.Id, "Leitura", ProviderKind.Codex, null, null, "Análise", "", ConversationAccess.ReadOnly);
        await other.SaveConversationAsync(conversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.BeginRunAsync(new(Guid.NewGuid().ToString(), conversation.Id, "teste", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null)));
        stop.Cancel(); Assert.False(operation.IsCompleted); runner.Release.SetResult(); var result = await operation;
        Assert.Equal(ValidationState.Cancelled, result.State); Assert.Equal(ValidationState.Passed, Assert.Single(result.Results!).State);
        Assert.Equal(ValidationState.Cancelled, (await other.GetTaskIntegrationValidationsAsync(project.Id)).Single().State);
        using var available = new RepositoryIntegrationLock().Acquire(preparation.Reservation.Target.CommonGitDirectory);
    }

    [Fact]
    public async Task ConfigurationChangedDuringCommandsCannotProducePassingEvidenceForCurrentCriteria()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, _, preparation) = await SeedAsync(fixture);
        var runner = new BlockingRunner(); var service = Service(store, fixture, runner);
        var preview = await service.PreviewAsync(project.Id, preparation.Reservation.Id, CancellationToken.None);
        var operation = service.ValidateAsync(project.Id, preview, CancellationToken.None); await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await store.SaveProjectValidationAsync(preview.Configuration with { Commands = [ProjectValidationTests.Command("fail")] });
        runner.Release.SetResult(); await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        var result = (await service.GetAsync(project.Id)).Single(); Assert.Equal(ValidationState.NeedsAttention, result.State);
        Assert.Equal(1, result.Configuration.Revision); Assert.Equal(2, (await store.GetProjectValidationAsync(project.Id)).Revision);
        Assert.Equal(ValidationState.Passed, Assert.Single(result.Results!).State);
    }

    [Fact]
    public async Task RecoveryMarksAbandonedIntentInterruptedAndCannotOverwriteOrReplayIt()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, _, preparation) = await SeedAsync(fixture);
        var reservation = await Deliveries(store, fixture).ReserveIntegrationAsync(project.Id, preparation.Reservation.Delivery.TaskId, preparation.Reservation.Target, CancellationToken.None);
        var intent = new TaskIntegrationValidation(project.Id, reservation, preparation, await store.GetProjectValidationAsync(project.Id), DateTimeOffset.UtcNow);
        await store.SaveTaskIntegrationValidationAsync(intent);
        var other = new SqliteWorkspaceStore(fixture.Database); await other.InitializeAsync(); await other.RecoverInterruptedRunsAsync();
        var recovered = (await other.GetTaskIntegrationValidationsAsync(project.Id)).Single(); Assert.Equal(ValidationState.Interrupted, recovered.State); Assert.NotNull(recovered.FinishedAt);
        Assert.Null(recovered.Results); Assert.Equal(TaskIntegrationState.NeedsAttention, (await other.GetTaskIntegrationsAsync(project.Id)).Last().State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FinishTaskIntegrationValidationAsync(intent with { State = ValidationState.Cancelled, FinishedAt = DateTimeOffset.UtcNow, Error = "obsoleto" }));
        Assert.Equal(preparation.Tree, recovered.Preparation.Tree); Assert.Equal(1, recovered.Configuration.Revision);
        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt")));
    }

    [Fact]
    public async Task InternalCancellationIsAttentionWithoutClaimingUserCancellationOrRunningCommands()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, _, preparation) = await SeedAsync(fixture);
        var runner = new BlockingRunner(); var service = new TaskIntegrationValidationService(store, Deliveries(store, fixture),
            new RepositoryIntegrationLock(), new InterruptedInspector(), runner);
        var preview = new TaskIntegrationValidationPreview(preparation, await store.GetProjectValidationAsync(project.Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ValidateAsync(project.Id, preview, CancellationToken.None));
        var result = (await service.GetAsync(project.Id)).Single(); Assert.Equal(ValidationState.NeedsAttention, result.State);
        Assert.Equal(0, runner.Calls); Assert.Empty(result.Results!); Assert.Contains("conferência", result.Error);
    }

    [Fact]
    public async Task MigrationFromSchema8PreservesConfigurationPreparationDeliveryAndHistory()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var (store, project, task, preparation) = await SeedAsync(fixture);
        var configuration = await store.GetProjectValidationAsync(project.Id);
        using (var connection = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TABLE task_integration_validations; PRAGMA user_version=8;"; command.ExecuteNonQuery(); }
        var migrated = new SqliteWorkspaceStore(fixture.Database); await migrated.InitializeAsync();
        Assert.Equal(JsonSerializer.Serialize(configuration), JsonSerializer.Serialize(await migrated.GetProjectValidationAsync(project.Id)));
        Assert.Equal(JsonSerializer.Serialize(preparation), JsonSerializer.Serialize((await migrated.GetTaskIntegrationPreparationsAsync(project.Id)).Single()));
        Assert.Equal(task.Delivery, (await fixture.CurrentAsync(migrated, project, task.Id)).Delivery);
        Assert.Single(await migrated.GetRunsAsync(task.ConversationId)); Assert.Empty(await migrated.GetTaskIntegrationValidationsAsync(project.Id));
    }

    private sealed class BlockingRunner : IValidationCommandRunner
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ValidationCommandResult> RunAsync(ProjectValidationCommand command, int index, string checkout, CancellationToken token)
        {
            Calls++; var started = DateTimeOffset.UtcNow; Started.SetResult(); await Release.Task;
            return new(index, ValidationState.Passed, 0, "resposta tardia simulada", "", false, started, DateTimeOffset.UtcNow);
        }
    }
    private sealed class InterruptedInspector : IGitTaskIntegrationValidationInspector
    {
        public Task VerifyAsync(TaskIntegrationPreparation preparation, CancellationToken token) => Task.FromException(new OperationCanceledException("Prazo interno da fixture"));
    }
}
