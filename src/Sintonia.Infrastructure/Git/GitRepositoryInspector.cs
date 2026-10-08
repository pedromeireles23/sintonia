using System.Collections;
using System.ComponentModel;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Git;

public sealed class GitRepositoryInspector : IGitRepositoryInspector
{
    private readonly ExecutableLaunch? _launch;
    private readonly TimeSpan _timeout;
    public GitRepositoryInspector(string? searchPath = null, ExecutableLaunch? launch = null, TimeSpan? timeout = null)
    {
        _launch = launch ?? Find(searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "");
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    public async Task<GitRepositoryDiagnostic> InspectAsync(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            return Result(directory, GitDiagnosticState.Failed, "A pasta do projeto não está disponível. Confira o caminho antes de atualizar.");
        if (_launch is null) return Result(directory, GitDiagnosticState.GitUnavailable, "Git não encontrado no PATH. O chat e a fila continuam disponíveis.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(_timeout);
        var environment = CleanEnvironment();
        async Task<ProcessProbeResult> Run(params string[] arguments)
        {
            var result = await ProcessProbe.RunAsync(_launch,
                new[] { "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false" }.Concat(arguments).ToArray(),
                directory, _timeout, deadline.Token, environment).ConfigureAwait(false);
            if (result.TimedOut) throw new TimeoutException();
            if (result.Truncated) throw new FormatException("A saída do Git excedeu o limite de 64 Ki caracteres. O diagnóstico não foi considerado completo.");
            return result;
        }
        try
        {
            var type = await Run("rev-parse", "--is-inside-work-tree", "--is-bare-repository").ConfigureAwait(false);
            if (type.ExitCode != 0)
            {
                if (type.StandardError.StartsWith("fatal: not a git repository", StringComparison.Ordinal))
                    return Result(directory, GitDiagnosticState.NotRepository, "Esta pasta não pertence a um repositório Git. Use o chat e a fila normalmente.");
                return Failure(directory, type);
            }
            var flags = type.StandardOutput.TrimEnd('\r', '\n').Split('\n').Select(s => s.TrimEnd('\r')).ToArray();
            if (flags.Length != 2 || flags.Any(s => s is not ("true" or "false"))) throw new FormatException("Resposta de identificação Git inválida.");
            if (flags[1] == "true") return Result(directory, GitDiagnosticState.BareRepository, "Repositório bare: não há pasta de trabalho para tarefas. Escolha uma cópia de trabalho.");
            if (flags[0] != "true") return Result(directory, GitDiagnosticState.Failed, "A pasta está na área interna do Git. Escolha a pasta de trabalho do projeto.");
            var root = await Run("rev-parse", "--show-toplevel").ConfigureAwait(false);
            if (root.ExitCode != 0) return Failure(directory, root);
            var rootPath = root.StandardOutput.TrimEnd('\r', '\n');
            if (!Path.IsPathFullyQualified(rootPath) || !Directory.Exists(rootPath)) throw new FormatException("Raiz Git inválida ou indisponível.");
            var status = await Run("status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all", "--ignore-submodules=none").ConfigureAwait(false);
            if (status.ExitCode != 0) return Failure(directory, status);
            return GitStatusParser.Parse(directory, rootPath, status.StandardOutput);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Result(directory, GitDiagnosticState.Failed, "A consulta Git excedeu o prazo. Atualize quando a pasta estiver disponível."); }
        catch (TimeoutException)
        { return Result(directory, GitDiagnosticState.Failed, "A consulta Git excedeu o prazo. Atualize quando a pasta estiver disponível."); }
        catch (FormatException exception) { return Result(directory, GitDiagnosticState.Failed, exception.Message); }
        catch (Win32Exception) { return Result(directory, GitDiagnosticState.Failed, "Não foi possível iniciar o Git. Confira a instalação e as permissões da pasta."); }
        catch (IOException) { return Result(directory, GitDiagnosticState.Failed, "A pasta ou o processo Git ficou indisponível durante a consulta."); }
    }
    private static GitRepositoryDiagnostic Failure(string directory, ProcessProbeResult result) => Result(directory, GitDiagnosticState.Failed,
        result.StandardError.Contains("detected dubious ownership", StringComparison.Ordinal)
            ? "O Git recusou a propriedade desta pasta. Confira a confiança do repositório na sua configuração Git; o Sintonia não altera essa regra."
            : $"O Git recusou a consulta (código {result.ExitCode}). Confira a pasta, as permissões e a configuração do repositório.");
    private static GitRepositoryDiagnostic Result(string directory, GitDiagnosticState state, string message) =>
        new(directory, state, null, null, null, false, false, null, null, null, [], DateTimeOffset.UtcNow, message);
    internal static ExecutableLaunch? Find(string path)
    {
        foreach (var part in path.Split(Path.PathSeparator))
        {
            var directory = part.Trim().Trim('"'); if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, "git.exe");
            if (File.Exists(candidate)) return new(candidate, [], "Git nativo");
        }
        return null;
    }
    internal static Dictionary<string, string?> CleanEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // Inherited repository/config/trace variables must not redirect the selected project or log secrets.
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) environment[key] = null;
        environment["GIT_OPTIONAL_LOCKS"] = "0"; environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GIT_NO_LAZY_FETCH"] = "1"; environment["GIT_ALLOW_PROTOCOL"] = "";
        environment["LC_ALL"] = "C"; environment["LANG"] = "C";
        return environment;
    }
}
