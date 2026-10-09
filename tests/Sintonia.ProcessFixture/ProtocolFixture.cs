using System.Text.Json;

internal static class ProtocolFixture
{
    private const string ThreadId = "c9511c7e-3bc3-4b0d-bfff-a602c04e99a1";
    public static async Task<int> RunCodexAsync(string scenario)
    {
        string? pendingTurn = null;
        var metadataPage = 0;
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
                case "model/list": Reply(id, new { data = new[] { new { model = "fixture-model", displayName = "Teste", isDefault = true, hidden = false } } }); break;
                case "skills/list":
                    if (!parameters.GetProperty("forceReload").GetBoolean()) return 20;
                    Reply(id, new { data = new[] { new { skills = new[]
                    {
                        new { name = "active-skill", enabled = true, scope = "user", pluginId = (string?)"example-plugin" },
                        new { name = "disabled-skill", enabled = false, scope = "repo", pluginId = (string?)null }
                    }, errors = new[] { new { message = "SEGREDO erro interno", path = "SEGREDO caminho" } } } } }); break;
                case "mcpServerStatus/list":
                    if (scenario == "rpc-extensions-wait") { await Task.Delay(TimeSpan.FromMinutes(1)); break; }
                    if (scenario == "rpc-extensions-exit") return 7;
                    if (scenario == "rpc-extensions-error") { Emit(new { id, error = new { code = -32601, message = "SEGREDO servidor" } }); break; }
                    if (scenario == "rpc-extensions-invalid") { Reply(id, new { data = "SEGREDO inválido" }); break; }
                    if (parameters.GetProperty("detail").GetString() != "toolsAndAuthOnly") return 21;
                    var firstPage = parameters.GetProperty("cursor").ValueKind == JsonValueKind.Null;
                    Reply(id, new { data = new[] { new
                    {
                        name = firstPage ? "first-server" : "second-server", runtimeStatus = scenario == "rpc-extensions-future" ? "SEGREDO estado futuro" : firstPage ? (string?)null : "authenticationRequired",
                        authStatus = scenario == "rpc-extensions-future" ? "SEGREDO autenticação futura" : firstPage ? "unsupported" : "notLoggedIn", pluginId = "example-plugin",
                        tools = new Dictionary<string, object> { ["example"] = new { description = "SEGREDO descrição", inputSchema = new { secret = "SEGREDO" } } },
                        toolsError = firstPage ? (string?)null : "SEGREDO erro", config = new { env = "SEGREDO ambiente", url = "SEGREDO URL" }
                    } }, nextCursor = scenario == "rpc-extensions-many" ? "page-" + ++metadataPage : firstPage || scenario == "rpc-extensions-repeat" ? "page-2" : (string?)null }); break;
                case "account/read": Reply(id, new { account = new { type = scenario == "rpc-api" ? "apiKey" : "chatgpt" } }); break;
                case "account/rateLimits/read":
                    if (scenario == "rpc-usage-wait") { await Task.Delay(TimeSpan.FromMinutes(1)); break; }
                    if (scenario == "rpc-usage-unsupported") { Emit(new { id, error = new { code = -32601, message = "SEGREDO: resposta interna não deve aparecer no painel" } }); break; }
                    if (scenario == "rpc-usage-invalid") { Reply(id, new { rateLimits = new { primary = new { usedPercent = "inválido" } } }); break; }
                    if (scenario == "rpc-usage-denied-invalid") { Reply(id, new { ordinaryUsageAllowed = false, rateLimits = new { primary = new { usedPercent = "inválido" } } }); break; }
                    Reply(id, new { ordinaryUsageAllowed = scenario is "rpc-usage-denied" or "rpc-usage-denied-invalid" ? false : scenario == "rpc-usage-unknown" ? (bool?)null : true,
                        accountId = "SEGREDO: identidade não deve sair do parser", rateLimits = new { primary = new { usedPercent = 90 } },
                        rateLimitsByLimitId = new Dictionary<string, object> { ["codex"] = new { limitId = "codex", limitName = "Codex",
                            primary = new { usedPercent = scenario is "rpc-usage-full-allowed" or "rpc-usage-unknown" ? 100 : 25, windowDurationMins = 300, resetsAt = 1893456000L },
                            secondary = new { usedPercent = 40, windowDurationMins = 10080, resetsAt = (long?)null } } } }); break;
                case "thread/read": Reply(id, new { thread = new { id = ThreadId, cwd = scenario == "rpc-wrong-directory" ? "C:\\elsewhere" : Environment.CurrentDirectory, modelProvider = "openai" } }); break;
                case "thread/start":
                case "thread/resume":
                    if (scenario.StartsWith("rpc-extensions", StringComparison.Ordinal)) return 22;
                    if (method.GetString() == "thread/resume" && scenario == "rpc-tokens-baseline")
                        Tokens("previous-turn", Breakdown(1000, 200, 100, 20, 50));
                    if (scenario.StartsWith("rpc-usage-", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "thread-started.txt"), "SIMULAÇÃO");
                    Reply(id, new { thread = new { id = ThreadId }, model = "fixture-model", modelProvider = "openai", approvalPolicy = parameters.GetProperty("approvalPolicy").GetString(),
                        sandbox = new { type = scenario == "rpc-unsafe" ? "dangerFullAccess" : parameters.GetProperty("sandbox").GetString() == "read-only" ? "readOnly" : "workspaceWrite" } }); break;
                case "turn/start":
                    var turn = Guid.NewGuid().ToString();
                    Reply(id, new { turn = new { id = turn } });
                    if (scenario.StartsWith("rpc-tokens-", StringComparison.Ordinal))
                    {
                        if (scenario == "rpc-tokens-malformed")
                            Notify("thread/tokenUsage/updated", new { threadId = ThreadId, turnId = turn, tokenUsage = new { total = new { inputTokens = "SEGREDO" } } });
                        else
                        {
                            var resumed = parameters.GetProperty("threadId").GetString() == ThreadId && scenario is "rpc-tokens-baseline" or "rpc-tokens-no-baseline";
                            var baseInput = resumed ? 1000 : 0; var baseOutput = resumed ? 200 : 0;
                            Tokens("another-turn", Breakdown(99999, 99999, 1, 1, 1));
                            var firstUsage = Breakdown(baseInput + 10, baseOutput + 3, (resumed ? 100 : 0) + 2, (resumed ? 20 : 0) + 1, (resumed ? 50 : 0) + 1);
                            Tokens(turn, firstUsage); Tokens(turn, firstUsage);
                            if (scenario == "rpc-tokens-reset") { Tokens(turn, Breakdown(4, 1, 0, 0, 0)); Tokens(turn, Breakdown(7, 3, 1, 1, 1)); }
                            else Tokens(turn, Breakdown(baseInput + 30, baseOutput + 10, (resumed ? 100 : 0) + 6, (resumed ? 20 : 0) + 3, (resumed ? 50 : 0) + 4));
                        }
                    }
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
                    else if (scenario is "rpc-interrupt" or "rpc-tokens-wait") pendingTurn = turn;
                    else if (scenario == "rpc-exit") return 7;
                    else Finish(turn, scenario is "rpc-failed" or "rpc-tokens-failed" ? "failed" : "completed");
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
        if (args[0].StartsWith("claude-extensions", StringComparison.Ordinal)) return await RunClaudeExtensionsAsync(args);
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
        var failed = args[0] is "claude-failed" or "claude-tokens-failed";
        Emit(new { type = "result", session_id = session, subtype = failed ? "error_max_turns" : "success",
            is_error = failed, result = prompt, errors = new[] { "Limite de teste" },
            usage = args[0].StartsWith("claude-tokens-", StringComparison.Ordinal) ? new { input_tokens = 10, output_tokens = 5, cache_read_input_tokens = 20, cache_creation_input_tokens = 3 } : null,
            modelUsage = new { restored_history_and_subagents = new { inputTokens = 999999, outputTokens = 999999 } }, total_cost_usd = 99999,
            permission_denials = args[0] == "claude-denied" ? new[] { new { tool_name = "Write" } } : [] });
        if ((await Console.In.ReadToEndAsync()).Length != 0) return 16; // No late answer after withdrawal/terminal result.
        return 0;
    }

