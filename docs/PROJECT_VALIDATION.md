# Validação configurável por projeto

O núcleo permite salvar até dez comandos por projeto. Cada comando tem nome do critério, caminho absoluto de um executável `.exe`, lista de argumentos literais, pasta relativa e prazo de 1 a 600 segundos. Nenhuma stack é obrigatória; o usuário pode configurar ferramentas de código, documentação ou outros arquivos. Lista vazia representa ausência de validação configurada.

Configurações ficam no SQLite (introduzidas no schema 8; histórico da combinação no schema 9), separadas por projeto e com revisão para recusar alterações concorrentes. Limpar comandos também aumenta a revisão. Parâmetros são copiados antes de salvar ou executar; mudanças posteriores na lista do chamador não alteram essa cópia.

`ValidationCommandRunner` inicia o executável diretamente, sem montar uma linha de shell. Wrappers `.cmd`/`.ps1` não são executados automaticamente; quando necessário, configure explicitamente o executável do interpretador e cada argumento. A pasta deve existir dentro do checkout e não usar links/junções ou `..`. O executável deve existir e ter caminho direto. Não há instalação/restauração automática de dependências nem interpretação de respostas dos modelos.

Stdout e stderr são lidos simultaneamente, com até 64 Ki caracteres por stream. Código zero significa que o comando passou; código diferente, timeout e cancelamento têm estados distintos. Saída truncada é identificada. Timeout/cancelamento encerram a árvore do processo e preservam a saída recebida. Variáveis Git herdadas são retiradas somente do processo. Diagnósticos existentes continuam com limite de 60 segundos; os comandos de validação admitem até dez minutos.

No Windows, `ValidationProcessJob` associa o comando a um Job Object com encerramento ao fechar o handle. Termina o grupo e aguarda até três segundos por zero processos ativos antes de devolver o resultado, inclusive quando o processo principal sai deixando filhos. Falha de associação/encerramento impede sucesso. Processos de servidor criados pelo comando também são encerrados; este fluxo não mantém serviços em background. A associação ocorre logo após `Process.Start`: não constitui uma barreira para processos criados antes dela ou trabalho acionado por serviços/APIs externas. Comportamento nativo conforme [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) e [TerminateJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-terminatejobobject).

Comandos usam as permissões locais do usuário e podem acessar rede e outros diretórios conforme suas ferramentas/configurações. A conferência Git do host permanece sem rede; essas restrições de diagnóstico não são impostas ao comando configurado. A pasta combinada separa arquivos, mas não é um sandbox. Argumentos e saídas são dados locais persistíveis: não inclua senhas ou outros segredos. Arquivos ignorados da origem não são copiados para a combinação.

## Validação da combinação

`TaskIntegrationValidationService.PreviewAsync` exige uma combinação `Combined`, ao menos um comando e origem/destino ainda correspondentes aos commits preparados. Retorna pasta/árvore e cópia dos comandos/revisão para uma confirmação futura pela interface. `ValidateAsync` recebe essa prévia, adquire a trava por Git comum e reserva o repositório antes de registrar a intenção e iniciar qualquer comando. Não inicia inferência.

A reserva bloqueia outras preparações e runs relacionados, inclusive projetos cadastrados em pastas de combinação. A revisão da configuração e a preparação persistida são comparadas numa transação antes de executar. Comandos rodam na ordem configurada; falha/timeout encerra a sequência. O resultado registra projeto, reserva, preparação, commit/árvore, revisão/argumentos, horários, saídas limitadas e código de cada comando encerrado. Término e liberação são atômicos; um término antigo não sobrescreve resultado existente.

`GitTaskIntegrationValidationInspector` verifica checkout/HEAD destacado/bloqueio/Git comum, entradas do índice, conflitos, arquivos novos e conteúdo de todos os arquivos versionados. Usa listas NUL nativas para caminhos literais e `hash-object` sem escrever objetos, preservando a normalização interna do Git, como CRLF, e desativando filtros externos. Não confia no cache de stat ou em `assume-unchanged`. Confere antes e depois de cada comando, com prazo total de 60 segundos por conferência, até mil arquivos e saídas Git completas de até 64 Ki caracteres. Índices/locks/arquivos não são alterados pela conferência. Limites, links, filtros e tipos incompatíveis geram recusa explícita.

Código zero não basta: se o comando mudar conteúdo versionado/índice, criar arquivo não ignorado ou alterar origem/destino, a operação fica `NeedsAttention`, com os arquivos e logs preservados. Um comando que só produz arquivos ignorados pode passar. Critérios alterados durante a execução também impedem `Passed`; o banco compara a configuração novamente ao salvar sucesso. Uma mudança posterior de critérios não reescreve registros históricos: sua revisão continuará visível e precisará ser revalidada antes de uma futura publicação.

