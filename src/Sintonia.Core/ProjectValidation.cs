namespace Sintonia.Core;

/// <summary>User-owned commands. No provider response is interpreted as a command.</summary>
public sealed record ProjectValidationCommand(string Name, string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory = ".", int TimeoutSeconds = 60)
{
    public void ValidateDefinition()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || Name.Contains('\0')
            || !Path.IsPathFullyQualified(Executable) || Executable.Length > 4096 || Executable.Contains('\0')
            || !Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || Arguments is null || Arguments.Count > 100 || Arguments.Any(a => a is null || a.Contains('\0'))
            || Arguments.Sum(a => a.Length + 3) + Executable.Length > 16000
            || TimeoutSeconds is < 1 or > 600 || string.IsNullOrWhiteSpace(WorkingDirectory)
            || WorkingDirectory.Length > 1000 || Path.IsPathRooted(WorkingDirectory)
            || WorkingDirectory.IndexOfAny([':', '\0', '*', '?']) >= 0
            || WorkingDirectory.Replace('\\', '/').Split('/').Any(p => p == ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase) || p.Length == 0))
            throw new ArgumentException("Configure nome, executável .exe absoluto, argumentos separados, pasta relativa e prazo entre 1 e 600 segundos.");
    }
}

public sealed record ProjectValidationConfiguration(string ProjectId, int Revision, IReadOnlyList<ProjectValidationCommand> Commands)
{
    public void ValidateDefinition()
    {
        if (!Guid.TryParse(ProjectId, out _) || Revision < 0 || Commands is null || Commands.Count > 10)
            throw new ArgumentException("Configuração de validação inválida; use até dez comandos.");
        foreach (var command in Commands) command.ValidateDefinition();
        if (Commands.Select(c => c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Commands.Count)
            throw new ArgumentException("Use nomes distintos para os comandos de validação.");
    }
    public ProjectValidationConfiguration Snapshot()
    {
        ValidateDefinition();
        return this with { Commands = Array.AsReadOnly(Commands.Select(c => c with { Arguments = Array.AsReadOnly(c.Arguments.ToArray()) }).ToArray()) };
    }
}

public enum ValidationState { Running, Passed, Failed, TimedOut, Cancelled, Interrupted, NeedsAttention }

public sealed record ValidationCommandResult(int CommandIndex, ValidationState State, int? ExitCode,
    string StandardOutput, string StandardError, bool Truncated, DateTimeOffset StartedAt, DateTimeOffset FinishedAt)
{
    public void ValidateDefinition()
    {
        if (CommandIndex < 0 || State is not (ValidationState.Passed or ValidationState.Failed or ValidationState.TimedOut or ValidationState.Cancelled)
            || StandardOutput is null || StandardError is null || StandardOutput.Length > 65536 || StandardError.Length > 65536
            || FinishedAt < StartedAt || State == ValidationState.Passed && ExitCode != 0
            || State == ValidationState.Failed && (ExitCode is null or 0))
            throw new ArgumentException("Resultado do comando de validação inválido.");
    }
}

public interface IValidationCommandRunner
{
    Task<ValidationCommandResult> RunAsync(ProjectValidationCommand command, int commandIndex, string checkoutDirectory, CancellationToken token);
}
