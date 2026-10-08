using System.Collections.Concurrent;
using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Providers;

public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ProviderProcess _process;
    private long _id;
    public event Action<JsonElement>? Notification;
    public event Action<JsonElement>? ServerRequest;
    public Task Completion => _process.Completion;

    public JsonRpcClient(ExecutableLaunch launch, string directory)
    {
        _process = new ProviderProcess(launch, ["app-server"], directory, ReceiveAsync);
        _ = ObserveExitAsync();
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _id);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            if (Completion.IsCompleted) throw new ProviderException("O processo Codex foi encerrado.");
            await _process.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }), cancellationToken).ConfigureAwait(false);
            var winner = await Task.WhenAny(completion.Task, Completion).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (winner == Completion && !completion.Task.IsCompleted)
            {
                await Completion.ConfigureAwait(false);
                throw new ProviderException("O processo Codex terminou antes de responder.");
            }
            return await completion.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, object parameters, CancellationToken token) =>
        _process.WriteLineAsync(JsonSerializer.Serialize(new { method, @params = parameters }), token);

    public Task RespondAsync(JsonElement id, object result, CancellationToken token) =>
        _process.WriteLineAsync(JsonSerializer.Serialize(new { id, result }), token);

    public Task RejectAsync(JsonElement id, CancellationToken token) => _process.WriteLineAsync(
        JsonSerializer.Serialize(new { id, error = new { code = -32601, message = "Solicitação não suportada pelo Sintonia." } }), token);

    private ValueTask ReceiveAsync(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.TryGetProperty("method", out _))
        {
            if (root.TryGetProperty("id", out var requestId))
            {
                if (ServerRequest is null) return new ValueTask(RejectAsync(requestId.Clone(), CancellationToken.None));
                ServerRequest.Invoke(root.Clone());
            }
            else Notification?.Invoke(root.Clone());
        }
        else if (root.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && _pending.TryRemove(number, out var pending))
        {
            if (root.TryGetProperty("error", out var error))
                pending.TrySetException(new ProviderException("Codex: " + error.GetProperty("message").GetString()));
            else if (root.TryGetProperty("result", out var result)) pending.TrySetResult(result.Clone());
            else pending.TrySetException(new ProviderException("Resposta Codex sem resultado."));
        }
        return ValueTask.CompletedTask;
    }

    private async Task ObserveExitAsync()
    {
        Exception failure = new ProviderException("O processo Codex terminou antes de responder.");
        try { await Completion.ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        foreach (var item in _pending.Values) item.TrySetException(failure);
    }

    public ValueTask DisposeAsync() => _process.DisposeAsync();
}
