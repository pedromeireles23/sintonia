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

## 08/10/2026 — Registro de commits revisados e reserva serial

- Schema 6 guarda commit/árvore da entrega, tentativa aprovada e worktree. Registro exige a mesma consulta de diffs, tarefa aprovada e status limpo, com mudanças já commitadas. Persistência idempotente, comparação transacional de aprovação/run/vínculo e entrega imutável; não faz commits, altera refs ou integra arquivos.
- Prévia confere origem registrada e destino original com branch/commit/base/estado compatíveis. Reserva por Git comum coordena stores do mesmo banco, recusa execuções/preparações relacionadas e bloqueia novos runs/preparações, incluindo projetos com pastas sobrepostas e worktrees registradas. ID de reserva impede liberação obsoleta. Recuperação marca atenção sem repetir operações; nenhum estado declara integração concluída ou libera dependentes.
- Validação: build sem avisos/erros; 202 testes xUnit (62 Core + 140 Infrastructure), com 13 casos novos de Git real/SQLite e runs simulados. Commit/árvore, índice/lock/original preservados, mudanças sem commit, revisão obsoleta, origem/destino alterados, concorrência, idempotência, bloqueios, interrupção e migração cobertos. A limpeza da fixture de migração desabilita pooling; comparação do registro verifica valores serializados, pois listas internas dos records não têm igualdade por conteúdo. Nenhum modelo chamado.
- Limites: registro requer commits existentes; pastas e objetos Git continuam sujeitos a mudanças externas. Reserva prepara a coordenação, sem executar merge ou testes da combinação. Não há reserva pela UI enquanto o executor estiver pendente.
- Próximo incremento: registrar o commit revisado pelo painel WPF de diffs e mostrar o registro persistido. Depois, integração serial verificada e liberação de dependentes.

## 08/10/2026 — Identidade dos objetos Git da entrega

- Comandos de worktrees/diffs/entregas ignoram refs de substituição somente no processo, usando --no-replace-objects. O commit/árvore registrados correspondem aos objetos originais; refs e configuração persistente são preservadas.
- Validação: build sem avisos/erros; regressão com Git real cria uma substituição que aponta para outra árvore, confere registro da árvore original e verifica que a ref permaneceu intacta. Suíte com 203 testes xUnit (62 Core + 141 Infrastructure). Nenhum modelo chamado.
- Próximo incremento permanece o registro visual do commit revisado; merge e validação da combinação continuam pendentes.

## 08/10/2026 — Registro visual do commit revisado

- Registrar commit revisado conectado ao painel de diffs. Exige aprovação, comparação desde a base e ausência de mudanças locais. Confirmação mostra commit/tarefa e abrangência; recusa não chama serviço. Cabeçalho mostra commit registrado, horário e integração pendente. Atualizar a fila mostra o registro em Pasta de trabalho; navegar/reabrir conserva o projeto/tarefa/registro.
- Falha ou mudança após a consulta retira resultados e orienta atualização. Cancelamento/fechamento aguarda o registro; captura tardia cancelada antes da persistência não salva. Transação concluída permanece registrada. Nenhum merge ou liberação de dependentes pela UI.
- Validação: build sem avisos/erros, 203 testes xUnit (62 Core + 141 Infrastructure); novo WPF --deliveries e regressões de central/fila/worktrees/diffs com zero erros de binding. Git real em pasta de teste comprova registro, índice/original preservados e reabertura; runs/temporização simulados verificam aprovação, recusa, revisão obsoleta, cancelamento/captura tardia e fechamento da central. Nenhum modelo chamado. Layout normal/mínimo revisado; captura identificada no README. Release atualizado e abertura/encerramento conferidos.
- Próximo incremento: executor de integração serial e validação da combinação em pasta separada, preservando origem/destino e arquivos locais/ignorados. Recuperação deve distinguir operações vivas de interrupções; só o resultado integrado validado poderá liberar dependentes. M3 continua parcial.

## 08/10/2026 — Recuperação de reservas com executor vivo

