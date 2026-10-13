namespace Sintonia.Core;

/// <summary>Built-in function with a one-line summary, ready-to-use instructions and the provider suggested for its starter profile.</summary>
public sealed record WorkspaceFunctionTemplate(string Name, string Summary, string Instructions, ProviderKind SuggestedProvider, bool StarterProfile = true);

public static class WorkspaceFunctionCatalog
{
    public const string CustomFunctionName = "Personalizada";

    public static IReadOnlyList<WorkspaceFunctionTemplate> Functions { get; } =
    [
        new("Conversa", "Perguntas e conversa livre com o contexto do projeto.",
            "Responda com base no contexto deste projeto. Seja direto, explique suposições e peça esclarecimentos quando faltar informação. Não altere arquivos sem pedido explícito.",
            ProviderKind.Claude, StarterProfile: false),
        new(PlanProposalFormat.ChiefFunctionName, "Planeja o trabalho e propõe tarefas para revisão.",
            "Atue como chefe deste projeto. Delimite tarefas pequenas com critérios verificáveis, dependências e indicação de Codex ou Claude. Proponha um plano para revisão; não execute nem delegue automaticamente e não aprove entregas pelo usuário.",
            ProviderKind.Claude),
        new("Desenvolvimento", "Implementa mudanças pequenas e validadas.",
            "Implemente a tarefa com mudanças pequenas. Examine as instruções do projeto, preserve alterações existentes e valide o resultado. Não use outros agentes sem pedido explícito.",
            ProviderKind.Codex),
        new("Interface", "Cria e ajusta telas com foco em usabilidade e acessibilidade.",
            "Implemente ou ajuste a interface seguindo o padrão visual do projeto. Cuide de layout, textos, estados vazios, de carregamento e de erro, e de acessibilidade (contraste, teclado e leitores de tela). Confira tamanhos de janela diferentes e preserve alterações existentes.",
            ProviderKind.Claude),
        new("Testes", "Escreve e executa testes do comportamento pedido.",
            "Escreva ou ajuste testes para o comportamento pedido, cobrindo casos normais, limites e falhas. Siga as convenções de teste do projeto, execute os testes relevantes e informe resultados e lacunas. Não altere código de produção sem necessidade explícita.",
            ProviderKind.Codex),
        new("Revisão", "Revisa entregas e aponta problemas concretos.",
            "Revise o escopo solicitado, explique problemas concretos e critérios de validação. Não altere arquivos durante a revisão.",
            ProviderKind.Claude),
        new("Documentação", "Escreve e atualiza a documentação do projeto.",
            "Escreva ou atualize a documentação pedida a partir do código e das decisões do projeto. Use linguagem clara e precisa, mantenha a terminologia existente e confira comandos, exemplos e links. Não altere código.",
            ProviderKind.Claude),
        new("Análise", "Investiga questões e recomenda um caminho.",
            "Investigue a questão com base em evidências do projeto. Separe fatos de hipóteses, compare alternativas com vantagens e riscos e termine com uma recomendação justificada. Não altere arquivos durante a análise.",
            ProviderKind.Codex),
        new(CustomFunctionName, "Instruções definidas por você para a tarefa.", "", ProviderKind.Codex, StarterProfile: false)
    ];

    public static WorkspaceFunctionTemplate? Find(string? name) =>
        Functions.FirstOrDefault(f => string.Equals(f.Name, name?.Trim(), StringComparison.Ordinal));

    /// <summary>True when the text is empty or still one of the built-in instructions, so switching function may replace it.</summary>
    public static bool IsReplaceable(string? instructions) =>
        string.IsNullOrWhiteSpace(instructions) || Functions.Any(f => f.Instructions == instructions);

    public static IReadOnlyList<WorkspaceFunctionProfile> StarterProfiles() =>
        Functions.Where(f => f.StarterProfile).Select(f => new WorkspaceFunctionProfile(Guid.NewGuid().ToString(), f.Name, f.Name, f.SuggestedProvider, null, f.Instructions, 0)).ToArray();
}
