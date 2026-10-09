using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Providers;

public sealed partial class ClaudeConversationProvider : IProviderExtensionReader
{
    public async Task<ProviderExtensionSnapshot> ReadExtensionsAsync(string directory, CancellationToken token)
    {
        try { return await ReadExtensionsCoreAsync(directory, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ProviderException or JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new ProviderException("Inventário de extensões Claude não concluído; detalhes internos omitidos."); }
    }

    private async Task<ProviderExtensionSnapshot> ReadExtensionsCoreAsync(string directory, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        token = deadline.Token;
        await CheckSubscriptionAsync(directory, token).ConfigureAwait(false);
        var extensions = new List<ProviderExtension>();
        var warnings = new List<string>
        {
            "Comandos de inicialização podem incluir skills, mas não identificam todas as skills disponíveis ao modelo.",
            "Plugins listados estão instalados/habilitados; carga e uso não são comprovados por esta consulta.",
            "Metadados MCP não comprovam execução de ferramentas. Controles interativos não suportados são recusados."
        };
        var plugins = await ProcessProbe.RunAsync(Launch, ["plugin", "list", "--json"], directory, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        if (plugins.ExitCode != 0 || plugins.Truncated || plugins.TimedOut)
            throw new ProviderException("Não foi possível obter o inventário de plugins Claude; saída interna omitida.");
        using (var document = JsonDocument.Parse(plugins.StandardOutput))
        {
            var list = document.RootElement;
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 2000)
                throw new ProviderException("Inventário de plugins Claude inválido ou acima do limite local.");
            foreach (var plugin in list.EnumerateArray())
                extensions.Add(new(ProviderExtensionKind.Plugin, ExtensionMetadata.Name(plugin, "id"),
                    plugin.GetProperty("enabled").GetBoolean(),
                    ExtensionMetadata.Known(plugin, "scope", "user", "project", "local", "managed", "session", "synced")));
        }

        // Same native settings/extensions as normal execution. No user message, model turn, or persistence.
        // Native initialization can start configured MCPs and hooks; never use bare/safe-mode to conceal them.
        TaskCompletionSource<JsonElement>? pending = null;
        string? pendingId = null;
        var control = 0;
        ProviderProcess? controlProcess = null;
        await using var process = new ProviderProcess(Launch,
            ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--permission-prompt-tool", "stdio", "--permission-mode", "manual", "--permission-prompts", "host", "--no-session-persistence"],
            directory, ReceiveAsync);
        controlProcess = process;
        var initialized = await RequestAsync("initialize", "sintonia-extension-initialize").ConfigureAwait(false);
        foreach (var command in ExtensionMetadata.Array(initialized, "commands").EnumerateArray())
            extensions.Add(new(ProviderExtensionKind.Command, ExtensionMetadata.Name(command, "name")));
        var mcp = await RequestAsync("mcp_status", "sintonia-extension-mcp").ConfigureAwait(false);
        foreach (var server in ExtensionMetadata.Array(mcp, "mcpServers").EnumerateArray())
        {
            var failed = server.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
            extensions.Add(new(ProviderExtensionKind.Mcp, ExtensionMetadata.Name(server, "name"),
                Scope: ExtensionMetadata.Known(server, "scope", "user", "project", "local", "managed", "dynamic", "claudeai"),
                Status: ExtensionMetadata.Known(server, "status", "connected", "failed", "needs-auth", "pending", "disabled"),
                ToolCount: ExtensionMetadata.ToolCount(server), DiscoveryFailed: failed));
            if (failed)
                warnings.Add("Um MCP informou falha; mensagem interna omitida.");
        }
        if (control > 0) warnings.Add($"Controles interativos recusados durante o diagnóstico: {control}.");
        return new(Kind, extensions, warnings);

        async Task<JsonElement> RequestAsync(string subtype, string id)
        {
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingId = id;
            pending = completion;
            await process.WriteLineAsync(JsonSerializer.Serialize(new { type = "control_request", request_id = id, request = new { subtype } }), token).ConfigureAwait(false);
            var winner = await Task.WhenAny(completion.Task, process.Completion).WaitAsync(token).ConfigureAwait(false);
            if (winner == process.Completion && !completion.Task.IsCompleted)
                throw new ProviderException("Claude encerrou o diagnóstico antes de responder; saída interna omitida.");
            return await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }

        async ValueTask ReceiveAsync(string line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() == "control_response")
            {
                var response = root.GetProperty("response");
                if (response.GetProperty("request_id").GetString() != pendingId) return;
                if (response.GetProperty("subtype").GetString() == "success")
                    pending?.TrySetResult(response.GetProperty("response").Clone());
                else pending?.TrySetException(new ProviderException("Claude recusou a consulta de metadados; mensagem interna omitida."));
            }
            else if (root.GetProperty("type").GetString() == "control_request")
            {
                if (++control > 16) throw new ProviderException("Claude excedeu o limite de controles do diagnóstico.");
                var id = ExtensionMetadata.Name(root, "request_id");
                var target = controlProcess ?? throw new ProviderException("Controle Claude recebido antes de conectar o diagnóstico.");
                await target.WriteLineAsync(JsonSerializer.Serialize(new { type = "control_response", response = new
                {
                    subtype = "error", request_id = id, error = "Diagnóstico de metadados: solicitação interativa recusada."
                } }), token).ConfigureAwait(false);
            }
        }
    }
}