| Estado | Significado |
| --- | --- |
| Running | Intenção salva; comandos em andamento |
| Passed | Todos os comandos saíram com zero e árvore/critério foram conferidos |
| Failed | Código diferente de zero; sequência interrompida |
| TimedOut | Prazo do comando excedido; sequência interrompida |
| Cancelled | Cancelamento; resposta tardia não produz sucesso |
| Interrupted | Proprietário encerrou antes de salvar; recuperação não repete comandos |
| NeedsAttention | Conteúdo/critério alterado, processo indisponível ou outra recusa |

Cancelar após salvar a intenção retorna o registro `Cancelled` e conserva a saída recebida; cancelamento anterior pode lançar `OperationCanceledException`. Uma transação terminal concluída conserva seu resultado. Enquanto o executor mantém a trava, recuperação preserva `Running`; depois da saída do proprietário marca `Interrupted`, sem executar comandos, modificar ou apagar arquivos. Se houver encerramento inesperado, saídas de comandos ainda não salvas podem faltar: não há checkpoint contínuo neste incremento.

`Passed` é evidência histórica da árvore versionada conferida, não prova permanente de que arquivos físicos, dependências ignoradas, executável ou ambiente continuam iguais. Processos externos ainda podem alterar o checkout após a conferência. Publicação futura deve revalidar configurações, origem/destino e arquivos. Comandos e saída recebida não são critérios de revisão humana automaticamente aprovados. Worktrees continuam compartilhando objetos/configuração Git.

## Estado e próximos passos

Configuração, runner, operação serial, conferência e histórico estão conectados ao WPF. Na central, **Critérios de validação** edita até dez comandos, sua ordem e revisão, com rascunho separado e descarte confirmado. Argumentos usam uma lista JSON de strings para preservar espaços, aspas, argumentos vazios e quebras de linha; não há interpretação automática de shell. Edição obsoleta conserva o rascunho e exige reverter/atualizar antes de salvar novamente.

Nos diffs, **Validar combinação** abre o histórico da preparação registrada. **Conferir e validar** apresenta a pasta, árvore, revisão, executáveis, argumentos e prazos em uma prévia integral com rolagem; confirmar executa uma única sequência. Recusar não cria intenção nem inicia comandos. Os resultados mostram critérios, estados, códigos, stdout/stderr e truncamento. Projeto/preparação ficam fixos ao navegar. Cancelamento e fechamento aguardam execução e recarga; a central aguarda todos os painéis antes de encerrar. Logs de comandos em andamento ainda não têm checkpoint contínuo.

Teste WPF `--validation` aprovado com Git/processos reais e temporização simulada, sem modelos: configuração/revisão, argumentos literais, recusa, prévia obsoleta, sucesso/falha, sequência interrompida, logs/histórico, cancelamento/tardio, reabertura e fechamento. Zero erros de binding; capturas normal/mínima revisadas. Build sem avisos/erros. [Publicação confirmada](TASK_PUBLICATION.md) conectada ao mesmo painel; `Passed` sozinho não libera sucessores.

Testes cobrem isolamento/configuração/revisão, migrações, argumentos literais, streams, falha, timeout/cancelamento com filho, árvore/índices preservados, conteúdo alterado apesar de `assume-unchanged`, recusa de critérios obsoletos, recuperação viva/abandonada, resultado tardio e dependentes bloqueados. Git real em pastas temporárias e processos de teste, sem modelos.

Evidências deste incremento: build completo sem avisos/erros; 247 testes xUnit verificados (65 Core + 182 Infrastructure). Execução completa: 65 Core aprovados e 179/180 Infrastructure; a falha identificou uma variável Git que a configuração do diagnóstico sobrescrevia antes da remoção. Correção remove as variáveis pelas chaves herdadas, sem registrar seus valores. Em seguida, 20 testes selecionados passaram, incluindo a correção, Job Object/filho após saída do principal e validação real preservando árvore/índices. Dois testes de interrupção interna/cancelamento passaram após ajuste de estado. WPF --combinations aprovado sem erros de binding; Release atualizado e abertura/encerramento conferidos. A contagem é agregada de casos verificados; não representa uma única execução completa posterior às correções.

Comandos de inspeção conforme [git ls-tree](https://git-scm.com/docs/git-ls-tree), [git ls-files](https://git-scm.com/docs/git-ls-files) e [git hash-object](https://git-scm.com/docs/git-hash-object). Na versão instalada, o formatador genérico `%(path)` da árvore ainda escapa certos nomes mesmo com `-z`; o host usa o formato padrão com NUL, verificado com acentos/espaços.
