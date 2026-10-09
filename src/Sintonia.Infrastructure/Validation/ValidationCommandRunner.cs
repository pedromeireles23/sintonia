using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Git;

namespace Sintonia.Infrastructure.Validation;

public sealed class ValidationCommandRunner : IValidationCommandRunner
{
    public async Task<ValidationCommandResult> RunAsync(ProjectValidationCommand command, int commandIndex, string checkoutDirectory, CancellationToken token)
    {
        command.ValidateDefinition(); token.ThrowIfCancellationRequested();
        if (commandIndex < 0 || !Path.IsPathFullyQualified(checkoutDirectory)) throw new ArgumentException("Pasta de validação inválida.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkoutDirectory));
        var directory = Path.GetFullPath(Path.Combine(root, command.WorkingDirectory));
        if (!string.Equals(directory, root, StringComparison.OrdinalIgnoreCase) && !directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A pasta de execução deve ficar dentro da combinação.");
        GitTaskWorktreeManager.CheckPath(directory); GitTaskWorktreeManager.CheckPath(command.Executable);
        if (!Directory.Exists(directory) || !File.Exists(command.Executable))
            throw new InvalidOperationException("A pasta ou o executável configurado não existe. Prepare as dependências explicitamente.");
        var started = DateTimeOffset.UtcNow;
        var result = await ProcessProbe.RunValidationAsync(new(command.Executable, [], "Validação configurada"), command.Arguments,
            directory, TimeSpan.FromSeconds(command.TimeoutSeconds), token).ConfigureAwait(false);
        var state = result.Cancelled ? ValidationState.Cancelled : result.TimedOut ? ValidationState.TimedOut
            : result.ExitCode == 0 ? ValidationState.Passed : ValidationState.Failed;
        return new(commandIndex, state, result.ExitCode, result.StandardOutput, result.StandardError, result.Truncated, started, DateTimeOffset.UtcNow);
    }
}
