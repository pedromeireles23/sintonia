# Acompanhamento da chefia e revisões do plano

O Sintonia entrega o estado salvo do plano à sessão de chefia e aplica uma nova proposta somente depois de revisão e confirmação. Codex e Claude conservam suas próprias sessões; o aplicativo encaminha contexto explícito entre elas.

## Fluxo visual

1. Na **Fila de tarefas**, selecione o plano encaminhado e abra **Chefia e plano**. O resumo distingue execução, revisão, aprovação e integração Git.
2. Escreva a orientação e use **Preparar acompanhamento no chat**. A central retoma a chefia selecionada ou a que originou o plano, preservando provedor/modelo e seu rascunho. A mensagem contém a definição completa, revisão da fila, estados, tentativas, resultados, notas humanas, pastas e commits. Preparar não chama modelos.
3. Revise o pedido na central e use **Enviar**. A chefia continua em leitura, sem autorização de escrita. Sua resposta pode produzir outra proposta completa `sintonia-plan`.
4. Em **Revisar planos**, confira e confirme a nova proposta. Na fila, atualize a lista, selecione essa proposta e use **Conferir e atualizar este plano** em **Chefia e plano**.
5. A confirmação mostra o plano e os IDs adicionados, removidos ou alterados. Recusar preserva a fila. Aplicar não executa tarefas, repete tentativas, aprova entregas ou integra código.

Não há acompanhamento em background ou rodadas automáticas. **Iniciar tarefas disponíveis** continua distribuindo uma rodada explícita nas vagas livres.

## Preservação e concorrência

`TaskPlanUpdates` exige um plano completo com grafo válido. Uma tarefa fica preservada assim que tem tentativa, estado diferente de Pending, worktree ou entrega registrada. Seu ID e definição inteira precisam permanecer iguais, inclusive provedor/modelo, instruções, acesso, escopo, dependências e critérios. Mudanças são permitidas somente antes desse ponto.

Tarefas preservadas conservam sessões, tentativas, respostas, aprovações, commits e publicações. Tarefas pendentes alteradas mantêm seu ID interno e conversa ainda sem runs/identificador nativo; novas tarefas recebem novas conversas. Remover uma tarefa pendente retira seu vínculo com a fila, mas conserva a conversa. Propostas encaminhadas ou aplicadas ficam sem edição e não podem gerar uma segunda cópia de tarefas.

Schema 12 acrescenta `task_plan_revisions`: cada aplicação conserva a definição anterior, a proposta aprovada que a substituiu, data e revisão. A resposta original e as propostas permanecem imutáveis. Revisão da fila, revisão/estado da proposta e estados das tarefas são conferidos numa transação; duas atualizações concorrentes da mesma revisão têm um único vencedor. Uma reserva de execução também confere o contrato e os limites usados para montar o pedido, evitando executar um contexto obsoleto e sem consumir uma tentativa recusada.

Preparar o contexto relê resultados do banco. Navegação, edição do rascunho, cancelamento ou encerramento durante a consulta impedem substituir a mensagem. Respostas são dados de contexto; não concedem permissões. Trechos extensos são identificados e reduzidos até caberem; definição completa permanece preservada. Orientação aceita até 8.000 caracteres, acompanhamento até 190.000 e pedido final com rascunho até 200.000. Excesso é recusado preservando o rascunho.

## Limites de execução

Em **Função, permissões e sessões**, configure e use **Aplicar limites**:

- 3–7 sessões simultâneas por projeto, padrão 3, com teto global 7.
- 1–3 tentativas por tarefa, padrão 3. Conta o histórico existente; reduzir não reinicia contadores nem cancela uma execução em andamento.
- 1–300 segundos por execução do chat ou da fila, padrão 300. A configuração é capturada na reserva e alterações valem para novos inícios.

Prazo excedido cancela o provedor e registra Failed com mensagem de timeout e resposta parcial. Resultado tardio não vira sucesso. Cancelamento explícito mantém Cancelled. Vaga permanece reservada até o executor encerrar e o resultado ser persistido; efeitos em arquivos precisam ser conferidos antes de retomar. Comandos de validação têm seus próprios prazos, separados dos limites de modelos.

Schema 12 guarda tentativas/prazo em `project_execution_limits`, com a mesma revisão da configuração de sessões. Migração preserva o histórico e usa os padrões para projetos existentes. Rascunhos inválidos ou obsoletos não substituem a configuração salva.

Os adaptadores atuais não fornecem uma quota restante uniforme e verificada das assinaturas. Não há limite agregado de créditos, tokens ou custo, nem estimativa apresentada como quota real. Consumo disponível permanece uma capacidade pendente; limites de tempo, tentativas e concorrência estão implementados.

## Validação

Seis testes novos de persistência cobrem preservação de sessões/resultados, mudanças pendentes, recusa de mudanças iniciadas, remoção conservadora, revisões obsoletas/concorrentes, contrato/limites obsoletos na reserva, migração e timeout com sucesso tardio. Regressões de fila/concorrência e 69 testes Core passaram.

WPF `--chief-plan` percorre objetivo/plano/fila/resultado/acompanhamento/proposta revisada, recusa/confirmação, sessão nativa de chefia retomada em leitura, rascunhos inválidos/extensos preservados, atualização sem execução duplicada e reabertura. Capturas normal/mínima e ações com rolagem conferidas; zero erros de binding. WPF `--sessions`, `--queue` e `--proposals` passaram. Provedores simulados; nenhum modelo foi chamado.
