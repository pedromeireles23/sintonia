# Roadmap do Sintonia

Aplicativo Windows em C#/.NET 10 e WPF que serve como canal de comunicação e coordenação entre agentes do Codex e do Claude, com várias sessões nos projetos que o usuário precisar. Os agentes produzem as entregas; o Sintonia encaminha contexto/resultados e acompanha o trabalho conjunto. Os marcos são incrementos verificáveis; não representam datas prometidas.

Em 09/10/2026, o usuário retirou o M5 de artes e biblioteca de assets: a produção e organização desses arquivos podem ser solicitadas nas tarefas dos agentes, com suas ferramentas disponíveis. A criação de um módulo especializado duplicaria responsabilidades e ampliaria o produto além do canal de comunicação solicitado. Imagens seguem o fluxo geral de entregas. Os identificadores M6/M7 foram preservados para manter as referências históricas. Justificativa na decisão 011 de [docs/DECISIONS.md](docs/DECISIONS.md).

## M0 — Base .NET e fluxo demonstrativo

Estado: concluído e validado no Windows; execução demonstrativa inteiramente simulada.

- [x] Registrar propósito, arquitetura, contexto e política de commits.
- [x] Criar solução e projetos Core, Infrastructure, Desktop e testes.
- [x] Criar janela WPF com funções, tarefas e estados.
- [x] Implementar regras de dependências e limites de concorrência.
- [x] Demonstrar sessões dos dois provedores com simulação claramente identificada.
- [x] Validar build e testes; registrar limitações e publicar o incremento.

Aceite: a janela abre no Windows; as funções podem ser atribuídas aos dois provedores; dependentes aguardam entrega aprovada. Nenhuma simulação é apresentada como chamada real.

## M1 — Prova das duas integrações

Estado: integração real de leitura/retomada/interrupção comprovada nos dois provedores; host Claude com recusa/autorização real de escrita comprovadas. Comparação completa de extensões e negativas reais de outras ferramentas ainda pendentes. Evidências em [docs/PROVIDER_PROBES.md](docs/PROVIDER_PROBES.md).

- [x] Detectar executáveis e versões sem acessar credenciais.
- [x] Validar handshake, eventos e interrupção do Codex App Server por stdio.
- [x] Validar Claude CLI: saída estruturada, nova sessão e retomada explícita.
- [ ] Comparar skills, plugins e MCPs esperados com capacidades disponíveis.
- [ ] Validar o fluxo de permissões e negativas de cada provedor (Codex: negativa real de sandbox; Claude: recusa/autorização real de Write; demais ferramentas ainda parciais).
- [x] Testar caminhos com espaços, acentos, prompts extensos e encerramento de processos filhos.
- [x] Registrar versões e comportamentos efetivamente testados.

Aceite: Codex e Claude realizam uma tarefa pequena cada numa pasta de teste, sem bypass global de permissões. Falhas e recusas não são registradas como sucesso.

## M2 — Primeiro produto utilizável

Estado: concluído. Central real utilizável, com autorizações, chefia editável, fila persistente com início explícito/revisão/ajustes e biblioteca de perfis reutilizáveis. Validação: 141 testes xUnit e fluxos WPF de central, planos, fila e perfis; integração nativa tem as provas finitas documentadas. Escrita direta exclusiva no mesmo projeto; limites de M1/M3/M4 permanecem explícitos.

- [x] Cadastrar várias pastas de projeto, alternar entre elas e preservar seus contextos separados.
- [x] Criar chat central por projeto com seleção de provedor/modelo conforme as capacidades verificadas.
- [x] Atribuir funções com provedor/modelo padrão e instruções adicionais.
- [x] Enviar tarefas para sessões novas ou retomadas.
- [x] Listar sessões por projeto/estado e abrir suas conversas dentro do Sintonia.
- [x] Mostrar mensagens, ferramentas utilizadas e resultados em tempo real.
- [x] Salvar tarefas, sessões, execuções, propostas e eventos em SQLite.
- [x] Oferecer revisão, ajuste, cancelamento e diagnóstico de falhas na fila real.
- [x] Reabrir o aplicativo com histórico preservado e estado reconciliado.

