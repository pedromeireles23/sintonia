using System.Text;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Git;

public sealed class GitTaskDiffReader(GitTaskWorktreeManager manager, TimeSpan? timeout = null) : IGitTaskDiffReader
{
    private readonly TimeSpan _timeout = ValidTimeout(timeout ?? TimeSpan.FromSeconds(20));
    private static readonly string[] DiffOptions = ["--no-ext-diff", "--no-textconv", "--no-color", "--no-relative", "--ignore-submodules=all",
        "--find-renames=50%", "-l1000", "--diff-algorithm=myers", "--unified=3", "--src-prefix=a/", "--dst-prefix=b/"];

    public async Task<TaskDiffSnapshot> ScanAsync(TaskWorktree worktree, CancellationToken cancellationToken)
    {
        if (worktree.State != TaskWorktreeState.Ready) throw new InvalidOperationException("A worktree ainda não está pronta para revisão.");
        using var deadline = Deadline(cancellationToken);
        await manager.ValidateAsync(worktree, deadline.Token).ConfigureAwait(false);
        var status = await StatusAsync(worktree, deadline.Token).ConfigureAwait(false);
        var local = GitStatusParser.Parse(worktree.WorkingDirectory, worktree.CheckoutDirectory, status);
        var names = await manager.RunWithoutFiltersAsync(worktree.CheckoutDirectory, deadline.Token,
            new[] { "--literal-pathspecs", "diff" }.Concat(DiffOptions).Concat(["--name-status", "-z", worktree.BaseCommit, "--"]).ToArray()).ConfigureAwait(false);
        RequireSuccess(names);
        var files = GitDiffNameParser.Parse(names.StandardOutput).ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var change in local.Changes)
        {
            GitDiffNameParser.ValidatePath(change.Path);
            if (change.OriginalPath is { } original) GitDiffNameParser.ValidatePath(original);
            var file = files.TryGetValue(change.Path, out var existing) ? existing : new TaskDiffFile(change.Path, change.OriginalPath, '.', null);
            files[change.Path] = file with { LocalChange = change.IsUntracked && file.LocalChange is { IsUntracked: false } ? file.LocalChange : change,
                HasUntrackedContent = file.HasUntrackedContent || change.IsUntracked };
        }
        if (files.Count > GitDiffNameParser.MaxFiles) throw new InvalidOperationException("A consulta excedeu 1.000 arquivos. Nenhum resultado parcial foi aceito.");
        var snapshot = new TaskDiffSnapshot(worktree, local.HeadCommit!, status, files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(), DateTimeOffset.UtcNow);
        await EnsureCurrentAsync(snapshot, deadline.Token).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); return snapshot;
    }

    public async Task<TaskFileDiff> ReadAsync(TaskDiffSnapshot snapshot, TaskDiffFile file, TaskDiffView view, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(view) || !snapshot.Files.Contains(file)) throw new ArgumentException("Selecione um arquivo desta consulta e uma comparação válida.");
        GitDiffNameParser.ValidatePath(file.Path);
        if (file.OriginalPath is { } original) GitDiffNameParser.ValidatePath(original);
        using var deadline = Deadline(cancellationToken);
        await EnsureCurrentAsync(snapshot, deadline.Token).ConfigureAwait(false);
        TaskFileDiff content;
        if (file.HasUntrackedContent && (view != TaskDiffView.Index || file.LocalChange?.IsUntracked == true))
            content = view == TaskDiffView.Index ? new(TaskDiffContentState.NoChanges, "", "Arquivo novo ainda não preparado para commit.", DateTimeOffset.UtcNow)
                : await ReadNewFileAsync(snapshot.Worktree, file.Path, deadline.Token).ConfigureAwait(false);
        else
        {
            var arguments = new List<string> { "--literal-pathspecs", "diff" }; arguments.AddRange(DiffOptions);
            if (view == TaskDiffView.SinceBase) arguments.Add(snapshot.Worktree.BaseCommit);
            else if (view == TaskDiffView.Index) arguments.Add("--cached");
            arguments.Add("--"); arguments.Add(file.Path);
            var previousPath = view == TaskDiffView.SinceBase ? file.OriginalPath : file.LocalChange?.OriginalPath;
            if (previousPath is { } before) { GitDiffNameParser.ValidatePath(before); arguments.Add(before); }
            var patch = await manager.RunWithoutFiltersAsync(snapshot.Worktree.CheckoutDirectory, deadline.Token, arguments, allowTruncation: true).ConfigureAwait(false);
            RequireSuccess(patch);
            content = patch.Truncated ? new(TaskDiffContentState.TooLarge, "", "Diff maior que 64 Ki caracteres. Prévia indisponível; confira o arquivo por outra ferramenta.", DateTimeOffset.UtcNow)
                : patch.StandardOutput.Length == 0 ? new(TaskDiffContentState.NoChanges, "", "Sem diferença nesta comparação. Confira as outras opções e o estado local.", DateTimeOffset.UtcNow)
                : patch.StandardOutput.Contains("\nBinary files ", StringComparison.Ordinal) ? new(TaskDiffContentState.Binary, patch.StandardOutput, "Arquivo binário: o Git mostra apenas o resumo da alteração.", DateTimeOffset.UtcNow)
                : new(TaskDiffContentState.Text, patch.StandardOutput, "Diff textual consultado. Conteúdo e estado podem mudar; atualize após alterações.", DateTimeOffset.UtcNow);
        }
        await EnsureCurrentAsync(snapshot, deadline.Token).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); return content;
    }

    private async Task EnsureCurrentAsync(TaskDiffSnapshot snapshot, CancellationToken token)
    {
        await manager.ValidateAsync(snapshot.Worktree, token).ConfigureAwait(false);
        var status = await StatusAsync(snapshot.Worktree, token).ConfigureAwait(false);
        if (status != snapshot.LocalStatus) throw new InvalidOperationException("A branch, o commit ou o estado local mudou. Atualize a lista de diffs.");
    }
    private async Task<string> StatusAsync(TaskWorktree worktree, CancellationToken token)
    {
        var result = await manager.RunWithoutFiltersAsync(worktree.CheckoutDirectory, token,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all", "--ignore-submodules=none"]).ConfigureAwait(false);
        RequireSuccess(result); return result.StandardOutput;
    }
    private static async Task<TaskFileDiff> ReadNewFileAsync(TaskWorktree worktree, string path, CancellationToken token)
    {
        var full = Path.GetFullPath(Path.Combine(worktree.CheckoutDirectory, path.Replace('/', Path.DirectorySeparatorChar)));
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Prévia de arquivos novos por links/junções não é suportada. Confira o caminho diretamente.");
        const int limit = 64 * 1024;
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var buffer = new byte[limit + 1]; var count = 0;
        while (count < buffer.Length)
        { var read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false); if (read == 0) break; count += read; }
        if (count > limit) return new(TaskDiffContentState.TooLarge, "", "Arquivo novo maior que 64 KiB. Prévia indisponível; o arquivo permanece preservado.", DateTimeOffset.UtcNow);
        if (buffer.AsSpan(0, count).Contains((byte)0)) return Binary();
        try
        {
            var text = new UTF8Encoding(false, true).GetString(buffer, 0, count).TrimStart('\uFEFF');
            return new(TaskDiffContentState.Text, text, "Conteúdo atual de arquivo novo em UTF-8. Ainda não faz parte de um commit.", DateTimeOffset.UtcNow);
        }
        catch (DecoderFallbackException) { return Binary(); }
        static TaskFileDiff Binary() => new(TaskDiffContentState.Binary, "", "Arquivo binário ou texto fora de UTF-8. Prévia textual indisponível.", DateTimeOffset.UtcNow);
    }
    private CancellationTokenSource Deadline(CancellationToken token)
    { var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(_timeout); return deadline; }
    private static TimeSpan ValidTimeout(TimeSpan timeout) => timeout > TimeSpan.Zero && timeout <= TimeSpan.FromSeconds(60)
        ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));
    private static void RequireSuccess(ProcessProbeResult result)
    { if (result.ExitCode != 0) throw new InvalidOperationException($"O Git recusou a consulta de diff (código {result.ExitCode}). Confira a pasta e atualize novamente."); }
}
