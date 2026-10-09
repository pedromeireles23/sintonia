using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Git;

/// <summary>Read the entire tracked content, ignoring the index stat cache and assume-unchanged bits.</summary>
public sealed class GitTaskIntegrationValidationInspector(GitTaskWorktreeManager manager) : IGitTaskIntegrationValidationInspector
{
    public async Task VerifyAsync(TaskIntegrationPreparation preparation, CancellationToken token)
    {
        preparation.ValidateDefinition();
        if (preparation.State != TaskIntegrationPreparationState.Combined) throw new InvalidOperationException("A combinação ainda não tem uma árvore sem conflitos.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var directory = preparation.CheckoutDirectory;
        await new GitTaskIntegrationPreparer(manager).ValidateCheckoutAsync(preparation, deadline.Token).ConfigureAwait(false);
        await VerifyContentAsync(directory, preparation.Tree!, deadline.Token).ConfigureAwait(false);
        await new GitTaskIntegrationPreparer(manager).ValidateCheckoutAsync(preparation, deadline.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
    internal async Task VerifyContentAsync(string directory, string treeId, CancellationToken token, bool allowUntracked = false)
    {
        directory = Path.GetFullPath(directory);
        GitTaskWorktreeManager.CheckPath(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await manager.CheckTreeAsync(directory, treeId, deadline.Token, forIntegration: true).ConfigureAwait(false);
        var tree = await RunAsync(directory, deadline.Token, "ls-tree", "--full-tree", "-r", "-z", treeId).ConfigureAwait(false);
        var expected = Entries(tree).Select(e =>
        {
            if (!e[7..].StartsWith("blob ", StringComparison.Ordinal)) throw new FormatException("Objeto inválido na árvore combinada.");
            return e.Remove(7, 5);
        }).ToArray();
        if (expected.Length > 1000) throw new InvalidOperationException("A árvore excedeu o limite de mil arquivos para validação completa.");
        var paths = expected.Select(e => e[(e.IndexOf('\t') + 1)..]).ToArray();
        await VerifyIndexAsync(directory, expected, deadline.Token).ConfigureAwait(false);
        await VerifyStatusAsync(directory, deadline.Token, allowUntracked).ConfigureAwait(false);
        for (var offset = 0; offset < paths.Length;)
        {
            var start = offset; var chunk = new List<string>(); var length = 0;
            while (offset < paths.Length && (chunk.Count == 0 || length + paths[offset].Length < 8000))
            {
                var path = paths[offset++]; var absolute = Path.GetFullPath(Path.Combine(directory, path));
                if (Path.IsPathRooted(path) || !absolute.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Caminho inválido na árvore Git.");
                GitTaskWorktreeManager.CheckPath(absolute);
                if (!File.Exists(absolute)) throw new InvalidOperationException("Um arquivo da árvore combinada foi removido. Prepare novamente.");
                chunk.Add(path); length += path.Length + 3;
            }
            // Git applies only its built-in normalization (for example CRLF); external filters are disabled by RunWithoutFiltersAsync.
            var hashes = (await RunAsync(directory, deadline.Token, ["hash-object", "--", .. chunk]).ConfigureAwait(false))
                .StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (hashes.Length != chunk.Count) throw new FormatException("Conferência de conteúdo Git incompleta.");
            for (var i = 0; i < chunk.Count; i++)
                if (expected[start + i].Split([' ', '\t'], 3)[1] != hashes[i])
                    throw new InvalidOperationException("O conteúdo da combinação mudou. Prepare uma nova combinação antes de validar.");
        }
        await VerifyIndexAsync(directory, expected, deadline.Token).ConfigureAwait(false);
        await VerifyStatusAsync(directory, deadline.Token, allowUntracked).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
    private async Task VerifyIndexAsync(string directory, string[] expected, CancellationToken token)
    {
        var index = Entries(await RunAsync(directory, token, "ls-files", "--stage", "-z").ConfigureAwait(false));
        var normalized = index.Select(entry =>
        {
            var separator = entry.IndexOf('\t');
            if (separator < 0 || !entry[..separator].EndsWith(" 0", StringComparison.Ordinal)) throw new InvalidOperationException("O índice contém conflitos ou entradas incompatíveis.");
            return entry[..(separator - 2)] + entry[separator..];
        }).ToArray();
        if (!expected.SequenceEqual(normalized)) throw new InvalidOperationException("O índice da combinação mudou. Prepare novamente.");
    }
    private async Task VerifyStatusAsync(string directory, CancellationToken token, bool allowUntracked)
    {
        var status = await RunAsync(directory, token, "status", "--porcelain=v2", "--branch", "-z", allowUntracked ? "--untracked-files=no" : "--untracked-files=all", "--ignore-submodules=none").ConfigureAwait(false);
        var parsed = GitStatusParser.Parse(directory, directory, status.StandardOutput);
        if (parsed.Changes.Any(c => c.IsConflict || (c.IsUntracked ? !allowUntracked : c.WorktreeStatus != '.')))
            throw new InvalidOperationException("Há conflitos, arquivos novos ou alterações fora do índice da combinação. Confira a pasta preservada.");
    }
    private static string[] Entries(ProcessProbeResult result)
    {
        if (result.StandardOutput.Length > 0 && !result.StandardOutput.EndsWith('\0')) throw new FormatException("Lista Git incompleta.");
        var entries = result.StandardOutput.Split('\0').SkipLast(1).ToArray();
        if (entries.Any(e => e.IndexOf('\t') < 8 || !e.StartsWith("100644 ", StringComparison.Ordinal) && !e.StartsWith("100755 ", StringComparison.Ordinal)))
            throw new InvalidOperationException("A validação ainda não suporta este tipo de entrada Git.");
        return entries;
    }
    private async Task<ProcessProbeResult> RunAsync(string directory, CancellationToken token, params string[] command)
    {
        var result = await manager.RunWithoutFiltersAsync(directory, token, command).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"O Git recusou a conferência de validação (código {result.ExitCode}).");
        return result;
    }
}
