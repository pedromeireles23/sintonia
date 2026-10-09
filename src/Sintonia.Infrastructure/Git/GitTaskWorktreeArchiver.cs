using Sintonia.Core;

namespace Sintonia.Infrastructure.Git;

public sealed partial class GitTaskWorktreeManager
{
    public string GetArchiveDirectory(TaskPublication publication, string operationId)
    {
        publication.ValidateDefinition();
        if (publication.State != TaskPublicationState.Published || !Guid.TryParse(operationId, out var id)) throw new ArgumentException("Publicação/operação de arquivamento inválida.");
        var archive = Path.Combine(_root, "archives", Guid.Parse(publication.Reservation.Delivery.TaskId).ToString("N"), id.ToString("N"));
        EnsureArchiveLocation(new(operationId, publication, archive, null)); return archive;
    }
    private void EnsureArchiveLocation(TaskWorktreeCleanupPreview preview)
    {
        preview.ValidateDefinition(); EnsureManagedLocation(preview.Publication.Reservation.Delivery.Worktree);
        var expected = Path.Combine(_root, "archives", Guid.Parse(preview.Publication.Reservation.Delivery.TaskId).ToString("N"), Guid.Parse(preview.Id).ToString("N"));
        if (!SamePath(expected, preview.ArchiveDirectory)) throw new InvalidOperationException("O arquivo está fora da pasta gerenciada desta operação.");
        CheckPath(expected);
        if (Directory.Exists(expected) || File.Exists(expected)) throw new InvalidOperationException("A pasta de arquivo já existe. Nenhum arquivo será sobrescrito.");
    }
    public async Task VerifyArchiveAsync(TaskWorktreeCleanupPreview preview, CancellationToken token)
    {
        EnsureArchiveLocation(preview); var delivery = preview.Publication.Reservation.Delivery; using var deadline = Deadline(token);
        await ValidateAsync(delivery.Worktree, deadline.Token).ConfigureAwait(false);
        var head = await RunAsync(delivery.Worktree.CheckoutDirectory, deadline.Token, "rev-parse", "HEAD").ConfigureAwait(false); RequireSuccess(head);
        if (head.StandardOutput.TrimEnd('\r', '\n') != delivery.Commit) throw new InvalidOperationException("A worktree tem outro commit. Confira as mudanças posteriores antes de arquivar.");
        await new GitTaskIntegrationValidationInspector(this).VerifyContentAsync(delivery.Worktree.CheckoutDirectory, delivery.Tree, deadline.Token, allowUntracked: true).ConfigureAwait(false);
        await VerifyRevisionAsync(delivery.Worktree.RepositoryDirectory, preview.Publication.Commit!, deadline.Token).ConfigureAwait(false);
        await ValidateAsync(delivery.Worktree, deadline.Token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
    }
    public async Task ArchiveAsync(TaskWorktreeCleanupPreview preview)
    {
        // The rename preserves all files, including ignored/untracked files. No recursive delete or force is used.
        using var deadline = Deadline(CancellationToken.None);
        var worktree = preview.Publication.Reservation.Delivery.Worktree;
        try
        {
            using (await HoldArchiveIndexAsync(worktree, deadline.Token).ConfigureAwait(false))
            {
                await VerifyArchiveAsync(preview, deadline.Token).ConfigureAwait(false);
                await Task.Run(() =>
                {
                    deadline.Token.ThrowIfCancellationRequested(); EnsureArchiveLocation(preview);
                    Directory.CreateDirectory(Path.GetDirectoryName(preview.ArchiveDirectory)!); CheckPath(preview.ArchiveDirectory);
                    // Both resolved paths were checked against the exact owned roots before moving on Windows.
                    Directory.Move(worktree.CheckoutDirectory, preview.ArchiveDirectory);
                }, deadline.Token).ConfigureAwait(false);
            }
            if (Directory.Exists(worktree.CheckoutDirectory) || File.Exists(worktree.CheckoutDirectory))
                throw new InvalidOperationException("A pasta original reapareceu. O registro Git foi preservado para conferência.");
            // Only retire this now-missing checkout. Do not prune other worktrees or delete its branch.
            var entry = (await ListAsync(worktree.RepositoryDirectory, deadline.Token).ConfigureAwait(false)).SingleOrDefault(e => SamePath(e.Directory, worktree.CheckoutDirectory));
            if (entry is null || entry.Branch != "refs/heads/" + worktree.Branch || entry.Head != preview.Publication.Reservation.Delivery.Commit || entry.LockReason != LockReason(worktree))
                throw new InvalidOperationException("O registro Git mudou durante o arquivamento. Confira a pasta preservada.");
            RequireSuccess(await RunAsync(worktree.RepositoryDirectory, deadline.Token, "worktree", "unlock", worktree.CheckoutDirectory).ConfigureAwait(false));
            if (Directory.Exists(worktree.CheckoutDirectory) || File.Exists(worktree.CheckoutDirectory)) throw new InvalidOperationException("A pasta original reapareceu antes de retirar o registro Git.");
            RequireSuccess(await RunAsync(worktree.RepositoryDirectory, deadline.Token, "worktree", "remove", worktree.CheckoutDirectory).ConfigureAwait(false));
            if ((await ListAsync(worktree.RepositoryDirectory, deadline.Token).ConfigureAwait(false)).Any(e => SamePath(e.Directory, worktree.CheckoutDirectory)))
                throw new InvalidOperationException("O Git ainda conserva o registro da worktree. Confira o arquivo preservado.");
            CheckPath(preview.ArchiveDirectory);
            if (!Directory.Exists(preview.ArchiveDirectory)) throw new InvalidOperationException("O arquivo preservado não está disponível. Confira a pasta.");
            var branch = await RunAsync(worktree.RepositoryDirectory, deadline.Token, "rev-parse", "--verify", "refs/heads/" + worktree.Branch).ConfigureAwait(false); RequireSuccess(branch);
            if (branch.StandardOutput.TrimEnd('\r', '\n') != preview.Publication.Reservation.Delivery.Commit) throw new InvalidOperationException("A branch da entrega mudou durante a operação.");
            await VerifyRevisionAsync(worktree.RepositoryDirectory, preview.Publication.Commit!, deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            // A failed metadata retirement keeps the files in the archive. Restore only our own lock, never move files back.
            try
            {
                using var repair = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var entry = (await ListAsync(worktree.RepositoryDirectory, repair.Token).ConfigureAwait(false)).SingleOrDefault(e => SamePath(e.Directory, worktree.CheckoutDirectory));
                if (entry is { LockReason: null } && entry.Branch == "refs/heads/" + worktree.Branch && entry.Head == preview.Publication.Reservation.Delivery.Commit)
                    RequireSuccess(await RunAsync(worktree.RepositoryDirectory, repair.Token, "worktree", "lock", "--reason", LockReason(worktree), worktree.CheckoutDirectory).ConfigureAwait(false));
            }
            catch { /* Preserve the original failure and both recorded paths for recovery. */ }
            throw;
        }
    }
    private async Task<FileStream> HoldArchiveIndexAsync(TaskWorktree worktree, CancellationToken token)
    {
        await ValidateAsync(worktree, token).ConfigureAwait(false);
        var result = await RunAsync(worktree.CheckoutDirectory, token, "rev-parse", "--path-format=absolute", "--git-path", "index").ConfigureAwait(false); RequireSuccess(result);
        var index = Path.GetFullPath(result.StandardOutput.TrimEnd('\r', '\n')); CheckPath(index);
        var expectedRoot = Path.Combine(worktree.CommonGitDirectory, "worktrees");
        var relative = Path.GetRelativePath(expectedRoot, index);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Length != 2 || relative.StartsWith("..", StringComparison.Ordinal)
            || Path.GetFileName(index) != "index" || !File.Exists(index)) throw new InvalidOperationException("O índice está fora dos metadados da worktree registrada.");
        // Git writers respect index.lock. DeleteOnClose retires this exact handle before removing metadata.
        return new FileStream(index + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
    }
}
