using System.Text.Json;

internal static class LocalElicitationServer
{
    internal const string Name = "sintonia_elicitation";
    internal const string Tool = "request_format";
    internal const string Message = "Prova local Sintonia: qual formato usar?";
    internal const string Marker = "SINTONIA_ELICITATION_DECLINED";
    internal const string AuditFile = "local-server-evidence.json";
    internal static JsonElement Schema => JsonSerializer.SerializeToElement(new
    {
        type = "object", properties = new { formato = new { type = "string", @enum = new[] { "Resumo", "Detalhes" } } },
        required = new[] { "formato" }
    });
    internal sealed record Audit(string State, bool Initialized, string? Protocol, bool FormSupported,
        int ToolCalls, int Elicitations, string? Action, bool ContentReturned, int Unexpected);

    internal static async Task<int> RunAsync(string root, string nonce, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root) || nonce.Length != 32 || !nonce.All(Uri.IsHexDigit)) return 2;
        var path = Path.Combine(root, AuditFile);
        if (File.Exists(path)) return 2; // The server cannot be restarted in the same proof.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90)); token = deadline.Token;
        var initialized = false; var form = false; string? protocol = null; string? action = null;
        var calls = 0; var elicitations = 0; var unexpected = 0; var contentReturned = false; var messages = 0;
        JsonElement? callId = null;
        await Save("Prepared");
        try
        {
            while (await Console.In.ReadLineAsync(token) is { } line)
            {
                if (line.Length > 128_000 || ++messages > 64) throw new InvalidDataException();
                using var document = JsonDocument.Parse(line); var message = document.RootElement;
                if (message.GetProperty("jsonrpc").GetString() != "2.0") throw new InvalidDataException();
                if (!message.TryGetProperty("method", out var method))
                {
                    if (callId is null || message.GetProperty("id").GetString() != "sintonia-form") throw new InvalidDataException();
                    var result = message.GetProperty("result");
                    action = result.GetProperty("action").GetString(); contentReturned = result.TryGetProperty("content", out _);
                    if (action != "decline" || result.EnumerateObject().Count() != 1) throw new InvalidDataException();
                    await Save("Completed");
                    await Emit(new { jsonrpc = "2.0", id = callId.Value, result = new
                        { content = new[] { new { type = "text", text = Marker } }, isError = false } });
                    callId = null;
                    continue;
                }
                if (method.GetString() == "notifications/initialized") continue;
                var id = message.GetProperty("id");
                switch (method.GetString())
                {
                    case "initialize":
                        if (initialized) throw new InvalidDataException();
                        var parameters = message.GetProperty("params"); protocol = parameters.GetProperty("protocolVersion").GetString();
                        if (protocol is not ("2025-11-25" or "2025-06-18")) throw new InvalidDataException();
                        var capabilities = parameters.GetProperty("capabilities");
                        form = capabilities.TryGetProperty("elicitation", out var elicitation) && elicitation.ValueKind == JsonValueKind.Object
                            && (!elicitation.EnumerateObject().Any() || elicitation.TryGetProperty("form", out _));
                        initialized = true; await Save("Initialized");
                        await Emit(new { jsonrpc = "2.0", id, result = new { protocolVersion = protocol,
                            capabilities = new { tools = new { } }, serverInfo = new { name = Name, version = "1.0-test" } } });
                        break;
                    case "ping": await Emit(new { jsonrpc = "2.0", id, result = new { } }); break;
                    case "tools/list":
                        if (!initialized) throw new InvalidDataException();
                        await Emit(new { jsonrpc = "2.0", id, result = new { tools = new[] { new
                        {
                            name = Tool, description = "Prova local finita de recusa, sem rede ou efeitos externos.",
                            inputSchema = new { type = "object", properties = new { nonce = new { type = "string" } }, required = new[] { "nonce" }, additionalProperties = false },
                            annotations = new { readOnlyHint = true, destructiveHint = false, openWorldHint = false }
                        } } } });
                        break;
                    case "tools/call":
                        if (!initialized || !form || ++calls != 1 || callId is not null) throw new InvalidDataException();
                        var tool = message.GetProperty("params"); var arguments = tool.GetProperty("arguments");
                        if (tool.GetProperty("name").GetString() != Tool || arguments.EnumerateObject().Count() != 1
                            || arguments.GetProperty("nonce").GetString() != nonce) throw new InvalidDataException();
                        callId = id.Clone(); elicitations = 1; await Save("Requesting");
                        await Emit(new { jsonrpc = "2.0", id = "sintonia-form", method = "elicitation/create",
                            @params = new { mode = "form", message = Message, requestedSchema = Schema } });
                        break;
                    default: unexpected++; throw new InvalidDataException();
                }
            }
            return action == "decline" ? 0 : 2;
        }
        catch (Exception) { await Save("Interrupted"); return 2; }

        Task Save(string state) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Audit(state, initialized, protocol,
            form, calls, elicitations, action, contentReturned, unexpected), new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        async Task Emit(object value)
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)); await Console.Out.FlushAsync(token);
        }
    }
}
