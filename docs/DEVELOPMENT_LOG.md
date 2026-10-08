# Registro de desenvolvimento

Registro resumido de incrementos e pontos de retomada. Consulte `git log` para hashes e cronologia exata dos commits.

## 08/10/2026 — Preparação do repositório

- Repositório inicialmente vazio clonado na pasta existente do usuário.
- Nome Sintonia adotado conforme pasta e repositório.
- Stack definida como C#/.NET 10, WPF e MVVM.
- Contexto, roadmap, arquitetura e instruções para Codex e Claude criados.
- Política: commits pequenos de incrementos validados e atualização do contexto ao avançar.
- Validação: revisão documental; nenhum build ou teste .NET ainda, pois a solução está pendente.
- Próximo incremento: M0, solução .NET e primeiro painel WPF com demonstração identificada.

## 08/10/2026 — Solução e núcleo do M0

- Projetos Core, Infrastructure, Desktop e testes criados com .NET 10, nullable e warnings tratados como erros. Dependências NuGet registradas em lockfiles.
- Política pura de dependências/concorrência e coordenador com reserva atômica, aprovação, cancelamento, falha e novas tentativas preservadas em memória.
- Cancelamento conserva a vaga até a tentativa terminar; entregas em revisão não liberam dependentes. Troca de provedor não modifica tentativas ativas.
- Validação: 17 testes xUnit aprovados; build da solução. WPF ainda contém somente a janela base, sem painel ou integração real.
- Próximo incremento: painel MVVM em português e adaptadores explicitamente simulados.

## 08/10/2026 — Painel WPF e conclusão do M0

- Painel MVVM em português com funções editáveis, fila, entrega/eventos e histórico de sessões por tentativa. Simulação sinalizada na janela, sessões, eventos e resultados.
- Dois adaptadores simulados com atraso assíncrono/cancelável. O fluxo aprova cinco entregas e distribui as dependentes; nenhum CLI, modelo ou arquivo do jogo é utilizado.
- Validação: build sem avisos/erros, 17 testes do núcleo e teste executável da janela nativa com o fluxo completo e zero erros de binding. Capturas revisadas em tamanho normal e mínimo; painel de revisão com rolagem e ações acessíveis.
- Limites: estado somente em memória, cenário fixo, sem abertura de projeto, persistência ou integrações reais. Rejeição devolve a tarefa à fila; instruções de ajuste entram com o fluxo real.
- Próximo incremento: preparação limitada do M1 com detecção e conferência das interfaces instaladas; depois prova real pequena de cada provedor.

## 08/10/2026 — Preparação limitada do M1

- Diagnóstico .NET consulta somente versão/ajuda, sem shell, credenciais ou inferência. Detecção distingue ausência, layout incompatível e falha. Streams simultâneos limitados e cancelamento/timeout com encerramento da árvore do processo.
- Detectados Codex CLI `0.162.0-alpha.2` nativo e Claude Code `2.1.277` nativo por instalação npm. Interfaces anunciadas e fontes oficiais registradas em `docs/PROVIDER_PROBES.md`; flags não foram apresentadas como integração pronta.
- Validação: 11 testes de Infrastructure e 17 do Core aprovados, incluindo argumentos literais, saída excessiva, falha, timeout com filho e cancelamento. Build da solução e diagnóstico real limitado a versão/ajuda.
- Limites: nenhuma inferência, handshake, parsing nativo, retomada, negativa de permissão ou extensão foi validada. O painel continua simulado e o M1 está parcial.
- Próximo incremento: handshake stdio sem turno, contratos/parsing com fixtures e uma prova real pequena de cada provedor com permissões verificadas antes de conectar à janela.

## 08/10/2026 — Direção de central geral de projetos

