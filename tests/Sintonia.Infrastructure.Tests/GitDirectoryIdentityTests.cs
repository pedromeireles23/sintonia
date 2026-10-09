using Sintonia.Core;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Validation;
using Xunit.Abstractions;

namespace Sintonia.Infrastructure.Tests;

[Collection("Git environment")]
public sealed class GitDirectoryIdentityTests(ITestOutputHelper output)
{
    [Fact]
    public void WindowsExtendedPathMustIdentifyTheSameExistingDirectory()
    {
        using var fixture = new TaskWorktreeTests.Fixture();
        Assert.True(GitDirectoryIdentity.Same(fixture.Root, fixture.Root + Path.DirectorySeparatorChar));
        Assert.True(GitDirectoryIdentity.Same(fixture.Root, "\\\\?\\" + fixture.Root));
        var other = Path.Combine(fixture.Root, "outra pasta"); Directory.CreateDirectory(other);
        Assert.False(GitDirectoryIdentity.Same(other, "\\\\?\\" + fixture.Root));
    }

    [Fact]
    public void IdenticalNamesAndContentsCannotProveDirectoryIdentity()
    {
        using var fixture = new TaskWorktreeTests.Fixture();
        var first = Path.Combine(fixture.Root, "first", "checkout");
        var second = Path.Combine(fixture.Root, "second", "checkout");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "marker.txt"), "same"); File.WriteAllText(Path.Combine(second, "marker.txt"), "same");
        Assert.False(GitDirectoryIdentity.Same(first, second));
        Assert.False(GitDirectoryIdentity.Same(first, Path.Combine(fixture.Root, "missing")));
        Assert.False(GitDirectoryIdentity.Same(first, Path.Combine(first, "marker.txt")));
    }

    [Fact]
    public void MissingDirectoriesAndDevicesCannotProveAnAlias()
    {
        using var fixture = new TaskWorktreeTests.Fixture();
        var missing = Path.Combine(fixture.Root, "missing");
        Assert.False(GitDirectoryIdentity.Same(missing, "\\\\?\\" + missing));
        // NUL exists as a device, but cannot establish the identity of a directory.
        Assert.False(GitDirectoryIdentity.Same(fixture.Root, "\\\\?\\" + Path.GetPathRoot(fixture.Root) + "NUL"));
    }

    [Fact]
    public async Task ADirectoryJunctionIsRefusedEvenWhenItTargetsTheSameDirectory()
    {
        using var fixture = new TaskWorktreeTests.Fixture();
        var target = Path.Combine(fixture.Root, "target"); Directory.CreateDirectory(target);
        var junction = Path.Combine(fixture.Root, "junction");
        var result = await Sintonia.Infrastructure.Diagnostics.ProcessProbe.RunAsync(new("cmd.exe", [], "Junction fixture"),
            ["/d", "/c", "mklink", "/J", junction, target], fixture.Root, TimeSpan.FromSeconds(10));
        Assert.Equal(0, result.ExitCode);
        try
        {
            Assert.False(GitDirectoryIdentity.Same(junction, target));
            Assert.False(GitDirectoryIdentity.Same(target, junction));
            Assert.False(GitDirectoryIdentity.Same(junction, junction));
            Assert.Equal(target, new[] { junction, target }.Single(path => GitDirectoryIdentity.Same(path, target)));
        }
        finally { Directory.Delete(junction); } // Remove only this fixture's junction, never its target.
    }

    [Fact]
    public async Task DefaultIntegrationDirectorySupportsPreparationReopeningValidationAndPublication()
    {
        using var fixture = new TaskWorktreeTests.Fixture();
        var (store, project, task, review) = await TaskDeliveryTests.PrepareAsync(fixture);
        var deliveries = new TaskDeliveryService(store, new GitTaskDeliveryInspector(fixture.Manager));
        await deliveries.RegisterAsync(project.Id, review, CancellationToken.None);
        var native = new GitTaskIntegrationPreparer(fixture.Manager);
        var service = new TaskIntegrationPreparationService(store, deliveries, new RepositoryIntegrationLock(), native);
        var preparation = await service.PrepareAsync(project.Id, task.Id,
            await service.PreviewAsync(project.Id, task.Id, CancellationToken.None), CancellationToken.None);
        try
        {
            Assert.Equal(TaskIntegrationPreparationState.Combined, preparation.State);
            var defaultRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sintonia", "integrations"));
            Assert.Equal(Path.Combine(defaultRoot, Guid.Parse(preparation.Reservation.Id).ToString("N")), preparation.CheckoutDirectory);
            var gitRoot = (await fixture.GitAsync(preparation.CheckoutDirectory, "rev-parse", "--show-toplevel")).StandardOutput.Trim();
            Assert.True(GitDirectoryIdentity.Same(gitRoot, preparation.CheckoutDirectory));
            output.WriteLine("Registered directory: " + preparation.CheckoutDirectory);
            output.WriteLine("Git directory: " + gitRoot);
            output.WriteLine("Different path spelling: " + !string.Equals(Path.GetFullPath(gitRoot), preparation.CheckoutDirectory, StringComparison.OrdinalIgnoreCase));
            await Assert.ThrowsAsync<InvalidOperationException>(() => native.PrepareAsync(preparation with
                { State = TaskIntegrationPreparationState.Preparing, Tree = null }, CancellationToken.None));

            var reopened = new SqliteWorkspaceStore(fixture.Database); await reopened.InitializeAsync(); await reopened.RecoverInterruptedRunsAsync();
            var saved = Assert.Single(await reopened.GetTaskIntegrationPreparationsAsync(project.Id));
            Assert.Equal(preparation, saved);
            await new GitTaskIntegrationValidationInspector(fixture.Manager).VerifyAsync(saved, CancellationToken.None);
            var differentCommon = Path.Combine(fixture.Root, "different-repository.git"); Directory.CreateDirectory(differentCommon);
            var wrongRepository = saved with { Reservation = saved.Reservation with
            {
                Target = saved.Reservation.Target with { CommonGitDirectory = differentCommon },
                Delivery = saved.Reservation.Delivery with
                { Worktree = saved.Reservation.Delivery.Worktree with { CommonGitDirectory = differentCommon } }
            } };
            wrongRepository.ValidateDefinition();
            await Assert.ThrowsAsync<InvalidOperationException>(() => new GitTaskIntegrationValidationInspector(fixture.Manager).VerifyAsync(wrongRepository, CancellationToken.None));
            var reopenedDeliveries = new TaskDeliveryService(reopened, new GitTaskDeliveryInspector(fixture.Manager));
            await reopened.SaveProjectValidationAsync(new(project.Id, 0,
                [ProjectValidationTests.Command("validation-context") with { WorkingDirectory = "portal" }]));
            var validations = new TaskIntegrationValidationService(reopened, reopenedDeliveries, new RepositoryIntegrationLock(),
                new GitTaskIntegrationValidationInspector(fixture.Manager), new ValidationCommandRunner());
            var validated = await validations.ValidateAsync(project.Id,
                await validations.PreviewAsync(project.Id, saved.Reservation.Id, CancellationToken.None), CancellationToken.None);
            Assert.Equal(ValidationState.Passed, validated.State); Assert.Equal(0, Assert.Single(validated.Results!).ExitCode);
            var publications = new TaskPublicationService(reopened, reopenedDeliveries, new RepositoryIntegrationLock(), new GitTaskPublisher(fixture.Manager));
            var publication = await publications.PublishAsync(project.Id,
                await publications.PreviewAsync(project.Id, validated.Reservation.Id, CancellationToken.None), CancellationToken.None);
            Assert.Equal(TaskPublicationState.Published, publication.State);
            Assert.Equal(saved.Tree, (await fixture.GitAsync(fixture.Repository, "rev-parse", "HEAD^{tree}")).StandardOutput.Trim());
            Assert.Equal("entrega salva\n", (await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt"))).Replace("\r\n", "\n", StringComparison.Ordinal));
            var finalStore = new SqliteWorkspaceStore(fixture.Database); await finalStore.InitializeAsync(); await finalStore.RecoverInterruptedRunsAsync();
            Assert.Equal(TaskPublicationState.Published, Assert.Single(await finalStore.GetTaskPublicationsAsync(project.Id)).State);
            var batch = Assert.Single(await finalStore.GetTaskBatchesAsync(project.Id)); Assert.True(WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks));
            Assert.Single(await finalStore.GetRunsAsync(task.ConversationId));

            // Matching directory identities must not relax the expected worktree lock.
            await fixture.GitAsync(fixture.Repository, "worktree", "unlock", gitRoot);
            await fixture.GitAsync(fixture.Repository, "worktree", "lock", "--reason", "changed by fixture", gitRoot);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new GitTaskIntegrationValidationInspector(fixture.Manager).VerifyAsync(saved, CancellationToken.None));
        }
        finally
        {
            var expected = Path.GetFullPath(native.GetCheckoutDirectory(preparation.Reservation));
            if (Path.GetFullPath(preparation.CheckoutDirectory) != expected || Path.GetFileName(expected) != Guid.Parse(preparation.Reservation.Id).ToString("N"))
                throw new InvalidOperationException("Unexpected fixture cleanup directory.");
            GitTaskWorktreeManager.CheckPath(expected);
            if (Directory.Exists(expected)) Directory.Delete(expected, recursive: true);
        }
    }
}
