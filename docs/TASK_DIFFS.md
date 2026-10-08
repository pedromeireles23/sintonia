# Revisão de diffs por tarefa

O núcleo M3 consulta a worktree e o commit de base registrados na tarefa, em leitura. Não aprova, integra, prepara arquivos para commit nem chama modelos. A conexão visual à fila entra no próximo incremento.

## Comparações

| Opção | Conteúdo |
| --- | --- |
| Desde a base | Diferenças entre o commit confirmado na preparação e os arquivos atuais, incluindo commits posteriores da tarefa |
| Preparado para commit | Índice comparado ao HEAD atual da tarefa |
| Na pasta | Arquivos rastreados comparados ao índice |

A lista combina diferenças desde a base e status local: arquivos commitados, preparados, não preparados, novos, renomeados, excluídos e conflitos. Caminhos se referem à raiz de toda a worktree, inclusive fora da subpasta do projeto. Arquivos ignorados não entram na lista. Quando há exclusão preparada e um arquivo novo no mesmo caminho, ambos os estados são preservados.

Arquivos não versionados oferecem conteúdo atual em UTF-8, até 64 KiB, nas comparações desde a base/na pasta. A opção do índice mostra que ainda não estão preparados, ou mostra a exclusão preparada quando o caminho tem ambos os estados. Links/junções são recusados na leitura direta. Binários e texto fora de UTF-8 têm indicação explícita.

## Limites e consistência

- Git nativo com argumentos separados e caminhos literais. Sem shell, ferramentas externas de diff, textconv, hooks, fsmonitor ou filtros clean/process. Variáveis Git herdadas não redirecionam o projeto nem habilitam traces; configuração persistente não é alterada.
- Prazo total padrão de 20 segundos por consulta/listagem ou leitura de um arquivo; stdout/stderr limitados a 64 Ki caracteres. Listas incompletas, formatos desconhecidos ou mais de 1.000 caminhos são recusados por inteiro. Diff textual excessivo não mostra uma prévia parcial.
- Antes/depois da consulta, conferir registro da worktree, diretório real, Git comum, branch, ancestralidade da base e status local. Mudança de HEAD/status exige atualizar a lista. Tarefa em execução, sem worktree pronta ou alterada durante a leitura é recusada pelo serviço.
- A consulta é um retrato local, não uma revisão imutável de conteúdo. Alterações que mantêm o mesmo status podem ocorrer entre leituras; a futura integração precisará fixar e verificar o conjunto revisado. A aprovação de uma entrega em worktree continua sem liberar dependentes.
- Conflitos permanecem visíveis e preservados. Este incremento não tenta resolvê-los, descartar alterações ou criar commits.

O formato NUL preserva espaços e acentos. Referência de sintaxe e comparações: [documentação oficial de git diff](https://git-scm.com/docs/git-diff).