- Trava de arquivo exclusiva por Git comum, independente da thread e liberada pelo sistema ao encerrar o processo. Recuperação conserva reservas cujo executor ainda mantém a trava e retém as travas adquiridas até concluir a transação; não repete efeitos Git. Arquivos marcadores não são apagados para evitar corridas.
- Validação: 15 testes de entregas aprovados, incluindo processo separado que mantém a trava, recuperação concorrente, exclusividade e encerramento do proprietário. Build sem avisos/erros. Nenhum modelo chamado, sem mudança de schema ou fluxo visual.
- Próximo incremento: combinar commits registrados numa worktree separada, usando essa trava durante toda a operação e persistindo intenção/resultados antes de liberar a reserva. Testes da combinação, publicação e dependentes permanecem pendentes.

## 08/10/2026 — Executor da combinação em pasta separada

- Schema 7 persiste intenção antes de criar checkout e registra árvore combinada ou conflitos, com término/liberação atômicos. Executor usa nova worktree bloqueada/destacada, merge ort sem commit/fast-forward e IDs de origem/destino conferidos. Índices, branches, arquivos locais e ignorados originais preservados. Preparação não valida/publica/libera dependentes.
- Trava mantida da reserva ao resultado. Recuperação conserva operação viva, marca intenções abandonadas em atenção e não repete/limpa efeitos. Cancelamento descarta resposta tardia e conserva arquivos parciais; novas tentativas usam diretórios distintos. Banco recusa liberação separada durante combinação e runs em suas pastas registradas.
- Validação: 216 testes xUnit verificados (62 Core + 154 Infrastructure), incluindo 12 casos novos de Git real/SQLite e runs simulados. Cobertura de combinação/conflito, identidade/índices/ignorados, filtros/submódulos, hooks/driver padrão, prévia obsoleta, diretório existente, cancelamento/tardio, concorrência/recuperação e migração. Ajustes de fixture respeitam CRLF do checkout Windows, inicializam o gitlink de teste e consultam assinatura no escopo local para evitar o override da própria fixture. Nenhum modelo chamado.
- Limites: recusa links, filtros e drivers personalizados; configuração/objetos continuam compartilhados. Resultado salvo não comprova que arquivos externos continuam iguais nem substitui testes do conjunto. Publicação e disponibilidade da revisão para dependentes permanecem pendentes.
- Próximo incremento: conectar preparação/resultado ao painel WPF e validar confirmação, recusa, cancelamento, reabertura e fechamento.

## 08/10/2026 — Preparação visual da combinação

- Preparar combinação conectado ao painel de diffs, após registro do commit aprovado. Confere origem/destino e confirma commits/branch/pasta de referência; recusa não reserva/cria checkout e prévia obsoleta exige atualização. Resumo copiável com rolagem mostra estado salvo, pasta, árvore ou conflitos; a lista abaixo continua sendo a da tarefa.
- Navegação/reabertura preservam projeto/tarefa e consultam o resultado. Cancelamento/fechamento aguarda o executor e atenção persistida, descarta resposta tardia e conserva arquivos parciais. Combinação não valida/publica/libera dependentes.
- Validação: build sem avisos/erros e 216 testes xUnit verificados (62 Core + 154 Infrastructure). WPF --combinations com Git real/runs simulados cobre registro exigido, recusa, prévia obsoleta, combinação/conflito, original/índice/ignorado preservados, navegação/reabertura, cancelamento/tardio e fechamento da central. Regressões --deliveries/--diffs também passaram; zero erros de binding. Capturas normal/mínima revisadas; resumo limitado com rolagem conserva espaço de revisão no mínimo. Nenhum modelo chamado. Release atualizado, abertura/encerramento conferidos.
- Próximo incremento: comandos/critérios de validação por projeto, execução limitada/cancelável na pasta combinada e resultado vinculado à árvore conferida. Depois, publicação confirmada e disponibilidade da revisão para dependentes; worktrees continuam preservadas. M3 parcial.

## 09/10/2026 — Configuração e processos de validação geral

