using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Git;

/// <summary>Only combine in a new detached checkout; never publish, commit, reset, abort, remove or fetch.</summary>
public sealed class GitTaskIntegrationPreparer(GitTaskWorktreeManager manager, string? managedRoot = null) : IGitTaskIntegrationPreparer
{
    private readonly string _root = Path.GetFullPath(managedRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sintonia", "integrations"));
    public string GetCheckoutDirectory(TaskIntegrationReservation reservation) => Path.Combine(_root, Guid.Parse(reservation.Id).ToString("N"));

    public async Task<TaskIntegrationPreparation> PrepareAsync(TaskIntegrationPreparation intent, CancellationToken token)
    {
        intent.ValidateDefinition();
        if (intent.State != TaskIntegrationPreparationState.Preparing || intent.CheckoutDirectory != GetCheckoutDirectory(intent.Reservation))
            throw new InvalidOperationException("A combinação não corresponde à pasta gerenciada desta instalação.");
        GitTaskWorktreeManager.CheckPath(_root); GitTaskWorktreeManager.CheckPath(intent.CheckoutDirectory);
        if (File.Exists(intent.CheckoutDirectory) || Directory.Exists(intent.CheckoutDirectory))
            throw new InvalidOperationException("A pasta de combinação já existe. Seus arquivos foram preservados; crie uma nova preparação.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var reservation = intent.Reservation; var original = reservation.Target.RepositoryDirectory;
        var inspector = new GitTaskDeliveryInspector(manager);
        if (await inspector.InspectTargetAsync(reservation.Delivery, deadline.Token).ConfigureAwait(false) != reservation.Target)
            throw new InvalidOperationException("Origem ou destino mudou após a reserva. Confira novamente.");
        var bases = await manager.RunAsync(original, deadline.Token, "merge-base", "--all", reservation.Target.Commit, reservation.Delivery.Commit).ConfigureAwait(false);
        RequireSuccess(bases);
        var commits = new[] { reservation.Target.Commit, reservation.Delivery.Commit, reservation.Delivery.Worktree.BaseCommit }
            .Concat(bases.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal);
        foreach (var commit in commits)
            await manager.CheckTreeAsync(original, commit, deadline.Token, forIntegration: true).ConfigureAwait(false);
        Directory.CreateDirectory(_root); GitTaskWorktreeManager.CheckPath(_root);
        RequireSuccess(await RunAsync(original, deadline.Token, "worktree", "add", "--detach", "--lock", "--reason", LockReason(intent),
            intent.CheckoutDirectory, reservation.Target.Commit).ConfigureAwait(false));
        await ValidateCheckoutAsync(intent, deadline.Token).ConfigureAwait(false);
        var before = await StatusAsync(intent, deadline.Token).ConfigureAwait(false);
        if (before.Changes.Count != 0) throw new InvalidOperationException("A pasta recém-criada contém alterações. Confira seus arquivos preservados.");
        var merge = await RunAsync(intent.CheckoutDirectory, deadline.Token, "merge", "--strategy=ort", "--no-commit", "--no-ff", "--no-edit",
            "--no-stat", "--no-autostash", "--no-rerere-autoupdate", "--no-verify-signatures", "--no-gpg-sign", "--no-overwrite-ignore", reservation.Delivery.Commit).ConfigureAwait(false);
        if (merge.ExitCode is not (0 or 1)) RequireSuccess(merge);
        await ValidateCheckoutAsync(intent, deadline.Token).ConfigureAwait(false);
        var after = await StatusAsync(intent, deadline.Token).ConfigureAwait(false);
        var conflicts = after.Changes.Where(c => c.IsConflict).Select(c => c.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (merge.ExitCode == 1 && conflicts.Length == 0) RequireSuccess(merge);
        TaskIntegrationPreparation result;
        if (conflicts.Length != 0) result = intent with { State = TaskIntegrationPreparationState.Conflicted, Conflicts = conflicts };
        else
        {
            if (after.Changes.Any(c => c.WorktreeStatus != '.' || c.IsUntracked))
                throw new InvalidOperationException("Os arquivos da combinação mudaram fora do índice. Confira a pasta preservada.");
            var tree = await RunAsync(intent.CheckoutDirectory, deadline.Token, "write-tree").ConfigureAwait(false); RequireSuccess(tree);
            result = intent with { State = TaskIntegrationPreparationState.Combined, Tree = tree.StandardOutput.TrimEnd('\r', '\n') };
            var final = await StatusAsync(intent, deadline.Token).ConfigureAwait(false);
            if (!after.Changes.SequenceEqual(final.Changes)) throw new InvalidOperationException("A pasta da combinação mudou durante a conferência.");
        }
        if (await inspector.InspectTargetAsync(reservation.Delivery, deadline.Token).ConfigureAwait(false) != reservation.Target)
            throw new InvalidOperationException("Origem ou destino mudou durante a combinação. Confira a pasta preservada.");
        token.ThrowIfCancellationRequested(); result.ValidateDefinition(); return result;
    }
    private Task<ProcessProbeResult> RunAsync(string directory, CancellationToken token, params string[] command) =>
        manager.RunWithoutFiltersAsync(directory, token,
            new[] { "-c", "core.sparseCheckout=false", "-c", "core.sparseCheckoutCone=false", "-c", "core.protectNTFS=true", "-c", "merge.default=text",
                "-c", "merge.renormalize=false", "-c", "merge.renameLimit=1000", "-c", "rerere.enabled=false", "-c", "merge.autoStash=false",
                "-c", "commit.gpgSign=false", "-c", "user.name=Sintonia", "-c", "user.email=sintonia@localhost" }.Concat(command).ToArray());
    internal async Task ValidateCheckoutAsync(TaskIntegrationPreparation intent, CancellationToken token)
    {
        GitTaskWorktreeManager.CheckPath(intent.CheckoutDirectory);
        var list = await RunAsync(intent.CheckoutDirectory, token, "worktree", "list", "--porcelain", "-z").ConfigureAwait(false); RequireSuccess(list);
        var entry = GitWorktreeListParser.Parse(list.StandardOutput).SingleOrDefault(e => SamePath(e.Directory, intent.CheckoutDirectory));
        if (entry is null || entry.Head != intent.Reservation.Target.Commit || entry.Branch is not null || entry.LockReason != LockReason(intent) || entry.Prunable)
            throw new InvalidOperationException("O vínculo da pasta de combinação mudou. Confira HEAD, bloqueio e diretório.");
        var root = await RunAsync(intent.CheckoutDirectory, token, "rev-parse", "--show-toplevel").ConfigureAwait(false); RequireSuccess(root);
        var common = await RunAsync(intent.CheckoutDirectory, token, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false); RequireSuccess(common);
        if (!SamePath(root.StandardOutput.TrimEnd('\r', '\n'), intent.CheckoutDirectory)
            || !SamePath(common.StandardOutput.TrimEnd('\r', '\n'), intent.Reservation.Target.CommonGitDirectory))
            throw new InvalidOperationException("A pasta da combinação foi redirecionada para outro repositório.");
    }
    private async Task<GitRepositoryDiagnostic> StatusAsync(TaskIntegrationPreparation intent, CancellationToken token)
    {
        var status = await RunAsync(intent.CheckoutDirectory, token, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all", "--ignore-submodules=none").ConfigureAwait(false);
        RequireSuccess(status); return GitStatusParser.Parse(intent.CheckoutDirectory, intent.CheckoutDirectory, status.StandardOutput);
    }
    private static string LockReason(TaskIntegrationPreparation intent) => "Sintonia: combinação " + intent.Reservation.Id;
    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static void RequireSuccess(ProcessProbeResult result)
    { if (result.ExitCode != 0) throw new InvalidOperationException($"O Git recusou a combinação (código {result.ExitCode}). Confira a pasta preservada; nenhum arquivo original foi sobrescrito."); }
}