    private static async Task<int> RunClaudeExtensionsAsync(string[] args)
    {
        if (args.Contains("plugin"))
        {
            Emit(new[] { new { id = "example@market", enabled = true, scope = "user", installPath = "SEGREDO caminho" },
                new { id = "disabled@market", enabled = false, scope = "project", installPath = "SEGREDO caminho" } });
            return 0;
        }
        if (!args.Contains("--no-session-persistence") || !args.Contains("manual") || !args.Contains("host")
            || args.Any(a => a is "--bare" or "--safe-mode" or "--dangerously-skip-permissions" or "--system-prompt")) return 23;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() == "control_response")
            {
                if (root.GetProperty("response").GetProperty("subtype").GetString() != "error") return 24;
                continue;
            }
            if (root.GetProperty("type").GetString() != "control_request") return 25; // Never a user prompt.
            var id = root.GetProperty("request_id").GetString();
            var subtype = root.GetProperty("request").GetProperty("subtype").GetString();
            if (subtype == "initialize")
            {
                if (args[0] == "claude-extensions-control") Emit(new { type = "control_request", request_id = "tool-1", request = new { subtype = "can_use_tool", tool_name = "Write", input = new { secret = "SEGREDO" } } });
                Emit(new { type = "control_response", response = new { subtype = "success", request_id = id,
                    response = new { commands = new[] { new { name = "example:review", description = "SEGREDO descrição" } }, account = "SEGREDO identidade" } } });
            }
            else if (subtype == "mcp_status")
            {
                if (args[0] == "claude-extensions-wait") { await Task.Delay(TimeSpan.FromMinutes(1)); continue; }
                if (args[0] == "claude-extensions-exit") return 7;
                if (args[0] == "claude-extensions-error") { Emit(new { type = "control_response", response = new { subtype = "error", request_id = id, error = "SEGREDO interno" } }); continue; }
                if (args[0] == "claude-extensions-invalid") { Emit(new { type = "control_response", response = new { subtype = "success", request_id = id, response = new { mcpServers = "SEGREDO inválido" } } }); continue; }
                Emit(new { type = "control_response", response = new { subtype = "success", request_id = id,
                    response = new { mcpServers = new[] { new { name = "example-mcp", status = "needs-auth", scope = "user", error = "SEGREDO erro",
                        tools = new[] { new { name = "example", description = "SEGREDO" } }, config = new { env = "SEGREDO", headers = "SEGREDO" } } } } } });
            }
            else return 26;
        }
        return 0;
    }

    private static void Finish(string turn, string status) => Notify("turn/completed", new { threadId = ThreadId,
        turn = new { id = turn, status, error = new { message = "Falha controlada" },
            items = new[] { new { id = "m1", type = "agentMessage", text = "Resposta final.", phase = "final_answer" } } } });
    private static void Notify(string method, object parameters) => Emit(new { method, @params = parameters });
    private static object Breakdown(long input, long output, long read, long write, long reasoning) => new
    { inputTokens = input, outputTokens = output, totalTokens = input + output, cachedInputTokens = read, cacheWriteInputTokens = write, reasoningOutputTokens = reasoning };
    private static void Tokens(string turn, object total) => Notify("thread/tokenUsage/updated", new { threadId = ThreadId, turnId = turn,
        tokenUsage = new { total, last = Breakdown(888, 222, 20, 10, 30) } });
    private static void Reply(JsonElement id, object result) => Emit(new { id, result });
    private static void Emit(object data) => Console.WriteLine(JsonSerializer.Serialize(data));
}