- Schema 8 guarda até dez comandos por projeto, com revisão contra edição concorrente e cópia de argumentos. Nome do critério, executável .exe absoluto, argumentos separados, pasta relativa e prazo de 1–600 segundos; nenhuma stack obrigatória ou conversão de respostas dos modelos em comandos.
- Runner usa processo direto e streams assíncronos limitados. Falha, timeout e cancelamento distintos, com saída conservada e encerramento de filhos. Diagnósticos antigos continuam limitados a 60 segundos. Não instala dependências nem copia arquivos ignorados.
- Validação: build completo sem avisos/erros; 18 testes selecionados aprovados, incluindo 13 novos de configuração/runner e cinco regressões de processos. Isolamento por projeto, revisão/reabertura/migração, argumentos literais, pasta, streams/falha/limites, timeout/cancelamento com filho e entradas ausentes. Sem modelos.
- Próximo incremento: operação serial na combinação com reserva, intenção/resultados persistidos e árvore conferida antes/depois. Editor e execução WPF, publicação e dependentes ainda pendentes.

## 09/10/2026 — Conferência e histórico de validação da combinação

- Schema 9 persiste intenção/resultados vinculados à preparação, árvore, comandos/revisão e reserva. Execução serial com trava por Git comum, falha/timeout interrompendo sequência, saídas/códigos/horários preservados, cancelamento tardio sem sucesso e recuperação viva/abandonada sem repetir comandos. Término/liberação atômicos; critérios obsoletos recusados no início e na transação de sucesso.
- Conferência compara índice e hashes de todos os arquivos versionados antes/depois de cada comando, ignorando cache/assume-unchanged sem alterar o índice. Arquivos novos ou conteúdo alterado impedem Passed; ignorados podem permanecer. Listas NUL nativas preservam acentos/espaços, corrigindo também a verificação de filtros/drivers no manager de worktrees. Comandos mantêm configuração/rede normais das ferramentas, com redirecionamento Git herdado retirado somente do processo.
- Validação automatizada cobre estados, conjunto completo de critérios, logs/reabertura, migrações, preservação de índices/original, arquivos alterados, revisão obsoleta/durante execução, concorrência, recuperação e cancelamento. Runs/processos de teste e Git real; nenhum modelo chamado. Fluxo WPF --combinations aprovado com zero erros de binding.
- Job Object no Windows controla descendentes associados e os encerra antes de retornar, inclusive após a saída do processo principal. Falha de associação/encerramento recusa sucesso; não mantém serviços em background nem constitui sandbox ou captura trabalho iniciado por mecanismos externos.
- Verificados 247 testes xUnit (65 Core + 182 Infrastructure), build sem avisos/erros. Execução completa encontrou uma variável Git herdada removida de forma incompleta; corrigida removendo as variáveis pelas chaves herdadas, com 20 testes selecionados aprovados. Dois casos adicionais conferem interrupção interna em atenção e cancelamento pedido pelo usuário. Contagem agregada; não houve nova execução completa após essas correções. Release atualizado e abertura/encerramento conferidos.
- Limites: Passed registra a árvore versionada conferida; ambiente, ferramentas e entradas ignoradas não são imutáveis. Logs ainda não têm checkpoint contínuo durante execução; interrupção pode perder saída não salva. Não publica, resolve conflitos ou libera dependentes.
- Próximo incremento: editor por projeto e execução/histórico WPF, mostrando comandos/pastas/árvore/revisão antes de iniciar e aguardando cancelamento/fechamento. Depois publicação confirmada e disponibilidade de revisão integrada para dependentes.

## 09/10/2026 — Núcleo de 3–7 sessões por projeto

- Pedido do usuário atualizado para mínimo 3 e máximo 7, com chefe e trabalhadores Codex/Claude no mesmo projeto. Configuração por projeto com revisão, padrão 3, teto global 7 e várias sessões do mesmo provedor.
- Schema 10 guarda limite e escopo efetivo por run. Admissão no serviço e reserva transacional no SQLite impedem excesso entre stores, duplicação e conflito de pastas. Redução de limite não cancela sessões; cancelamento segura a vaga até encerramento/persistência.
- Escrita em worktrees distintas, prontas e validadas agora admite concorrência. Original continua exclusivo para escrita; publicação e liberação de dependentes continuam pendentes.
- Validação: build sem avisos/erros, 69 testes Core e 31 selecionados de Infrastructure aprovados. Limites 3/5/7, mistura/repetição de provedor, cancelamento tardio, concorrência entre stores, escopo congelado, sobreposição de projetos e migração cobertos. Git real verifica dois escritores em checkouts separados, sem modificar original. Nenhum modelo chamado.
- Próximo incremento: seletor WPF 3–7 e início em grupo das tarefas disponíveis do plano aprovado, com cancelamento/fechamento aguardados.