- Registrado o pedido de múltiplos projetos, chat com provedor/modelo, chefia configurável e abertura/retomada de sessões no Sintonia. M2/M4 ajustados; M1 continua o próximo incremento de código.
- Registrado o padrão de usar assinaturas pelos CLIs, sem fallback automático para API keys. Comandos de status consultados na conversa anterior informaram ChatGPT no Codex e `claude.ai`/Pro no Claude, sem exposição de identidade/segredos ou inferência.
- Fontes oficiais confirmam interfaces para escolha de modelo e retomada; capacidades na versão instalada ainda exigem prova. Abertura nos aplicativos gráficos originais não foi prometida como recurso validado.
- Validação: revisão documental, links e consistência entre escopo, contexto, arquitetura, decisões e roadmap. Nenhuma alteração de código ou teste sem relação com a documentação.
- Próximo passo: concluir a prova limitada das integrações por assinatura; depois implementar projetos/chat/sessões persistentes.

## 08/10/2026 — Conversas reais e retomada por assinatura

- Contrato de conversa e adaptadores Codex/Claude com eventos, identificador nativo, modelo, login por assinatura obrigatório, limites, resultado final, falha/bloqueio e cancelamento. Prompts via protocolo/stdin, sem interpretação de shell.
- Provas reais finitas: cada provedor leu a amostra, conferiu soma 25/marcador e retomou a mesma conversa sem reler arquivos. Modelos `gpt-6.1-sol`/`claude-opus-5`. Interrupção após início do stream verificada em ambos. Amostras/evidência local ignoradas em artifacts.
- Sandbox elevado Codex falhou nas ACLs; modo oficial unelevated, restrito ao processo do cliente, permitiu leitura e bloqueou escrita. Nenhuma configuração global alterada ou bypass adotado. Claude carregou skills/plugins/MCPs; alguns servidores precisam de autenticação ou falharam.
- Validação: build isolado para preservar janela demonstrativa aberta, sem avisos/erros; 45 testes xUnit (17 Core + 28 Infrastructure), com correlação, texto extenso/acento, falha, recusa, autorização explícita, retomada e interrupção simulada. Chamadas reais não integram a suíte automática.
- Limites: autorização interativa do Claude e equivalência completa de extensões pendentes. A janela permanece demonstrativa até o próximo incremento.
- Próximo passo: cadastro/alternância de projetos, chat central e sessões com histórico SQLite e retomada na janela.

## 08/10/2026 — Persistência de projetos e conversas

- SQLite com schema explícito/versionado, parâmetros, transações, pastas únicas e vínculo imutável de projeto/provedor. Identificador nativo e resposta parcial recebem checkpoints; histórico conserva resultados, falhas, bloqueios e cancelamentos.
- Serviço de chat reserva duas vagas/uma por IA; conversa com escrita executa sozinha no projeto até encerrar. Recuperação identifica interrupção sem repetir efeitos automaticamente.
- Validação: oito novos testes de persistência/serviço com provedores de teste: reabertura, duplicata, separação de projetos, checkpoint/recuperação, atualização indevida, resultado/falha/bloqueio, concorrência e cancelamento. Microsoft.Data.Sqlite 10.0.12 em lockfiles; operações executadas fora da thread WPF.
- Próximo incremento: central WPF com cadastro de pastas, escolha de modelo/função e abertura/retomada das conversas reais.

## 08/10/2026 — Central WPF conectada aos provedores reais

- Janela principal com cadastro/alternância de projetos, chat, IA/modelo, funções/instruções, leitura/escrita, atividade, sessões por projeto e cancelamento. Abrir conversa retoma o identificador nativo ao enviar; trocar IA exige nova conversa. Catálogo Codex consultado sem inferência; Claude oferece aliases anunciados e entrada de identificador.
- Histórico em LOCALAPPDATA/Sintonia/workspace.db. Uma instância por sessão Windows protege a reconciliação. Encerrar a janela cancela e aguarda os processos antes de fechar. Demonstração anterior permanece em janela separada, identificada.
- Permissões Codex podem ser respondidas na central por ação, com comando/diff concreto; solicitações sem prévia suficiente são recusadas. Claude ainda usa recusa de prompts sem host. Chefia atual acrescenta instruções de planejamento, sem distribuição automática.
- Validação: 55 testes xUnit, build sem avisos/erros, teste WPF com dois projetos/autorização/duas IAs em leitura simultânea/retomada/cancelamento/reabertura e zero erros de binding. Capturas revistas em tamanho normal/mínimo. Executável Release em artifacts/app abriu/encerrou a central. Prova opcional real fez exatamente um turno de leitura por IA na janela e conferiu soma/marcador e SQLite; não integra CI.
- Próximo incremento: host Claude e propostas de chefia estruturadas/tarefas revisáveis. Integração Git paralela permanece no M3; não anunciar chefia automática como pronta.

