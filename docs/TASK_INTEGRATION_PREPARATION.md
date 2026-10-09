# Preparação da combinação de uma entrega

O executor combina o commit registrado de uma tarefa com o commit confirmado da branch original numa **nova pasta separada**. Guarda intenção e resultado no SQLite. Essa preparação não publica arquivos no projeto, cria um commit de merge, executa testes do projeto ou libera dependentes.

## Contratos e persistência

`TaskIntegrationPreparationService` consulta a prévia por `TaskDeliveryService`, adquire a trava por Git comum e confere novamente origem/destino antes da reserva. A tarefa precisa estar aprovada, com o mesmo commit/árvore/run registrados e worktree limpa. O destino precisa estar limpo, numa branch local e conter a base da tarefa. Uma prévia obsoleta não cria a pasta.

O schema 7 acrescenta `task_integration_preparations`, preservando as entregas e reservas do schema 6. A intenção inclui ID da reserva, origem/destino fixados e diretório gerenciado, e é salva **antes** de qualquer criação Git. Caminhos têm nome derivado do ID da reserva e ficam fora da origem/destino. Cada nova tentativa conserva as pastas anteriores e usa outro ID; não há repetição automática de uma preparação parcial.

Estados:

| Estado | Significado |
| --- | --- |
| Preparing | Intenção salva; executor em andamento |
| Combined | Merge sem conflitos e árvore do índice identificada; validação pendente |
| Conflicted | Caminhos conflitantes registrados; arquivos e estágios do índice preservados |
| NeedsAttention | Falha, cancelamento ou interrupção; pasta pode conter efeitos parciais |

Resultado e liberação da reserva são uma transação. Falha conserva mensagem e registra atenção. Preparação concluída não pode ser sobrescrita por um término antigo. Cancelamento anterior ao registro do resultado descarta respostas tardias; uma transação concluída conserva seu resultado. A reserva não pode ser liberada separadamente enquanto sua preparação está em andamento.

A recuperação tenta a trava de integração e deixa uma preparação viva intacta. Quando o proprietário encerrou, marca intenção/reserva interrompidas sem repetir merge, apagar arquivos ou manipular o destino. Novos runs também são bloqueados se o projeto aponta para uma pasta de combinação registrada durante uma reserva desse repositório.

## Git e arquivos preservados

`GitTaskIntegrationPreparer` usa uma worktree bloqueada, com HEAD destacado no commit de destino confirmado. Executa `merge --strategy=ort --no-commit --no-ff` com o commit registrado. HEAD permanece no destino de referência; o resultado fica no índice/arquivos dessa nova worktree. `write-tree` identifica o conjunto sem criar commits ou mover branches. Com conflitos, não declara uma árvore combinada: conserva os arquivos e o índice não resolvido para inspeção.

Antes/depois, confere raiz, Git comum, HEAD destacado e bloqueio da worktree, além da identidade/limpeza de origem/destino. Inspeciona as árvores de destino, entrega, base registrada e bases efetivas do merge. Recusa submódulos, links simbólicos e filtros de checkout ou drivers personalizados de merge. Filtros e hooks ficam desativados nos processos; driver padrão é o `text` interno, sem mudar configuração persistente. Estratégia, ausência de autostash, assinatura e rerere são explícitas. Regras de confiança continuam aplicadas; não consulta rede ou ignora permissões globalmente.

Prazo total de 60 segundos para o executor, streams assíncronos limitados a 64 Ki caracteres e cancelamento com encerramento da árvore do processo. Listas extensas/incompletas são recusadas; no máximo 1.000 caminhos de conflito persistidos. A integração não recebe comandos arbitrários de modelos nem presume uma stack específica.

Índices, commits, branches, arquivos locais e ignorados de origem/destino ficam preservados. Ignorados não são copiados para a combinação; configurações e dependências necessárias aos testes ainda precisarão de um fluxo explícito. Worktrees compartilham objetos/configuração Git e não constituem um sandbox. Processos externos podem modificar arquivos após as conferências: `Combined` é um estado salvo da preparação, não uma prova permanente de que a pasta continua idêntica.

## Validação e próximos passos

Testes usam Git real em pastas temporárias e runs/temporização simulados, sem chamar modelos. Conferem combinação, conflitos, árvores/HEAD/índices, ignorados, configurações/hooks, caminhos existentes, prévia obsoleta, filtros/submódulos, cancelamento com resposta tardia, reservas vivas, recuperação sem repetição e migração do schema 6.

Ainda faltam comandos/critérios de validação configuráveis para projetos gerais, resolução/revisão de conflitos, publicação confirmada no destino e disponibilidade da revisão correta para dependentes. Não há limpeza automática de worktrees. A preparação não equivale à integração concluída do M3.

Comportamento do merge conforme [git merge](https://git-scm.com/docs/git-merge); checkout destacado e bloqueio conforme [git worktree](https://git-scm.com/docs/git-worktree).
