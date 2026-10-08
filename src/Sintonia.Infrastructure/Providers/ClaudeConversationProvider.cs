using System.Text;
using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Providers;

public sealed class ClaudeConversationProvider(ExecutableLaunch? executable = null) : IConversationProvider
{
    public ProviderKind Kind => ProviderKind.Claude;
    private ExecutableLaunch Launch => executable ?? ExecutableLocator.Find(Kind)
        ?? throw new ProviderException("Claude não encontrado. Instale e entre no Claude Code com sua assinatura.");

    public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress,
        CancellationToken cancellationToken)
    {
        CodexConversationProvider.ValidateRequest(request);
        await CheckSubscriptionAsync(request.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        var id = request.NativeSessionId ?? Guid.NewGuid().ToString();
        var arguments = new List<string> { "--print", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--permission-mode", request.Access == ConversationAccess.ReadOnly ? "plan" : "manual", "--permission-prompts", "none",
            request.NativeSessionId is null ? "--session-id" : "--resume", id };
        if (!string.IsNullOrWhiteSpace(request.Instructions)) arguments.AddRange(["--append-system-prompt", request.Instructions]);
        if (!string.IsNullOrWhiteSpace(request.Model)) arguments.AddRange(["--model", request.Model]);
        var parser = new ClaudeStreamParser(id, progress);
        await using var process = new ProviderProcess(Launch, arguments, request.WorkingDirectory, line =>
        {
            parser.Accept(line);
            return ValueTask.CompletedTask;
        });
        try
        {
            await process.WriteLineAsync(request.Prompt, token).ConfigureAwait(false);
            process.CloseInput();
            await process.Completion.WaitAsync(token).ConfigureAwait(false);
            return parser.GetResult(process.ExitCode);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ProviderException("Claude excedeu o limite de cinco minutos e foi encerrado.");
        }
    }

    public async Task CheckSubscriptionAsync(string directory, CancellationToken token)
    {
        // Check presence only: never expose values or read credential stores.
        string[] incompatible = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY"];
        if (incompatible.Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            throw new ProviderException("Claude possui configuração de API/provedor externo no ambiente. Use um ambiente autenticado pela assinatura.");
        var probe = await ProcessProbe.RunAsync(Launch, ["auth", "status", "--json"], directory, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (probe.ExitCode != 0 || probe.TimedOut || probe.Truncated) throw new ProviderException("Não foi possível conferir o login Claude.");
        using var status = JsonDocument.Parse(probe.StandardOutput);
        var root = status.RootElement;
        if (!root.GetProperty("loggedIn").GetBoolean() || root.GetProperty("authMethod").GetString() != "claude.ai"
            || root.GetProperty("apiProvider").GetString() != "firstParty"
            || !root.TryGetProperty("subscriptionType", out var subscription) || string.IsNullOrWhiteSpace(subscription.GetString()))
            throw new ProviderException("Entre no Claude Code pela sua assinatura. O Sintonia não muda automaticamente para API.");
    }
}

/// <summary>Parses documented stream events; accepts additive fields, rejects missing terminal success.</summary>
public sealed class ClaudeStreamParser(string sessionId, IProgress<ConversationEvent> progress)
{
    private string _model = "";
    private JsonElement? _result;
    private readonly HashSet<string> _denials = [];
    private readonly StringBuilder _fallback = new();
    public void Accept(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.TryGetProperty("session_id", out var session) && session.GetString() != sessionId)
            throw new ProviderException("Claude retornou um identificador de sessão diferente do solicitado.");
        switch (root.GetProperty("type").GetString())
        {
            case "system" when root.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "init":
                _model = root.GetProperty("model").GetString()!;
                progress.Report(new(ConversationEventKind.Session, "Sessão Claude conectada por assinatura.", sessionId, _model));
                if (root.TryGetProperty("plugins", out var plugins))
                    foreach (var plugin in plugins.EnumerateArray()) progress.Report(new(ConversationEventKind.Diagnostic, "Plugin: " + plugin.GetProperty("name").GetString()));
                if (root.TryGetProperty("mcp_servers", out var mcp))
                    foreach (var server in mcp.EnumerateArray()) progress.Report(new(ConversationEventKind.Diagnostic,
                        "MCP: " + server.GetProperty("name").GetString() + " — " + server.GetProperty("status").GetString()));
                if (root.TryGetProperty("skills", out var skills))
                    foreach (var skill in skills.EnumerateArray()) progress.Report(new(ConversationEventKind.Diagnostic, "Skill: " + skill.GetString()));
                foreach (var field in new[] { "plugin_errors", "mcp_server_errors" })
                    if (root.TryGetProperty(field, out var errors) && errors.GetArrayLength() > 0)
                        progress.Report(new(ConversationEventKind.Diagnostic, $"Extensões com falha: {field} ({errors.GetArrayLength()})."));
                break;
            case "system" when root.TryGetProperty("subtype", out var denied) && denied.GetString() == "permission_denied":
                RecordDenial(root.TryGetProperty("tool_name", out var name) ? name.GetString()! : "ferramenta");
                break;
            case "stream_event":
                var ev = root.GetProperty("event");
                if (ev.TryGetProperty("delta", out var delta) && delta.TryGetProperty("type", out var type) && type.GetString() == "text_delta")
                    progress.Report(new(ConversationEventKind.TextDelta, delta.GetProperty("text").GetString()!));
                break;
            case "assistant" when !root.TryGetProperty("parent_tool_use_id", out var parent) || parent.ValueKind == JsonValueKind.Null:
                foreach (var content in root.GetProperty("message").GetProperty("content").EnumerateArray())
                {
                    if (content.GetProperty("type").GetString() == "text")
                    {
                        var text = content.GetProperty("text").GetString()!;
                        _fallback.Append(text.AsSpan(0, Math.Min(text.Length, 256_000 - _fallback.Length)));
                    }
                    else if (content.GetProperty("type").GetString() == "tool_use")
                        progress.Report(new(ConversationEventKind.Tool, content.GetProperty("name").GetString()!));
                }
                break;
            case "result":
                _result = root.Clone();
                if (root.TryGetProperty("permission_denials", out var denials))
                    foreach (var denial in denials.EnumerateArray()) RecordDenial(denial.GetProperty("tool_name").GetString()!);
                break;
        }
    }

    public ConversationResult GetResult(int exitCode)
    {
        if (_result is not { } result || exitCode != 0) throw new ProviderException($"Claude terminou sem resultado válido (código {exitCode}).");
        if (result.GetProperty("is_error").GetBoolean() || result.GetProperty("subtype").GetString() != "success")
            throw new ProviderException("Claude: " + (result.TryGetProperty("errors", out var errors)
                ? string.Join("; ", errors.EnumerateArray().Select(e => e.GetString())) : "execução sem conclusão válida."));
        var text = result.TryGetProperty("result", out var body) ? body.GetString() : _fallback.ToString();
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_model)) throw new ProviderException("Claude terminou sem resposta ou modelo identificado.");
        if (text.Length > 256_000) text = text[..256_000] + "\n[Resposta truncada pelo limite local]";
        return new(sessionId, _model, text, _denials.Count == 0 ? ConversationOutcome.Completed : ConversationOutcome.Blocked, _denials.ToArray());
    }

    private void RecordDenial(string tool)
    {
        if (_denials.Add(tool)) progress.Report(new(ConversationEventKind.PermissionDenied, "Permissão recusada: " + tool));
    }
}
