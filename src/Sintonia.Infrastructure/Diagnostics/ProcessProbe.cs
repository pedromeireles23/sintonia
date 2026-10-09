using System.Diagnostics;
using System.Text;

namespace Sintonia.Infrastructure.Diagnostics;

public sealed record ProcessProbeResult(int ExitCode, string StandardOutput, string StandardError, bool Truncated, bool TimedOut, bool Cancelled = false);

public static class ProcessProbe
{
    public static Task<ProcessProbeResult> RunAsync(ExecutableLaunch launch, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Diagnósticos devem durar no máximo 60 segundos.");
        return RunCoreAsync(launch, arguments, workingDirectory, timeout, cancellationToken, environmentOverrides, captureCancellation: false);
    }

    internal static Task<ProcessProbeResult> RunValidationAsync(ExecutableLaunch launch, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        // Validation tools retain normal repository/network configuration. Remove inherited redirection,
        // without imposing the read-only Git diagnostic's no-network flags on user-owned commands.
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) environment[key] = null;
        environment["GIT_TERMINAL_PROMPT"] = "0";
        return RunCoreAsync(launch, arguments, workingDirectory, timeout, token, environment, captureCancellation: true);
    }

    private static async Task<ProcessProbeResult> RunCoreAsync(ExecutableLaunch launch, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentOverrides, bool captureCancellation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(launch.FileName)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in launch.PrefixArguments.Concat(arguments)) start.ArgumentList.Add(argument);
        if (environmentOverrides is not null)
            foreach (var (key, value) in environmentOverrides)
                if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(timeout);
        using var job = captureCancellation ? new ValidationProcessJob() : null;
        process.Start();
        if (job is not null) await job.AttachAsync(process).ConfigureAwait(false);
        process.StandardInput.Close();
        var stdout = ReadBoundedAsync(process.StandardOutput, lifetime.Token);
        var stderr = ReadBoundedAsync(process.StandardError, lifetime.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            if (job is not null) await job.StopAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            if (job is not null) await job.StopAsync().ConfigureAwait(false);
            await StopAsync(process).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited) await StopAsync(process).ConfigureAwait(false);
            lifetime.Cancel();
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (!captureCancellation) cancellationToken.ThrowIfCancellationRequested();
        return new(process.ExitCode, output.Text, error.Text, output.Truncated || error.Truncated, timedOut, cancellationToken.IsCancellationRequested);
    }

    private static async Task StopAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int limit = 64 * 1024;
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                var retained = Math.Min(count, limit - text.Length);
                text.Append(buffer, 0, retained);
                truncated |= retained < count;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        return (text.ToString(), truncated);
    }
}
