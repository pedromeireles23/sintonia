using Sintonia.Core;

namespace Sintonia.Infrastructure;

/// <summary>Demonstrates orchestration only. Never starts a CLI or writes project files.</summary>
public sealed class SimulatedProviderAdapter(ProviderKind kind, TimeSpan? stepDelay = null) : IProviderAdapter
{
    private readonly TimeSpan _stepDelay = stepDelay ?? TimeSpan.FromMilliseconds(800);
    public ProviderKind Kind => kind;
    public bool IsSimulated => true;

    public async Task<ProviderResult> ExecuteAsync(ProviderRequest request, IProgress<ProviderEvent> progress,
        CancellationToken cancellationToken)
    {
        string[] steps = [
            $"[SIMULAÇÃO] Sessão demonstrativa de {Kind} iniciada. Nenhum CLI foi chamado.",
            $"Função: {request.Function.Name}. Instruções adicionais: {request.Function.Instructions}",
            request.Dependencies.Count == 0 ? "Sem dependências; preparando uma entrega de exemplo."
                : $"Recebidas {request.Dependencies.Count} entregas aprovadas, vinculadas às tentativas de origem.",
            "Entrega fictícia pronta. Aguardando sua revisão no Sintonia."
        ];
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new(ProviderEventKind.Message, step));
            await Task.Delay(_stepDelay, cancellationToken).ConfigureAwait(false);
        }
        var result = $"ENTREGA SIMULADA · {request.Task.Title}\n\n" +
            $"Este texto ilustra a entrega da função {request.Function.Name} atribuída a {Kind}.\n\n" +
            $"Objetivo proposto:\n{request.Task.Description}\n\n" +
            "Critérios para a futura entrega real:\n• Implementação no diretório autorizado.\n• Verificação das regras e dos casos de falha.\n• Resumo dos arquivos alterados e das limitações.\n\n" +
            "Nenhum código ou arquivo de jogo foi produzido. Aprovar este exemplo apenas libera a próxima tarefa da demonstração.";
        progress.Report(new(ProviderEventKind.Result, "Resultado demonstrativo disponível para revisão."));
        return new(result);
    }
}
