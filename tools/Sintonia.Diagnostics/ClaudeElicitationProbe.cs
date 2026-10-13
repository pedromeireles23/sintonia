using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

internal static class ClaudeElicitationProbe
{
    internal const string EvidenceFile = "elicitation-evidence.json";
    internal sealed record Evidence(string Version, string State, string Route, int Calls, int Requests, int Matches,
        int Declines, int UnexpectedControls, int ModelEvents, int UserMessagesSent, bool ResultMatches,
        int ResultLength, string? ResultHash, string? FailureCode);

    public static async Task RunAsync(CancellationToken token)
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "provider-probes", "Elicitation Claude " + Guid.NewGuid()));
        Directory.CreateDirectory(root);
        Console.WriteLine("Prova local elicitation: um mcp_call, zero prompts, 90s no máximo, sem retry.");
        Console.WriteLine("Pasta exclusiva e evidência parcial: " + root);
        var native = ExecutableLocator.Find(ProviderKind.Claude) ?? throw new ProviderException("Claude não encontrado.");
        var nonce = Guid.NewGuid().ToString("N");
        var serverPath = Path.ChangeExtension(typeof(ClaudeElicitationProbe).Assembly.Location, ".exe");
        if (!File.Exists(serverPath)) throw new ProviderException("Executável local do diagnóstico não encontrado.");
        var launch = CreateLaunch(native, new(serverPath, [], "Servidor MCP local de teste"), root, nonce);
        var version = await ProcessProbe.RunAsync(native, ["--version"], root, TimeSpan.FromSeconds(10), token);
        if (version.ExitCode != 0 || version.TimedOut || version.Truncated || version.StandardOutput.Trim().Length is < 1 or > 100)
            throw new ProviderException("Não foi possível conferir a versão Claude.");
        await new ClaudeConversationProvider(launch).CheckSubscriptionAsync(root, token);
        await RunCaseAsync(root, launch, nonce, version.StandardOutput.Trim(), token);
        Verify(root);
        Console.WriteLine("PASS: elicitation form real recebida e recusada; servidor local confirmou decline sem dados. Nenhum prompt ou turno de modelo enviado.");
    }

    internal static ExecutableLaunch CreateLaunch(ExecutableLaunch native, ExecutableLaunch server, string root, string nonce) => native with
    {
        PrefixArguments = native.PrefixArguments.Concat(new[]
        {
            "--mcp-config", JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object>
            {
                [LocalElicitationServer.Name] = new { type = "stdio", command = server.FileName,
                    args = server.PrefixArguments.Concat(new[] { "mcp-elicitation-server", root, nonce }).ToArray() }
            } }), "--strict-mcp-config", "--max-turns", "1"
        }).ToArray()
    };

    internal static async Task RunCaseAsync(string root, ExecutableLaunch launch, string nonce, string version, CancellationToken token)
    {
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new ProviderException("Pasta já usada; não repetir chamadas MCP.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90)); token = deadline.Token;
        var calls = 0; var requests = 0; var matches = 0; var declines = 0; var unexpected = 0; var modelEvents = 0;
        var resultMatches = false; var resultLength = 0; string? resultHash = null; string? failure = null;
        var sequence = 0; var pendingId = ""; TaskCompletionSource<JsonElement>? pending = null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        ProviderProcess? active = null;
        await Save("Prepared");
        try
        {
            await using var process = new ProviderProcess(launch,
                ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                    "--permission-prompt-tool", "stdio", "--permission-mode", "plan", "--permission-prompts", "host", "--no-session-persistence"],
                root, ReceiveAsync);
            active = process;
            await RequestAsync(new { subtype = "initialize" });
            var status = await RequestAsync(new { subtype = "mcp_status" });
            var servers = status.GetProperty("mcpServers").EnumerateArray().ToArray();
            if (servers.Length != 1 || servers[0].GetProperty("name").GetString() != LocalElicitationServer.Name
                || servers[0].GetProperty("status").GetString() is not ("connected" or "pending"))
                throw new ProviderException("Servidor MCP local indisponível ou fora do escopo.");
            calls = 1; await Save("Calling");
            // Trusted native control, deliberately fixed to this no-effect local tool. Never offered by the workspace adapter.
            var returned = await RequestAsync(new { subtype = "mcp_call", tool = "mcp__" + LocalElicitationServer.Name + "__" + LocalElicitationServer.Tool,
                arguments = new { nonce } });
            var result = returned.GetRawText(); resultLength = result.Length;
            resultHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result)));
            resultMatches = resultLength <= 128_000 && result.Contains(LocalElicitationServer.Marker, StringComparison.Ordinal)
                && !HasError(returned);
            Check(Snapshot("Completed"), ReadAudit(root));
            if (Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) is not (EvidenceFile or LocalElicitationServer.AuditFile)))
                throw new ProviderException("Entradas inesperadas na pasta da prova.");
            await Save("Completed");
        }
        catch (Exception exception)
        {
            failure ??= exception is OperationCanceledException ? "Cancelled" : "ProofFailed";
            await Save("Interrupted");
            throw new ProviderException("Prova local de elicitation não concluída; evidência parcial preservada, sem retry. Detalhes internos omitidos.");
        }

        Evidence Snapshot(string state) => new(version, state, "mcp_call", calls, requests, matches, declines, unexpected,
            modelEvents, 0, resultMatches, resultLength, resultHash, failure);
        Task Save(string state) => File.WriteAllTextAsync(Path.Combine(root, EvidenceFile),
            JsonSerializer.Serialize(Snapshot(state), new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        async Task<JsonElement> RequestAsync(object request)
        {
            pendingId = "sintonia-local-" + ++sequence; pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await active!.WriteLineAsync(JsonSerializer.Serialize(new { type = "control_request", request_id = pendingId, request }), token);
            var winner = await Task.WhenAny(pending.Task, active.Completion).WaitAsync(token);
            if (winner == active.Completion && !pending.Task.IsCompleted)
            {
                await active.Completion; throw new ProviderException("Claude encerrou a consulta antes da resposta.");
            }
            return await pending.Task.WaitAsync(token);
        }
        async ValueTask ReceiveAsync(string line)
        {
            using var document = JsonDocument.Parse(line); var message = document.RootElement;
            switch (message.GetProperty("type").GetString())
            {
                case "control_response":
                    var response = message.GetProperty("response");
                    if (response.GetProperty("request_id").GetString() != pendingId) throw new ProviderException("Resposta de controle não correlacionada.");
                    if (response.GetProperty("subtype").GetString() == "success") pending?.TrySetResult(response.GetProperty("response").Clone());
                    else
                    {
                        failure = calls == 1 ? "ControlCallRejected" : "MetadataRejected";
                        pending?.TrySetException(new ProviderException("Controle nativo recusado; detalhes internos omitidos."));
                    }
                    break;
                case "control_request":
                    var id = message.GetProperty("request_id").GetString(); var request = message.GetProperty("request");
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || !ids.Add(id) || ++requests != 1 || calls != 1)
                        throw new ProviderException("Controle duplicado ou fora do momento esperado.");
                    if (!Matches(request))
                    {
                        unexpected++;
                        await active!.WriteLineAsync(ClaudeStdioSession.CreateUnsupportedControlResponse(id, "unsupported"), token);
                        throw new ProviderException("Controle fora do escopo da prova.");
                    }
                    matches = 1;
                    await active!.WriteLineAsync(ClaudeStdioSession.CreateUnsupportedControlResponse(id, "elicitation"), token);
                    declines = 1;
                    Console.WriteLine("Controle elicitation form exato conferido; resposta compartilhada do host: decline, sem dados.");
                    break;
                case "assistant" or "user" or "stream_event" or "result":
                    modelEvents++; throw new ProviderException("Evento de turno inesperado numa prova sem modelos.");
                case "control_cancel_request": throw new ProviderException("Claude retirou o controle durante a prova.");
                case "system": break; // Initialization/hooks metadata, never persisted.
                default: throw new ProviderException("Mensagem nativa desconhecida na prova.");
            }
        }
    }

    internal static bool Matches(JsonElement request) => request.GetProperty("subtype").GetString() == "elicitation"
        && request.GetProperty("mcp_server_name").GetString() == LocalElicitationServer.Name
        && request.GetProperty("message").GetString() == LocalElicitationServer.Message
        && (!request.TryGetProperty("mode", out var mode) || mode.GetString() == "form")
        && JsonElement.DeepEquals(request.GetProperty("requested_schema"), LocalElicitationServer.Schema);
    private static bool HasError(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(property =>
            property.Name is "isError" or "is_error" && property.Value.ValueKind == JsonValueKind.True || HasError(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasError), _ => false
    };
    private static LocalElicitationServer.Audit ReadAudit(string root) => JsonSerializer.Deserialize<LocalElicitationServer.Audit>(
        File.ReadAllText(Path.Combine(root, LocalElicitationServer.AuditFile))) ?? throw new ProviderException("Auditoria local ausente.");
    public static void Verify(string root) => Check(JsonSerializer.Deserialize<Evidence>(File.ReadAllText(Path.Combine(root, EvidenceFile)))
        ?? throw new ProviderException("Evidência local ausente."), ReadAudit(root));
    private static void Check(Evidence evidence, LocalElicitationServer.Audit audit)
    {
        if (string.IsNullOrWhiteSpace(evidence.Version) || evidence.State != "Completed" || evidence.Route != "mcp_call"
            || evidence.Calls != 1 || evidence.Requests != 1 || evidence.Matches != 1 || evidence.Declines != 1
            || evidence.UnexpectedControls != 0 || evidence.ModelEvents != 0 || evidence.UserMessagesSent != 0
            || !evidence.ResultMatches || evidence.ResultLength is <= 0 or > 128_000 || evidence.ResultHash is not { Length: 64 }
            || !evidence.ResultHash.All(Uri.IsHexDigit) || evidence.FailureCode is not null
            || audit.State != "Completed" || !audit.Initialized || !audit.FormSupported || audit.ToolCalls != 1
            || audit.Elicitations != 1 || audit.Action != "decline" || audit.ContentReturned || audit.Unexpected != 0)
            throw new ProviderException("Evidência não confirma elicitation form e recusa sem modelo.");
    }
}
