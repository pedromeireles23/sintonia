using System.Text.Json;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Providers;

public sealed partial class CodexConversationProvider
{
    private static async Task<IReadOnlyList<ProviderModel>> ReadModelsAsync(JsonRpcClient rpc, CancellationToken token)
    {
        var models = new List<ProviderModel>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < 5; pageNumber++)
        {
            var page = await rpc.RequestAsync("model/list", new { cursor, limit = 100 }, token).ConfigureAwait(false);
            foreach (var entry in page.GetProperty("data").EnumerateArray())
            {
                if (entry.GetProperty("hidden").GetBoolean()) continue;
                var id = MetadataText(entry, "model", 120) ?? throw new ProviderException("Catálogo Codex sem identificador de modelo.");
                if (models.Any(m => m.Id == id)) throw new ProviderException("Catálogo Codex com modelo repetido.");
                var efforts = new List<ProviderReasoningEffort>();
                if (entry.TryGetProperty("supportedReasoningEfforts", out var options) && options.ValueKind == JsonValueKind.Array)
                    foreach (var option in options.EnumerateArray())
                    {
                        var effort = MetadataText(option, "reasoningEffort", 32);
                        if (effort is null || efforts.Any(e => e.Id == effort)) throw new ProviderException("Catálogo Codex com reasoning inválido.");
                        ModelSettings.ValidateEffort(effort);
                        efforts.Add(new(effort, MetadataText(option, "description", 240) ?? ""));
                        if (efforts.Count > 32) throw new ProviderException("Catálogo Codex acima do limite de opções de reasoning.");
                    }
                var defaultEffort = MetadataText(entry, "defaultReasoningEffort", 32);
                ModelSettings.ValidateEffort(defaultEffort);
                models.Add(new(id, MetadataText(entry, "displayName", 120) ?? id, entry.GetProperty("isDefault").GetBoolean(),
                    MetadataText(entry, "description", 400) ?? "", defaultEffort, efforts));
                if (models.Count > 500) throw new ProviderException("Catálogo Codex acima do limite local.");
            }
            cursor = MetadataText(page, "nextCursor", 1024);
            if (cursor is null) return models;
            if (!cursors.Add(cursor)) throw new ProviderException("Catálogo Codex repetiu a paginação.");
        }
        throw new ProviderException("Catálogo Codex não terminou dentro do limite de páginas.");
    }

    private static async Task<ProviderModelDefaults?> ReadModelDefaultsAsync(JsonRpcClient rpc, string directory, CancellationToken token)
    {
        // Project only model/effort and known provenance categories. Never publish config, layers or paths.
        try
        {
            var response = await rpc.RequestAsync("config/read", new { cwd = directory, includeLayers = false }, token).ConfigureAwait(false);
            var config = response.GetProperty("config");
            var model = MetadataText(config, "model", 120);
            var effort = MetadataText(config, "model_reasoning_effort", 32);
            var modelKey = "model"; var effortKey = "model_reasoning_effort";
            if (config.TryGetProperty("models", out var grouped) && grouped.ValueKind == JsonValueKind.Object
                && grouped.TryGetProperty("new_thread", out var fresh) && fresh.ValueKind == JsonValueKind.Object)
            {
                if (MetadataText(fresh, "model", 120) is { } freshModel) { model = freshModel; modelKey = "models.new_thread.model"; }
                if (MetadataText(fresh, "model_reasoning_effort", 32) is { } freshEffort) { effort = freshEffort; effortKey = "models.new_thread.model_reasoning_effort"; }
            }
            ModelSettings.ValidateEffort(effort);
            return new(model, effort, ConfigOrigin(response, modelKey), ConfigOrigin(response, effortKey));
        }
        catch (Exception exception) when (exception is ProviderException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { return null; }
    }

    private static string ConfigOrigin(JsonElement response, string key)
    {
        if (!response.TryGetProperty("origins", out var origins) || origins.ValueKind != JsonValueKind.Object
            || !origins.TryGetProperty(key, out var origin) || !origin.TryGetProperty("name", out var name))
            return "Configuração efetiva da instalação";
        return MetadataText(name, "type", 40) switch
        {
            "user" => MetadataText(name, "profile", 120) is null ? "Configuração do usuário" : "Perfil do usuário",
            "project" => "Configuração do projeto", "sessionFlags" => "Configuração desta execução",
            "packagedDefaults" => "Padrão do pacote instalado",
            "system" or "mdm" or "enterpriseManaged" or "legacyManagedConfigTomlFromFile" or "legacyManagedConfigTomlFromMdm" => "Configuração administrada",
            _ => "Configuração efetiva da instalação"
        };
    }

    internal static string? MetadataText(JsonElement value, string key, int limit)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String)
            return null;
        var text = field.GetString();
        return !string.IsNullOrWhiteSpace(text) && text.Length <= limit && !text.Any(char.IsControl) ? text : null;
    }
}
