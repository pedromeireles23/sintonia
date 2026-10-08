# Registro de entregas e reserva de integração

O painel de diffs registra o commit de uma entrega já aprovada e revisada. O registro guarda tarefa, tentativa aprovada, vínculo da worktree, commit, árvore Git e horário no SQLite. Não cria commits, prepara arquivos, altera refs, executa merge nem libera dependentes.

## Usar o registro

1. Confira a entrega na fila e use **Aprovar entrega**. Salve as mudanças em um commit na worktree da tarefa por uma ferramenta Git de sua escolha.
2. Abra **Pasta de trabalho → Revisar diffs** e use **Atualizar diffs** para consultar a tarefa aprovada.
3. Confira a comparação **Desde o commit de base**, selecionando arquivos da lista. O painel indica alterações ainda sem commit, se existirem.
4. Use **Registrar commit revisado** e confira o commit/tarefa na confirmação. Recusar não salva; mudança posterior à consulta exige nova leitura.
5. O cabeçalho mostra commit registrado, horário e integração pendente. Atualizar/reabrir conserva o registro; **Atualizar** na fila atualiza sua aba Pasta de trabalho.

Registro e leituras bloqueiam outras operações no painel até terminar. **Cancelar operação** aguarda o término; uma transação já concluída conserva seu registro. Em caso de interrupção, atualizar/reabrir mostra o estado persistido. Fechar painel/central cancela e aguarda; capturas tardias canceladas antes da persistência não registram a entrega. Navegar na fila/central não muda o projeto/tarefa do painel.

## Conteúdo registrado

`TaskDeliveryService.RegisterAsync` recebe a consulta de diffs revisada. Exige a tarefa aprovada, a mesma tentativa concluída e a mesma worktree pronta. O Git confere HEAD/status antes/depois e exige ausência de alterações locais/arquivos novos reportados. O commit precisa continuar igual ao da consulta; a árvore é identificada a partir desse objeto Git.

Mudanças ainda sem commit podem ser consultadas no painel de diffs, mas precisam ser salvas em Git antes do registro. Arquivos ignorados ficam fora da entrega; arquivos grandes/binários versionados pertencem à árvore mesmo quando a prévia é limitada. O registro abrange todo o checkout, inclusive fora da subpasta cadastrada.

Schema 6 acrescenta `task_deliveries` e `task_integrations` sem substituir projetos, sessões, tarefas ou worktrees. O banco confere aprovação, run concluído e vínculo na mesma transação que salva. Repetir o mesmo registro é idempotente; outro commit/árvore não sobrescreve a entrega. Reabrir conserva o registro.

Os identificadores fixam conteúdo Git, não a pasta de trabalho. Os comandos de worktrees/diffs/entregas usam `--no-replace-objects`, evitando que refs de substituição redirecionem o objeto consultado. As refs existentes são preservadas. A worktree/branch continuam preservadas; alterações externas, remoção de objetos ou limpeza do Git não são recuperadas pelo registro. O próximo passo de integração precisa conferir novamente a existência dos objetos e a igualdade do conteúdo/origem.

## Reserva serial

O serviço consulta o destino original e exige raiz/Git comum compatíveis, branch local, commit contendo a base e status sem alterações. Confere também que a origem continua igual ao commit/árvore registrados. Uma prévia obsoleta é recusada antes da reserva.

O SQLite reserva por Git comum, inclusive entre instâncias do mesmo banco. A reserva exige ausência de execuções e preparações relacionadas e impede novos runs ou preparações. A relação cobre diretórios sobrepostos com a origem e as worktrees registradas desse Git comum. Pastas independentes continuam disponíveis; outros checkouts sem vínculo e processos externos precisam de conferência física antes de integrar.

Cada reserva tem ID próprio. Liberar uma reserva antiga não libera uma nova. Falha/cancelamento registra `NeedsAttention`; liberação sem efeitos registra `Released`. Nenhum estado representa integração concluída. Recuperação de `Reserved` registra atenção, preserva a entrega e não repete Git. A reserva não funciona como trava do sistema de arquivos ou de programas externos.

Não há botão de reserva enquanto o executor de integração estiver pendente. Aprovação, registro e reserva permanecem insuficientes para liberar dependentes. A futura integração deverá fixar origem/destino, preservar alterações e arquivos ignorados, testar a combinação e registrar o resultado real.

## Validação

Testes com Git real em pastas temporárias e runs simulados cobrem commit/árvore revisados, índice/lock/original preservados, recusa de mudanças sem commit e consultas obsoletas, origem/destino alterados, branch destacada, idempotência/reabertura, concorrência entre stores, bloqueio de runs/preparações, reserva antiga, recuperação e migração do schema 5. Cancelamento com captura tardia não salva uma entrega. Nenhum modelo é chamado.

Teste WPF `--deliveries` cobre botão/limites por aprovação e commit, recusa, mudanças após revisão, registro real em Git de teste, cancelamento/captura tardia, reabertura, navegação, índice/original preservados e encerramento da central durante registro. Layout normal/mínimo revisado; zero erros de binding. Runs e temporização são simulados, sem modelos. Reserva/executor de integração continuam sem fluxo visual.

Sintaxe dos objetos conforme [git rev-parse](https://git-scm.com/docs/git-rev-parse); comparações conforme [git diff](https://git-scm.com/docs/git-diff); consulta sem substituições conforme [git replace](https://git-scm.com/docs/git-replace).
