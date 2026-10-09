using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

internal static class ClaudeMcpPermissionProbe
{
    internal const string Server = "sintoniaLearn";
    internal const string Tool = "mcp__sintoniaLearn__microsoft_docs_fetch";
    internal const string Page = "https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/task-cancellation";
    internal const string Prompt = "Prova finita de autorização MCP. Use exatamente uma vez a ferramenta " + Tool
        + " com estes parâmetros exatos:\n{\"url\":\"" + Page + "\"}\n"
        + "Não use outras ferramentas, arquivos ou agentes. Se houver recusa, pare sem repetir ou contornar. "
        + "Se a leitura for autorizada, diga em uma frase como a página explica o cancelamento de tarefas. Pare após esta chamada.";

    internal sealed record ToolEvidence(string ToolUseId, string ToolName, bool? IsError, int TextLength,
        string TextHash, bool Truncated, bool ExpectedPageFound);
    internal sealed record StepEvidence(string Step, int Requests, int MatchingRequests, int Approvals,
        int ToolCalls, string Outcome, string SessionId, string Model, RunTokenUsage? TokenUsage,
        IReadOnlyList<ToolEvidence> Results);
    internal sealed record Evidence(string Server, string Tool, string Page, string State, IReadOnlyList<StepEvidence> Steps);