## 08/10/2026 — Autorizações do Claude na central

- Claude agora usa stream-json bidirecional com inicialização e pedidos de permissão correlacionados. A central apresenta provedor, pasta e parâmetros completos; aprovação vale para a ação exibida, sem regras permanentes. Leitura, ausência de host, prévia incompleta/excessiva e interações especiais são recusadas. Regras/hooks/extensões locais preservados, sem bypass.
- Decisões assíncronas não bloqueiam eventos. Retirada pelo CLI, cancelamento pelo usuário e término inesperado removem decisões pendentes; falha do host recusa a ação. Recusas locais produzem resultado bloqueado mesmo quando o CLI não as repete. Protocolo limita quantidade/tamanho e rejeita IDs repetidos ou conclusão com decisão pendente.
- Validação: 75 testes xUnit (17 Core + 58 Infrastructure), build sem avisos/erros e teste WPF de autorização Codex/Claude, recusa, cancelamento de decisão, concorrência, retomada e reabertura SQLite, com zero erros de binding. Captura revisada no tamanho mínimo com prévia e botões acessíveis.
- Prova real finita: dois turnos por assinatura com Claude Code `2.1.277`, modelo `claude-opus-5`. Primeira Write recusada e arquivo ausente; segunda, na mesma sessão, autorizada com caminho/conteúdo conferidos. Ask rules acrescentadas somente ao processo de teste. Evidência local ignorada em artifacts/provider-probes; teste real fora da suíte/CI. Release local atualizado em artifacts/app e abertura/encerramento verificados.
- Limites: prova real de Write não equivale a validar todas as ferramentas/MCPs. AskUserQuestion/ExitPlanMode ainda sem fluxo próprio; tarefas autônomas em background após o resultado terminal não são acompanhadas. Chefia permanece uma instrução de planejamento.
- Próximo incremento: proposta de chefia estruturada, validada e editável para revisão; depois tarefas persistentes e distribuição controlada. Integração Git paralela continua no M3.

## 08/10/2026 — Contrato e validação de propostas

- Core com PlanProposal/ProposedTask e formato versionado sintonia-plan. Um único bloco explícito evita converter texto livre ou escolher silenciosamente entre planos. Campos obrigatórios, provedores/acesso, limites e critérios de entrega validados; grafo reutiliza as regras existentes. Escopo aceita caminhos relativos e '.' explícito, sem interpretar comandos ou prometer isolamento de arquivos.
- Validação: build do Core e 51 testes aprovados, incluindo 34 novos casos de formato, enums/campos ausentes/extras/repetidos, dependências/ciclos, escopo Windows, limites e preservação de texto literal. Nenhum modelo chamado; sem mudança da janela ou do banco neste incremento.
- Próximo incremento: conectar chefia em leitura ao formato, salvar propostas com origem no histórico e abrir revisão editável/confirmável. Confirmar plano não deve iniciar tarefas ou conceder permissões.

## 08/10/2026 — Chefia e revisão de planos persistentes

