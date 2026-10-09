# Sessões e distribuição por projeto

O projeto admite de **3 a 7 sessões simultâneas**, com padrão de 3. Sessões cadastradas e histórico não têm esse limite: as vagas contam apenas execuções ativas, incluindo o chefe enquanto produz uma resposta. Codex e Claude podem ocupar qualquer combinação das vagas, inclusive várias sessões do mesmo provedor. Chat e fila compartilham um teto de 7 execuções no banco desta instalação.

`ProjectExecutionSettings` guarda o limite por projeto e uma revisão para impedir edições obsoletas. Reduzir o limite não cancela sessões existentes; novos inícios aguardam a quantidade ativa cair abaixo do limite. Schema 10 acrescenta `project_execution_settings` e `run_execution_scopes`, preservando os dados anteriores. Projetos sem configuração usam 3.

`WorkspaceExecutionPolicy` é compartilhada pelo serviço e pela reserva transacional de runs no SQLite. A reserva salva o acesso e a raiz efetivos da execução: editar uma conversa posteriormente não muda sua reserva. Cancelamento conserva a vaga até o provedor parar e o resultado ser persistido. Reservas entre stores do mesmo banco contam juntas; uma tentativa recusada não consome a tentativa da tarefa.

## Pastas e alterações

- Leitura pode ocorrer em várias sessões, inclusive com o chefe consultando a pasta original enquanto tarefas trabalham em outras worktrees.
- Escrita direta na pasta original executa sozinha no projeto.
- Tarefas independentes de escrita podem executar juntas quando cada uma possui uma worktree pronta, registrada e validada antes da tentativa. A comparação usa a raiz do checkout, inclusive para projetos em subpastas.
- Pastas sobrepostas com uma execução de escrita são recusadas, inclusive entre projetos cadastrados separadamente.
- Preparações e integrações Git conservam suas reservas próprias. Prepare as worktrees antes de iniciar o grupo de tarefas.

Worktrees separam alterações e compartilham objetos/configuração Git; não são um sandbox. [Publicação](TASK_PUBLICATION.md) é serial, confirmada e registrada. Aprovação, combinação ou validação Passed não liberam dependentes de entregas ainda não integradas.

## Chefia e interface

O chefe já produz um plano estruturado com função, provedor/modelo, escopo, dependências e critérios. Revisar e confirmar o plano cria as sessões na fila, sem iniciar modelos. O início das tarefas continua sendo uma ação separada, com permissões e revisão das entregas preservadas.

A central oferece o seletor em **Função, permissões e sessões**. Escolha 3–7 e use **Aplicar limite**; a escolha em rascunho ainda não muda a reserva. A contagem mostra execuções no projeto e na central.

Na fila, **Iniciar tarefas disponíveis** lê novamente o plano e o limite salvos e distribui uma rodada nas vagas livres. Cada tarefa conserva o provedor/modelo e sua sessão. Dependências indisponíveis, tarefas em revisão, tentativas esgotadas e pastas incompatíveis ficam aguardando. Não há repetição automática, aprovação automática ou loop de modelos. Prepare as worktrees de escrita antes de iniciar o grupo.

**Cancelar** interrompe o grupo iniciado por esse painel; uma tarefa iniciada separadamente conserva seu controle. Fechar a fila cancela e aguarda suas execuções/preparações; fechar a central aguarda todos os seus processos. A fila e seu contador continuam vinculados ao projeto capturado, mesmo ao navegar na central. Sessões podem ser consultadas pela lista da central durante a execução do grupo. Seleção/revisão na fila ficam disponíveis ao terminar a rodada.

O chefe ainda não acompanha ou modifica automaticamente o plano após cada resultado. O fluxo atual é objetivo → proposta do chefe → revisão/confirmação → fila → início individual ou em grupo → revisão das entregas.

## Validação

Build completo sem avisos/erros, suíte completa com 257 testes aprovados (69 Core + 188 Infrastructure); núcleo e testes WPF usam provedores simulados e Git real. Cobertura de limites 3/5/7 com provedores misturados e repetidos, oitava sessão recusada, cancelamento aguardando o executor, concorrência entre stores, redução do limite, escopo congelado, pastas sobrepostas e migração do schema 9. Git real verifica escritores em checkouts validados distintos e original preservado.

WPF `--sessions` percorre chefia/plano/fila, sete sessões misturadas com três tarefas de escrita (duas Codex e uma Claude), aplicação/redução do limite, edição concorrente recusada com rascunho preservado, preservação de outra tarefa ao cancelar o grupo, projeto fixo, reabertura e fechamento aguardando os sete executores. Capturas normal/mínima revisadas; zero erros de binding. Regressões de central, propostas, perfis, fila e worktrees aprovadas. Não foi exercitado um grupo de sete modelos reais nem consumida quota neste incremento.

Release local atualizado em artifacts/app; abertura e encerramento do executável normal conferidos.
