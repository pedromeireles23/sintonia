using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Git;

/// <summary>Only inspect existing commits. No staging, commits, ref changes or merge.</summary>
public sealed class GitTaskDeliveryInspector(GitTaskWorktreeManager manager) : IGitTaskDeliveryInspector
{
    public async Task<TaskDeliveryCommit> CaptureAsync(TaskDiffSnapshot snapshot, CancellationToken cancellationToken)
    {
        using var deadline = Deadline(cancellationToken);
        var current = await new GitTaskDiffReader(manager).ScanAsync(snapshot.Worktree, deadline.Token).ConfigureAwait(false);
        if (current.HeadCommit != snapshot.HeadCommit || current.LocalStatus != snapshot.LocalStatus)
            throw new InvalidOperationException("O commit ou o estado local mudou. Atualize os diffs antes de registrar.");
        if (current.Files.Any(f => f.LocalChange is not null || f.HasUntrackedContent))
            throw new InvalidOperationException("Salve as mudanças em um commit e deixe a worktree limpa antes de registrar. Arquivos ignorados ficam fora da entrega.");
        var tree = await ObjectIdAsync(snapshot.Worktree.CheckoutDirectory, current.HeadCommit + "^{tree}", deadline.Token).ConfigureAwait(false);
        var final = await new GitTaskDiffReader(manager).ScanAsync(snapshot.Worktree, deadline.Token).ConfigureAwait(false);
        if (final.HeadCommit != current.HeadCommit || final.LocalStatus != current.LocalStatus)
            throw new InvalidOperationException("A worktree mudou durante o registro. Atualize os diffs.");
        cancellationToken.ThrowIfCancellationRequested(); return new(current.HeadCommit, tree);
    }
    public async Task<TaskIntegrationTarget> InspectTargetAsync(TaskDelivery delivery, CancellationToken cancellationToken)
    {
        delivery.ValidateDefinition(); using var deadline = Deadline(cancellationToken);
        var source = await new GitTaskDiffReader(manager).ScanAsync(delivery.Worktree, deadline.Token).ConfigureAwait(false);
        var commit = await CaptureAsync(source, deadline.Token).ConfigureAwait(false);
        if (commit.Commit != delivery.Commit || commit.Tree != delivery.Tree)
            throw new InvalidOperationException("A worktree difere da entrega registrada. Preserve a entrega e confira os arquivos antes de integrar.");
        var directory = delivery.Worktree.RepositoryDirectory;
        GitTaskWorktreeManager.CheckPath(directory); GitTaskWorktreeManager.CheckPath(delivery.Worktree.CommonGitDirectory);
        var root = await ReadAsync(directory, deadline.Token, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        var common = await ReadAsync(directory, deadline.Token, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
        var branch = await ReadAsync(directory, deadline.Token, "symbolic-ref", "HEAD").ConfigureAwait(false);
        var head = await ObjectIdAsync(directory, "HEAD^{commit}", deadline.Token).ConfigureAwait(false);
        var target = new TaskIntegrationTarget(root, common, branch, head); target.ValidateFor(delivery);
        var ancestry = await manager.RunAsync(directory, deadline.Token, "merge-base", "--is-ancestor", delivery.Worktree.BaseCommit, head).ConfigureAwait(false);
        if (ancestry.ExitCode != 0) throw new InvalidOperationException("O destino não contém a base da tarefa. Confira o histórico antes de integrar.");
        var status = await manager.RunWithoutFiltersAsync(directory, deadline.Token,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all", "--ignore-submodules=none"]).ConfigureAwait(false);
        RequireSuccess(status);
        var diagnostic = GitStatusParser.Parse(directory, directory, status.StandardOutput);
        if (diagnostic.Changes.Count != 0 || diagnostic.HeadCommit != head || "refs/heads/" + diagnostic.Branch != branch)
            throw new InvalidOperationException("O destino mudou ou contém alterações locais. Preserve os arquivos e confira novamente.");
        // Reservations coordinate Sintonia jobs; external Git/filesystem writers still require revalidation before a future merge.
        if (await ReadAsync(directory, deadline.Token, "symbolic-ref", "HEAD").ConfigureAwait(false) != branch
            || await ObjectIdAsync(directory, "HEAD^{commit}", deadline.Token).ConfigureAwait(false) != head)
            throw new InvalidOperationException("O destino mudou durante a consulta.");
        var finalSource = await new GitTaskDiffReader(manager).ScanAsync(delivery.Worktree, deadline.Token).ConfigureAwait(false);
        if (finalSource.HeadCommit != source.HeadCommit || finalSource.LocalStatus != source.LocalStatus)
            throw new InvalidOperationException("A origem mudou durante a consulta.");
        var finalStatus = await manager.RunWithoutFiltersAsync(directory, deadline.Token,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all", "--ignore-submodules=none"]).ConfigureAwait(false);
        RequireSuccess(finalStatus);
        if (finalStatus.StandardOutput != status.StandardOutput
            || await ReadAsync(directory, deadline.Token, "rev-parse", "--show-toplevel").ConfigureAwait(false) != root
            || await ReadAsync(directory, deadline.Token, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false) != common)
            throw new InvalidOperationException("O destino mudou durante a consulta.");
        cancellationToken.ThrowIfCancellationRequested(); return target;
    }
    private async Task<string> ObjectIdAsync(string directory, string revision, CancellationToken token)
    {
        var id = await ReadAsync(directory, token, "rev-parse", "--verify", revision).ConfigureAwait(false);
        if (id.Length is not (40 or 64) || !id.All(Uri.IsHexDigit)) throw new FormatException("Objeto Git inválido.");
        return id;
    }
    private async Task<string> ReadAsync(string directory, CancellationToken token, params string[] arguments)
    {
        var result = await manager.RunAsync(directory, token, arguments).ConfigureAwait(false); RequireSuccess(result);
        return result.StandardOutput.TrimEnd('\r', '\n');
    }
    private static CancellationTokenSource Deadline(CancellationToken token)
    { var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20)); return deadline; }
    private static void RequireSuccess(ProcessProbeResult result)
    { if (result.ExitCode != 0) throw new InvalidOperationException("O Git recusou a conferência da entrega/destino. Confira a pasta, a branch e o commit."); }
}
