namespace Sintonia.Infrastructure.Git;

public sealed record GitWorktreeEntry(string Directory, string? Head, string? Branch, string? LockReason, bool Prunable);

public static class GitWorktreeListParser
{
    public static IReadOnlyList<GitWorktreeEntry> Parse(string output)
    {
        var result = new List<GitWorktreeEntry>();
        string? directory = null, head = null, branch = null, locked = null;
        var prunable = false; var attributes = new HashSet<string>(StringComparer.Ordinal);
        if (!output.EndsWith("\0\0", StringComparison.Ordinal)) throw new FormatException("Lista de worktrees incompleta.");
        foreach (var line in output.Split('\0').SkipLast(1))
        {
            if (line.Length == 0)
            {
                if (directory is null || (!attributes.Contains("bare") && head is null)) throw new FormatException("Registro Git incompleto.");
                result.Add(new(directory, head, branch, locked, prunable));
                directory = head = branch = locked = null; prunable = false; attributes.Clear(); continue;
            }
            var separator = line.IndexOf(' '); var name = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..];
            if (!attributes.Add(name) || (directory is null && name != "worktree")) throw new FormatException("Registro Git inválido.");
            switch (name)
            {
                case "worktree":
                    if (!Path.IsPathFullyQualified(value)) throw new FormatException("Caminho Git inválido."); directory = value; break;
                case "HEAD":
                    if (value.Length is not (40 or 64) || !value.All(Uri.IsHexDigit)) throw new FormatException("Commit Git inválido."); head = value; break;
                case "branch":
                    if (!value.StartsWith("refs/heads/", StringComparison.Ordinal)) throw new FormatException("Branch Git inválida."); branch = value; break;
                case "locked": locked = value; break;
                case "prunable": prunable = true; break;
                case "bare" or "detached": if (value.Length != 0) throw new FormatException("Atributo Git inválido."); break;
                default: throw new FormatException("Atributo Git desconhecido; atualize o Sintonia antes de continuar.");
            }
        }
        if (result.Select(e => Path.GetFullPath(e.Directory)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new FormatException("Lista Git com caminhos duplicados.");
        return result;
    }
}
