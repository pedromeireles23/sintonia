using System.Diagnostics;
using System.Text;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Providers;

/// <summary>Owns one child tree, drains both streams, and never evaluates shell text.</summary>
public sealed class ProviderProcess : IAsyncDisposable
{
    private const int LineLimit = 2 * 1024 * 1024;
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1);
    private readonly StringBuilder _stderr = new();
    public Task Completion { get; }
    public int ExitCode => _process.ExitCode;
    public string StandardError => _stderr.ToString();

    public ProviderProcess(ExecutableLaunch launch, IEnumerable<string> arguments, string directory,
        Func<string, ValueTask> receive)
    {
        var start = new ProcessStartInfo(launch.FileName)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in launch.PrefixArguments.Concat(arguments)) start.ArgumentList.Add(argument);
        _process = new Process { StartInfo = start };
        _process.Start();
        Completion = PumpAsync(receive);
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public void CloseInput() => _process.StandardInput.Close();

    private async Task PumpAsync(Func<string, ValueTask> receive)
    {
        var error = DrainErrorAsync();
        try
        {
            var line = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await _process.StandardOutput.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        if (line.Length > 0)
                        {
                            await receive(line.ToString().TrimEnd('\r')).ConfigureAwait(false);
                            line.Clear();
                        }
                    }
                    else
                    {
                        if (line.Length >= LineLimit) throw new ProviderException("Evento do provedor excedeu o limite de tamanho.");
                        line.Append(buffer[i]);
                    }
                }
            }
            if (line.Length > 0) await receive(line.ToString()).ConfigureAwait(false);
            await _process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
            _lifetime.Cancel();
            await error.ConfigureAwait(false);
        }
    }

    private async Task DrainErrorAsync()
    {
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false)) > 0)
                _stderr.Append(buffer, 0, Math.Min(count, 65536 - _stderr.Length));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task StopAsync()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (_process.HasExited) { }
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await StopAsync().ConfigureAwait(false);
        try { await Completion.ConfigureAwait(false); }
        catch (Exception) { /* Completion is the operation's error channel; disposal only releases resources. */ }
        finally { _process.Dispose(); _lifetime.Dispose(); _writeGate.Dispose(); }
    }
}
