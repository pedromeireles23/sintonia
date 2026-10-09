using System.Text.Json;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Providers;

/// <summary>Only the installed account/rateLimits/read display and admission fields; no identities, credit balances or upsell data.</summary>
public static class CodexUsageParser
{
    public static ProviderUsageSnapshot Parse(JsonElement data, DateTimeOffset checkedAt)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException("Resposta de limites incompatível.");
        var buckets = new List<ProviderUsageBucket>();
        if (data.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind != JsonValueKind.Null)
        {
            if (map.ValueKind != JsonValueKind.Object) throw new FormatException("Categorias de limites incompatíveis.");
            foreach (var bucket in map.EnumerateObject())
            {
                if (buckets.Count == 32) throw new FormatException("A consulta excedeu o limite de categorias.");
                buckets.Add(Bucket(bucket.Name, bucket.Value));
            }
        }
        else if (data.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            buckets.Add(Bucket(Text(legacy, "limitId") ?? "codex", legacy));
        var allowed = IncludedUsageAllowed(data);
        var snapshot = new ProviderUsageSnapshot(ProviderKind.Codex, checkedAt, buckets, allowed);
        try { snapshot.ValidateDefinition(); }
        catch (ArgumentException) { throw new FormatException("Dados de limites incompatíveis."); }
        return snapshot.HasData ? snapshot : snapshot with { UnavailableReason = "O Codex não informou percentuais ou disponibilidade de uso nesta consulta." };
    }
    public static bool? IncludedUsageAllowed(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException("Resposta de limites incompatível.");
        if (!data.TryGetProperty("ordinaryUsageAllowed", out var permission) || permission.ValueKind == JsonValueKind.Null) return null;
        if (permission.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException("Disponibilidade de uso incompatível.");
        return permission.GetBoolean();
    }
    private static ProviderUsageBucket Bucket(string id, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException("Categoria de limites incompatível.");
        return new(id, Text(data, "limitName"), Window(data, "primary"), Window(data, "secondary"));
    }
    private static ProviderUsageWindow? Window(JsonElement data, string field)
    {
        if (!data.TryGetProperty(field, out var window) || window.ValueKind == JsonValueKind.Null) return null;
        if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetInt32(out var percent))
            throw new FormatException("Percentual de uso incompatível.");
        DateTimeOffset? reset = null; var timestamp = Integer(window, "resetsAt");
        if (timestamp is { } seconds)
        {
            try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { throw new FormatException("Horário de renovação incompatível."); }
        }
        return new(percent, Integer(window, "windowDurationMins"), reset);
    }
    private static string? Text(JsonElement data, string field)
    {
        if (!data.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FormatException("Texto de limites incompatível.");
        return value.GetString();
    }
    private static long? Integer(JsonElement data, string field)
    {
        if (!data.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw new FormatException("Janela de limites incompatível.");
        return number;
    }
}
