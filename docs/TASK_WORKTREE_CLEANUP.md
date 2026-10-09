# Arquivamento assistido de worktrees publicadas

Na **Fila de tarefas**, selecione uma entrega aprovada e publicada no projeto. Em **Pasta de trabalho**, use **Arquivar worktree integrada**. A prévia mostra os commits da entrega/publicação, a branch preservada, a pasta atual e o destino do arquivo. Recusar conserva tudo no local original.

O Sintonia move a pasta inteira para `%LOCALAPPDATA%/Sintonia/worktrees/archives/<tarefa>/<operação>`, preservando arquivos versionados, ignorados, não rastreados e diretórios vazios. Depois retira somente o registro Git da pasta original, que já não existe. A branch, os commits, as sessões, as tentativas, a revisão e a publicação permanecem. O projeto original e as pastas de combinação ficam intactos.

**O arquivo continua ocupando espaço em disco.** Ele conserva os arquivos para recuperação manual, mas não é um checkout Git ativo: seu arquivo `.git` passa a apontar para metadados retirados. Não execute o provedor nessa pasta nem a trate como uma worktree pronta. Não há restauração automática, descarte de arquivos, exclusão de branches ou arquivamento de combinações neste incremento.

## Conferências e reserva

Somente a entrega do run aprovado e publicado pode ser arquivada. O checkout precisa estar na localização gerenciada, registrado/bloqueado pela mesma tarefa, na branch esperada e exatamente no commit da entrega. Índice e conteúdo versionado precisam corresponder à árvore registrada, inclusive diante de `assume-unchanged`. Um commit novo, alteração versionada, conflito, destino ocupado, índice bloqueado ou caminho incompatível impede a movimentação. Arquivos ignorados e não rastreados são conservados dentro do arquivo; não são apagados ou considerados entregas publicadas.

`TaskWorktreeCleanupService` usa a trava de arquivo por Git comum e a reserva SQLite já compartilhada com combinação, validação e publicação. Schema 13 grava intenção e reserva juntas, antes de alterar a pasta. A transação compara publicação/run/worktree e a operação anterior; recusas e resultados antigos não alteram operações novas. Runs e preparações relacionados ao repositório aguardam a reserva. Uma trava temporária de índice impede escritores Git durante a última conferência e a movimentação.

Os caminhos completos de origem/destino são conferidos contra as raízes gerenciadas antes da movimentação no Windows. O manager usa renomeação de diretório, sem cópia parcial ou exclusão recursiva. Após mover, usa `worktree unlock` e `worktree remove` somente para o checkout original ausente, sem force, prune, reset ou exclusão da branch. Colisão ou falha conserva os arquivos e exige conferência; não há rollback que mova arquivos sobre uma pasta reaparecida.

## Cancelamento e recuperação

- Antes de iniciar a operação nativa, cancelar registra **Cancelled**, sem mover a pasta, e permite nova prévia explícita.
- Após iniciar, o aplicativo aguarda o resultado real mesmo com cancelamento tardio ou fechamento. Os comandos Git têm prazo total de 60 segundos; eventual reparação do bloqueio tem até 10 segundos. A renomeação nativa é aguardada, sem interrupção no meio da movimentação.
- **Archived** mostra o caminho preservado e retira ações que precisam do checkout, como diffs e nova integração. Dependentes continuam disponíveis pela publicação e conferem a revisão integrada na sua própria pasta.
- **NeedsAttention** ou **Interrupted** conserva os dois caminhos e o histórico. Recuperação deixa o executor vivo intacto pela trava e marca somente intenções abandonadas; não repete Git ou movimentação. Um arquivo de operação anterior existente impede nova tentativa. Quando a pasta original segue intacta e não existe arquivo anterior, uma nova prévia pode reconferir a entrega antes de tentar novamente.

Git, sistema de arquivos e SQLite não formam uma transação única. Outros processos podem alterar arquivos ou refs; uma falha depois de mover pode deixar o arquivo completo com registro Git ainda pendente, ou com registro já retirado. Confira ambos os caminhos e `git worktree list` antes de qualquer recuperação manual. Não apague o arquivo `.git`, o diretório preservado ou branches para contornar uma falha.

Para recuperar arquivos em um checkout utilizável, a restauração futura deverá conferir branch/commit e criar um checkout novo antes de reconciliar os arquivos preservados. Esse fluxo ainda não está implementado; o arquivamento atual preserva evidência e arquivos, sem prometer retomada da sessão nativa no caminho antigo.

## Validação

Testes usam Git nativo em pastas temporárias, runs/provedores simulados e nenhuma chamada de modelo. Cobrem preservação integral, branch/publicação/dependência/histórico, alterações ocultas/preparadas e commit posterior, colisões, índice bloqueado, cancelamento antes/depois, falha ambígua, reserva entre stores, recuperação sem repetição e migração do schema 12.

O fluxo WPF `--cleanup` percorre registro, combinação, validação e publicação reais antes de testar recusa, mudança posterior, arquivamento, retirada das ações do checkout, reabertura e dependente disponível. Capturas normal/mínima permitem verificar os caminhos e ações com rolagem.

[Worktrees](TASK_WORKTREES.md) · [Publicação](TASK_PUBLICATION.md) · [Concorrência](SESSION_CONCURRENCY.md)
