# Diagnóstico Git por projeto

O painel mostra o estado local do repositório antes da preparação de worktrees. É uma consulta em leitura: preserva arquivos, índice, branches e configurações. Não chama Codex, Claude ou serviços de rede.

## Usar o painel

1. Selecione uma pasta de projeto na central.
2. Abra **Diagnóstico Git**. A consulta começa ao carregar a janela.
3. Confira raiz, branch, commit, acompanhamento e alterações.
4. Use **Atualizar diagnóstico** após mudanças; **Cancelar consulta** interrompe somente esta consulta.

O projeto da janela fica indicado no cabeçalho e permanece o mesmo ao alternar projetos na central. Abra outro painel para consultar a nova seleção. Fechar a janela cancela a consulta; fechar a central cancela e aguarda suas consultas e execuções dos provedores.

Uma pasta dentro de um repositório mostra alterações de **todo o repositório**, com caminhos relativos à raiz indicada. Não assumir que o relatório está limitado à subpasta cadastrada.

## Entender o resultado

| Informação | Significado |
| --- | --- |
| Raiz | Pasta de trabalho identificada pelo Git, inclusive em worktree existente |
| Branch / commit | Estado atual; HEAD destacado e repositório sem commits aparecem explicitamente |
| Acompanhamento | Branch configurada e contagens conforme referências locais, sem atualizar remotos |
| Preparado para commit | Diferença registrada no índice |
| Na pasta | Diferença entre arquivos da pasta e o índice |
| Não versionado / novo | Arquivo presente e ainda não acompanhado pelo Git |
| Antes | Nome de origem de um caminho renomeado ou copiado |
| Conflito | Caminho ainda não resolvido no índice; consulta concluída não equivale a integração aprovada |
| Submódulo | Estado reportado pelo Git para o módulo; o painel não revisa seus arquivos internos |

Um caminho pode ter alterações tanto no índice como na pasta, aparecendo nas duas contagens. Uma renomeação ocupa uma linha, com caminho atual e origem. Arquivos ignorados não são listados. **Sem alterações locais** exige uma consulta completa e válida; não afirma que arquivos ignorados estejam preservados ou que uma entrega tenha sido revisada.

Pasta sem repositório, Git ausente, repositório bare e falhas têm mensagens próprias. **Chat e fila continuam disponíveis em projetos sem Git.** Não há criação automática de repositório, commit ou alteração de regras de confiança. Bare não tem pasta de trabalho; a área interna .git também não é uma pasta adequada para executar tarefas.

## Limites e implementação

- Git nativo encontrado no PATH absoluto; wrappers de shell e entradas relativas não são executados.
- Prazo total de 20 segundos, com stdout/stderr assíncronos e até 64 Ki caracteres retidos por stream.
- Saída truncada ou formato incompleto são recusados. Resultado anterior é retirado durante atualização; resposta tardia de uma consulta cancelada é descartada.
- Variáveis GIT_* herdadas são removidas somente do processo de consulta, evitando redirecionamento de pasta/índice/configuração ou traces herdados. Locks opcionais, fsmonitor, cache de arquivos novos e protocolos de rede ficam desativados nesse processo.
- As configurações locais/globais de Git, incluindo confiança e arquivos ignorados, continuam sujeitas às regras do Git. Erros não publicam stderr bruto que possa incluir configuração sensível.
- O resultado não é persistido no banco nem representa uma reserva de branch/commit. O projeto pode mudar durante ou depois da consulta; operações futuras precisam conferir suas próprias precondições.

Formato baseado na [documentação do status porcelain](https://git-scm.com/docs/git-status); identificação conforme [rev-parse](https://git-scm.com/docs/git-rev-parse). A opção de leitura sem atualização opcional do índice segue [Git --no-optional-locks](https://git-scm.com/docs/git).

Criação de worktrees por tarefa, revisão de diffs e integração verificada ainda estão pendentes. Escrita permanece serial no mesmo projeto. Uma worktree separa alterações, sem constituir um sandbox de segurança.

## Validação

Git 2.46.0.windows.1 real verificado em pastas temporárias: alterações, renomeação, conflito, subpasta, HEAD destacado, branch sem commits, worktree existente, bare e preservação de índice/lock. Testes com processo auxiliar cobrem timeout, cancelamento, falha e saída excessiva; parser cobre nomes literais e entradas inválidas.

O teste WPF `--git` abre o painel pelo botão da central usando Git real em pasta de teste. Confere apresentação normal/mínima, raiz e alterações, índice preservado, troca de projeto, Git/repositório ausentes, cancelamento, resposta tardia e fechamento da janela/central. Conflitos/falhas de apresentação e o chat sem Git usam componentes de teste identificados. Nenhum modelo é chamado; zero erros de binding.
