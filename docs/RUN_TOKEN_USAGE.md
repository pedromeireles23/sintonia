# Tokens por execução

## Contratos e escopo

Contratos conferidos em 09/10/2026 para Codex `0.162.0-alpha.2` e Claude Code `2.1.277`, sem iniciar modelos. Fonte Codex: schema da instalação para `thread/tokenUsage/updated`, [App Server](https://learn.chatgpt.com/docs/app-server) e [contadores no protocolo oficial](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/protocol.rs). `total` acumula a sessão; `last` é a última resposta do modelo, que pode ser repetida em notificações. Nenhum deles é somado indiscriminadamente como consumo de um turno.

Fonte Claude: [rastreamento oficial de uso](https://code.claude.com/docs/en/agent-sdk/cost-tracking). Em entrada stream-json, `result.usage` pertence ao turno principal; exclui subagentes. `modelUsage`/USD podem incluir histórico restaurado e não são usados. A saída das mensagens assistant pode ser provisória; este incremento lê somente o resultado terminal. Erros podem omitir consumo e ficam marcados como parciais.

`RunTokenUsage` normaliza entrada, saída, total, cache lido/escrito e raciocínio quando disponíveis. Entrada Claude inclui entrada sem cache mais leitura/criação de cache; esses componentes não são acrescentados novamente ao total. Raciocínio Codex já pertence à saída. Ausência não vira zero. Campos negativos, overflow, total incompatível e componentes maiores que seus totais são ignorados como telemetria inválida; a resposta da tarefa pode continuar válida. Não há valores monetários, medição de quota ou promessa de contabilizar toda a árvore de agentes.

## Codex: intervalos observados

- Apenas notificações da thread da execução e do turno iniciado entram na medição. Notificações que chegam antes da resposta de turn/start são guardadas numa fila limitada e correlacionadas depois.
- Thread nova tem base zero. Em retomada, uma amostra recebida antes do pedido do turno pode fornecer base. Sem essa amostra, o primeiro contador recebido durante o turno vira base; não é atribuído à execução. A medição fica indisponível até outro intervalo ser observado.
- Diferenças positivas de contadores válidos são acumuladas uma vez. Eventos repetidos não aumentam tokens. Redução/reset inicia outro intervalo sem reduzir a medição anterior; não se estima o trecho perdido. Valores que representam preenchimento de janela sem entrada/saída coerentes são recusados.
- Todos os snapshots Codex são **parciais**: descrevem intervalos observados do agente principal, com possíveis lacunas e sem uso de threads filhas. Término bem-sucedido da tarefa não transforma observação em total garantido.

## Histórico

Schema 14 acrescenta `run_token_usage`, vinculado ao run, sem alterar tentativas, permissões ou revisão de tarefas. Histórico anterior permanece sem medição. Snapshot substitui o anterior, não é somado a ele; redução de entrada/saída no mesmo run é recusada na transação. Contagem de outro provedor e consumo herdado na abertura de um run são recusados.

`WorkspaceChatService` registra a primeira amostra em checkpoint e atualizações conforme o intervalo existente de dois segundos; término grava a última medição junto ao resultado. Falha/cancelamento conserva dados já recebidos. Recuperação preserva checkpoints e marca a execução interrompida; não chama o provedor novamente. Uma interrupção abrupta pode perder o intervalo desde o último checkpoint. Claude sem resultado terminal permanece sem medição neste incremento.

## Validação e próximo passo

Testes de protocolo usam processos simulados: vários passos, repetições, turno alheio, retomada com/sem base, reset, nulos/campos incompatíveis/overflow, totais restaurados Claude excluídos, falha/cancelamento e persistência. Migração do schema 13, reabertura, recuperação, redução recusada e preservação quando o término não traz medição são verificadas. Nenhum modelo chamado; emissão desses contadores por um turno real ainda não foi exercitada neste incremento.

## Exibição WPF

Respostas do chat e da fila mostram os tokens do próprio run, incluindo contagens parciais e indisponibilidade explícita. Reabrir conserva a medição salva. Atualização visual tardia não substitui a medição recebida no resultado. Na aba **Tentativas**, a medição acompanha a tentativa selecionada; não soma tentativas nem herda dados de outra resposta. Textos extensos ficam acessíveis por rolagem no tamanho mínimo.

WPF `--tokens` valida chat, provedores misturados, medição informada/parcial, ausência de campos, cancelamento, navegação e reabertura. `--queue` verifica consumos distintos das tentativas antigas/atuais, dependências, revisão e reabertura. Dados/provedores simulados, capturas normal/mínima revisadas, zero erros de binding e nenhum modelo chamado. Regressões `--usage` e `--chief-plan` aprovadas.

Limites agregados seguem pendentes: observações parciais, escopos distintos e uso ausente não sustentam promessa de teto integral de consumo. Próximo incremento: limite explícito sobre tokens informados, com soma por run e bloqueio de novos envios ao alcançar o teto observado, sempre indicando lacunas de medição. Tokens não representam saldo de assinatura.
