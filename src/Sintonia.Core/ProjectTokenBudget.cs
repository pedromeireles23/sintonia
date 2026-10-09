using System.Globalization;

namespace Sintonia.Core;

/// <summary>Lifetime project observations plus the unobserved part of each active run's admission reservation.</summary>
public sealed record ProjectTokenBudget(ProjectExecutionSettings Settings, long ReportedTokens, long ReservedTokens,
    int MissingRuns, int PartialRuns, int ActiveRuns, bool Overflow = false)
{
    public bool CanAdmit(out string? reason)
    {
        if (Settings.MaxReportedTokens is not { } limit) { reason = null; return true; }
        if (Overflow || ReportedTokens >= limit || ReservedTokens > limit - ReportedTokens
            || Settings.TokenReservation > limit - ReportedTokens - ReservedTokens)
        {
            reason = "O limite de tokens informados não comporta outro início. Atualize o consumo e confira o limite/reserva do projeto; execuções ativas podem ultrapassar a reserva.";
            return false;
        }
        reason = null; return true;
    }

    public string Describe()
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var limit = Settings.MaxReportedTokens is { } maximum ? maximum.ToString("N0", culture) : "desativado";
        return $"Desde o cadastro: {(Overflow ? "pelo menos " : "")}{ReportedTokens.ToString("N0", culture)} tokens informados · {ReservedTokens.ToString("N0", culture)} reservados ainda não informados · limite {limit}. "
            + $"{ActiveRuns} execuções ativas; {PartialRuns} medições parciais; {MissingRuns} execuções encerradas sem medição. "
            + "Somente agente principal; lacunas/subagentes e quota da assinatura não entram. Reserva é margem de admissão, não teto da execução."
            + (CanAdmit(out _) ? "" : " Novos envios bloqueados pelo limite.");
    }
}