- Chefia força leitura e retira o callback de autorização no serviço; contrato do plano é acrescentado ao pedido sem acumular instruções no histórico. Resposta concluída válida gera proposta; falha de parsing mantém o resultado original do chat. Abrir revisão reconcilia respostas ainda sem proposta, sem inferência ou sobrescrita.
- Schema 2 acrescenta propostas com vínculo único ao run/projeto, definição validada, estado Draft/Approved e revisão. Criação exige resposta concluída do mesmo projeto; edição compara revisão e mantém origem. Migração do schema 1 conserva conversas/execuções. Rascunho editado retira aprovação anterior.
- Janela Revisar planos permite editar responsável/modelo/função/acesso recomendado, instruções, escopo, dependências e critérios, adicionar/remover tarefas, validar, salvar e confirmar. Confirmação não executa tarefas nem concede permissões. Campos têm rolagem e ações permanecem acessíveis no tamanho mínimo. Troca de plano protege alterações não salvas; revisões obsoletas são recusadas.
- Validação: 117 testes xUnit (51 Core + 66 Infrastructure), build sem avisos/erros, testes WPF da central e do editor com zero erros de binding. A regressão da central revelou seleção sobrescrita durante carregamento do projeto; correção preserva a seleção existente e a reabertura passou. Capturas revisadas; imagem de dados simulados identificada no README. Release local atualizado e abertura/encerramento verificados.
- Prova real: exatamente um turno de planejamento em leitura com Claude `claude-opus-5`, sem ferramentas solicitadas, produzindo duas tarefas para os dois provedores e dependência válida. Plano em artifacts/provider-probes, ignorado; fora de CI/loop. Geração real pelo Codex não exercitada neste incremento.
- Próximo incremento: fila real persistente a partir de plano confirmado, despacho explícito e revisão de entregas/dependências. Escrita continua serial por projeto; worktrees e integração pertencem ao M3.

## 08/10/2026 — Núcleo persistente da fila geral

- Registrada a reafirmação do usuário: projetos gerais, sem especialização em jogos. Contexto, escopo e M6 agora descrevem tipos de projeto e validação configurável; exemplos históricos permanecem demonstrativos.
- Schema 3 acrescenta cópia imutável de plano aprovado, tarefas, estados e vínculos a conversas/runs. Encaminhamento exige revisão atual aprovada e é idempotente. Conversas de tarefas não permitem desvio pelo chat nem alteração de contrato.
- Despacho compartilha reservas do chat, limite de três tentativas, contexto dos runs aprovados e ajustes explícitos. Banco reserva tarefa/tentativa e finaliza entrega/estado atomicamente. Revisão exige o run atual concluído; falha, recusa, cancelamento e interrupção não liberam dependências. Recuperação não repete efeitos.
- Validação: build sem avisos/erros e suíte xUnit aprovada; oito novos testes cobrem aprovação, duplicação, origem, ajustes, revisão obsoleta, permissões, limite, concorrência, cancelamento, recuperação e migração do schema 2. Provedores de teste, sem chamadas pagas.
- Próximo incremento: janela da fila com despacho, atividade, histórico e revisão das entregas. Núcleo concluído não representa fluxo visual pronto.

## 08/10/2026 — Fila geral na central e prova real finita

- Janela Fila de tarefas conectada aos adaptadores reais: encaminhamento sem inferência, início explícito, resposta/eventos, histórico, cancelamento, permissões e revisão com aprovação ou ajustes. Jobs e decisões compartilham o serviço/painel da central. Conversas de tarefas são consultáveis, com envio e contrato protegidos. Planos encaminhados aparecem preservados na revisão.
- Ajustes retomam a sessão e dependentes recebem contexto da tentativa aprovada. Tentativa anterior não pode ser revisada. A janela conserva ações acessíveis com rolagem em tamanho mínimo; captura versionada identifica provedores simulados. Nenhuma exigência de jogo/engine na fila, exemplos/documentação atuais usam um portal geral.
- Validação: 126 testes xUnit (52 Core + 74 Infrastructure), build sem avisos/erros, testes WPF de central/planos/fila com zero erros de binding e capturas revisadas. Regressão da central encontrou cancelamento logo após envio registrado como falha; removida a rejeição antecipada para preservar a tentativa cancelada, e o fluxo passou. Release local atualizado; abertura/encerramento conferidos.
- Prova real: exatamente dois turnos em leitura na fila, Codex gpt-6.1-sol e Claude claude-opus-5, resposta com marcador/soma verificados, liberação por revisão do diagnóstico, transferência do resultado aprovado e reabertura SQLite. Plano fornecido pelo diagnóstico em pasta exclusiva; não simula geração pela chefia nem autorização automática em projeto do usuário. Fora de CI/loops, sem novas tentativas. Escrita e autorização visual pela fila testadas com provedores simulados; integração Git permanece pendente.
- Próximo incremento M2: perfis reutilizáveis de função com provedor/modelo padrão e instruções persistentes. Depois M3: worktrees, diffs e integração verificada; escrita permanece serial no mesmo projeto.