    public static async Task RunAsync(CancellationToken token)
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "provider-probes", "MCP Claude " + Guid.NewGuid()));
        Directory.CreateDirectory(root);
        Console.WriteLine("Prova real MCP: somente leitura pública Microsoft Learn; dois turnos no máximo, 90s/três iterações por turno, sem retry.");
        Console.WriteLine("Pasta exclusiva e evidência parcial: " + root);
        var native = ExecutableLocator.Find(ProviderKind.Claude) ?? throw new ProviderException("Claude não encontrado.");
        var launch = CreateLaunch(native);
        // Auth and connection metadata precede any model turn. Do not write native settings or log server configs/errors.
        try
        {
            await new ClaudeConversationProvider(launch).CheckSubscriptionAsync(root, token).ConfigureAwait(false);
            await CheckReadyAsync(launch, root, token).ConfigureAwait(false);
            await RunCaseAsync(root, observe => new ClaudeConversationProvider(launch, observe), token).ConfigureAwait(false);
            Verify(root);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ProviderException("Prova MCP não concluída; pasta e eventual evidência parcial preservadas, sem reenvio. Detalhes internos omitidos.");
        }
        Console.WriteLine("PASS: leitura MCP recusada e autorizada pelo host; resultado público correlacionado, sessão/modelo retomados.");
    }

    internal static ExecutableLaunch CreateLaunch(ExecutableLaunch native)
    {
        var settings = JsonSerializer.Serialize(new { permissions = new { ask = new[] { Tool } } });
        var config = JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object>
            { [Server] = new { type = "http", url = "https://learn.microsoft.com/api/mcp" } } });
        return native with { PrefixArguments = native.PrefixArguments.Concat(new[]
        {
            "--settings", settings, "--mcp-config", config, "--strict-mcp-config", "--tools", "",
            "--disallowedTools", "mcp__sintoniaLearn__microsoft_docs_search,mcp__sintoniaLearn__microsoft_code_sample_search",
            "--max-turns", "3" // Scalar last; do not let variadic tool options consume auth/status.
        }).ToArray() };
    }

    internal static async Task RunCaseAsync(string root, Func<Action<ClaudeToolResult>, IConversationProvider> createProvider, CancellationToken token)
    {
        var evidencePath = Path.Combine(root, "mcp-evidence.json");
        if (File.Exists(evidencePath)) throw new ProviderException("Evidência da prova já existe; não reenviar turnos.");
        var steps = new List<StepEvidence>();
        await SaveAsync("Prepared");
        try
        {
            var first = await SendAsync(false, null).ConfigureAwait(false);
            await SaveAsync("DeniedVerified");
            await SendAsync(true, first).ConfigureAwait(false);
            await SaveAsync("Completed");
        }
        catch { await SaveAsync("Interrupted"); throw; }

        Task SaveAsync(string state) => File.WriteAllTextAsync(evidencePath,
            JsonSerializer.Serialize(new Evidence(Server, Tool, Page, state, steps), new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);

        async Task<ConversationResult> SendAsync(bool approve, ConversationResult? previous)
        {
            var requests = 0; var matches = 0; var approvals = 0; var toolCalls = 0;
            var results = new ConcurrentQueue<ToolEvidence>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var provider = createProvider(result => results.Enqueue(Project(result)));
            var progress = new ImmediateProgress<ConversationEvent>(ev =>
            {
                if (ev.Kind == ConversationEventKind.Tool) Interlocked.Increment(ref toolCalls);
            });
            var result = await provider.SendAsync(new(root, Prompt, previous?.Model, previous?.NativeSessionId,
                Access: ConversationAccess.WorkspaceWrite, PermissionHandler: (permission, _) =>
                {
                    var number = Interlocked.Increment(ref requests);
                    var match = Matches(permission, root);
                    if (match) Interlocked.Increment(ref matches);
                    var accept = approve && number == 1 && match;
                    if (accept) Interlocked.Increment(ref approvals);
                    Console.WriteLine($"MCP: ação {(accept ? "autorizada" : "recusada")}; URL/parâmetros {(match ? "conferem" : "divergem")}.");
                    return Task.FromResult(accept);
                }), progress, deadline.Token).ConfigureAwait(false);
            var step = new StepEvidence(approve ? "allow" : "deny", requests, matches, approvals, toolCalls,
                result.Outcome.ToString(), result.NativeSessionId, result.Model, result.TokenUsage, results.ToArray());
            steps.Add(step);
            await SaveAsync(approve ? "AllowReturned" : "DenyReturned");
            CheckStep(step, approve);
            if ((approve && result.PermissionDenials.Count > 0) || (!approve && !result.PermissionDenials.Contains(Tool))
                || previous is not null && (previous.NativeSessionId != result.NativeSessionId || previous.Model != result.Model))
                throw new ProviderException("Prova MCP com negativas ou retomada divergentes.");
            Console.WriteLine($"PASS MCP {(approve ? "autorização" : "recusa")}: resultado conferido; modelo {result.Model}.");
            return result;
        }
    }

    internal static bool Matches(ConversationPermission permission, string root)
    {
        if (permission.Provider != ProviderKind.Claude || permission.Kind != Tool
            || !string.Equals(Path.GetFullPath(permission.WorkingDirectory), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) return false;
        var start = permission.Description.IndexOf("\n\n", StringComparison.Ordinal);
        if (start < 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(permission.Description[(start + 2)..]);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.EnumerateObject().Count() == 1
                && doc.RootElement.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && url.GetString() == Page;
        }
        catch (JsonException) { return false; }
    }

    internal static ToolEvidence Project(ClaudeToolResult result) => new(result.ToolUseId, result.ToolName, result.IsError,
        result.Text.Length, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Text))), result.Truncated,
        result.Text.Contains("Task cancellation", StringComparison.OrdinalIgnoreCase)
        && result.Text.Contains("CancellationTokenSource", StringComparison.Ordinal)
        && result.Text.Contains("OperationCanceledException", StringComparison.Ordinal));

    public static void Verify(string root)
    {
        var evidence = JsonSerializer.Deserialize<Evidence>(File.ReadAllText(Path.Combine(root, "mcp-evidence.json")))
            ?? throw new ProviderException("Evidência MCP ausente.");
        if (evidence.Server != Server || evidence.Tool != Tool || evidence.Page != Page || evidence.State != "Completed" || evidence.Steps.Count != 2)
            throw new ProviderException("Evidência MCP incompleta ou divergente.");
        CheckStep(evidence.Steps[0], false); CheckStep(evidence.Steps[1], true);
        if (evidence.Steps[0].SessionId != evidence.Steps[1].SessionId || evidence.Steps[0].Model != evidence.Steps[1].Model)
            throw new ProviderException("Evidência MCP não confirma retomada.");
    }

    private static void CheckStep(StepEvidence step, bool approve)
    {
        if (step.Step != (approve ? "allow" : "deny") || step.Requests != 1 || step.MatchingRequests != 1
            || step.Approvals != (approve ? 1 : 0) || step.ToolCalls != 1
            || step.Outcome != (approve ? "Completed" : "Blocked")
            || string.IsNullOrWhiteSpace(step.SessionId) || string.IsNullOrWhiteSpace(step.Model)
            || step.Results.Count > 1 || (approve && step.Results.Count != 1)
            || step.Results.Any(r => r.ToolName != Tool || string.IsNullOrWhiteSpace(r.ToolUseId) || r.Truncated
                || r.TextLength <= 0 || r.TextHash.Length != 64 || !r.TextHash.All(Uri.IsHexDigit)
                || (approve ? r.IsError == true || !r.ExpectedPageFound : r.IsError != true || r.ExpectedPageFound)))
            throw new ProviderException("A prova MCP não confirmou uma única decisão e o resultado esperado; não repetir chamadas.");
    }

    private static async Task CheckReadyAsync(ExecutableLaunch launch, string root, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(50)); token = deadline.Token;
        TaskCompletionSource<JsonElement>? pending = null;
        var pendingId = ""; var sequence = 0; var controls = 0;
        ProviderProcess? active = null;
        await using var process = new ProviderProcess(launch,
            ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--permission-prompt-tool", "stdio", "--permission-mode", "manual", "--permission-prompts", "host", "--no-session-persistence"], root, ReceiveAsync);
        active = process;
        await RequestAsync("initialize").ConfigureAwait(false);
        // Bounded metadata observations in one process; no reconnects, user messages, or model turns.
        for (var observation = 0; observation < 3; observation++)
        {
            if (observation > 0) await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            var status = await RequestAsync("mcp_status").ConfigureAwait(false);
            var servers = status.GetProperty("mcpServers").EnumerateArray().ToArray();
            if (servers.Length != 1 || servers[0].GetProperty("name").GetString() != Server)
                throw new ProviderException("A prova MCP anunciou servidores além do escopo público.");
            var server = servers[0]; var state = server.GetProperty("status").GetString();
            if (state == "connected")
            {
                if (!server.GetProperty("tools").EnumerateArray().Any(t => t.GetProperty("name").GetString() == "microsoft_docs_fetch"))
                    throw new ProviderException("MCP conectado sem a ferramenta esperada.");
                Console.WriteLine("MCP público conectado; ferramenta descoberta sem modelos."); return;
            }
            if (state != "pending") break;
        }
        throw new ProviderException("MCP público não ficou disponível no prazo da prova; nenhum prompt enviado.");

        async Task<JsonElement> RequestAsync(string subtype)
        {
            pendingId = "sintonia-public-mcp-" + ++sequence;
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await process.WriteLineAsync(JsonSerializer.Serialize(new { type = "control_request", request_id = pendingId, request = new { subtype } }), token);
            var winner = await Task.WhenAny(pending.Task, process.Completion).WaitAsync(token);
            if (winner == process.Completion && !pending.Task.IsCompleted) throw new ProviderException("MCP encerrou a consulta antes de responder.");
            return await pending.Task.WaitAsync(token);
        }
        async ValueTask ReceiveAsync(string line)
        {
            using var doc = JsonDocument.Parse(line); var message = doc.RootElement;
            if (message.GetProperty("type").GetString() == "control_response")
            {
                var response = message.GetProperty("response");
                if (response.GetProperty("request_id").GetString() != pendingId) return;
                if (response.GetProperty("subtype").GetString() == "success") pending?.TrySetResult(response.GetProperty("response").Clone());
                else pending?.TrySetException(new ProviderException("Consulta MCP recusada; detalhes internos omitidos."));
            }
            else if (message.GetProperty("type").GetString() == "control_request")
            {
                if (++controls > 16) throw new ProviderException("Excesso de controles na consulta MCP.");
                await active!.WriteLineAsync(JsonSerializer.Serialize(new { type = "control_response", response = new
                { subtype = "error", request_id = message.GetProperty("request_id").GetString(), error = "Metadata only; interaction refused." } }), token);
            }
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
