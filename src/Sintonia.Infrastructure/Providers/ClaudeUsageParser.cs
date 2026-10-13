using System.Globalization;
using System.Text.Json;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Providers;

/// <summary>Only the plan rate-limit windows of the experimental get_usage reply. Session cost, spend, credits,
/// subscription, breakdowns and behaviors are ignored, and blocking is never inferred from percentages.</summary>
public static class ClaudeUsageParser
{
    private static readonly (string Field, string Name)[] ScopedWeekly = [("seven_day_opus", "Opus · semanal"), ("seven_day_sonnet", "Sonnet · semanal")];

    public static ProviderUsageSnapshot Parse(JsonElement data, DateTimeOffset checkedAt)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException("Resposta de uso incompatível.");
        if (data.TryGetProperty("rate_limits_available", out var available) && available.ValueKind == JsonValueKind.False)
            return ProviderUsageSnapshot.Unavailable(ProviderKind.Claude, "O Claude Code informou que os limites da assinatura não estão disponíveis para esta conta.") with { CheckedAt = checkedAt };
        if (!data.TryGetProperty("rate_limits", out var limits) || limits.ValueKind == JsonValueKind.Null)
            return ProviderUsageSnapshot.Unavailable(ProviderKind.Claude, "O Claude Code não informou limites nesta consulta.") with { CheckedAt = checkedAt };
        if (limits.ValueKind != JsonValueKind.Object) throw new FormatException("Limites de uso incompatíveis.");
        var buckets = new List<ProviderUsageBucket>();
        var session = Window(limits, "five_hour", 300);
        var weekly = Window(limits, "seven_day", 10080);
        if (session is not null || weekly is not null) buckets.Add(new("claude", "Assinatura Claude", session, weekly));
        foreach (var (field, name) in ScopedWeekly)
            if (Window(limits, field, 10080) is { } scoped) buckets.Add(new(field, name, null, scoped));
        var snapshot = new ProviderUsageSnapshot(ProviderKind.Claude, checkedAt, buckets, null);
        try { snapshot.ValidateDefinition(); }
        catch (ArgumentException) { throw new FormatException("Dados de limites incompatíveis."); }
        return snapshot.HasData ? snapshot : snapshot with { UnavailableReason = "O Claude Code não informou percentuais nesta consulta." };
    }

    private static ProviderUsageWindow? Window(JsonElement limits, string field, long minutes)
    {
        if (!limits.TryGetProperty(field, out var window) || window.ValueKind == JsonValueKind.Null) return null;
        if (window.ValueKind != JsonValueKind.Object) throw new FormatException("Janela de uso incompatível.");
        if (!window.TryGetProperty("utilization", out var used) || used.ValueKind == JsonValueKind.Null) return null;
        if (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent is < 0 or > 1000)
            throw new FormatException("Percentual de uso incompatível.");
        DateTimeOffset? reset = null;
        if (window.TryGetProperty("resets_at", out var resets) && resets.ValueKind != JsonValueKind.Null)
        {
            if (resets.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(resets.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                throw new FormatException("Horário de renovação incompatível.");
            reset = parsed;
        }
        return new((int)Math.Round(percent, MidpointRounding.AwayFromZero), minutes, reset);
    }
}
