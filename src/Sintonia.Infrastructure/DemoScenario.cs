using Sintonia.Core;

namespace Sintonia.Infrastructure;

public static class DemoScenario
{
    public static TaskCoordinator Create(TimeSpan? stepDelay = null) => new(
        [
            new("systems", "Sistemas", "Defina contratos pequenos e testáveis para o jogo.", ProviderKind.Codex),
            new("gameplay", "Gameplay", "Priorize controles responsivos e regras de movimento claras.", ProviderKind.Codex),
            new("interface", "Interface", "Crie uma interface legível e conectada aos contratos aprovados.", ProviderKind.Claude),
            new("review", "Revisão", "Revise comportamento, integração e casos de falha.", ProviderKind.Claude)
        ],
        [
            new("01", "Definir contrato de movimento", "Propor entradas, velocidade e estados do jogador para um protótipo de plataforma.", "systems", []),
            new("02", "Desenhar HUD de energia", "Especificar uma barra de energia e seus estados de alerta, sem escolher uma engine.", "interface", []),
            new("03", "Implementar salto e deslocamento", "Usar o contrato aprovado para descrever movimento, salto e limites do jogador.", "gameplay", ["01"]),
            new("04", "Conectar HUD ao jogador", "Combinar o movimento e o HUD aprovados, com atualização de energia e reinício da partida.", "interface", ["02", "03"]),
            new("05", "Revisar o protótipo", "Conferir a entrega combinada e propor verificações de entrada, energia e reinício.", "review", ["04"])
        ],
        [new SimulatedProviderAdapter(ProviderKind.Codex, stepDelay), new SimulatedProviderAdapter(ProviderKind.Claude, stepDelay)],
        new(2, 1, 1));
}
