using System.Text;

namespace Sintonia.Core;

public sealed record WorkspaceFunctionProfile(string Id, string Name, string FunctionName, ProviderKind Provider,
    string? Model, string Instructions, int Revision);

public static class WorkspaceFunctionProfiles
{
    public static WorkspaceFunctionProfile Normalize(WorkspaceFunctionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 64 || profile.Revision < 0)
            throw new ArgumentException("Identificador ou revisão de perfil inválido.");
        var name = Label(profile.Name, "nome do perfil", 80);
        var function = Label(profile.FunctionName, "função", 80);
        if (!Enum.IsDefined(profile.Provider)) throw new ArgumentException("Provedor de perfil inválido.");
        var model = string.IsNullOrWhiteSpace(profile.Model) ? null : Label(profile.Model, "modelo", 120);
        var limit = function == PlanProposalFormat.ChiefFunctionName ? PlanProposalFormat.ChiefAdditionalInstructionsLimit : 8000;
        if (profile.Instructions is null || profile.Instructions.Length > limit)
            throw new ArgumentException($"As instruções desta função devem ter até {limit} caracteres.");
        return profile with { Name = name, FunctionName = function, Model = model };
    }

    public static string NameKey(string name) => name.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private static string Label(string? value, string label, int limit)
    {
        var text = value?.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(text) || text.Length > limit || text.Any(char.IsControl))
            throw new ArgumentException($"Informe {label} sem quebras de linha, com até {limit} caracteres.");
        return text;
    }
}
