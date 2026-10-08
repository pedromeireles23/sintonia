using System.Security.Cryptography;
using Sintonia.Core;
using Sintonia.Infrastructure.Git;

namespace Sintonia.Infrastructure.Tests;

[Collection("Git environment")]
public sealed class TaskDiffTests
{
    private static async Task<TaskWorktree> PrepareAsync(TaskWorktreeTests.Fixture fixture)
    {
        await fixture.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "remove.txt"), "remover\n");
        await fixture.GitAsync(fixture.Repository, "add", "remove.txt"); await fixture.GitAsync(fixture.Repository, "commit", "-m", "diff fixture");
        var project = new WorkspaceProject("project", "Portal", fixture.Project);
        var plan = await fixture.Manager.PlanAsync(project, Guid.NewGuid().ToString(), CancellationToken.None);
        await fixture.Manager.PrepareAsync(plan, CancellationToken.None); return plan with { State = TaskWorktreeState.Ready };
    }

    [Fact]
    public async Task ListsCommittedStagedUnstagedRenamedDeletedAndNewFilesWithoutWriting()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var worktree = await PrepareAsync(fixture); var reader = new GitTaskDiffReader(fixture.Manager);
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "committed.txt"), "commit salvo\n");
        await fixture.GitAsync(worktree.CheckoutDirectory, "add", "portal/committed.txt"); await fixture.GitAsync(worktree.CheckoutDirectory, "commit", "-m", "delivery");
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "portal.txt"), "preparado\n");
        await fixture.GitAsync(worktree.CheckoutDirectory, "add", "portal/portal.txt");
        await File.AppendAllTextAsync(Path.Combine(worktree.WorkingDirectory, "portal.txt"), "na pasta\n");
        await fixture.GitAsync(worktree.CheckoutDirectory, "mv", "portal/ação com espaços.txt", "portal/nome novo ação.txt");
        File.Delete(Path.Combine(worktree.CheckoutDirectory, "remove.txt"));
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "novo $(literal).txt"), "arquivo novo\n");
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "ignore.local"), "ignorado");
        var gitDir = (await fixture.GitAsync(worktree.CheckoutDirectory, "rev-parse", "--absolute-git-dir")).StandardOutput.Trim();
        var index = Path.Combine(gitDir, "index"); var hash = SHA256.HashData(await File.ReadAllBytesAsync(index)); var modified = File.GetLastWriteTimeUtc(index);
        await File.WriteAllTextAsync(index + ".lock", "lock externo");
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt"));
        var snapshot = await reader.ScanAsync(worktree, CancellationToken.None);
        Assert.NotEqual(worktree.BaseCommit, snapshot.HeadCommit);
        Assert.Equal('A', snapshot.Files.Single(f => f.Path.EndsWith("committed.txt")).BaseStatus);
        var portal = snapshot.Files.Single(f => f.Path == "portal/portal.txt"); Assert.Equal('M', portal.LocalChange!.IndexStatus); Assert.Equal('M', portal.LocalChange.WorktreeStatus);
        var renamed = snapshot.Files.Single(f => f.BaseStatus == 'R'); Assert.Equal("portal/ação com espaços.txt", renamed.OriginalPath);
        Assert.Contains(snapshot.Files, f => f.Path == "remove.txt" && f.BaseStatus == 'D');
        Assert.DoesNotContain(snapshot.Files, f => f.Path.EndsWith("ignore.local"));
        var combined = await reader.ReadAsync(snapshot, portal, TaskDiffView.SinceBase, CancellationToken.None);
        Assert.Contains("-base", combined.Text); Assert.Contains("+preparado", combined.Text); Assert.Contains("+na pasta", combined.Text);
        var staged = await reader.ReadAsync(snapshot, portal, TaskDiffView.Index, CancellationToken.None);
        Assert.Contains("+preparado", staged.Text); Assert.DoesNotContain("+na pasta", staged.Text);
        var local = await reader.ReadAsync(snapshot, portal, TaskDiffView.WorkingTree, CancellationToken.None);
        Assert.Contains("+na pasta", local.Text); Assert.DoesNotContain("-base", local.Text);
        var rename = await reader.ReadAsync(snapshot, renamed, TaskDiffView.SinceBase, CancellationToken.None); Assert.Contains("rename from", rename.Text);
        var added = snapshot.Files.Single(f => f.HasUntrackedContent);
        Assert.Equal("arquivo novo\n", (await reader.ReadAsync(snapshot, added, TaskDiffView.SinceBase, CancellationToken.None)).Text);
        Assert.Equal(TaskDiffContentState.NoChanges, (await reader.ReadAsync(snapshot, added, TaskDiffView.Index, CancellationToken.None)).State);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(index))); Assert.Equal(modified, File.GetLastWriteTimeUtc(index));
        Assert.Equal("lock externo", await File.ReadAllTextAsync(index + ".lock")); Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(fixture.Project, "portal.txt")));
    }

    [Fact]
    public async Task StagedDeletionAndUntrackedReplacementKeepBothStates()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var worktree = await PrepareAsync(fixture); var reader = new GitTaskDiffReader(fixture.Manager);
        await fixture.GitAsync(worktree.CheckoutDirectory, "rm", "--cached", "portal/portal.txt");
        var snapshot = await reader.ScanAsync(worktree, CancellationToken.None); var file = snapshot.Files.Single(f => f.Path == "portal/portal.txt");
        Assert.True(file.HasUntrackedContent); Assert.Equal('D', file.LocalChange!.IndexStatus);
        Assert.Contains("deleted file", (await reader.ReadAsync(snapshot, file, TaskDiffView.Index, CancellationToken.None)).Text);
        Assert.Equal("base", (await reader.ReadAsync(snapshot, file, TaskDiffView.WorkingTree, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task CleanCheckoutAndBinaryOrLargeContentHaveExplicitResults()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var worktree = await PrepareAsync(fixture); var reader = new GitTaskDiffReader(fixture.Manager);
        Assert.Empty((await reader.ScanAsync(worktree, CancellationToken.None)).Files);
        await File.WriteAllBytesAsync(Path.Combine(worktree.WorkingDirectory, "binary.bin"), [0, 1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(worktree.WorkingDirectory, "legacy.txt"), [0xff, 0xfe]);
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "large.txt"), new string('a', 65537));
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "portal.txt"), new string('a', 70000));
        var snapshot = await reader.ScanAsync(worktree, CancellationToken.None);
        foreach (var name in new[] { "binary.bin", "legacy.txt" })
            Assert.Equal(TaskDiffContentState.Binary, (await reader.ReadAsync(snapshot, snapshot.Files.Single(f => f.Path.EndsWith(name)), TaskDiffView.SinceBase, CancellationToken.None)).State);
        foreach (var name in new[] { "large.txt", "portal.txt" })
        {
            var content = await reader.ReadAsync(snapshot, snapshot.Files.Single(f => f.Path.EndsWith(name)), TaskDiffView.SinceBase, CancellationToken.None);
            Assert.Equal(TaskDiffContentState.TooLarge, content.State); Assert.Empty(content.Text);
        }
        await File.WriteAllBytesAsync(Path.Combine(worktree.WorkingDirectory, "portal.txt"), [0, 1, 2]);
        snapshot = await reader.ScanAsync(worktree, CancellationToken.None);
        Assert.Equal(TaskDiffContentState.Binary, (await reader.ReadAsync(snapshot, snapshot.Files.Single(f => f.Path.EndsWith("portal.txt")), TaskDiffView.SinceBase, CancellationToken.None)).State);
    }

    [Fact]
    public async Task SnapshotRejectsChangedHeadStatusBranchAndUnlistedPaths()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var worktree = await PrepareAsync(fixture); var reader = new GitTaskDiffReader(fixture.Manager);
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "portal.txt"), "mudança");
        var snapshot = await reader.ScanAsync(worktree, CancellationToken.None); var file = snapshot.Files.Single();
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(snapshot, file with { Path = "../../outside" }, TaskDiffView.SinceBase, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(snapshot, file, (TaskDiffView)100, CancellationToken.None));
        await fixture.GitAsync(worktree.CheckoutDirectory, "add", "portal/portal.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(snapshot, file, TaskDiffView.SinceBase, CancellationToken.None));
        snapshot = await reader.ScanAsync(worktree, CancellationToken.None); file = snapshot.Files.Single();
        await fixture.GitAsync(worktree.CheckoutDirectory, "commit", "-m", "change");
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(snapshot, file, TaskDiffView.SinceBase, CancellationToken.None));
        await fixture.GitAsync(worktree.CheckoutDirectory, "checkout", "--detach");
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ScanAsync(worktree, CancellationToken.None));
    }

    [Fact]
    public async Task ExternalDiffTextconvCleanFiltersAndFsmonitorNeverExecute()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var worktree = await PrepareAsync(fixture); var reader = new GitTaskDiffReader(fixture.Manager);
        var marker = Path.Combine(fixture.Root, "external-ran"); var script = Path.Combine(fixture.Root, "external.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho ran > '" + marker.Replace('\\', '/') + "'\ncat\n");
        var command = "\"" + script.Replace('\\', '/') + "\"";
        foreach (var key in new[] { "diff.external", "diff.fixture.command", "diff.fixture.textconv", "filter.fixture.clean", "filter.fixture.process", "core.fsmonitor" })
            await fixture.GitAsync(worktree.CheckoutDirectory, "config", key, command);
        await File.WriteAllTextAsync(Path.Combine(worktree.CheckoutDirectory, ".gitattributes"), "portal/* diff=fixture filter=fixture\n");
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "portal.txt"), "mudança");
        var snapshot = await reader.ScanAsync(worktree, CancellationToken.None);
        Assert.Contains("+mudança", (await reader.ReadAsync(snapshot, snapshot.Files.Single(f => f.Path == "portal/portal.txt"), TaskDiffView.SinceBase, CancellationToken.None)).Text);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ConflictsAreVisibleAndInspectionDoesNotResolveThem()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var worktree = await PrepareAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, "portal.txt"), "original branch\n");
        await fixture.GitAsync(fixture.Repository, "add", "portal/portal.txt"); await fixture.GitAsync(fixture.Repository, "commit", "-m", "original change");
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "portal.txt"), "task branch\n");
        await fixture.GitAsync(worktree.CheckoutDirectory, "add", "portal/portal.txt"); await fixture.GitAsync(worktree.CheckoutDirectory, "commit", "-m", "task change");
        // Merge is fixture setup only, never part of the reader.
        var merge = await Sintonia.Infrastructure.Diagnostics.ProcessProbe.RunAsync(new("git.exe", [], "Teste"),
            ["-c", "core.hooksPath=NUL", "merge", "main"], worktree.CheckoutDirectory, TimeSpan.FromSeconds(10)); Assert.NotEqual(0, merge.ExitCode);
        var reader = new GitTaskDiffReader(fixture.Manager); var snapshot = await reader.ScanAsync(worktree, CancellationToken.None);
        var file = snapshot.Files.Single(); Assert.True(file.LocalChange!.IsConflict);
        Assert.Contains("<<<<<<<", (await reader.ReadAsync(snapshot, file, TaskDiffView.SinceBase, CancellationToken.None)).Text);
        Assert.Contains("<<<<<<<", await File.ReadAllTextAsync(Path.Combine(worktree.WorkingDirectory, "portal.txt")));
    }

    [Fact]
    public async Task TaskServiceRejectsLiveOrChangedTaskAndWrongProjectWithoutModelCalls()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); await fixture.InitializeAsync(); var (store, project, batch) = await fixture.SeedAsync(); var task = batch.Tasks[0];
        var prep = new TaskWorktreeService(store, fixture.Manager); await prep.PrepareAsync(project.Id, await prep.PreviewAsync(project.Id, task.Id, CancellationToken.None), CancellationToken.None);
        var service = new TaskDiffService(store, new GitTaskDiffReader(fixture.Manager)); var review = await service.ScanAsync(project.Id, task.Id, CancellationToken.None);
        Assert.Empty(review.Snapshot.Files);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync(project.Id, batch.Tasks[1].Id, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync("wrong", task.Id, CancellationToken.None));
        var run = new ChatRun(Guid.NewGuid().ToString(), task.ConversationId, "pedido", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run, task.Id, review.Task.Worktree);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync(project.Id, task.Id, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadAsync(project.Id, review, new("portal/portal.txt", null, 'M', null), TaskDiffView.SinceBase, CancellationToken.None));
    }

    [Fact]
    public async Task IndexRenameUsesItsOwnOriginAfterEarlierCommittedRename()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var worktree = await PrepareAsync(fixture);
        await fixture.GitAsync(worktree.CheckoutDirectory, "mv", "portal/ação com espaços.txt", "portal/intermediate.txt");
        await fixture.GitAsync(worktree.CheckoutDirectory, "commit", "-m", "first rename");
        await fixture.GitAsync(worktree.CheckoutDirectory, "mv", "portal/intermediate.txt", "portal/final.txt");
        var reader = new GitTaskDiffReader(fixture.Manager); var snapshot = await reader.ScanAsync(worktree, CancellationToken.None);
        var file = snapshot.Files.Single(); Assert.Equal("portal/ação com espaços.txt", file.OriginalPath); Assert.Equal("portal/intermediate.txt", file.LocalChange!.OriginalPath);
        Assert.Contains("rename from portal/intermediate.txt", (await reader.ReadAsync(snapshot, file, TaskDiffView.Index, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task TaskChangeDuringScanAndLateCancellationDiscardResults()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); await fixture.InitializeAsync(); var (store, project, batch) = await fixture.SeedAsync(); var task = batch.Tasks[0];
        var prep = new TaskWorktreeService(store, fixture.Manager); await prep.PrepareAsync(project.Id, await prep.PreviewAsync(project.Id, task.Id, CancellationToken.None), CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var late = new TaskDiffService(store, new MutatingReader(() => { stop.Cancel(); return Task.CompletedTask; }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late.ScanAsync(project.Id, task.Id, stop.Token));
        var changing = new TaskDiffService(store, new MutatingReader(async () =>
        {
            var current = await fixture.CurrentAsync(store, project, task.Id);
            await store.BeginRunAsync(new(Guid.NewGuid().ToString(), task.ConversationId, "pedido", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null), task.Id, current.Worktree);
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => changing.ScanAsync(project.Id, task.Id, CancellationToken.None));
    }

    [Fact]
    public async Task LiteralTrackedPathsDoNotSelectOtherFiles()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var worktree = await PrepareAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "file[abc].txt"), "literal base\n");
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "filea.txt"), "other base\n");
        await fixture.GitAsync(worktree.CheckoutDirectory, "add", "."); await fixture.GitAsync(worktree.CheckoutDirectory, "commit", "-m", "names");
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "file[abc].txt"), "literal change\n");
        await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "filea.txt"), "other change\n");
        var reader = new GitTaskDiffReader(fixture.Manager); var snapshot = await reader.ScanAsync(worktree, CancellationToken.None);
        var patch = await reader.ReadAsync(snapshot, snapshot.Files.Single(f => f.Path.EndsWith("file[abc].txt")), TaskDiffView.WorkingTree, CancellationToken.None);
        Assert.Contains("+literal change", patch.Text); Assert.DoesNotContain("other change", patch.Text);
    }

    private sealed class MutatingReader(Func<Task> change) : IGitTaskDiffReader
    {
        public async Task<TaskDiffSnapshot> ScanAsync(TaskWorktree worktree, CancellationToken cancellationToken)
        { await change(); return new(worktree, worktree.BaseCommit, "", [], DateTimeOffset.UtcNow); }
        public Task<TaskFileDiff> ReadAsync(TaskDiffSnapshot snapshot, TaskDiffFile file, TaskDiffView view, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CancellationTimeoutAndMissingGitNeverYieldSuccessfulScan()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var worktree = await PrepareAsync(fixture);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitTaskDiffReader(fixture.Manager).ScanAsync(worktree, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitTaskDiffReader(fixture.Manager, TimeSpan.FromMilliseconds(1)).ScanAsync(worktree, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitTaskDiffReader(new(fixture.Managed, "")).ScanAsync(worktree, CancellationToken.None));
    }

    [Theory]
    [InlineData("M\0missing")]
    [InlineData("R100\0old\0")]
    [InlineData("R101\0old\0new\0")]
    [InlineData("X\0unknown\0")]
    [InlineData("M\0../outside\0")]
    [InlineData("M\0C:/absolute\0")]
    [InlineData("M\0a\0D\0a\0")]
    public void NameParserRejectsPartialUnsupportedAndUnsafeRecords(string output) => Assert.Throws<FormatException>(() => GitDiffNameParser.Parse(output));

    [Fact]
    public void NameParserPreservesLiteralPathsAndRejectsExcessiveLists()
    {
        var files = GitDiffNameParser.Parse("M\0docs/ação com $(literal).txt\0R100\0old name\0new name\0");
        Assert.Equal("docs/ação com $(literal).txt", files[0].Path); Assert.Equal("old name", files[1].OriginalPath);
        Assert.Throws<FormatException>(() => GitDiffNameParser.Parse(string.Concat(Enumerable.Range(0, 1001).Select(i => $"A\0file{i}\0"))));
    }
}
