using Sintonia.Core;

namespace Sintonia.Infrastructure.Git;

/// <summary>Publish a tested tree by fast-forwarding to a recorded merge commit. Never force, reset, fetch or push.</summary>
public sealed class GitTaskPublisher(GitTaskWorktreeManager manager) : IGitTaskPublisher
{
    public async Task VerifyAsync(TaskIntegrationValidation validation, CancellationToken token)
    {
        validation.ValidateDefinition();
        if (validation.State != ValidationState.Passed) throw new InvalidOperationException("A árvore precisa passar pelos critérios antes da publicação.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var inspector = new GitTaskIntegrationValidationInspector(manager);
        await inspector.VerifyAsync(validation.Preparation, deadline.Token).ConfigureAwait(false);
        var target = validation.Reservation.Target;
        if (await new GitTaskDeliveryInspector(manager).InspectTargetAsync(validation.Reservation.Delivery, deadline.Token).ConfigureAwait(false) != target)
            throw new InvalidOperationException("Origem ou destino mudou. Prepare e valide outra combinação.");
        var tree = await ReadAsync(target.RepositoryDirectory, deadline.Token, "rev-parse", "--verify", target.Commit + "^{tree}").ConfigureAwait(false);
        await inspector.VerifyContentAsync(target.RepositoryDirectory, tree, deadline.Token).ConfigureAwait(false);
        await inspector.VerifyContentAsync(validation.Reservation.Delivery.Worktree.CheckoutDirectory, validation.Reservation.Delivery.Tree, deadline.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
    public async Task<string> CreateCommitAsync(TaskPublication intent, CancellationToken token)
    {
        intent.ValidateDefinition();
        var target = intent.Reservation.Target;
        var commit = await ReadAsync(target.RepositoryDirectory, token, "commit-tree", intent.Validation.Preparation.Tree!, "-p", target.Commit,
            "-p", intent.Reservation.Delivery.Commit, "-m", "Sintonia: integrar entrega " + intent.Reservation.Delivery.TaskId).ConfigureAwait(false);
        if (!TaskDelivery.IsObjectId(commit)) throw new InvalidOperationException("O Git não retornou o commit da árvore validada.");
        return commit;
    }
    public async Task ApplyAsync(TaskPublication intent)
    {
        intent.ValidateDefinition(); if (intent.Commit is null) throw new InvalidOperationException("Registre o commit antes de aplicar a entrega.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var token = deadline.Token;
        var target = intent.Reservation.Target; var directory = target.RepositoryDirectory;
        if (await ReadAsync(directory, token, "symbolic-ref", "HEAD").ConfigureAwait(false) != target.Branch
            || await ReadAsync(directory, token, "rev-parse", "HEAD").ConfigureAwait(false) != target.Commit)
            throw new InvalidOperationException("A branch de destino mudou antes da aplicação.");
        await ReadAsync(directory, token, "merge", "--ff-only", "--no-edit", "--no-stat", "--no-autostash", "--no-overwrite-ignore",
            "--no-rerere-autoupdate", "--no-verify-signatures", "--no-gpg-sign", intent.Commit).ConfigureAwait(false);
        if (await ReadAsync(directory, token, "symbolic-ref", "HEAD").ConfigureAwait(false) != target.Branch
            || await ReadAsync(directory, token, "rev-parse", "HEAD").ConfigureAwait(false) != intent.Commit
            || await ReadAsync(directory, token, "rev-parse", "HEAD^{tree}").ConfigureAwait(false) != intent.Validation.Preparation.Tree)
            throw new InvalidOperationException("O destino não corresponde ao commit da árvore validada. Confira os efeitos preservados.");
        await new GitTaskIntegrationValidationInspector(manager).VerifyContentAsync(directory, intent.Validation.Preparation.Tree!, token).ConfigureAwait(false);
    }
    private async Task<string> ReadAsync(string directory, CancellationToken token, params string[] command)
    {
        var result = await manager.RunWithoutFiltersAsync(directory, token,
            new[] { "-c", "user.name=Sintonia", "-c", "user.email=sintonia@localhost", "-c", "commit.gpgSign=false", "-c", "merge.autoStash=false", "-c", "rerere.enabled=false" }.Concat(command).ToArray()).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"O Git recusou a publicação (código {result.ExitCode}). Confira o destino e os arquivos preservados.\n{result.StandardError}");
        return result.StandardOutput.TrimEnd('\r', '\n');
    }
}
