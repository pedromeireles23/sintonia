using System.Globalization;

namespace Sintonia.Core;

/// <summary>Provider-reported main-loop tokens observed in this run; never subscription quota or billing.</summary>
public sealed record RunTokenUsage(ProviderKind Provider, long InputTokens, long OutputTokens, long TotalTokens,
    long? CacheReadTokens = null, long? CacheWriteTokens = null, long? ReasoningOutputTokens = null, bool IsPartial = true)
{
    public void ValidateDefinition()
    {
        if (!Enum.IsDefined(Provider) || InputTokens < 0 || OutputTokens < 0 || TotalTokens < 0
            || CacheReadTokens is < 0 || CacheWriteTokens is < 0 || ReasoningOutputTokens is < 0
            || CacheReadTokens > InputTokens || CacheWriteTokens > InputTokens || ReasoningOutputTokens > OutputTokens)
            throw new ArgumentException("Contagem de tokens inválida.");
        try { if (checked(InputTokens + OutputTokens) != TotalTokens) throw new ArgumentException("Total de tokens incompatível."); }
        catch (OverflowException) { throw new ArgumentException("Contagem de tokens acima do limite suportado."); }
    }
    public string Describe()
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var scope = IsPartial ? "Medição parcial do agente principal" : "Uso informado do turno principal";
        var detail = $"{TotalTokens.ToString("N0", culture)} tokens · entrada {InputTokens.ToString("N0", culture)} · saída {OutputTokens.ToString("N0", culture)}";
        if (CacheReadTokens is { } read) detail += $" · cache lido {read.ToString("N0", culture)}";
        if (CacheWriteTokens is { } write) detail += $" · cache escrito {write.ToString("N0", culture)}";
        if (ReasoningOutputTokens is { } reasoning) detail += $" · raciocínio {reasoning.ToString("N0", culture)}";
        return scope + ": " + detail + ". Cache/raciocínio já incluídos; subagentes e quota da assinatura não estão nesta medição.";
    }
}