## 08/10/2026 — Persistência de perfis reutilizáveis

- Schema 4 acrescenta biblioteca local de perfis com nome, função, provedor/modelo, instruções e revisão. Nomes normalizados são únicos; atualização/exclusão compara revisão e mantém edições concorrentes. Perfis não armazenam permissões nem alteram sessões já copiadas.
- Validação de campos, enums, tamanhos e preservação literal das instruções. Limite da chefia compartilha a composição do contrato usada no serviço; instruções adicionais não ultrapassam os 8.000 caracteres do pedido completo.
- Validação: build sem avisos/erros, 141 testes xUnit (62 Core + 79 Infrastructure). Quinze casos novos cobrem limites, nomes Unicode/case, persistência, revisão obsoleta, exclusão, cópia de conversa e migração do schema 3 com fila/proposta/run preservados. Sem chamadas de modelos.
- Próximo incremento: editor da biblioteca e aplicação explícita a novas conversas na central. Persistência ainda não representa fluxo visual pronto.

## 08/10/2026 — Biblioteca visual de perfis e fechamento do M2

- Perfis de função permite criar, editar, reverter e excluir configurações reutilizáveis entre projetos. Nomes/funções personalizados, provedor, modelo opcional e instruções ficam no SQLite. Rascunhos protegidos contra troca/descarte e exclusão confirmada; revisões concorrentes recusadas sem perder a edição local.
- Nova com perfil prepara outra conversa em leitura e preserva a mensagem digitada, sem enviar pedidos ou modificar/interromper sessões existentes. Edição/exclusão mantém os parâmetros de conversas/tarefas copiadas. Funções personalizadas conservam o nome na retomada/reabertura; a chefia mantém contrato e acesso de leitura.
- Validação: build sem avisos/erros, 141 testes xUnit (62 Core + 79 Infrastructure), fluxos WPF de central, planos, fila e perfis com zero erros de binding. Biblioteca testada em dois projetos, com retomada, histórico, exclusão, revisão concorrente, edição/aplicação durante execução ativa e reabertura. A verificação de retomada aguardou o fim do carregamento do projeto antes de enviar. Provedores de teste; nenhuma chamada real de modelo neste incremento. Captura mínima revisada e identificada no README.
- M2 concluído. Release local atualizado em artifacts/app e abertura/encerramento conferidos. Limitações de provedores e de acompanhamento automático permanecem documentadas.
- Próximo incremento M3: diagnóstico Git por projeto em leitura, mostrando repositório, branch e alterações locais antes de preparar worktrees. Projetos sem Git permanecem utilizáveis; escrita continua serial até integração verificada.

## 08/10/2026 — Núcleo de diagnóstico Git em leitura

- Contrato de diagnóstico Git com estados explícitos para pasta comum, Git ausente, bare e erro. Consulta raiz, branch, commit, acompanhamento local, alterações preparadas/não preparadas, nomes anteriores e conflitos. Parser porcelain v2/NUL preserva espaços, acentos e caracteres literais; saída truncada/inválida não vira diagnóstico completo.
- Reutiliza processo assíncrono com streams limitados, prazo total de 20 segundos e cancelamento da árvore. Consulta sem atualização opcional do índice, fsmonitor ou rede. Variáveis Git herdadas não redirecionam o projeto nem habilitam traces; configurações de confiança continuam aplicadas. Nenhum comando de alteração de repositório no serviço.
- Validação: build sem avisos/erros e 157 testes xUnit (62 Core + 95 Infrastructure). Dezesseis novos casos cobrem parser, Git real em pastas temporárias, raiz de subpasta, renomeação, exclusão, arquivos novos, conflito, ausência de commits, HEAD destacado, worktree existente, bare, índice/lock preservados, ambiente, timeout, cancelamento e erro. Limpeza dos repositórios de teste trata objetos somente leitura do Git no Windows dentro da pasta temporária verificada. Nenhum modelo chamado.
- Próximo incremento: painel visual de diagnóstico por projeto na central. Worktrees por tarefa e integração paralela ainda pendentes; escrita permanece serial.