## 09/10/2026 — Limite de sessões e distribuição em grupo no WPF

- Central oferece 3–7 sessões em Função, permissões e sessões, com aplicação explícita, rascunho separado do limite salvo e contagem por projeto/central. Revisão obsoleta é recusada, atualizando a configuração em vigor sem perder a escolha do usuário.
- Iniciar tarefas disponíveis relê o lote aprovado/limite, escolhe uma rodada nas vagas livres e encaminha cada tarefa à própria sessão Codex/Claude. Dependências, três tentativas, estado de revisão e exclusividade de pastas continuam aplicados. Sem rodadas/repetições automáticas.
- Tokens próprios do grupo conservam uma tarefa iniciada separadamente. Fechar fila/central cancela e aguarda; recarga/histórico fazem parte da operação antes de liberar controles. Contador/projeto da fila permanecem fixos ao navegar. Painel com rolagem conserva controles no tamanho mínimo.
- Validação: build completo sem avisos/erros e suíte completa de 257 testes xUnit aprovada (69 Core + 188 Infrastructure). WPF --sessions percorreu chefia/plano/fila, 3/7 vagas, sete sessões misturadas, três escritores em worktrees Git reais (duas Codex/uma Claude), edição concorrente, redução sem interromper sessões, cancelamento que preserva outra tarefa, reabertura e fechamento aguardado. Zero erros de binding, capturas normal/mínima revisadas.
- Regressões de central, propostas, perfis, fila e worktrees aprovadas. Correção mantém Busy até finalizar o histórico; teste de worktrees reabre painel novo após fechamento aguardado. Provedores/processos simulados e Git real; nenhum modelo chamado. Release atualizado e abertura/encerramento conferidos.
- Limites: sete modelos reais simultâneos não foram exercitados. Chefia propõe o plano e o aplicativo distribui mediante início; acompanhamento/atualização automática do plano ainda pendentes. Publicação e dependentes de worktrees continuam pendentes.
- Próximo incremento M3: editor e execução/histórico WPF dos comandos de validação já implementados no núcleo; depois publicação confirmada e disponibilidade da revisão integrada para dependentes.

## 09/10/2026 — Editor e histórico visual de validação

- Critérios de validação por projeto na central, com até dez comandos ordenáveis, argumentos literais, pasta/prazo, rascunho/revisão e descarte confirmado. Edição concorrente recusa gravação preservando o rascunho.
- Validar combinação nos diffs abre histórico fixado à preparação/projeto. Prévia integral com rolagem mostra comandos/pastas/árvore/revisão antes do início explícito; recusa não inicia processos. Resultados exibem estados, códigos, stdout/stderr e truncamento. Cancelamento/fechamento aguardam execução e recarga; central aguarda os painéis.
- Build sem avisos/erros. WPF --validation aprovado com Git/processos reais e temporização simulada: recusa/prévia obsoleta, configuração/revisão, argumentos vazios/aspas/quebras de linha, sucesso/falha e sequência interrompida, logs/histórico, cancelamento/tardio, navegação/reabertura e fechamento. Zero erros de binding; capturas normal/mínima revisadas. Nenhum modelo chamado.
- Limites: histórico salvo ao encerrar, sem checkpoint contínuo de logs. Passed ainda não publica nem libera dependentes. Próximo incremento: publicação Git confirmada, com origem/destino/arquivos/critérios reconferidos e revisão integrada vinculada às dependências.

## 09/10/2026 — Retirada do M5 de artes e reafirmação do canal entre agentes

