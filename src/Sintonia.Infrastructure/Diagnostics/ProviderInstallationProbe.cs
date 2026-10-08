using System.Text.RegularExpressions;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Diagnostics;

public enum InstallationStatus { Detected, Missing, Unsupported, Failed }
public sealed record ProviderInstallationReport(ProviderKind Provider, InstallationStatus Status, string? Version,
    string? InstallationKind, IReadOnlyList<string> AdvertisedOptions, string? Diagnostic);

/// <summary>Only version/help probes. Detection is not an authentication or model-access check.</summary>
public sealed partial class ProviderInstallationProbe(string? searchPath = null)
{
    public async Task<ProviderInstallationReport> InspectAsync(ProviderKind provider, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var launch = ExecutableLocator.Find(provider, searchPath);
            if (launch is null) return new(provider, InstallationStatus.Missing, null, null, [], "Executável não encontrado no PATH.");
            var versionResult = await ProcessProbe.RunAsync(launch, ["--version"], workingDirectory,
                TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (versionResult.ExitCode != 0 || versionResult.TimedOut || versionResult.Truncated)
                return Failure(provider, "A consulta de versão falhou, excedeu o prazo ou retornou saída excessiva.");
            var version = (provider == ProviderKind.Codex ? CodexVersion() : ClaudeVersion()).Match(versionResult.StandardOutput);
            if (!version.Success) return Failure(provider, "O formato da versão não foi reconhecido.");
            var helpArguments = provider == ProviderKind.Codex ? new[] { "app-server", "--help" } : ["--help"];
            var help = await ProcessProbe.RunAsync(launch, helpArguments, workingDirectory,
                TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (help.ExitCode != 0 || help.TimedOut || help.Truncated)
                return Failure(provider, "A consulta da interface falhou, excedeu o prazo ou retornou saída excessiva.");
            string[] expected = provider == ProviderKind.Codex ? ["--listen", "--stdio"]
                : ["--print", "--input-format", "--output-format", "--resume", "--session-id", "--append-system-prompt", "--permission-mode", "--permission-prompts", "--max-budget-usd"];
            var advertised = expected.Where(option => Regex.IsMatch(help.StandardOutput,
                @"(?<![\w-])" + Regex.Escape(option) + @"(?![\w-])", RegexOptions.CultureInvariant)).ToArray();
            return new(provider, InstallationStatus.Detected, version.Groups[1].Value, launch.InstallationKind,
                Array.AsReadOnly(advertised), "Somente versão/ajuda verificadas. Autenticação, permissões, extensões e inferência ainda não foram testadas.");
        }
        catch (NotSupportedException exception) { return new(provider, InstallationStatus.Unsupported, null, null, [], exception.Message); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or TimeoutException)
        {
            // Do not export stderr, environment variables, credentials or user configuration.
            return Failure(provider, "Não foi possível consultar o processo local. Confira a instalação e o diretório de trabalho.");
        }
    }

    private static ProviderInstallationReport Failure(ProviderKind provider, string diagnostic) => new(provider, InstallationStatus.Failed, null, null, [], diagnostic);
    [GeneratedRegex(@"^codex-cli\s+([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CodexVersion();
    [GeneratedRegex(@"^([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)\s+\(Claude Code\)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeVersion();
}
