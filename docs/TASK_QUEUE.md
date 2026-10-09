# Fila de tarefas de projetos gerais

A fila trabalha com qualquer projeto suportado pelos provedores e uma pasta cadastrada. Não exige engine, Git ou contexto de jogo. As funções são instruções de trabalho para sessões normais de Codex/Claude.

## Fluxo

1. Gere um plano pela chefia, revise e confirme em **Revisar planos**.
2. Em **Fila de tarefas**, escolha o plano aprovado e **Encaminhar à fila**. Isso salva sua revisão e cria tarefas/conversas, sem chamar modelos.
3. Selecione uma tarefa disponível e **Iniciar tentativa**. Apenas essa tarefa é enviada; não há despacho automático das próximas.
4. Confira resposta, arquivos, eventos e critérios. **Solicitar ajustes** exige uma nota e permite nova tentativa na mesma sessão. **Aprovar entrega** registra a decisão; dependentes exigem aprovação e integração Git quando a entrega usa worktree.

Antes da primeira tentativa com escrita, a aba **Pasta de trabalho** permite preparar uma worktree explicitamente. Pasta, branch e base ficam persistidas; tentativas/ajustes usam o mesmo checkout e sessão. Confira configurações e arquivos disponíveis antes de executar. [Fluxo, recuperação e limites](TASK_WORKTREES.md).

Com a worktree pronta e a tarefa parada, use **Revisar diffs** na mesma aba. Selecione arquivo e comparação desde a base, índice ou pasta; confira renomeações, exclusões, arquivos novos e conflitos. A janela permanece vinculada à tarefa ao navegar. [Fluxo, atualização e limites](TASK_DIFFS.md).

Depois de aprovar a entrega e salvar as mudanças em Git, use **Registrar commit revisado** no painel de diffs. O registro conserva commit/árvore e tentativa aprovada, sem integrar arquivos. Atualizar a fila mostra o commit em Pasta de trabalho. [Fluxo e limites do registro](TASK_DELIVERIES.md).

Com o commit registrado, **Preparar combinação** nesse painel confirma origem/destino e cria uma pasta separada para o merge, com árvore ou conflitos persistidos. Testes, publicação e liberação de dependentes continuam pendentes. [Fluxo e limites da combinação](TASK_INTEGRATION_PREPARATION.md).

Planos encaminhados são preservados e não aceitam edição. Um plano entra na fila uma única vez; repetir o encaminhamento não duplica tarefas. Para outro planejamento, gere uma nova proposta.

## Estado e revisão

Na fila → executando → aguardando revisão → entrega aprovada ou ajustes solicitados. Falha, permissão recusada, cancelamento e interrupção têm estados próprios e não liberam dependências. Término do modelo não equivale a aprovação da entrega nem a integração Git.

Cada tentativa tem run, resposta, erros, eventos e identificador nativo preservados. A aba **Tentativas** permite consultar respostas anteriores; somente a entrega atual concluída pode ser revisada. Decisões obsoletas são recusadas no banco. Aprovação da entrega é definitiva neste incremento; não há revogação nem edição posterior de dependências da cópia encaminhada.

O pedido de uma tarefa contém objetivo, função/instruções, acesso recomendado, escopo, critérios e ajuste anterior. Dependentes recebem IDs dos runs aprovados, resposta até 4.000 caracteres e nota até 2.000 por dependência; trechos extensos são identificados. Não há memória compartilhada automática. Arquivos necessários são consultados no projeto, sujeito às permissões do provedor. Contexto e escopo orientam a entrega; não constituem sandbox nem revisão Git fixada.

## Limites e recuperação

- Até três tentativas por tarefa, todas iniciadas pelo usuário. Não há loops de modelos ou novas tentativas automáticas.
- Chat e fila compartilham até duas execuções globais e uma por provedor. Leitura independente admite concorrência; escrita trabalha sozinha no mesmo projeto.
- Autorizações usam o host já existente e aparecem também na janela da fila. Confirmar plano, encaminhar ou aprovar entrega não concede permissões de ferramentas.
- Cancelar aguarda encerramento do processo antes de liberar capacidade. Não desfaz arquivos já modificados; confira efeitos antes de iniciar outra tentativa.
- Encerrar a central cancela e aguarda os jobs do chat e da fila. Reabertura marca runs/tarefas em execução como interrompidos, sem reenvio.
- Schema 3 acrescenta task_batches/work_tasks, preservando projetos, propostas e histórico anteriores. Reserva e run, assim como término e estado da tarefa, são gravados em transações.
- Conversas de tarefas podem ser consultadas na central; envios e mudanças de contrato passam pela fila.

Worktrees por tarefa e revisão de diffs estão disponíveis; integração Git, limites configuráveis/consumo e atualização automática do plano pela chefia permanecem pendentes. Entregas aprovadas em worktrees mantêm dependentes bloqueados neste incremento. Alterações externas nos arquivos não são fixadas por aprovação; integração verificável é o marco M3.

## Validação

126 testes xUnit, teste WPF da fila com provedores simulados, regressões da central/planos e capturas em tamanho mínimo. O teste visual cobre permissões, ajustes, sessão retomada, revisão de tentativa antiga, dependências, cancelamento, troca de projeto e reabertura; nenhum modelo é chamado.

Diagnóstico opcional `rtk proxy dotnet run --project tools/Sintonia.Diagnostics --no-build -- queue`: no máximo dois turnos reais de leitura, um por provedor, em pasta exclusiva de artifacts/provider-probes, prazo total de três minutos. O plano é fornecido pelo diagnóstico e a revisão é uma decisão do próprio teste após conferir o resultado. Não representa geração de plano nem autorização automática em um projeto do usuário. Para na primeira falha, sem nova tentativa; consome quota e fica fora de CI/loops.
