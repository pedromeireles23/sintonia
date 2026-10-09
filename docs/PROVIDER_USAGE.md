# Uso e quota dos provedores

## Contrato verificado

Em 09/10/2026, o Codex CLI `0.162.0-alpha.2` expõe `account/rateLimits/read` no App Server. A consulta verifica o login ChatGPT já usado pelo adaptador, lê metadados e encerra o processo sem criar thread ou turno. Uma consulta real limitada, sem inferência, confirmou janelas de uso e disponibilidade. Percentuais pessoais e identidade da conta não são registrados neste documento.

O contrato foi conferido no schema gerado pela instalação e na [documentação oficial do App Server](https://learn.chatgpt.com/docs/app-server). Claude Code `2.1.277` documenta `/usage` no CLI interativo, mas não foi confirmada consulta equivalente no protocolo stream-json do Sintonia. Veja a [documentação oficial de uso do Claude](https://code.claude.com/docs/en/costs). O adaptador Claude retorna indisponibilidade explícita sem iniciar processos; o usuário pode consultar `/usage` no Claude Code ou as configurações de uso em claude.ai.

## Dados e decisões

- O snapshot pertence à conta, compartilhada entre projetos e aplicativos. O horário indica quando a resposta foi recebida; não garante acesso futuro. Não é persistido no SQLite.
- `rateLimitsByLimitId` tem prioridade, inclusive quando vazio. O campo legado `rateLimits` só é usado se o mapa estiver ausente ou nulo. Tipos incompatíveis recusam a consulta.
- Cada categoria pode fornecer janela principal/secundária, percentual usado, duração em minutos e renovação em segundos Unix. Ausência/nulo permanecem indisponíveis. Percentual restante é `clamp(100 - usedPercent, 0, 100)`, sem saldo absoluto de assinatura, conversão para dinheiro ou estimativa de tokens.
- `ordinaryUsageAllowed` é a decisão explícita de disponibilidade do backend no schema instalado. Antes de abrir/retomar thread e iniciar cada turno Codex, uma consulta nova interrompe o envio se esse campo for `false`, mesmo com percentuais inválidos. Percentuais e horários não permitem inferir bloqueio ou recuperação. `null`, método não suportado ou consulta falha produzem diagnóstico de disponibilidade desconhecida; o próprio Codex aplica seus limites ao pedido.
- Consultas têm prazo de vinte segundos, subordinado ao cancelamento e prazo da execução. Nenhuma repetição automática, compra de créditos, reset de limites ou troca para autenticação/cobrança de API é realizada.
- Parser só conserva os campos tipados acima. Identidade da conta, créditos, cobrança e ofertas são ignorados; erros brutos do servidor não chegam ao diagnóstico da consulta. Streams continuam assíncronos e o encerramento aguarda o processo filho.

Diagnóstico manual, sem modelos: `dotnet run --project tools/Sintonia.Diagnostics -- usage Codex` ou `usage Claude`. Neste ambiente, prefixar com `rtk proxy`. A saída informa que não inicia conversa/turno e contém apenas o snapshot normalizado.

## Limites do incremento

O botão **Uso dos provedores** da central abre o painel, consulta uma vez e permite **Atualizar uso** e **Cancelar consulta**. O provedor pode ser trocado enquanto não há consulta ativa; a troca e cada atualização retiram dados anteriores. O painel conserva a pasta capturada para executar o CLI, mesmo ao navegar na central, mas exibe dados da conta inteira. Cancelamento descarta respostas tardias e mantém os controles ocupados até o leitor encerrar. Fechar o painel ou a central cancela e aguarda as consultas. Campos ausentes aparecem como indisponíveis; barras só aparecem quando há percentual informado. Horários são exibidos no fuso local, com offset.

Consumo por execução e limites agregados de tokens/custo continuam pendentes. Um percentual da conta não pode ser atribuído a uma tarefa, e a estimativa monetária local do Claude não representa quota da assinatura. Eventos de tokens exigem contrato, contagem e persistência próprios antes de servir a um orçamento. Consultas do painel/diagnóstico não alteram tentativas, concorrência ou revisão das entregas. A conferência antes do envio ocorre dentro da execução já reservada: uma recusa vira falha da tentativa pelos contratos existentes, mesmo sem iniciar turno do modelo.

Testes automatizados usam processos de protocolo simulados, sem modelos: mapa/legado, nulos, valores incompatíveis, normalização, ausência de campos, autorização explícita, 100% sem bloqueio inferido, falha, cancelamento e ausência de thread/turno na leitura. WPF `--usage` usa leitores/dados de teste e percorre atualização, troca de provedor, navegação, falha, ausência Claude, cancelamento/resposta tardia, reabertura e fechamento aguardado. Capturas normal/mínima revisadas, ações acessíveis por rolagem e zero erros de binding. Nenhum modelo chamado.
