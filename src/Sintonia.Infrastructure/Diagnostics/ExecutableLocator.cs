using Sintonia.Core;

namespace Sintonia.Infrastructure.Diagnostics;

public sealed record ExecutableLaunch(string FileName, IReadOnlyList<string> PrefixArguments, string InstallationKind);

/// <summary>Resolves native executables and known npm layouts without evaluating shell wrappers.</summary>
public static class ExecutableLocator
{
    public static ExecutableLaunch? Find(ProviderKind provider, string? searchPath = null, string? codexApplicationBin = null)
    {
        return FindOnPath(provider, searchPath) ?? (provider == ProviderKind.Codex
            ? FindCodexApplication(codexApplicationBin ?? (searchPath is null
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin") : null))
            : null);
    }

    /// <summary>The Codex desktop app keeps its CLI in versioned folders outside PATH; use the newest copy only when PATH has none.</summary>
    private static ExecutableLaunch? FindCodexApplication(string? binDirectory)
    {
        if (binDirectory is null || !Directory.Exists(binDirectory)) return null;
        var newest = Directory.EnumerateFiles(binDirectory, "codex.exe",
                new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(file => new FileInfo(file)).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        return newest is null ? null : new(newest.FullName, [], "Executável do aplicativo Codex");
    }

    private static ExecutableLaunch? FindOnPath(ProviderKind provider, string? searchPath)
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