## 08/10/2026 — Diagnóstico Git na central

- Diagnóstico Git conectado ao projeto selecionado: consulta ao abrir, raiz/branch/commit/acompanhamento local, alterações preparadas/na pasta/novas, nomes anteriores, submódulos e conflitos. A janela conserva o projeto de origem ao navegar na central e mostra horário da consulta. Estados sem Git/repositório, bare, sem commits, HEAD destacado e falha são explícitos.
- Atualizar retira o resultado anterior até obter resposta completa. Cancelar preserva a reserva da consulta até parar e descarta resposta tardia. Fechar o painel interrompe a consulta; fechar a central cancela e aguarda suas consultas Git junto às execuções dos provedores. Projeto sem Git continua com chat/fila.
- Validação: build sem avisos/erros; suíte xUnit do núcleo com 157 testes aprovados (62 Core + 95 Infrastructure). Fluxos WPF de central, planos, fila, perfis e Git passaram com zero erros de binding. Git real em pasta de teste para raiz de subpasta, mudanças/renomeação e índice preservado; falha/conflito visual e chat sem Git usam componentes de teste. Resposta após cancelamento e fechamento do painel/central com consulta pendente verificados. Capturas normal/mínima revisadas; tabela e ações acessíveis em 900×760. Nenhum modelo chamado.
- Release local atualizado em artifacts/app e abertura/encerramento conferidos. README e documentação identificam o diagnóstico como consulta em leitura, com criação de worktrees/diffs/integração ainda pendentes.
- Próximo incremento M3: preparação explícita de worktree por tarefa, registro do diretório/revisão base e tratamento de subpastas/ausência de commits. Escrita continua serial até a integração ser verificada.

## 08/10/2026 — Núcleo de preparação persistente de worktrees

- Schema 5 registra a intenção antes da criação Git, com checkout/branch por tarefa, commit confirmado, Git comum, subpasta e estado. Preparação explícita antes da primeira tentativa; interrupção conserva efeitos e exige retomada. Reserva impede criação simultânea no mesmo repositório e despacho com vínculo obsoleto.
- Git nativo cria fora do original sem sobrescrever destinos/branches, copiar alterações locais ou executar hooks/filtros de checkout. Subpastas, ausência de commits e recursos incompatíveis têm diagnóstico. Validação física precede cada tentativa; sessão e diretório são estáveis, escrita segue serial, dependentes de entrega em worktree aguardam integração.
- Validação: build sem avisos/erros, 169 testes xUnit (62 Core + 107 Infrastructure). Doze casos novos com Git real em pastas temporárias e provedores simulados, incluindo preservação do índice/original, base fixa, colisões, cancelamento parcial, recuperação, migração, alteração de branch/pasta, dependências e concorrência. Nenhum modelo chamado.
- Próximo incremento: prévia/confirmação, preparação/cancelamento e detalhes da worktree na fila WPF. Diffs e integração verificada permanecem pendentes.

## 08/10/2026 — Worktrees na fila WPF

