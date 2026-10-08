using System.Text.Json;

internal static class ProtocolFixture
{
    private const string ThreadId = "c9511c7e-3bc3-4b0d-bfff-a602c04e99a1";
    public static async Task<int> RunCodexAsync(string scenario)
    {
        string? pendingTurn = null;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var method))
            {
                if (root.TryGetProperty("result", out var response) && response.TryGetProperty("decision", out var decision) && decision.GetString() == "decline")
                    Notify("item/completed", new { threadId = ThreadId, turnId = pendingTurn, item = new { id = "cmd1", type = "commandExecution", status = "declined" } });
                if (pendingTurn is not null) Finish(pendingTurn, "completed");
                pendingTurn = null;
                continue;
            }
            if (!root.TryGetProperty("id", out var id)) continue;
            var parameters = root.GetProperty("params");
            switch (method.GetString())
            {
                case "initialize": Reply(id, new { userAgent = "fixture" }); break;
                case "account/read": Reply(id, new { account = new { type = scenario == "rpc-api" ? "apiKey" : "chatgpt" } }); break;
                case "thread/read": Reply(id, new { thread = new { id = ThreadId, cwd = scenario == "rpc-wrong-directory" ? "C:\\elsewhere" : Environment.CurrentDirectory, modelProvider = "openai" } }); break;
                case "thread/start":
                case "thread/resume":
                    Reply(id, new { thread = new { id = ThreadId }, model = "fixture-model", modelProvider = "openai", approvalPolicy = parameters.GetProperty("approvalPolicy").GetString(),
                        sandbox = new { type = scenario == "rpc-unsafe" ? "dangerFullAccess" : parameters.GetProperty("sandbox").GetString() == "read-only" ? "readOnly" : "workspaceWrite" } }); break;
                case "turn/start":
                    var turn = Guid.NewGuid().ToString();
                    Reply(id, new { turn = new { id = turn } });
                    Notify("item/agentMessage/delta", new { threadId = ThreadId, turnId = turn, itemId = "m1", delta = "Resposta " });
                    Notify("item/completed", new { threadId = ThreadId, turnId = turn, item = new { id = "m1", type = "agentMessage", text = "Resposta final.", phase = "final_answer" } });
                    if (scenario == "rpc-denied")
                    {
                        pendingTurn = turn;
                        Emit(new { id = "permission-1", method = "item/commandExecution/requestApproval", @params = new { threadId = ThreadId, turnId = turn, command = "comando de teste" } });
                    }
                    else if (scenario == "rpc-interrupt") pendingTurn = turn;
                    else if (scenario == "rpc-exit") return 7;
                    else Finish(turn, scenario == "rpc-failed" ? "failed" : "completed");
                    break;
                case "turn/interrupt": Reply(id, new { }); Finish(pendingTurn!, "interrupted"); pendingTurn = null; break;
                case "echo": Reply(id, parameters); break;
                default: Emit(new { id, error = new { code = -32601, message = "Método desconhecido" } }); break;
            }
        }
        return 0;
    }

    public static async Task<int> RunClaudeAsync(string[] args)
    {
        if (args.Contains("auth"))
        {
            Emit(new { loggedIn = true, authMethod = args[0] == "claude-api" ? "api_key" : "claude.ai", apiProvider = "firstParty", subscriptionType = "pro" });
            return 0;
        }
        var index = Array.FindIndex(args, a => a is "--session-id" or "--resume");
        var session = args[index + 1];
        var prompt = await Console.In.ReadToEndAsync();
        if (args[0] == "claude-wait") { await Task.Delay(TimeSpan.FromMinutes(5)); return 0; }
        Emit(new { type = "system", subtype = "init", session_id = session, model = "fixture-model", plugins = Array.Empty<object>(), mcp_servers = Array.Empty<object>() });
        Emit(new { type = "stream_event", session_id = session, @event = new { delta = new { type = "text_delta", text = "Parcial" } } });
        if (args[0] == "claude-exit") return 7;
        Emit(new { type = "result", session_id = session, subtype = args[0] == "claude-failed" ? "error_max_turns" : "success",
            is_error = args[0] == "claude-failed", result = prompt.TrimEnd(), errors = new[] { "Limite de teste" },
            permission_denials = args[0] == "claude-denied" ? new[] { new { tool_name = "Write" } } : [] });
        return 0;
    }

    private static void Finish(string turn, string status) => Notify("turn/completed", new { threadId = ThreadId,
        turn = new { id = turn, status, error = new { message = "Falha controlada" },
            items = new[] { new { id = "m1", type = "agentMessage", text = "Resposta final.", phase = "final_answer" } } } });
    private static void Notify(string method, object parameters) => Emit(new { method, @params = parameters });
    private static void Reply(JsonElement id, object result) => Emit(new { id, result });
    private static void Emit(object data) => Console.WriteLine(JsonSerializer.Serialize(data));
}
