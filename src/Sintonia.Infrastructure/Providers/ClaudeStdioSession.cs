using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Providers;

/// <summary>One bounded turn using Claude's bidirectional control protocol.</summary>
internal sealed class ClaudeStdioSession : IAsyncDisposable
{
    private const string InitializeId = "sintonia-initialize";
    private static readonly JsonSerializerOptions PreviewOptions = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly ProviderProcess _process;
    private readonly ConversationRequest _request;
    private readonly string _sessionId;
    private readonly IProgress<ConversationEvent> _progress;
    private readonly ClaudeStreamParser _parser;
    private readonly CancellationTokenSource _lifetime;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _permissions = new();
    private readonly ConcurrentBag<Task> _handlers = [];
    private readonly HashSet<string> _requestIds = [];
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _resultReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _terminal;

    public ClaudeStdioSession(ExecutableLaunch launch, IEnumerable<string> arguments, ConversationRequest request,
        string sessionId, IProgress<ConversationEvent> progress, CancellationToken token)
    {
        _request = request; _sessionId = sessionId; _progress = progress;
        _parser = new(sessionId, progress);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _process = new(launch, arguments, request.WorkingDirectory, ReceiveAsync);
    }

    public async Task<ConversationResult> SendAsync(CancellationToken token)
    {
        await _process.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "control_request", request_id = InitializeId, request = new { subtype = "initialize" }
        }), token).ConfigureAwait(false);
        try
        {
            await WaitAsync(_initialized.Task, token).WaitAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new ProviderException("Claude não confirmou o protocolo de autorização em sessenta segundos."); }
        await _process.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "user", session_id = _sessionId, parent_tool_use_id = (string?)null,
            message = new { role = "user", content = _request.Prompt }
        }), token).ConfigureAwait(false);
        await WaitAsync(_resultReceived.Task, token).ConfigureAwait(false);
        await Task.WhenAll(_handlers).WaitAsync(token).ConfigureAwait(false);
        if (_failure.Task.IsCompleted) await _failure.Task.ConfigureAwait(false);
        _process.CloseInput();
        await _process.Completion.WaitAsync(token).ConfigureAwait(false);
        return _parser.GetResult(_process.ExitCode);
    }

    private async Task WaitAsync(Task expected, CancellationToken token)
    {
        var winner = await Task.WhenAny(expected, _process.Completion, _failure.Task).WaitAsync(token).ConfigureAwait(false);
        await winner.ConfigureAwait(false);
        if (winner != expected)
            throw new ProviderException("Claude terminou antes de confirmar o protocolo de autorização.");
    }

    private ValueTask ReceiveAsync(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "control_response":
                var response = root.GetProperty("response");
                if (response.GetProperty("request_id").GetString() == InitializeId)
                {
                    if (response.GetProperty("subtype").GetString() == "success") _initialized.TrySetResult(true);
                    else _initialized.TrySetException(new ProviderException("Claude recusou a inicialização do protocolo de autorização."));
                }
                break;
            case "control_request":
                if (_terminal) throw new ProviderException("Claude solicitou autorização depois do resultado terminal.");
                var id = root.GetProperty("request_id").GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || _permissions.Count >= 16 || _requestIds.Count >= 1024)
                    throw new ProviderException("Solicitação de autorização Claude inválida ou acima do limite local.");
                if (!_requestIds.Add(id)) throw new ProviderException("Claude repetiu um identificador de autorização.");
                var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                if (!_permissions.TryAdd(id, stop))
                {
                    stop.Dispose();
                    throw new ProviderException("Claude repetiu um identificador de autorização pendente.");
                }
                // Do not await the UI here: stdout must keep draining, including cancellation frames.
                _handlers.Add(HandleRequestAsync(id, root.GetProperty("request").Clone(), stop));
                break;
            case "control_cancel_request":
                var cancelledId = root.GetProperty("request_id").GetString();
                if (cancelledId is not null && _permissions.TryRemove(cancelledId, out var pending))
                {
                    try { pending.Cancel(); }
                    catch (ObjectDisposedException) { /* The answer won the race. */ }
                }
                break;
            default:
                _parser.Accept(line);
                if (root.GetProperty("type").GetString() == "result")
                {
                    if (!_permissions.IsEmpty) throw new ProviderException("Claude concluiu com autorizações ainda pendentes.");
                    _terminal = true;
                    _resultReceived.TrySetResult(true);
                }
                break;
        }
        return ValueTask.CompletedTask;
    }

    private async Task HandleRequestAsync(string id, JsonElement request, CancellationTokenSource stop)
    {
        try
        {
            if (request.GetProperty("subtype").GetString() != "can_use_tool")
            {
                _parser.RecordDenial("controle não suportado");
                _permissions.TryRemove(id, out _);
                await _process.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "control_response", response = new
                    {
                        subtype = "error", request_id = id, error = "Solicitação não suportada pelo Sintonia."
                    }
                }), stop.Token).ConfigureAwait(false);
                return;
            }
            var tool = request.TryGetProperty("tool_name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() : null;
            var hasInput = request.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object;
            var preview = hasInput ? JsonSerializer.Serialize(input, PreviewOptions) : "";
            var reviewable = !string.IsNullOrWhiteSpace(tool) && tool.Length <= 200 && !tool.Any(char.IsControl)
                && hasInput && input.EnumerateObject().Any() && preview.Length <= 64_000
                && tool is not ("AskUserQuestion" or "ExitPlanMode");
            var allow = false;
            if (_request.Access == ConversationAccess.WorkspaceWrite && reviewable && _request.PermissionHandler is { } handler)
            {
                _progress.Report(new(ConversationEventKind.Diagnostic, "Aguardando autorização Claude: " + tool));
                try
                {
                    allow = await handler(new(ProviderKind.Claude, tool!, $"Claude · {tool}\n\n{preview}",
                        _request.WorkingDirectory), stop.Token).WaitAsync(stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    _progress.Report(new(ConversationEventKind.Diagnostic, "Falha ao obter autorização Claude; ação recusada."));
                }
            }
            stop.Token.ThrowIfCancellationRequested();
            if (!allow) _parser.RecordDenial(tool ?? "ferramenta");
            else _progress.Report(new(ConversationEventKind.Diagnostic, "Autorização Claude concedida para esta ação: " + tool));
            object decision = allow
                ? new { behavior = "allow", updatedInput = input }
                : new { behavior = "deny", message = "Ação recusada pelo Sintonia. Não repita nem contorne a recusa." };
            _permissions.TryRemove(id, out _);
            await _process.WriteLineAsync(JsonSerializer.Serialize(new
            {
                type = "control_response", response = new { subtype = "success", request_id = id, response = decision }
            }), stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception)
        {
            _failure.TrySetException(new ProviderException("Falha no protocolo de autorização Claude; execução encerrada."));
        }
        finally
        {
            _permissions.TryRemove(id, out _);
            stop.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _process.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_handlers).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