- Neste chat, o usuário questionou por que implementar geração de imagens e biblioteca no Sintonia se os agentes já podem realizar esse trabalho com suas ferramentas. Autorizou retirar o M5 e reafirmou o propósito: comunicação entre agentes do Codex e do Claude, trabalhando em conjunto com várias sessões nos projetos necessários.
- Marco e previsões de adaptador/biblioteca de imagens removidos. Produção e organização de arquivos ficam nas tarefas dos agentes; o Sintonia encaminha contexto/resultados e acompanha o fluxo geral de entregas. M6/M7 mantêm seus identificadores históricos. Justificativa registrada na decisão 011 e alinhada em README, contexto, escopo, arquitetura, roadmap e instruções.
- Validação: revisão documental, referências locais, diagrama e consistência de escopo/estado; diff sem erros de whitespace. Nenhum build, teste .NET ou modelo chamado neste incremento de documentação. Alterações de código já presentes na pasta pertencem a outro incremento e não entram neste commit.
- Limites: geração de imagens pela integração e paridade com os aplicativos originais continuam sem prova. Quantidade de projetos e sessões abertas é distinta de execuções ativas; limites atuais de 3–7 por projeto e sete globalmente preservados.
- Próximo incremento de código permanece M3: concluir e validar o editor/execução/histórico WPF dos comandos de validação em andamento; depois publicação confirmada e disponibilidade da revisão integrada para dependentes.

## 09/10/2026 — Publicação serial e revisão integrada das dependências

- Schema 11 registra intenção/candidato/resultado. Publicação exige Passed vigente e conteúdo/origem/destino reconferidos; salva commit com árvore validada e pais destino/entrega antes de aplicar fast-forward local. Sem force/reset/autostash/push, com ignorados e worktrees preservados. Reserva/trava duram até persistir; recuperação não repete operações.
- Publicar no projeto conectado ao painel WPF com prévia/recusa, histórico e fechamento aguardado. Cancelamento antes da aplicação evita checkout; após iniciá-la aguarda e registra sucesso ou atenção real. Falha após checkout preserva candidato/efeitos e bloqueia dependentes. Published compatível libera sucessores; prompt inclui resultado/revisão/commit/árvore e a pasta precisa conter a revisão integrada.
- Build sem avisos/erros, 69 testes Core, sete testes novos de publicação e 26 regressões de validação/fila/concorrência aprovados, incluindo colisão com arquivo ignorado. WPF --publication aprovado com Git/processos reais e runs simulados, zero erros de binding, capturas normal/mínima revisadas. Nenhum modelo chamado.
- Correções verificadas: normalização de caminhos Git/Windows na conferência de conteúdo; fixture compara CRLF de checkout sem confundir normalização interna do Git com falha de publicação. Limites: processos externos e Git/SQLite não têm atomicidade conjunta; falhas preservam atenção. Limpeza automática de worktrees continua ausente. Próximo incremento: acompanhamento/atualização assistida do plano no M4.

## 09/10/2026 — Acompanhamento da chefia, revisão da fila e limites adicionais

- Chefia e plano prepara na central o estado/resultados/revisões/commits, preservando rascunho e sessão nativa de chefia em leitura. Preparar não envia; resposta gera outra proposta revisável. Confirmação aplica somente tarefas ainda não iniciadas/sem worktree; histórico iniciado, sessões e publicações ficam preservados. Sem nova execução ou aprovação automática.
- Schema 12 guarda definições anteriores e propostas consumidas. Aplicação transacional compara revisões e estados; propostas aplicadas ficam sem edição/segundo encaminhamento. Contrato da tarefa e limites são reconferidos na reserva; contexto obsoleto não consome tentativa. Remoção pendente conserva conversa.
- Central configura 1–3 tentativas por tarefa e 1–300s por execução, com a mesma revisão do limite 3–7. Rascunho inválido/obsoleto preservado. Timeout cancela e aguarda executor, grava falha/parcial e recusa sucesso tardio; cancelamento explícito continua separado. Configuração nova vale para próximos inícios.
- Build sem avisos/erros, 69 Core e 20 testes selecionados de Infrastructure aprovados, incluindo seis novos de revisão/concorrência/migração/timeout. WPF --chief-plan, --sessions, --queue e --proposals aprovados, zero erros de binding. Capturas normal/mínima com rolagem revisadas. Provedores simulados, Git real no teste de sessões e nenhum modelo chamado.
- Release local atualizado em `artifacts/app`; abertura/encerramento do executável normal conferidos. Teste de schema futuro agora consulta a versão migrada e usa a seguinte, evitando tratar um schema suportado como futuro; verificação corrigida aprovada em Release. Referências locais dos documentos conferidas, captura da chefia identificada como teste no README.
- Verificação geral de 270 testes: 69 Core e 200 Infrastructure aprovados na rodada completa; apenas a expectativa antiga de schema futuro falhou no build iniciado antes da correção. Depois do rebuild final sem avisos/erros, esse caso passou em Debug, além de Release. Todos os 270 casos foram verificados. Incrementos commitados e enviados normalmente para `pedromeireles23/sintonia`.
- Limites: os CLIs atuais não fornecem quota restante uniforme/verificada; sem limite agregado de tokens/custo ou saldo estimado. Acompanhamento e rodadas seguem explícitos; não há loop de modelos. Próximo incremento: limpeza assistida conservadora de worktrees encerradas no M3.

