# Revisão de diffs por tarefa

As leituras do painel M3 consultam a worktree e o commit de base registrados na tarefa, preservando arquivos/índice e sem chamar modelos. Registro de commit e preparação de combinação são ações explícitas separadas.

## Usar na fila

1. Selecione uma tarefa com **Worktree pronta** e sem tentativa em execução.
2. Na aba **Pasta de trabalho**, use **Revisar diffs**. A lista é consultada ao abrir a janela.
3. Selecione um arquivo e escolha **Desde o commit de base**, **Preparado para commit** ou **Na pasta, sem preparar**. Confira estado, caminho anterior e conteúdo.
4. Use **Ler novamente** para repetir a comparação ou **Atualizar diffs** para renovar a lista após mudanças. **Cancelar operação** aguarda o término e descarta leituras tardias.

Com a tarefa aprovada e a worktree sem mudanças locais, **Registrar commit revisado** guarda o commit/árvore da consulta desde a base, após confirmação. Salve as mudanças em Git antes de registrar. Cabeçalho mostra o registro persistido; essa ação não faz merge ou libera dependentes. [Fluxo e limites do registro](TASK_DELIVERIES.md).

Depois do registro, **Preparar combinação** confirma origem/destino e combina numa nova pasta separada. O resumo mostra pasta, árvore ou conflitos e pode ser copiado; os arquivos/diffs abaixo continuam sendo os da tarefa. Combinação não publica/testa/libera dependentes. [Fluxo e limites da preparação](TASK_INTEGRATION_PREPARATION.md).

A janela conserva o projeto e a tarefa capturados, mesmo ao mudar a seleção na fila ou na central. Cabeçalho mostra pasta/base; lista mostra estado desde a base, no índice e na pasta, além de renomeações e conflitos. Binários, prévias excessivas e comparações sem diferença têm mensagens próprias. Consulta pendente retira a prévia anterior e bloqueia seleção; erro, mudança de estado ou cancelamento retira a lista e exige nova atualização. Fechar o painel ou a central cancela e aguarda suas leituras.

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

## Validação

Testes automatizados com Git real em pastas temporárias verificam as três comparações, commits posteriores, renomeações sucessivas, exclusões, arquivos novos, conflitos, limites, caminhos literais e preservação de índice/lock/original. Cobrem ferramentas externas/filtros desabilitados, mudança de tarefa/status, cancelamento e timeout.

O teste WPF `--diffs` abre o painel pelo botão da fila, confere conteúdo desde base/índice/pasta, UTF-8/binários/grandes, renomeação/exclusão e conflito real. Verifica navegação com projeto/tarefa fixos, invalidação após mudança, cancelamento com resposta tardia e fechamento do painel/central. Layout normal/mínimo revisado e zero erros de binding. Temporização usa componente de teste; nenhum modelo é chamado. Integração verificada continua pendente.
