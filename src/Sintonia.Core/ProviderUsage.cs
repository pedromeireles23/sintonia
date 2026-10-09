namespace Sintonia.Core;

public sealed record ProviderUsageWindow(int UsedPercent, long? DurationMinutes, DateTimeOffset? ResetsAt)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
    public void ValidateDefinition()
    {
        if (UsedPercent < 0 || DurationMinutes is <= 0) throw new ArgumentException("Janela de uso inválida.");
    }
}
public sealed record ProviderUsageBucket(string Id, string? Name, ProviderUsageWindow? Primary, ProviderUsageWindow? Secondary)
{
    public void ValidateDefinition()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 200 || Id.Any(char.IsControl) || Name?.Length > 200 || Name?.Any(char.IsControl) == true)
            throw new ArgumentException("Identificação de limite inválida.");
        Primary?.ValidateDefinition(); Secondary?.ValidateDefinition();
    }
}
/// <summary>Account-wide observation, never a project budget or proof that the next request will succeed.</summary>
public sealed record ProviderUsageSnapshot(ProviderKind Provider, DateTimeOffset CheckedAt, IReadOnlyList<ProviderUsageBucket> Buckets,
    bool? OrdinaryUsageAllowed, string? UnavailableReason = null)
{
    public bool HasData => Buckets.Any(b => b.Primary is not null || b.Secondary is not null) || OrdinaryUsageAllowed is not null;
    public void ValidateDefinition()
    {
        if (!Enum.IsDefined(Provider) || Buckets.Count > 32 || UnavailableReason?.Length > 1000 || Buckets.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != Buckets.Count)
            throw new ArgumentException("Consulta de uso inválida.");
        foreach (var bucket in Buckets) bucket.ValidateDefinition();
    }
    public static ProviderUsageSnapshot Unavailable(ProviderKind provider, string reason) => new(provider, DateTimeOffset.UtcNow, [], null, reason);
}
public interface IProviderUsageReader
{
    ProviderKind Kind { get; }
    Task<ProviderUsageSnapshot> ReadUsageAsync(string directory, CancellationToken token);
}