## 09/10/2026 — Arquivamento assistido das worktrees de entregas publicadas

- Fila permite Arquivar worktree integrada após aprovação/publicação, com prévia de origem/destino/commits/branch e recusa sem efeitos. Move a pasta inteira intacta para arquivo gerenciado, incluindo ignorados, não rastreados e diretórios vazios; retira somente o registro Git da origem ausente. Branch, commits, sessões, tentativas, revisão, publicação e dependentes permanecem. O painel prioriza estado/caminho arquivado e retira ações que precisam do checkout.
- Schema 13 registra intenção e reserva compartilhada atomicamente, comparando a publicação e operação anterior. Trava por Git comum e índice protege a conferência/movimentação; commit posterior, alteração oculta/preparada, colisão, índice bloqueado ou caminho externo impede movimentação. Falhas conservam ambos os caminhos. Cancelamento anterior não move; tardio/fechamento aguarda o resultado nativo. Recuperação preserva executor vivo, marca abandonados e não repete efeitos; resultados antigos e arquivo anterior existente impedem repetição indevida.
- Build Debug/Release sem avisos/erros, 69 Core, nove novos testes de arquivamento e 36 casos selecionados em Release aprovados. Esses 36 incluem reexecução da preservação integral/dependência com bloqueio de nova integração e 35 regressões de publicação, alterações rastreadas/não rastreadas na validação, diffs, revisão do plano, concorrência e migração/schema futuro. WPF --cleanup passou em Release com Git/processos reais, recusa/mudança posterior, ignorados/rascunhos, histórico/reabertura/dependentes disponíveis e zero erros de binding. Capturas normal/mínima revisadas; 104 referências locais conferidas. Nenhum modelo chamado.
- Release local atualizado em `artifacts/app`; abertura/encerramento normal conferidos. Contrato em TASK_WORKTREE_CLEANUP.md e captura identificada como teste no README. Arquivo continua ocupando espaço e não é um checkout ativo; não há restauração, descarte, remoção de branch ou arquivamento das combinações. Próximo incremento M4: verificar contratos/eventos locais de consumo/quota sem chamadas de modelos nem saldo estimado; demais limites de provedores continuam documentados.

## 09/10/2026 — Consulta verificada de limites Codex

- Núcleo e adaptador consultam `account/rateLimits/read` com login ChatGPT, sem criar thread/turno. Snapshot tipado com janelas/percentuais/renovação, disponibilidade explícita e hora, compartilhado pela conta; mapa tem prioridade sobre legado e ausência não vira zero. Identidade, créditos e erros brutos descartados. Claude retorna quota indisponível sem lançar processo, com orientação para consulta nativa.
- Antes de cada envio Codex, recusa explícita `ordinaryUsageAllowed=false` interrompe antes da thread, independentemente do parsing das janelas. Nulos/falhas/percentuais não inventam permissão nem recuperação; diagnóstico indica que o provedor aplica seus limites. Consulta limitada a vinte segundos e cancelável, sem repetição ou cobrança alternativa.
- Schema local e documentação oficial conferidos; uma consulta real finita de metadados confirmou o contrato na versão instalada. Nenhum modelo chamado, nenhuma credencial acessada. Build dos adaptadores/diagnóstico e 57 testes de quota/provedores aprovados, incluindo 18 novos de parsing, falha, cancelamento e admissão. Contrato em PROVIDER_USAGE.md. Próximo incremento: painel WPF; quota Claude e consumo agregado ainda pendentes.

## 09/10/2026 — Painel visual de uso dos provedores

