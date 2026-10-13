namespace Sintonia.Core;

public static class ModelSettings
{
    // Effort IDs are advertised by the backend and may grow without a new client enum.
    public static void ValidateEffort(string? effort)
    {
        if (effort is not null && (effort.Length is < 1 or > 32
            || effort.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_')))
            throw new ArgumentException("Nível de reasoning inválido.");
    }

    public static string EffortLabel(string? effort) => effort switch
    {
        null => "Padrão da instalação",
        "none" => "Sem reasoning · none", "minimal" => "Mínimo · minimal", "low" => "Baixo · low",
        "medium" => "Médio · medium", "high" => "Alto · high", "xhigh" => "Muito alto · xhigh",
        "max" => "Máximo · max", "ultra" => "Ultra · ultra", _ => effort
    };
}
