using System.Globalization;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Git;

public static class GitDiffNameParser
{
    public const int MaxFiles = 1000;
    public static IReadOnlyList<TaskDiffFile> Parse(string output)
    {
        if (output.Length == 0) return [];
        if (!output.EndsWith('\0')) throw Invalid();
        var fields = output.Split('\0'); var files = new List<TaskDiffFile>(); var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length - 1;)
        {
            var status = fields[i++]; string? original = null;
            if (status.Length == 0 || !"ADMRTU".Contains(status[0])) throw Invalid();
            if (status[0] == 'R')
            {
                if (!int.TryParse(status.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var score) || score > 100 || i >= fields.Length - 1) throw Invalid();
                original = fields[i++]; ValidatePath(original);
            }
            else if (status.Length != 1) throw Invalid();
            if (i >= fields.Length - 1) throw Invalid();
            var path = fields[i++]; ValidatePath(path);
            if (!paths.Add(path) || files.Count >= MaxFiles) throw Invalid();
            files.Add(new(path, original, status[0], null));
        }
        return files.AsReadOnly();
    }
    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Contains('\\')
            || path.Split('/').Any(part => part is "" or "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw Invalid();
    }
    private static FormatException Invalid() => new("Lista de diffs incompleta, caminho incompatível ou limite de 1.000 arquivos excedido. Nenhum resultado parcial foi aceito.");
}