- Central abre Uso dos provedores com consulta inicial, atualização/cancelamento manuais e escolha Codex/Claude. Janelas/percentuais/renovação/hora local e decisão de uso incluído são explícitos. Dados compartilhados pela conta, sem saldo monetário ou atribuição ao projeto. Claude aponta consulta indisponível e o caminho nativo. Troca/atualização limpa dados antigos; falha/cancelamento não conserva snapshot e resposta tardia é descartada. Painel conserva pasta/provedor durante a leitura e fechar painel/central cancela e aguarda.
- Build Debug/Release sem avisos/erros; 69 Core e 93 casos selecionados de Infrastructure em Release aprovados (18 novos de quota, 39 regressões de protocolo e 36 de workspace/fila/concorrência/revisão do plano). WPF --usage percorreu consulta, nulos/recusa/falha, troca de provedor, navegação, concorrência, cancelamento/tardio, reabertura e fechamento aguardado do painel/central. --workspace e --chief-plan passaram em Release. Zero erros de binding; capturas normal/mínima revisadas e legenda de dados de teste no README. Nenhum modelo chamado.
- Release local atualizado em `artifacts/app`; abertura/encerramento normal conferidos. Suíte contém 297 casos; casos não afetados não foram repetidos integralmente. Consulta Codex é verificada, quota Claude permanece indisponível e consumo agregado segue pendente. Próximo incremento M4: consumo por execução a partir de eventos/contadores verificados e limites agregados compatíveis, distinguindo medição de tokens de quota da assinatura.

## 09/10/2026 — Registro de tokens por execução

- Schema 14 guarda snapshot tipado por run em checkpoint/término, preservando histórico anterior sem medição. Recuperação/cancelamento/falha conserva o que foi recebido, sem reenvio. Não soma snapshots, recusa redução/provedor diferente e não deixa nova tentativa herdar consumo. Cache/raciocínio são componentes já incluídos, não acréscimos ao total.
- Codex usa diferenças de contadores cumulativos, correlacionando thread/turno e notificações que chegam antes da resposta. Retomada sem base omite o primeiro intervalo, repetição não soma e reset conserva contagem anterior; todo snapshot é parcial. Claude usa apenas usage terminal do turno principal em streaming, excluindo subagentes e totais restaurados/modelUsage/USD. Falhas são parciais, ausência/telemetria inválida não vira zero ou falha da resposta. Contratos em RUN_TOKEN_USAGE.md.
- Build do núcleo/adaptadores/persistência e 69 Core + 92 casos selecionados de Infrastructure aprovados, incluindo 19 novos de contagem, retomada, repetição, reset, falha/cancelamento, migração/recuperação/consistência. Processos simulados, schema/documentação oficial conferidos e nenhuma chamada de modelos. Tokens de um turno real ainda não exercitados neste incremento. Próximo passo: exibição WPF; orçamento agregado permanece pendente por escopo/lacunas de medição.

## 09/10/2026 — Tokens no chat e no histórico das tentativas

- Respostas do chat/fila mostram consumo informado/parcial/indisponível por execução; campos de cache/raciocínio já incluídos. Tentativas acompanha a seleção antiga/atual sem somar runs ou herdar dados. Navegação/reabertura preserva a medição; evento visual tardio não troca o snapshot do resultado.
- Build Debug/Release sem avisos/erros; 69 Core e 112 casos selecionados de Infrastructure Release aprovados, incluindo 19 novos de tokens e 93 regressões de quota/protocolos/workspace/fila/concorrência/revisão de planos. WPF --tokens e --queue verificaram medição, escopos/parcialidade/ausência, cancelamento, duas pastas, tentativas distintas, dependências/revisão e reabertura. --usage e --chief-plan passaram. Zero erros de binding; capturas normal/mínima revisadas, dados de teste identificados no README.
- Release local atualizado em `artifacts/app`; abertura/encerramento normal conferidos. Nenhum modelo chamado e evento real de tokens continua sem prova neste incremento. Suíte contém 316 casos; testes não afetados não foram repetidos integralmente. Próximo passo M4: limite configurável sobre tokens informados, com soma por run/reserva e lacunas explícitas; quota Claude e contabilização integral de subagentes continuam indisponíveis.

