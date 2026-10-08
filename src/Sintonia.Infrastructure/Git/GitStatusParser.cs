using System.Globalization;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Git;

/// <summary>Parses porcelain v2 -z without splitting or unescaping file names.</summary>
public static class GitStatusParser
{
    public static GitRepositoryDiagnostic Parse(string directory, string root, string output)
    {
        if (!output.EndsWith('\0')) throw Invalid();
        var records = output.Split('\0'); var changes = new List<GitFileChange>();
        string? branch = null, oid = null, upstream = null; int? ahead = null, behind = null;
        var headers = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < records.Length - 1; i++)
        {
            var record = records[i];
            if (record.StartsWith("# ", StringComparison.Ordinal))
            {
                var parts = record.Split(' ', 3);
                if (parts.Length != 3) throw Invalid();
                if (parts[1] is not ("branch.head" or "branch.oid" or "branch.upstream" or "branch.ab")) continue;
                if (!headers.Add(parts[1]) || parts[2].Length == 0) throw Invalid();
                switch (parts[1])
                {
                    case "branch.head": branch = parts[2]; break;
                    case "branch.oid": oid = parts[2]; if (oid != "(initial)" && !IsHash(oid)) throw Invalid(); break;
                    case "branch.upstream": upstream = parts[2]; break;
                    case "branch.ab":
                        var counts = parts[2].Split(' ');
                        if (counts.Length != 2 || !counts[0].StartsWith('+') || !counts[1].StartsWith('-')
                            || !int.TryParse(counts[0].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var a)
                            || !int.TryParse(counts[1].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var b)) throw Invalid();
                        ahead = a; behind = b; break;
                }
                continue;
            }
            if (record.StartsWith("? ", StringComparison.Ordinal))
            { changes.Add(new(RequiredPath(record[2..]), null, '?', '?', false, true, "N...")); continue; }
            var type = record.FirstOrDefault();
            var count = type switch { '1' => 9, '2' => 10, 'u' => 11, _ => throw Invalid() };
            var fields = record.Split(' ', count, StringSplitOptions.None);
            if (fields.Length != count || fields[0].Length != 1 || fields[1].Length != 2
                || fields[1].Any(c => !".MADRTCU".Contains(c)) || fields[2].Length != 4
                || (fields[2] != "N..." && (fields[2][0] != 'S' || !".C".Contains(fields[2][1])
                    || !".M".Contains(fields[2][2]) || !".U".Contains(fields[2][3])))) throw Invalid();
            var modeCount = type == 'u' ? 4 : 3;
            for (var m = 3; m < 3 + modeCount; m++)
                if (fields[m].Length != 6 || fields[m].Any(c => c is < '0' or > '7')) throw Invalid();
            var hashCount = type == 'u' ? 3 : 2;
            for (var h = 3 + modeCount; h < 3 + modeCount + hashCount; h++)
                if (!IsHash(fields[h])) throw Invalid();
            string? original = null;
            if (type == '2')
            {
                var score = fields[8];
                if (score.Length < 2 || score[0] is not ('R' or 'C')
                    || !int.TryParse(score.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var similarity)
                    || similarity > 100 || ++i >= records.Length - 1) throw Invalid();
                original = RequiredPath(records[i]);
            }
            if (type == 'u' && fields[1] is not ("DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU")) throw Invalid();
            changes.Add(new(RequiredPath(fields[^1]), original, fields[1][0], fields[1][1], type == 'u', false, fields[2]));
        }
        if (branch is null || oid is null) throw Invalid();
        return new(directory, GitDiagnosticState.Available, root, branch == "(detached)" ? null : branch,
            oid == "(initial)" ? null : oid, branch == "(detached)", oid == "(initial)", upstream, ahead, behind,
            changes.AsReadOnly(), DateTimeOffset.UtcNow, "Consulta local concluída. O estado pode mudar; atualize antes de revisar o projeto.");
    }
    private static bool IsHash(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
    private static string RequiredPath(string value) => value.Length > 0 ? value : throw Invalid();
    private static FormatException Invalid() => new("O Git retornou um status incompleto ou em formato não suportado.");
}
