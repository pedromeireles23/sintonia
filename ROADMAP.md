# Roadmap do Sintonia

Aplicativo Windows em C#/.NET 10 e WPF para coordenar sessões de Codex e Claude. Os marcos são incrementos verificáveis; não representam datas prometidas.

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

Estado: preparação parcial. Detecção/ajuda verificadas, sem inferência. Evidências e próximo passo em [docs/PROVIDER_PROBES.md](docs/PROVIDER_PROBES.md).

- [x] Detectar executáveis e versões sem acessar credenciais.
- [ ] Validar handshake, eventos e interrupção do Codex App Server por stdio.
- [ ] Validar Claude CLI: saída estruturada, nova sessão e retomada explícita.
- [ ] Comparar skills, plugins e MCPs esperados com capacidades disponíveis.
- [ ] Validar o fluxo de permissões e negativas de cada provedor.
- [ ] Testar caminhos com espaços, acentos, prompts extensos e encerramento de processos filhos.
- [ ] Registrar versões e comportamentos efetivamente testados.

Aceite: Codex e Claude realizam uma tarefa pequena cada numa pasta de teste, sem bypass global de permissões. Falhas e recusas não são registradas como sucesso.

## M2 — Primeiro produto utilizável

- [ ] Abrir um projeto local e atribuir funções.
- [ ] Enviar tarefas para sessões novas ou retomadas.
- [ ] Mostrar mensagens, ferramentas utilizadas e resultados em tempo real.
- [ ] Salvar tarefas, sessões, execuções e eventos em SQLite.
- [ ] Oferecer revisão, ajuste, cancelamento e diagnóstico de falhas.
- [ ] Reabrir o aplicativo com histórico preservado e estado reconciliado.

Aceite: o usuário acompanha uma tarefa real do Claude e outra do Codex no mesmo painel, com resultados revisáveis e histórico local.

## M3 — Trabalho paralelo com Git

- [ ] Criar ou reutilizar worktrees adequadas por tarefa de escrita.
- [ ] Entregar especificações, contratos e resumos entre sessões.
- [ ] Vincular tarefas à revisão correta do código de suas dependências.
- [ ] Mostrar diffs e conflitos antes de integrar.
- [ ] Integrar alterações de forma serial e testar o conjunto.
- [ ] Preservar alterações locais e arquivos necessários ao limpar worktrees.

Aceite: duas tarefas modificam cópias diferentes; conflitos são visíveis; nenhuma alteração é perdida; a versão combinada é verificada. Projeto sem Git não recebe escrita paralela por padrão.

## M4 — Distribuição assistida

- [ ] Transformar um objetivo em proposta estruturada de tarefas.
- [ ] Permitir edição de função, provedor, dependências e entregas pelo usuário.
- [ ] Implementar limites de tentativas, tempo, concorrência e consumo disponível.
- [ ] Distinguir término de execução, aprovação da entrega e integração.

Aceite: um pedido pode ser dividido entre as duas ferramentas, respeitando dependências e limites controlados pelo aplicativo.

## M5 — Artes e biblioteca de assets

- [ ] Escolher e conectar um provedor de imagens.
- [ ] Criar briefing visual, referências, versões e prévia.
- [ ] Armazenar parâmetros, origem, custos disponíveis e arquivos gerados.
- [ ] Aprovar e exportar assets antes da importação no projeto.

Aceite: uma imagem é gerada e entregue como arquivo revisável. Evolução: imagens estáticas e texturas → sprites isolados → variações consistentes → animações validadas.

## M6 — Fluxo de desenvolvimento de jogo

- [ ] Definir modelo para a engine escolhida pelo usuário.
- [ ] Integrar código, especificações de assets, build e testes adequados.
- [ ] Preparar um pequeno recurso de jogo que combine entregas das duas ferramentas.

Aceite: uma entrega do jogo reúne código do Codex, código do Claude e, se configurado, um asset aprovado.

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
