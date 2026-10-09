using System.Text.Json;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Providers;

public sealed partial class CodexConversationProvider : IProviderExtensionReader
{
    private static string ConnectionDescription(string? status) => status switch
    {
        "notStarted" => "não iniciado", "starting" => "conectando", "connected" => "conectado",
        "authenticationRequired" => "autenticação necessária", "failed" => "falha", "cancelled" => "cancelado",
        "disabled" => "desabilitado", _ => "estado de sessão indisponível"
    };

    private static string AuthenticationDescription(string? status) => status switch
    {
        "unknown" => "desconhecida", "unsupported" => "não suportada pelo servidor", "notLoggedIn" => "login necessário",
        "bearerToken" => "token configurado", "oAuth" => "OAuth", _ => "indisponível"
    };

    public async Task<ProviderExtensionSnapshot> ReadExtensionsAsync(string directory, CancellationToken token)
    {
        try { return await ReadExtensionsCoreAsync(directory, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ProviderException or JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new ProviderException("Inventário de extensões Codex não concluído; detalhes internos omitidos."); }
    }

    private async Task<ProviderExtensionSnapshot> ReadExtensionsCoreAsync(string directory, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await using var rpc = new JsonRpcClient(Launch, directory);
        await InitializeAsync(rpc, deadline.Token).ConfigureAwait(false);
        return await ReadExtensionsAsync(rpc, directory, deadline.Token).ConfigureAwait(false);
    }

    private async Task<ProviderExtensionSnapshot> ReadExtensionsAsync(JsonRpcClient rpc, string directory, CancellationToken token)
    {
        var extensions = new List<ProviderExtension>();
        var warnings = new List<string>
        {
            "Plugins: inventário indisponível no contrato estável; plugin/list está em desenvolvimento e não é chamado.",
            "Metadados não comprovam uso das ferramentas. MCPs que solicitam elicitation são recusados pelo Sintonia."
        };
        var skills = await rpc.RequestAsync("skills/list", new { cwds = new[] { directory }, forceReload = true }, token).ConfigureAwait(false);
        foreach (var entry in ExtensionMetadata.Array(skills, "data").EnumerateArray())
        {
            foreach (var skill in ExtensionMetadata.Array(entry, "skills").EnumerateArray())
                extensions.Add(new(ProviderExtensionKind.Skill, ExtensionMetadata.Name(skill, "name"),
                    skill.GetProperty("enabled").GetBoolean(),
                    ExtensionMetadata.Known(skill, "scope", "user", "repo", "system", "admin"),
                    PluginId: ExtensionMetadata.OptionalName(skill, "pluginId")));
            var errors = ExtensionMetadata.Array(entry, "errors").GetArrayLength();
            if (errors > 0) warnings.Add($"Skills com falha de descoberta: {errors}. Mensagens internas omitidas.");
        }

        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var pageNumber = 0; pageNumber < 20; pageNumber++)
        {
            var page = await rpc.RequestAsync("mcpServerStatus/list", new { cursor, limit = 100, detail = "toolsAndAuthOnly" }, token).ConfigureAwait(false);
            foreach (var server in ExtensionMetadata.Array(page, "data").EnumerateArray())
            {
                var failed = server.TryGetProperty("toolsError", out var error) && error.ValueKind != JsonValueKind.Null;
                extensions.Add(new(ProviderExtensionKind.Mcp, ExtensionMetadata.Name(server, "name"),
                    Status: ExtensionMetadata.Known(server, "runtimeStatus", "notStarted", "starting", "connected", "authenticationRequired", "failed", "cancelled", "disabled"),
                    AuthStatus: ExtensionMetadata.Known(server, "authStatus", "unknown", "unsupported", "notLoggedIn", "bearerToken", "oAuth"),
                    ToolCount: ExtensionMetadata.ToolCount(server), PluginId: ExtensionMetadata.OptionalName(server, "pluginId"), DiscoveryFailed: failed));
                if (failed)
                    warnings.Add("Um MCP informou falha de descoberta de ferramentas; mensagem interna omitida.");
            }
            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind != JsonValueKind.Null ? next.GetString() : null;
            if (cursor is null) return new(Kind, extensions, warnings);
            if (!cursors.Add(cursor)) throw new ProviderException("Codex repetiu o cursor do inventário MCP; consulta interrompida.");
        }
        throw new ProviderException("Inventário MCP excedeu o limite de vinte páginas; consulta incompleta.");
    }
}
