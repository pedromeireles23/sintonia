using System.Collections.Concurrent;
using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Providers;

public sealed partial class CodexConversationProvider(ExecutableLaunch? executable = null) : IConversationProvider
{
    public ProviderKind Kind => ProviderKind.Codex;
    private ExecutableLaunch Launch
    {
        get
        {
            var launch = executable ?? ExecutableLocator.Find(Kind)
                ?? throw new ProviderException("Codex não encontrado. Instale e entre no CLI com sua conta ChatGPT.");
            // The installed elevated sandbox fails ACL setup. Use the supported restricted-token sandbox
            // for this client only; do not modify user config or disable sandbox enforcement.
            return OperatingSystem.IsWindows() && executable is null ? launch with
            {
                PrefixArguments = launch.PrefixArguments.Concat(new[] { "-c", "windows.sandbox=\"unelevated\"" }).ToArray()
            } : launch;
        }
    }

    public async Task<ProviderCapabilities> InspectAsync(string directory, CancellationToken cancellationToken)
    {
        await using var rpc = new JsonRpcClient(Launch, directory);
        await InitializeAsync(rpc, cancellationToken).ConfigureAwait(false);
        var models = new List<ProviderModel>();
        string? cursor = null;
        do
        {
            var page = await rpc.RequestAsync("model/list", new { cursor, limit = 100 }, cancellationToken).ConfigureAwait(false);
            foreach (var model in page.GetProperty("data").EnumerateArray())
                if (!model.GetProperty("hidden").GetBoolean()) models.Add(new(model.GetProperty("model").GetString()!,
                    model.GetProperty("displayName").GetString()!, model.GetProperty("isDefault").GetBoolean()));
            cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        } while (cursor is not null && models.Count < 500);
        var extensions = new List<string>();
        var warnings = new List<string>();
        var skills = await rpc.RequestAsync("skills/list", new { cwds = new[] { directory } }, cancellationToken).ConfigureAwait(false);
        foreach (var entry in skills.GetProperty("data").EnumerateArray())
        {
            foreach (var skill in entry.GetProperty("skills").EnumerateArray())
                if (skill.GetProperty("enabled").GetBoolean()) extensions.Add("Skill: " + skill.GetProperty("name").GetString());
            foreach (var error in entry.GetProperty("errors").EnumerateArray()) warnings.Add("Uma skill não carregou: " + error.GetProperty("message").GetString());
        }
        var mcp = await rpc.RequestAsync("mcpServerStatus/list", new { limit = 100 }, cancellationToken).ConfigureAwait(false);
        foreach (var server in mcp.GetProperty("data").EnumerateArray())
            extensions.Add("MCP: " + server.GetProperty("name").GetString());
        return new(Kind, models, extensions, warnings);
    }

