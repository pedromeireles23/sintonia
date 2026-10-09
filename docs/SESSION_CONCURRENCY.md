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

Worktrees separam alterações e compartilham objetos/configuração Git; não são um sandbox. Publicação continua serial e em desenvolvimento. Aprovação, combinação ou validação Passed não liberam dependentes de entregas ainda não integradas.

## Chefia e interface

O chefe já produz um plano estruturado com função, provedor/modelo, escopo, dependências e critérios. Revisar e confirmar o plano cria as sessões na fila, sem iniciar modelos. O início das tarefas continua sendo uma ação separada, com permissões e revisão das entregas preservadas.

Neste incremento, configuração e admissão são do núcleo. Seletor WPF de 3–7 e início em grupo das tarefas disponíveis são o próximo incremento. O chefe ainda não acompanha ou modifica automaticamente o plano após cada resultado.

## Validação

Build completo sem avisos/erros; 69 testes Core e 31 testes selecionados de Infrastructure aprovados. Cobertura de limites 3/5/7 com provedores misturados e repetidos, oitava sessão recusada, cancelamento aguardando o executor, concorrência entre stores, redução do limite, escopo congelado, pastas sobrepostas e migração do schema 9. Git real verifica dois escritores em checkouts validados distintos e original preservado. Provedores simulados; não foi exercitado um grupo de sete modelos reais nem consumida quota neste incremento.
