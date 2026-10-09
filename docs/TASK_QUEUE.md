# Fila de tarefas de projetos gerais

A fila trabalha com qualquer projeto suportado pelos provedores e uma pasta cadastrada. Não exige engine, Git ou contexto de jogo. As funções são instruções de trabalho para sessões normais de Codex/Claude.

## Fluxo

1. Gere um plano pela chefia, revise e confirme em **Revisar planos**.
2. Em **Fila de tarefas**, escolha o plano aprovado e **Encaminhar à fila**. Isso salva sua revisão e cria tarefas/conversas, sem chamar modelos.
3. Selecione uma tarefa disponível e **Iniciar tentativa**, ou use **Iniciar tarefas disponíveis** para distribuir uma rodada do plano nas vagas livres. O grupo respeita o limite salvo, as dependências e as pastas; não inicia rodadas seguintes automaticamente. [Configuração de 3–7 sessões](SESSION_CONCURRENCY.md).
4. Confira resposta, arquivos, eventos e critérios. **Solicitar ajustes** exige uma nota e permite nova tentativa na mesma sessão. **Aprovar entrega** registra a decisão; dependentes exigem aprovação e integração Git quando a entrega usa worktree.

Antes da primeira tentativa com escrita, a aba **Pasta de trabalho** permite preparar uma worktree explicitamente. Pasta, branch e base ficam persistidas; tentativas/ajustes usam o mesmo checkout e sessão. Confira configurações e arquivos disponíveis antes de executar. [Fluxo, recuperação e limites](TASK_WORKTREES.md).

Com a worktree pronta e a tarefa parada, use **Revisar diffs** na mesma aba. Selecione arquivo e comparação desde a base, índice ou pasta; confira renomeações, exclusões, arquivos novos e conflitos. A janela permanece vinculada à tarefa ao navegar. [Fluxo, atualização e limites](TASK_DIFFS.md).

Depois de aprovar a entrega e salvar as mudanças em Git, use **Registrar commit revisado** no painel de diffs. O registro conserva commit/árvore e tentativa aprovada, sem integrar arquivos. Atualizar a fila mostra o commit em Pasta de trabalho. [Fluxo e limites do registro](TASK_DELIVERIES.md).

Com o commit registrado, **Preparar combinação** confirma origem/destino e cria uma pasta separada para o merge, com árvore ou conflitos persistidos. Configure **Critérios de validação** na central e use **Validar combinação** no painel. Passed vigente permite **Publicar no projeto**, com confirmação e nova conferência do conteúdo. Publicação local registrada libera dependentes, cuja pasta precisa conter a revisão integrada. [Validação](PROJECT_VALIDATION.md) e [publicação](TASK_PUBLICATION.md).

Propostas encaminhadas ficam preservadas e sem edição. Um plano entra na fila uma única vez; repetir o encaminhamento não duplica tarefas. **Chefia e plano** prepara seu estado/resultados no chat. Uma nova proposta aprovada pode atualizar somente tarefas ainda não iniciadas e sem worktree, com confirmação e histórico preservado. [Fluxo e revisões](PLAN_UPDATES.md).

## Estado e revisão

Na fila → executando → aguardando revisão → entrega aprovada ou ajustes solicitados. Falha, permissão recusada, cancelamento e interrupção têm estados próprios e não liberam dependências. Término do modelo não equivale a aprovação da entrega nem a integração Git.

Cada tentativa tem run, resposta, erros, eventos e identificador nativo preservados. A aba **Tentativas** permite consultar respostas anteriores; somente a entrega atual concluída pode ser revisada. Decisões obsoletas são recusadas no banco. Aprovação da entrega é definitiva neste incremento; tarefas iniciadas não aceitam mudanças de contrato/dependências pela revisão do plano.

O pedido de uma tarefa contém objetivo, função/instruções, acesso recomendado, escopo, critérios e ajuste anterior. Dependentes recebem IDs dos runs aprovados, resposta até 4.000 caracteres e nota até 2.000 por dependência; trechos extensos são identificados. Não há memória compartilhada automática. Arquivos necessários são consultados no projeto, sujeito às permissões do provedor. Contexto e escopo orientam a entrega; não constituem sandbox nem revisão Git fixada.

## Limites e recuperação

- 1–3 tentativas por tarefa e 1–300s por execução, configuráveis por projeto, padrões 3/300. Todas as tentativas são explícitas. Timeout aguarda o executor, conserva parcial e não aceita sucesso tardio. Não há loops ou novas tentativas automáticas.
- Chat e fila compartilham até sete execuções globais, com limite de 3–7 por projeto (padrão 3), sem exclusividade por provedor. Escrita no original trabalha sozinha; tarefas com worktrees distintas e validadas admitem concorrência. [Regras de sessões](SESSION_CONCURRENCY.md).
- Autorizações usam o host já existente e aparecem também na janela da fila. Confirmar plano, encaminhar ou aprovar entrega não concede permissões de ferramentas.
- Cancelar aguarda encerramento do processo antes de liberar capacidade. Não desfaz arquivos já modificados; confira efeitos antes de iniciar outra tentativa.
- Encerrar a central cancela e aguarda os jobs do chat e da fila. Reabertura marca runs/tarefas em execução como interrompidos, sem reenvio.
- Schema 3 acrescenta task_batches/work_tasks, preservando projetos, propostas e histórico anteriores. Reserva e run, assim como término e estado da tarefa, são gravados em transações.
- Conversas de tarefas podem ser consultadas na central; envios e mudanças de contrato passam pela fila.

Worktrees/diffs/validação/publicação e acompanhamento assistido da chefia estão disponíveis. Limpeza assistida, quota restante/limites agregados de consumo e rodadas automáticas continuam ausentes. Aprovação de entrega em worktree exige Published compatível para liberar dependentes; arquivos são reconferidos na validação/publicação.

## Validação

126 testes xUnit, teste WPF da fila com provedores simulados, regressões da central/planos e capturas em tamanho mínimo. O teste visual cobre permissões, ajustes, sessão retomada, revisão de tentativa antiga, dependências, cancelamento, troca de projeto e reabertura; nenhum modelo é chamado.

Diagnóstico opcional `rtk proxy dotnet run --project tools/Sintonia.Diagnostics --no-build -- queue`: no máximo dois turnos reais de leitura, um por provedor, em pasta exclusiva de artifacts/provider-probes, prazo total de três minutos. O plano é fornecido pelo diagnóstico e a revisão é uma decisão do próprio teste após conferir o resultado. Não representa geração de plano nem autorização automática em um projeto do usuário. Para na primeira falha, sem nova tentativa; consome quota e fica fora de CI/loops.