Aceite: o usuário cadastra e alterna projetos, conversa com um provedor/modelo disponível, acompanha uma tarefa real do Claude e outra do Codex e abre/retoma suas sessões no Sintonia, com resultados revisáveis e histórico local. Abertura nos aplicativos originais depende de prova separada, conforme [o escopo do produto](docs/PRODUCT_SCOPE.md).

## M3 — Trabalho paralelo com Git

Estado: fluxo implementado com diagnóstico Git, worktrees, diffs, commit revisado, combinação separada, critérios/execução/histórico WPF, publicação serial e arquivamento assistido de entregas publicadas. Schema 13 registra intenção/resultado do arquivo usando a reserva compartilhada; Published libera dependentes e a pasta efetiva precisa conter o commit integrado. Reserva/trava e recuperação preservam executores vivos/efeitos sem repetir operações. Limite 3–7 por projeto, teto global 7, provedores misturados e despacho de uma rodada aprovados. Projetos sem Git continuam usando chat/fila; escrita direta permanece exclusiva. Arquivamento conserva todos os arquivos, branch e histórico, retirando somente o registro do checkout ausente. Espaço em disco, combinações e arquivos interrompidos são conservados; restauração/descarte automático permanece fora deste incremento. [Contrato](docs/TASK_WORKTREE_CLEANUP.md).

Validação atual: build sem avisos/erros, 69 testes Core, sete novos de publicação e 26 regressões de validação/fila/concorrência aprovados. WPF --publication/--validation/--sessions aprovados, zero erros de binding, capturas revisadas. Nenhum modelo chamado neste incremento.

Fechamento M3/M4: 270 testes verificados, incluindo reexecução aprovada do único teste com expectativa antiga de schema futuro. WPF --chief-plan/--queue/--proposals também aprovados. Release atualizado e abertura/encerramento normal conferidos; [registro completo](docs/DEVELOPMENT_LOG.md).

Arquivamento: build Debug/Release sem avisos/erros, 69 Core, nove testes novos e 36 casos selecionados em Release aprovados (um novo reexecutado e 35 regressões). WPF --cleanup aprovado, zero erros de binding; capturas normal/mínima revisadas e Release local atualizado. Git/processos reais, runs simulados e nenhum modelo chamado. A suíte contém 279 casos; testes não afetados não foram repetidos integralmente neste incremento.

- [x] Diagnosticar Git por projeto sem alterar arquivos, índice ou regras de confiança.
- [x] Criar worktrees por tarefa de escrita e retomar checkouts registrados pela mesma tarefa.
- [x] Entregar especificações, contratos e resumos entre sessões, incluindo resultado/revisão e commit/árvore publicados.
- [x] Vincular tarefas à revisão correta do código de suas dependências, com conferência de ancestralidade antes da inferência.
- [x] Mostrar diffs e conflitos antes de integrar.
- [x] Registrar commit/árvore revisados e preparar reserva serial por repositório.
- [x] Combinar entrega/destino numa pasta separada, preservando conflitos e resultados persistidos.
- [x] Integrar alterações de forma serial após validar a combinação, com confirmação e publicação registrada.
- [x] Oferecer arquivamento assistido da worktree de entregas publicadas, preservando arquivos locais, branch e histórico; mudanças versionadas posteriores recusam a operação. Combinações permanecem e o arquivo continua ocupando espaço.

Aceite: duas tarefas modificam cópias diferentes; conflitos são visíveis; nenhuma alteração é perdida; a versão combinada é verificada. Projeto sem Git não recebe escrita paralela por padrão.

## M4 — Distribuição assistida

Estado: fluxo assistido conectado. A chefia recebe estado/resultados pelo chat e sua proposta revisada pode atualizar tarefas ainda não iniciadas, com confirmação e revisão transacional. Tentativas (1–3), prazo (1–300s) e concorrência (3–7) configuráveis por projeto. Painel WPF de limites Codex conectado, com bloqueio antes do turno somente mediante indisponibilidade explícita do backend; quota Claude aparece indisponível por ausência de consulta verificada. Tokens por execução no chat/tentativas, com escopo do agente principal e medição parcial/ausente explícita. Limites agregados permanecem pendentes. Não há rodadas ou reenvios automáticos. [Fluxo e limites](docs/PLAN_UPDATES.md), [quota](docs/PROVIDER_USAGE.md) e [contagem](docs/RUN_TOKEN_USAGE.md).