- Pasta de trabalho mostra estado, branch, commit, diretório efetivo, checkout e origem. Preparar worktree consulta a prévia e confirma antes da criação, sem chamar modelos; recusa não grava intenção. Tentativas/ajustes conservam pasta e sessão. Aprovação avisa que dependentes aguardam integração Git.
- Cancelar aguarda preparação e registro do estado; fechar a central cancela e aguarda operações Git e painéis da fila. Projeto continua fixo ao navegar. Preview consulta raiz/base sem status do original, evitando executar filtros clean; a conferência de retomada desabilita filtros somente no próprio comando. Configuração persistente dos provedores/Git preservada.
- Validação: build sem avisos/erros, 170 testes xUnit (62 Core + 108 Infrastructure), regressões WPF da central/planos/fila/perfis/Git e novo fluxo de worktrees com zero erros de binding. Git real e provedor simulado cobrem confirmação/recusa, subpasta, original preservado, autorização no checkout, ajuste na mesma sessão, dependentes bloqueados, reabertura, cancelamento parcial e fechamento da central. Caso adicional comprova que preview/conferência não executam filtros clean. Capturas normal/mínima revisadas; nenhum modelo chamado.
- Release local atualizado em artifacts/app e abertura/encerramento conferidos. Documentação e roadmap atualizados; criação pronta não representa integração concluída.
- Próximo incremento M3: diffs por tarefa com base/pasta registradas e saídas limitadas; depois integração serial verificada e liberação das dependências.

## 08/10/2026 — Núcleo de revisão de diffs por tarefa

- Consulta a worktree/base persistidas da tarefa parada. Lista diferenças desde a base e status local; compara cada arquivo desde a base, índice ou pasta. Commits posteriores, renomeações sucessivas, exclusão preparada com arquivo novo no mesmo caminho e conflitos conservam seus estados.
- Git com caminhos literais, sem diff externo/textconv/filtros/fsmonitor ou alteração de índice. Leitura direta de arquivos novos limitada a UTF-8/64 KiB, recusando links/junções. Listas parciais/excessivas recusadas, grandes/binários explícitos. Validação de registro/branch/status e tarefa antes/depois da consulta; nenhum modelo chamado.
- Validação: build sem avisos/erros, 189 testes xUnit (62 Core + 127 Infrastructure). Dezenove casos novos com Git real em pastas temporárias cobrem as comparações, preservação de índice/lock/original, binários/grandes, origem de renomeação por comparação, conflitos, comandos externos desabilitados, caminhos literais, tarefa em execução/alterada, cancelamento e timeout. A consulta não substitui a futura revisão imutável da integração.
- Próximo incremento: painel WPF de diffs ligado à fila, com atualização/cancelamento e seleção de arquivo/comparação.

## 08/10/2026 — Revisão visual de diffs na fila

- Revisar diffs abre por Pasta de trabalho para uma tarefa parada com worktree pronta. Lista automática, seleção de arquivo/comparação base/índice/pasta, caminhos anteriores, arquivos novos e conflitos. Projeto/tarefa ficam fixos ao navegar na fila/central; consulta inclui todo o checkout. Binários/grandes/ausência de diferença são explícitos.
- Operação pendente retira a prévia e bloqueia seleção. Atualizar/erro/cancelamento retira a lista; estado alterado exige nova consulta. Resposta tardia descartada; painel/central cancelam e aguardam leituras ao fechar. Não aprova nem integra a entrega, nem fixa conteúdo revisado para integração.
- Validação: build sem avisos/erros, 189 testes xUnit neste incremento (62 Core + 127 Infrastructure, executados no núcleo). Novo teste WPF --diffs e regressões de central/fila/Git/worktrees com zero erros de binding. Git real em pasta de teste cobre comparações, commits posteriores, renomeação/exclusão, UTF-8/binários/limite, conflito e índice/original preservados. Navegação, invalidação após mudança, cancelamento/resposta tardia e fechamento verificados; temporização simulada, nenhum modelo chamado. Capturas normal/mínima revisadas; screenshot identificado no README.
- Release local atualizado em artifacts/app, abertura/encerramento conferidos. Documentação e roadmap atualizados; M3 continua parcial.
- Próximo incremento: conjunto imutável da entrega e reserva de integração serial, com verificação de origem/destino. Integração testada e liberação de dependentes vêm depois; worktrees continuam preservadas.