## 09/10/2026 — Admissão por tokens informados e reservas compartilhadas

- Schema 15 acrescenta limite opcional e reserva por início, na mesma revisão dos limites existentes. Soma o snapshot mais recente de cada run do projeto; execuções ativas contam somente a parcela reservada ainda não observada. Admissão transacional entre stores impede gastar a mesma margem duas vezes, antes de tentativa/inferência. Excesso recebido bloqueia novos inícios, preservando execuções ativas.
- Encerramento/recuperação liberam somente reserva pendente; medições de falha/cancelamento e lacunas históricas permanecem. Configuração alterada não muda reservas anteriores; overflow satura e bloqueia com limite ativo. Desativado por padrão, sem estimativa de quota/custo ou garantia de contagem integral. Contrato em TOKEN_BUDGET.md.
- Build Debug sem avisos/erros; 69 Core e 46 casos selecionados de Infrastructure aprovados, incluindo 15 novos de orçamento e 31 regressões de tokens/concorrência/revisão. Sem modelos. Próximo incremento: configuração/snapshot WPF e prova real finita de colaboração com Git.

## 09/10/2026 — Configuração e snapshot WPF do orçamento

- Central configura limite opcional/reserva por início junto aos limites existentes, mostra medição/reservas/lacunas por projeto e oferece atualização sem inferência. Rascunhos inválidos/obsoletos preservados; snapshot não altera rascunho. Chat/fila/rodada respeitam a margem visual, com autoridade final da transação no banco. Consultas serializadas e aguardadas ao fechar.
- Build Debug/Release sem avisos/erros; WPF --budget passou em ambos. --tokens/--queue/--chief-plan/--sessions aprovados; zero erros de binding e capturas normal/mínima revisadas. Regressão de cancelamento anterior à reserva corrigida: UI mostra Cancelada, sem inventar execução persistida. Nenhum modelo chamado. Próximo incremento: prova real finita de colaboração com Git/contadores.

## 09/10/2026 — Colaboração real com escrita, dependência publicada e tokens

- Diagnóstico opt-in collaboration forneceu plano de duas tarefas, com um turno Codex gpt-6.1-sol para JSON e um Claude claude-opus-5 para conferência Markdown. Worktrees, diffs/escopo, revisão, commit registrado, combinação, validação em processo real e publicação serial passaram; Claude recebeu contexto e a revisão integrada. Uma autorização Write conferiu caminho/conteúdo; assinatura, sem bypass/configuração global. Sem inferência de chefia, retries ou aprovação automática de projetos do usuário.
- Primeira preparação recusou divergência entre pasta lógica LocalAppData e caminho físico redirecionado pelo ambiente Codex desktop. Registro/pasta em atenção preservados; raiz explícita do diagnóstico corrigiu sua configuração. collaboration-resume reutilizou o run Codex concluído e fez apenas o turno Claude ainda pendente. Retomada após completar reutilizou ambos, sem modelos/publicações adicionais. Limite de seis minutos por comando e 90s por execução; comportamento do diretório padrão nesse ambiente continua pendente.
- Contadores reais preservados após reabertura: 41.715 Codex parciais e 97.617 Claude do turno principal, soma 139.332. Sem reservas ativas e plano fornecido sem medição explícita. Limite 100.000/reserva 20.000 admite o segundo início e bloqueia novos envios após o excesso real, sem cancelar execução ativa. Detalhes/arquivos locais em REAL_COLLABORATION.md.
- Build do diagnóstico Release sem avisos/erros; prova real e retomada sem modelos aprovadas. Release Desktop atualizado em artifacts/app e abertura/encerramento normal conferidos. Próximo incremento: compatibilidade do caminho padrão redirecionado e provas restantes de extensões/permissões.
- Fechamento: 331 casos xUnit aprovados (69 Core e todos os 262 Infrastructure em Debug, estes em 34m48s). WPF --budget Debug/Release e regressões --tokens/--queue/--chief-plan/--sessions aprovadas; zero erros de binding. Builds Debug/Release sem avisos/erros; 163 referências locais válidas e diff sem erros de whitespace. Testes automatizados sem modelos; prova finita continua com exatamente os dois turnos já registrados. Commits sincronizados normalmente com origin.
