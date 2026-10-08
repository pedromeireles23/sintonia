# Pastas Git por tarefa

O M3 permite preparar uma worktree para uma tarefa com escrita antes da primeira tentativa. A criação parte de um commit existente e mantém o projeto original intacto. Worktrees separam arquivos, mas compartilham o repositório Git e não são um sandbox de segurança.

## Contrato implementado

- `TaskWorktreeService` consulta a tarefa atual e prepara uma prévia sem criar arquivos. O chamador confirma diretório, branch e commit antes de chamar a preparação.
- Schema 5 registra a intenção antes de alterar o Git: tarefa, repositório, diretório Git comum, checkout, subpasta relativa do projeto, commit de base, branch, estado e erro.
- O destino padrão é `%LOCALAPPDATA%/Sintonia/worktrees/<id>` e a branch é `codex/sintonia/<id>`. O destino fica fora do repositório original. Caminhos com links/junções são recusados neste incremento.
- A criação usa Git nativo, argumentos separados, `worktree add -b` e um bloqueio identificado pela tarefa. Não usa force, reset, remoção, commit, fetch ou merge. Hooks e fsmonitor ficam desabilitados somente nos processos Git de preparação; configuração persistente e permissões dos provedores são preservadas.
- Alterações preparadas/não preparadas, arquivos novos e ignorados permanecem no original e não são copiados. Instruções e configurações versionadas vêm do commit. Arquivos locais ignorados, dependências e credenciais não são copiados automaticamente.
- A subpasta do projeto precisa existir no commit. São recusados repositórios sem commits, bare, Git ausente, submódulos e arquivos que usam filtros de checkout, como LFS. A mera instalação global do LFS não bloqueia projetos sem esses atributos. Consulta de atributos usa a árvore do commit confirmado.
- Processos leem stdout/stderr de forma assíncrona, com limite de 64 Ki caracteres por stream e prazo de 60 segundos por operação de preparação/validação. Nenhuma chamada de modelo é necessária para preparar.

## Interrupção e execução

`Preparing` bloqueia a tentativa. Falha/cancelamento ou recuperação do aplicativo registra `NeedsAttention`, conserva caminhos/branches e exige nova ação explícita. Uma repetição só aceita o mesmo vínculo; um checkout já criado precisa estar registrado, bloqueado pela tarefa, na branch e no commit esperado e sem alterações. Pastas ou branches existentes não são sobrescritas. Preparações do mesmo repositório são serializadas por reserva no SQLite, inclusive entre instâncias do store.

`Ready` permite executar na subpasta correspondente da worktree. Antes de cada tentativa, o serviço confere diretório real, registro, Git comum, branch, bloqueio e ancestralidade do commit de base. Alterações/commits produzidos por uma tentativa permanecem para a próxima, junto à mesma sessão nativa. Um vínculo alterado impede a chamada ao provedor sem consumir tentativa. O banco compara o vínculo esperado ao reservar o run, evitando que uma leitura anterior à preparação execute na pasta original.

Tarefas que não prepararam worktree continuam usando o projeto original. Escrita segue exclusiva no mesmo projeto lógico, mesmo com checkouts diferentes. Aprovar uma entrega numa worktree não integra seus arquivos: dependentes continuam bloqueados neste incremento. [Revisão de diffs](TASK_DIFFS.md) está disponível; integração verificada entra a seguir.

## Validação

Testes com Git nativo em pastas temporárias verificam preservação do original/índice, commit confirmado, subpastas, arquivos com acentos/espaços, registro/bloqueio, retomada, falha/cancelamento com efeitos parciais, colisões, migração do schema 4, pasta/branch alteradas, sessão e diretório estáveis, dependências não integradas e escrita serial. Provedores são simulados; nenhuma chamada paga é feita nesses testes.

## Fluxo na fila

1. Encaminhe um plano aprovado e selecione uma tarefa disponível com escrita, sem tentativas anteriores.
2. Abra **Pasta de trabalho** e use **Preparar worktree**. Confira pasta, branch e base na confirmação; recusar não cria arquivos nem grava intenção.
3. Aguarde **Worktree pronta**, confira os arquivos/configurações e use **Iniciar tentativa** separadamente. Autorizações mostram a pasta efetiva da tarefa.
4. Se houver interrupção, confira o estado e os arquivos antes de usar **Preparar worktree** novamente. **Cancelar** aguarda o registro do estado; fechar a central cancela e aguarda preparação e execução.

O painel conserva o projeto ao navegar na central. Mostra estado, pasta efetiva, checkout, branch, base, origem e diagnóstico. Seleção/início/revisão ficam bloqueados enquanto a preparação está ativa. Entrega e ajustes usam o fluxo existente da fila; a aprovação em worktree informa que dependentes aguardam integração. Não há remoção automática de worktrees.

Com a worktree pronta e a tarefa parada, **Revisar diffs** abre a comparação de arquivos desde a base, no índice ou na pasta. A consulta inclui todo o checkout e não fixa conteúdo para integração nem altera a aprovação da entrega.

Teste WPF `--worktrees` percorre recusa/confirmação pelos comandos visuais, preparação com Git real, escrita/autorização e ajuste com provedor simulado, reabertura, dependência bloqueada, troca de projeto, cancelamento parcial e fechamento da central. Layout normal/mínimo revisado, zero erros de binding. Preview evita status do original para não executar filtros clean; a conferência de retomada desabilita filtros somente no próprio comando.