    public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        await using var rpc = new JsonRpcClient(Launch, request.WorkingDirectory);
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<(string Text, bool Final)>();
        var changePreviews = new ConcurrentDictionary<string, string>();
        var denials = new ConcurrentQueue<string>();
        var responses = new ConcurrentBag<Task>();
        string? threadId = null;
        string? turnId = null;
        rpc.Notification += notification =>
        {
            var method = notification.GetProperty("method").GetString();
            if (!notification.TryGetProperty("params", out var data) || !data.TryGetProperty("threadId", out var thread)
                || thread.GetString() != threadId) return;
            switch (method)
            {
                case "item/started":
                    var startedItem = data.GetProperty("item");
                    if (startedItem.GetProperty("type").GetString() == "fileChange")
                    {
                        var preview = string.Join("\n\n", startedItem.GetProperty("changes").EnumerateArray().Select(change =>
                            change.GetProperty("path").GetString() + "\n" + change.GetProperty("diff").GetString()));
                        if (preview.Length is > 0 and <= 16000) changePreviews[startedItem.GetProperty("id").GetString()!] = preview;
                    }
                    break;
                case "item/agentMessage/delta":
                    progress.Report(new(ConversationEventKind.TextDelta, Limit(data.GetProperty("delta").GetString()!)));
                    break;
                case "item/completed":
                    var item = data.GetProperty("item");
                    var type = item.GetProperty("type").GetString();
                    if (type == "agentMessage") messages.Enqueue((Limit(item.GetProperty("text").GetString()!),
                        item.TryGetProperty("phase", out var phase) && phase.GetString() == "final_answer"));
                    else
                    {
                        progress.Report(new(ConversationEventKind.Tool, type + ": " + (item.TryGetProperty("status", out var status) ? status.GetString() : "concluído")));
                        if (type == "commandExecution" && item.TryGetProperty("aggregatedOutput", out var output)
                            && item.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.GetInt32() != 0)
                            progress.Report(new(ConversationEventKind.Diagnostic, Limit(output.GetString() ?? "")));
                        if (item.TryGetProperty("status", out var state) && state.GetString() == "declined") denials.Enqueue(type ?? "ferramenta");
                    }
                    break;
                case "turn/completed": completed.TrySetResult(data.GetProperty("turn").Clone()); break;
            }
        };
        rpc.ServerRequest += message => responses.Add(AnswerRequestAsync(rpc, message, request, threadId, changePreviews, denials, progress, token));
        try
        {
            await InitializeAsync(rpc, token).ConfigureAwait(false);
            await CheckIncludedUsageAsync(rpc, progress, token).ConfigureAwait(false);
            if (request.NativeSessionId is not null)
            {
                var stored = await rpc.RequestAsync("thread/read", new { threadId = request.NativeSessionId, includeTurns = false }, token).ConfigureAwait(false);
                var history = stored.GetProperty("thread");
                if (!SameDirectory(history.GetProperty("cwd").GetString()!, request.WorkingDirectory)
                    || history.GetProperty("modelProvider").GetString() != "openai")
                    throw new ProviderException("A sessão Codex pertence a outro diretório ou provedor. Abra uma nova conversa.");
            }
            var parameters = new Dictionary<string, object?>
            {
                ["cwd"] = request.WorkingDirectory, ["model"] = request.Model, ["modelProvider"] = "openai",
                ["approvalPolicy"] = "on-request", ["approvalsReviewer"] = "user",
                ["sandbox"] = request.Access == ConversationAccess.ReadOnly ? "read-only" : "workspace-write"
            };
            if (request.NativeSessionId is not null) parameters["threadId"] = request.NativeSessionId;
            var session = await rpc.RequestAsync(request.NativeSessionId is null ? "thread/start" : "thread/resume", parameters, token).ConfigureAwait(false);
            var expected = request.Access == ConversationAccess.ReadOnly ? "readOnly" : "workspaceWrite";
            if (session.GetProperty("sandbox").GetProperty("type").GetString() != expected
                || session.GetProperty("modelProvider").GetString() != "openai"
                || session.GetProperty("approvalPolicy").GetString() != "on-request")
                throw new ProviderException("Codex não aplicou as permissões solicitadas; nenhum turno foi iniciado.");
            threadId = session.GetProperty("thread").GetProperty("id").GetString()!;
            var model = session.GetProperty("model").GetString()!;
            progress.Report(new(ConversationEventKind.Session, "Sessão Codex conectada por assinatura.", threadId, model));
            // Function instructions are turn input; native global/project instructions remain intact.
            var prompt = string.IsNullOrWhiteSpace(request.Instructions) ? request.Prompt
                : $"Função nesta tarefa:\n{request.Instructions}\n\nPedido do usuário:\n{request.Prompt}";
            var start = await rpc.RequestAsync("turn/start", new { threadId, input = new[] { new { type = "text", text = prompt } } }, token).ConfigureAwait(false);
            turnId = start.GetProperty("turn").GetProperty("id").GetString();
            var winner = await Task.WhenAny(completed.Task, rpc.Completion).WaitAsync(token).ConfigureAwait(false);
            if (winner == rpc.Completion)
            {
                await rpc.Completion.ConfigureAwait(false);
                throw new ProviderException("Codex terminou sem confirmar o resultado do turno.");
            }
            var final = await completed.Task.ConfigureAwait(false);
            await Task.WhenAll(responses).ConfigureAwait(false);
            var state = final.GetProperty("status").GetString();
            if (state == "interrupted") throw new OperationCanceledException("Turno Codex interrompido.", token);
            if (state != "completed") throw new ProviderException("Codex: " + (final.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object ? error.GetProperty("message").GetString() : "turno sem conclusão válida."));
            // Use authoritative final items, not repeated partial tokens.
            var items = final.GetProperty("items").EnumerateArray()
                .Where(i => i.GetProperty("type").GetString() == "agentMessage").ToArray();
            var text = items.LastOrDefault(i => i.TryGetProperty("phase", out var phase) && phase.GetString() == "final_answer");
            if (text.ValueKind == JsonValueKind.Undefined) text = items.LastOrDefault();
            var result = text.ValueKind == JsonValueKind.Object ? Limit(text.GetProperty("text").GetString()!)
                : messages.LastOrDefault(m => m.Final).Text ?? messages.LastOrDefault().Text;
            if (string.IsNullOrWhiteSpace(result)) throw new ProviderException("Codex concluiu sem uma resposta textual.");
            return new(threadId, model, result, denials.IsEmpty ? ConversationOutcome.Completed : ConversationOutcome.Blocked,
                denials.Distinct().ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (threadId is not null && turnId is not null)
            {
                using var interrupt = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await rpc.RequestAsync("turn/interrupt", new { threadId, turnId }, interrupt.Token).ConfigureAwait(false);
                    await completed.Task.WaitAsync(interrupt.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ProviderException or IOException) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new ProviderException("Codex excedeu o limite de cinco minutos e foi interrompido.");
        }
    }

    private static async Task InitializeAsync(JsonRpcClient rpc, CancellationToken token)
    {
        await rpc.RequestAsync("initialize", new { clientInfo = new { name = "sintonia", title = "Sintonia", version = "0.1.0" } }, token).ConfigureAwait(false);
        await rpc.NotifyAsync("initialized", new { }, token).ConfigureAwait(false);
        var account = await rpc.RequestAsync("account/read", new { refreshToken = false }, token).ConfigureAwait(false);
        if (!account.TryGetProperty("account", out var identity) || identity.ValueKind != JsonValueKind.Object
            || identity.GetProperty("type").GetString() != "chatgpt")
            throw new ProviderException("Entre no Codex CLI com ChatGPT. O Sintonia exige login por assinatura e não usa fallback para API.");
    }

    private static async Task AnswerRequestAsync(JsonRpcClient rpc, JsonElement message, ConversationRequest request, string? threadId,
        ConcurrentDictionary<string, string> changePreviews, ConcurrentQueue<string> denials,
        IProgress<ConversationEvent> progress, CancellationToken token)
    {
        var method = message.GetProperty("method").GetString()!;
        var parameters = message.GetProperty("params");
        string? description = null;
        if (method == "item/commandExecution/requestApproval" && parameters.TryGetProperty("command", out var command)) description = command.GetString();
        if (method == "item/fileChange/requestApproval" && parameters.TryGetProperty("itemId", out var itemId))
            changePreviews.TryGetValue(itemId.GetString()!, out description);
        if (request.PermissionHandler is not null && !string.IsNullOrWhiteSpace(description)
            && parameters.TryGetProperty("threadId", out var targetThread) && targetThread.GetString() == threadId)
        {
            var cwd = parameters.TryGetProperty("cwd", out var directory) ? directory.GetString() ?? request.WorkingDirectory : request.WorkingDirectory;
            bool approved;
            try { approved = await request.PermissionHandler(new(ProviderKind.Codex, method, description, cwd), token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                progress.Report(new(ConversationEventKind.Diagnostic, "Não foi possível obter autorização; ação recusada."));
                approved = false;
            }
            if (approved)
            {
                await rpc.RespondAsync(message.GetProperty("id"), new { decision = "accept" }, token).ConfigureAwait(false);
                return;
            }
        }
        denials.Enqueue(method);
        progress.Report(new(ConversationEventKind.PermissionDenied, "Solicitação recusada: " + method));
        object? response = method switch
        {
            "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" => new { decision = "decline" },
            "item/permissions/requestApproval" => new { permissions = new { }, scope = "turn" },
            "mcpServer/elicitation/request" => new { action = "decline", content = (object?)null },
            "item/tool/requestUserInput" => new { answers = new { } },
            _ => null
        };
        if (response is null) await rpc.RejectAsync(message.GetProperty("id"), token).ConfigureAwait(false);
        else await rpc.RespondAsync(message.GetProperty("id"), response, token).ConfigureAwait(false);
    }

    internal static void ValidateRequest(ConversationRequest request)
    {
        if (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
            throw new ProviderException("Selecione uma pasta de projeto existente com caminho absoluto.");
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 200_000 || request.Instructions.Length > 8000)
            throw new ProviderException("Pedido vazio ou maior que o limite permitido.");
        if (request.NativeSessionId is not null && !Guid.TryParse(request.NativeSessionId, out _))
            throw new ProviderException("Identificador de sessão inválido.");
    }

    private static bool SameDirectory(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static string Limit(string text) => text.Length <= 256_000 ? text : text[..256_000] + "\n[Resposta truncada pelo limite local]";
}
