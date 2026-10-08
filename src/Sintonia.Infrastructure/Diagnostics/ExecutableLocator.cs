using Sintonia.Core;

namespace Sintonia.Infrastructure.Diagnostics;

public sealed record ExecutableLaunch(string FileName, IReadOnlyList<string> PrefixArguments, string InstallationKind);

/// <summary>Resolves native executables and known npm layouts without evaluating shell wrappers.</summary>
public static class ExecutableLocator
{
    public static ExecutableLaunch? Find(ProviderKind provider, string? searchPath = null)
    {
        var name = provider switch { ProviderKind.Codex => "codex", ProviderKind.Claude => "claude", _ => throw new ArgumentOutOfRangeException(nameof(provider)) };
        var path = searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in Directories(path))
        {
            var native = Path.Combine(directory, name + ".exe");
            if (File.Exists(native)) return new(native, [], "Executável nativo");
            if (!File.Exists(Path.Combine(directory, name + ".cmd")) && !File.Exists(Path.Combine(directory, name + ".ps1"))) continue;
            if (provider == ProviderKind.Claude)
            {
                var packaged = Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
                if (File.Exists(packaged)) return new(packaged, [], "Executável nativo via instalação npm");
            }
            var script = provider == ProviderKind.Claude
                ? Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-code", "cli.js")
                : Path.Combine(directory, "node_modules", "@openai", "codex", "bin", "codex.js");
            if (File.Exists(script))
            {
                var node = new[] { Path.Combine(directory, "node.exe") }
                    .Concat(Directories(path).Select(d => Path.Combine(d, "node.exe"))).FirstOrDefault(File.Exists);
                if (node is not null) return new(node, Array.AsReadOnly(new[] { script }), "Script npm via Node.js");
            }
            throw new NotSupportedException($"O wrapper de {name} encontrado no PATH não tem um layout suportado. Configure uma instalação nativa; nenhum shell foi executado.");
        }
        return null;
    }

    private static IEnumerable<string> Directories(string searchPath) => searchPath.Split(Path.PathSeparator)
        .Select(p => p.Trim().Trim('"'))
        // Do not silently launch a provider from the project directory through relative PATH entries.
        .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p));
}
