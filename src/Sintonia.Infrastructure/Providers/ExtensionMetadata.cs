using System.Text.Json;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Providers;

internal static class ExtensionMetadata
{
    public static string Name(JsonElement entry, string property) => OptionalName(entry, property)
        ?? throw new ProviderException("Inventário de extensões contém um identificador inválido.");

    public static string? OptionalName(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ProviderException("Metadados de extensões inválidos.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 || text.Any(char.IsControl))
            throw new ProviderException("Inventário de extensões contém um identificador inválido.");
        return text;
    }

    public static string? Known(JsonElement entry, string property, params string[] values)
    {
        if (!entry.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return values.Contains(text, StringComparer.Ordinal) ? text : null;
    }

    public static JsonElement Array(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 2000)
            throw new ProviderException("Inventário de extensões ausente, inválido ou acima do limite local.");
        return value;
    }

    public static int? ToolCount(JsonElement entry)
    {
        if (!entry.TryGetProperty("tools", out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength(),
            JsonValueKind.Object => value.EnumerateObject().Count(),
            _ => null
        };
    }
}
