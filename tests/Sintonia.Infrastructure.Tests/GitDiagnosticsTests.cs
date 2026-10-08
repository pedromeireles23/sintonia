using System.Security.Cryptography;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Git;

namespace Sintonia.Infrastructure.Tests;

[CollectionDefinition("Git environment", DisableParallelization = true)]
public sealed class GitEnvironmentCollection;

[Collection("Git environment")]
public sealed class GitDiagnosticsTests
{
    private static readonly string Hash = new('a', 40);
    private static string Headers => $"# branch.oid {Hash}\0# branch.head main\0";
    private static ExecutableLaunch Fixture => new(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), [], "Teste");

    [Fact]
    public void ParserPreservesLiteralNamesRenameConflictAndSubmoduleState()
    {
        var text = Headers + "# future.header ignored\0# branch.upstream origin/main\0# branch.ab +2 -3\0"
            + $"1 MM N... 100644 100644 100644 {Hash} {Hash} docs/ação com espaços.txt\0"
            + $"2 R. N... 100644 100644 100644 {Hash} {Hash} R100 novo nome\0antigo nome\0"
            + $"u UU N... 100644 100644 100644 100644 {Hash} {Hash} {Hash} conflito\0"
            + $"1 .M S.MU 160000 160000 160000 {Hash} {Hash} módulo\0? linha\n\t`$(texto)` \0";
        var result = GitStatusParser.Parse("project", "root", text);
        Assert.Equal("main", result.Branch); Assert.Equal("origin/main", result.Upstream); Assert.Equal(2, result.Ahead); Assert.Equal(3, result.Behind);
        Assert.Equal(5, result.Changes.Count); Assert.Equal("docs/ação com espaços.txt", result.Changes[0].Path);
        Assert.Equal('M', result.Changes[0].IndexStatus); Assert.Equal('M', result.Changes[0].WorktreeStatus);
        Assert.Equal("antigo nome", result.Changes[1].OriginalPath); Assert.True(result.Changes[2].IsConflict);
        Assert.Equal("S.MU", result.Changes[3].SubmoduleState); Assert.Equal("linha\n\t`$(texto)` ", result.Changes[4].Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# branch.oid invalid\0# branch.head main\0")]
    [InlineData("# branch.oid (initial)\0")]
    [InlineData("# branch.oid (initial)\0# branch.head main\0# branch.head other\0")]
    [InlineData("# branch.oid (initial)\0# branch.head main\0? incomplete")]
    [InlineData("# branch.oid (initial)\0# branch.head main\0? \0")]
    [InlineData("# branch.oid (initial)\0# branch.head main\0unknown data\0")]
    [InlineData("# branch.oid (initial)\0# branch.head main\0# branch.ab +oops -1\0")]
    public void ParserRejectsPartialOrUnsupportedStatus(string text) => Assert.Throws<FormatException>(() => GitStatusParser.Parse("project", "root", text));

    [Fact]
    public void IncompleteRenameNeverBecomesCleanOrPartiallySuccessful()
    {
        var text = Headers + $"2 R. N... 100644 100644 100644 {Hash} {Hash} R100 new\0";
        Assert.Throws<FormatException>(() => GitStatusParser.Parse("project", "root", text));
    }

    [Fact]
    public async Task RealGitShowsChangesFromSubdirectoryWithoutWritingIndexOrRunningFsmonitor()
    {
        using var repo = new Repository(); await repo.InitializeAsync();
        Directory.CreateDirectory(Path.Combine(repo.Path, "docs"));
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "docs", "ação inicial.txt"), "base\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "remove.txt"), "remove\n");
        await repo.RunAsync("add", "-f", "."); await repo.RunAsync("commit", "-m", "fixture");
        await repo.RunAsync("mv", "docs/ação inicial.txt", "docs/novo nome.txt");
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "docs", "novo nome.txt"), "modified\n");
        File.Delete(Path.Combine(repo.Path, "remove.txt"));
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "arquivo novo.txt"), "untracked\n");
        await repo.RunAsync("config", "core.fsmonitor", "a-command-that-must-never-run");
        var index = Path.Combine(repo.Path, ".git", "index"); var before = SHA256.HashData(await File.ReadAllBytesAsync(index));
        var modifiedAt = File.GetLastWriteTimeUtc(index);
        var lockFile = index + ".lock"; await File.WriteAllTextAsync(lockFile, "lock owned by test");
        var result = await new GitRepositoryInspector().InspectAsync(Path.Combine(repo.Path, "docs"));
        Assert.Equal(GitDiagnosticState.Available, result.State); Assert.Equal("main", result.Branch); Assert.NotNull(result.HeadCommit);
        Assert.Equal(System.IO.Path.GetFullPath(repo.Path), System.IO.Path.GetFullPath(result.RootDirectory!));
        var rename = Assert.Single(result.Changes, c => c.OriginalPath is not null);
        Assert.Equal("docs/ação inicial.txt", rename.OriginalPath); Assert.Equal("docs/novo nome.txt", rename.Path); Assert.Equal('M', rename.WorktreeStatus);
        Assert.Contains(result.Changes, c => c.Path == "remove.txt" && c.WorktreeStatus == 'D');
        Assert.Contains(result.Changes, c => c.Path == "arquivo novo.txt" && c.IsUntracked);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(index))); Assert.Equal(modifiedAt, File.GetLastWriteTimeUtc(index));
        Assert.Equal("lock owned by test", await File.ReadAllTextAsync(lockFile));
    }

    [Fact]
    public async Task UnbornDetachedAndLinkedWorktreeAreDistinguished()
    {
        using var repo = new Repository(); await repo.InitializeAsync(); var inspector = new GitRepositoryInspector();
        var unborn = await inspector.InspectAsync(repo.Path); Assert.True(unborn.IsUnborn); Assert.Null(unborn.HeadCommit); Assert.Equal("main", unborn.Branch);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "sample.txt"), "base\n");
        await repo.RunAsync("add", "-f", "."); await repo.RunAsync("commit", "-m", "fixture");
        await repo.RunAsync("checkout", "--detach"); var detached = await inspector.InspectAsync(repo.Path);
        Assert.True(detached.IsDetached); Assert.False(detached.IsUnborn); Assert.Null(detached.Branch); Assert.NotNull(detached.HeadCommit);
        var worktree = Path.Combine(repo.Path, "linked worktree");
        await repo.RunAsync("worktree", "add", "-b", "codex/linked", worktree);
        var linked = await inspector.InspectAsync(worktree); Assert.Equal(GitDiagnosticState.Available, linked.State);
        Assert.Equal("codex/linked", linked.Branch); Assert.Equal(System.IO.Path.GetFullPath(worktree), System.IO.Path.GetFullPath(linked.RootDirectory!));
    }

    [Fact]
    public async Task ConflictIsVisibleAndNotReportedAsSuccessfulIntegration()
    {
        using var repo = new Repository(); await repo.InitializeAsync();
        var file = Path.Combine(repo.Path, "conflict.txt"); await File.WriteAllTextAsync(file, "base\n");
        await repo.RunAsync("add", "-f", "."); await repo.RunAsync("commit", "-m", "base");
        await repo.RunAsync("checkout", "-b", "codex/change"); await File.WriteAllTextAsync(file, "branch\n");
        await repo.RunAsync("add", "-f", "."); await repo.RunAsync("commit", "-m", "branch");
        await repo.RunAsync("checkout", "main"); await File.WriteAllTextAsync(file, "main\n");
        await repo.RunAsync("add", "-f", "."); await repo.RunAsync("commit", "-m", "main");
        var merge = await repo.ExecuteAsync("merge", "codex/change"); Assert.NotEqual(0, merge.ExitCode);
        var result = await new GitRepositoryInspector().InspectAsync(repo.Path);
        Assert.Equal(GitDiagnosticState.Available, result.State); Assert.True(Assert.Single(result.Changes).IsConflict);
        Assert.Contains("<<<<<<<", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task NoRepositoryBareMissingGitAndMissingDirectoryHaveExplicitStates()
    {
        using var repo = new Repository(); var inspector = new GitRepositoryInspector();
        Assert.Equal(GitDiagnosticState.NotRepository, (await inspector.InspectAsync(repo.Path)).State);
        Assert.Equal(GitDiagnosticState.GitUnavailable, (await new GitRepositoryInspector(searchPath: "").InspectAsync(repo.Path)).State);
        Assert.Equal(GitDiagnosticState.Failed, (await inspector.InspectAsync(Path.Combine(repo.Path, "missing"))).State);
        await repo.RunAsync("init", "--bare", "--template=", "--initial-branch=main");
        Assert.Equal(GitDiagnosticState.BareRepository, (await inspector.InspectAsync(repo.Path)).State);
    }

    [Fact]
    public async Task InheritedGitDirectoryIndexAndConfigurationCannotRedirectTheQuery()
    {
        using var repo = new Repository(); await repo.InitializeAsync();
        var values = new Dictionary<string, string?> { ["GIT_DIR"] = Environment.GetEnvironmentVariable("GIT_DIR"),
            ["GIT_INDEX_FILE"] = Environment.GetEnvironmentVariable("GIT_INDEX_FILE"), ["GIT_CONFIG_COUNT"] = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT") };
        try
        {
            Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(repo.Path, "wrong"));
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", Path.Combine(repo.Path, "wrong-index"));
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "invalid");
            var result = await new GitRepositoryInspector().InspectAsync(repo.Path);
            Assert.Equal(GitDiagnosticState.Available, result.State); Assert.True(result.IsUnborn); Assert.Empty(result.Changes);
            Assert.False(File.Exists(Path.Combine(repo.Path, "wrong-index")));
        }
        finally { foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value); }
    }

    [Fact]
    public async Task TimeoutCancellationExitAndOversizedOutputNeverBecomeAvailable()
    {
        using var repo = new Repository();
        var waiting = Fixture with { PrefixArguments = ["wait"] };
        var result = await new GitRepositoryInspector(launch: waiting, timeout: TimeSpan.FromMilliseconds(250)).InspectAsync(repo.Path);
        Assert.Equal(GitDiagnosticState.Failed, result.State); Assert.Contains("prazo", result.Message);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitRepositoryInspector(launch: waiting).InspectAsync(repo.Path, stop.Token));
        var failed = await new GitRepositoryInspector(launch: Fixture with { PrefixArguments = ["fail"] }).InspectAsync(repo.Path);
        Assert.Equal(GitDiagnosticState.Failed, failed.State); Assert.Contains("código 7", failed.Message); Assert.DoesNotContain("Falha de teste", failed.Message);
        var flood = await new GitRepositoryInspector(launch: Fixture with { PrefixArguments = ["flood"] }).InspectAsync(repo.Path);
        Assert.Equal(GitDiagnosticState.Failed, flood.State); Assert.Contains("limite", flood.Message); Assert.Empty(flood.Changes);
    }

    private sealed class Repository : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sintonia-git-ação " + Guid.NewGuid());
        public Repository() => Directory.CreateDirectory(Path);
        public Task InitializeAsync() => RunAsync("init", "--template=", "--initial-branch=main");
        public async Task RunAsync(params string[] args)
        { var result = await ExecuteAsync(args); Assert.Equal(0, result.ExitCode); Assert.False(result.TimedOut); Assert.False(result.Truncated); }
        public Task<ProcessProbeResult> ExecuteAsync(params string[] args) => ProcessProbe.RunAsync(new("git.exe", [], "Teste Git"),
            new[] { "-c", "user.name=Sintonia Test", "-c", "user.email=sintonia@example.invalid", "-c", "commit.gpgSign=false",
                "-c", "core.hooksPath=" + System.IO.Path.Combine(Path, "no-hooks"), "-c", "core.fsmonitor=false", "-c", "core.excludesFile=" }.Concat(args).ToArray(),
            Path, TimeSpan.FromSeconds(10));
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path); var temporary = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(full).StartsWith("sintonia-git-ação "))
                throw new InvalidOperationException("Pasta de teste fora do diretório temporário.");
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(full, recursive: true);
        }
    }
}