Uso dos provedores: build Debug/Release sem avisos/erros; 69 Core e 93 casos selecionados de Infrastructure em Release aprovados, incluindo 18 novos de quota, 39 regressões de protocolo e 36 de workspace/fila/concorrência/revisão de planos. WPF --usage/--workspace/--chief-plan aprovados, zero erros de binding e capturas normal/mínima revisadas. Uma consulta real de metadados Codex confirmou o contrato sem iniciar modelos. A suíte contém 297 casos; testes não afetados não foram repetidos integralmente.

Tokens por execução: build Debug/Release sem avisos/erros; 69 Core em Debug e 112 casos selecionados de Infrastructure em Release aprovados. WPF --tokens/--queue/--usage/--chief-plan passaram, zero erros de binding; Release local atualizado e abertura/encerramento conferidos. Dados e processos de teste, sem chamar modelos. A suíte agora contém 316 casos, sem repetição integral dos testes não afetados; emissão de tokens em turno real ainda não exercitada.

- [x] Escolher uma função de chefe do projeto com provedor/modelo configurável.
- [x] Transformar um objetivo em proposta estruturada de tarefas, validada e salva para revisão.
- [x] Acompanhar resultados das sessões de trabalho e atualizar o plano pelo chat central, mediante envio, revisão e confirmação explícitos.
- [x] Permitir edição de função, provedor/modelo, escopo, dependências e critérios da proposta pelo usuário.
- [x] Implementar limites configuráveis de tentativas, tempo e concorrência, com reserva compartilhada e configuração revisionada.
- [x] Exibir quota Codex verificada e respeitar indisponibilidade explícita antes do envio; documentar consulta Claude indisponível, sem estimar saldo de assinatura.
- [x] Registrar/exibir tokens informados por execução, com contratos próprios dos provedores, escopo/lacunas explícitos e histórico preservado.
- [ ] Implementar limite agregado sobre tokens informados, sem prometer medição integral de subagentes/intervalos ausentes ou estimar quota da assinatura.
- [x] Distinguir término de execução, aprovação da entrega e integração Git publicada.

Aceite: um objetivo no chat pode ser planejado pelo chefe e dividido entre sessões das duas ferramentas, com plano revisável, dependências e limites controlados pelo aplicativo. Chefia não substitui permissões ou aprovação das entregas.

## M6 — Fluxos para projetos gerais

- [x] Permitir configurar comandos de validação e critérios adequados à stack ou ao tipo de projeto, com editor e execução/histórico WPF.
- [ ] Integrar entregas de código, documentação, análise e arquivos conforme o projeto.
- [ ] Preparar exemplos de projetos distintos que combinem entregas das duas ferramentas.

Aceite: projetos de tipos diferentes combinam entregas do Codex e do Claude e passam pelos critérios definidos pelo usuário. Arquivos visuais produzidos pelos agentes seguem esse mesmo fluxo geral.

## M7 — Distribuição e portfólio

- [ ] Criar instalador ou pacote Windows reproduzível.
- [ ] Testar em outra máquina e explicar dependências ausentes.
- [ ] Definir licença antes da publicação de release.
- [ ] Documentar arquitetura, decisões, instalação e demonstração real.
- [ ] Automatizar build e testes no GitHub; incluir screenshots e vídeo quando disponíveis.
- [ ] Validar atualização e preservação de dados.

Aceite: outra pessoa consegue instalar, entender e experimentar o aplicativo; o portfólio distingue funcionalidades prontas de itens planejados.

## Regras para todos os marcos

Skills e plugins compatíveis devem ser preservados. Permissões são verificadas nos provedores. Credenciais não entram no repositório. Cada incremento concluído tem validação adequada, contexto atualizado e commit descritivo. O MVP funcional é o M2; colaboração com integração robusta depende também do M3.
