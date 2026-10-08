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
                    if (scenario is "rpc-file-preview" or "rpc-file-no-preview")
                    {
                        pendingTurn = turn;
                        if (scenario == "rpc-file-preview") Notify("item/started", new { threadId = ThreadId, turnId = turn,
                            item = new { id = "file1", type = "fileChange", changes = new[] { new { path = "amostra.txt", diff = "-antes\n+depois" } } } });
                        Emit(new { id = "permission-1", method = "item/fileChange/requestApproval", @params = new { threadId = ThreadId, turnId = turn, itemId = "file1", reason = "alterar" } });
                    }
                    else if (scenario == "rpc-denied")
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
        if (!args.Contains("--input-format") || !args.Contains("--permission-prompt-tool") || !args.Contains("stdio")
            || !args.Contains("host") || args.Contains("none") || args.Contains("--dangerously-skip-permissions")) return 8;
        using var initialize = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
        var init = initialize.RootElement;
        if (init.GetProperty("type").GetString() != "control_request" || init.GetProperty("request").GetProperty("subtype").GetString() != "initialize") return 9;
        Emit(new { type = "control_response", response = new { subtype = args[0] == "claude-init-failed" ? "error" : "success",
            request_id = init.GetProperty("request_id").GetString(), response = new { } } });
        if (args[0] == "claude-init-failed") return 0;
        using var user = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
        var prompt = user.RootElement.GetProperty("message").GetProperty("content").GetString()!;
        if (user.RootElement.GetProperty("type").GetString() != "user" || user.RootElement.GetProperty("session_id").GetString() != session) return 10;
        if (args[0] == "claude-wait") { await Task.Delay(TimeSpan.FromMinutes(5)); return 0; }
        Emit(new { type = "system", subtype = "init", session_id = session, model = "fixture-model", plugins = Array.Empty<object>(), mcp_servers = Array.Empty<object>() });
        Emit(new { type = "stream_event", session_id = session, @event = new { delta = new { type = "text_delta", text = "Parcial" } } });
        if (args[0] == "claude-exit") return 7;
        if (args[0].StartsWith("claude-host", StringComparison.Ordinal))
        {
            var tool = args[0] switch { "claude-host-edit" => "Edit", "claude-host-command" => "Bash",
                "claude-host-mcp" => "mcp__example__action", "claude-host-question" => "AskUserQuestion", _ => "Write" };
            object input = args[0] switch
            {
                "claude-host-edit" => new { file_path = "ação.txt", old_string = "antes", new_string = "depois", replace_all = false },
                "claude-host-command" => new { command = "echo literal $(não executar)", description = "comando de teste" },
                "claude-host-mcp" => new { project = "projeto de teste", action = "ação externa de teste" },
                "claude-host-no-preview" => new { },
                "claude-host-large" => new { file_path = "ação.txt", content = new string('á', 65_000) },
                _ => new { file_path = "ação.txt", content = "ação\n$(literal) &|" }
            };
            var request = new { type = "control_request", request_id = "permission-1",
                request = new { subtype = args[0] == "claude-host-unknown" ? "future_control" : "can_use_tool", tool_name = tool, input } };
            Emit(request);
            Emit(new { type = "stream_event", session_id = session, @event = new { delta = new { type = "text_delta", text = "Evento durante autorização" } } });
            if (args[0] == "claude-host-exit") return 7;
            if (args[0] == "claude-host-duplicate") { Emit(request); await Console.In.ReadToEndAsync(); return 11; }
            if (args[0] == "claude-host-premature-result")
            {
                Emit(new { type = "result", session_id = session, subtype = "success", is_error = false, result = "Conclusão indevida" });
                await Console.In.ReadToEndAsync(); return 17;
            }
            if (args[0] == "claude-host-cancel")
            {
                // Give the host time to display the pending decision before withdrawing it.
                await Task.Delay(100);
                Emit(new { type = "control_cancel_request", request_id = "permission-1" });
            }
            else
            {
                using var answer = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
                var response = answer.RootElement.GetProperty("response");
                if (answer.RootElement.GetProperty("type").GetString() != "control_response"
                    || response.GetProperty("request_id").GetString() != "permission-1") return 12;
                if (args[0] == "claude-host-unknown")
                {
                    if (response.GetProperty("subtype").GetString() != "error") return 13;
                }
                else
                {
                    var decision = response.GetProperty("response");
                    if (decision.TryGetProperty("updatedPermissions", out _)) return 14;
                    if (decision.GetProperty("behavior").GetString() == "allow"
                        && !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(input), decision.GetProperty("updatedInput"))) return 15;
                    if (args[0] == "claude-host-two")
                    {
                        if (decision.GetProperty("behavior").GetString() != "allow") return 18;
                        Emit(new { type = "control_request", request_id = "permission-2",
                            request = new { subtype = "can_use_tool", tool_name = "Write", input = new { file_path = "outra-ação.txt", content = "segundo" } } });
                        using var second = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
                        var secondResponse = second.RootElement.GetProperty("response");
                        if (secondResponse.GetProperty("request_id").GetString() != "permission-2"
                            || secondResponse.GetProperty("response").GetProperty("behavior").GetString() != "deny") return 19;
                    }
                }
            }
        }
        Emit(new { type = "result", session_id = session, subtype = args[0] == "claude-failed" ? "error_max_turns" : "success",
            is_error = args[0] == "claude-failed", result = prompt, errors = new[] { "Limite de teste" },
            permission_denials = args[0] == "claude-denied" ? new[] { new { tool_name = "Write" } } : [] });
        if ((await Console.In.ReadToEndAsync()).Length != 0) return 16; // No late answer after withdrawal/terminal result.
        return 0;
    }

    private static void Finish(string turn, string status) => Notify("turn/completed", new { threadId = ThreadId,
        turn = new { id = turn, status, error = new { message = "Falha controlada" },
            items = new[] { new { id = "m1", type = "agentMessage", text = "Resposta final.", phase = "final_answer" } } } });
    private static void Notify(string method, object parameters) => Emit(new { method, @params = parameters });
    private static void Reply(JsonElement id, object result) => Emit(new { id, result });
    private static void Emit(object data) => Console.WriteLine(JsonSerializer.Serialize(data));
}
