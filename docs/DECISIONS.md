# Decisões técnicas

## 001 — Aplicativo Windows em .NET

Data: 08/10/2026. Estado: adotada.

Escolha: C#/.NET 10 com WPF e MVVM. O usuário escolheu uma interface visual para Windows e quer considerar o projeto para portfólio .NET. A demonstração Electron serviu à pesquisa e não define a stack do produto.

Consequências: reimplementar interface e regras em C#; usar componentes WPF; manter o núcleo independente da interface. Avalonia e WinUI permanecem alternativas para uma mudança futura explicitamente escolhida.

## 002 — Funções sobre sessões existentes

Estado: adotada.

Uma função é uma configuração de trabalho atribuída ao Codex ou ao Claude. As ferramentas mantêm seus próprios mecanismos de execução e extensões compatíveis. O Sintonia organiza tarefas e sessões; ambos os provedores precisam executar trabalho real.

## 003 — Execução local por adaptadores

Estado: adotada; transportes específicos a validar no M1.

Começar com instalações locais dos provedores, preservar seus perfis e não extrair credenciais. Investigar Codex App Server por stdio e Claude CLI estruturado. A arquitetura não exige chamadas diretas às APIs de modelos.

## 004 — Permissões e revisão explícitas

Estado: adotada.

Não exigir bypass global para o produto funcionar. Distinguir término de execução, aceite da entrega e integração. Worktrees não são isolamento de segurança. Se uma capacidade de aprovação não estiver disponível, mostrar a limitação e restringir a execução.

## 005 — Persistência e histórico de desenvolvimento

Estado: adotada.

SQLite será o armazenamento local do produto quando entrar o histórico real. Git será o histórico do desenvolvimento: commits pequenos ao concluir incrementos validados, contexto atualizado e sincronização normal com `origin`.

## 006 — Implementação independente

Estado: adotada.

O Maestro é uma referência arquitetural, não um fork. Seu código declara AGPL 3.0. Nenhum código foi incorporado; uma proposta futura de reutilização deve tratar licença e origem explicitamente.

## 007 — Central de projetos, chat e chefia configurável

Data: 08/10/2026. Estado: direção de produto adotada; implementação pendente.

O usuário quer cadastrar projetos variados, conversar num chat central com provedor/modelo escolhido, atribuir uma função de chefe e abrir as sessões de trabalho. O jogo é um exemplo opcional, não um cenário fixo do produto. O usuário reafirmou em 08/10/2026 que o foco são projetos gerais; o núcleo e o roadmap não exigem engine nem especialização em jogos.

Projetos e sessões devem manter contexto separado. O chat organiza vínculos com as conversas nativas; Codex e Claude não compartilham memória automaticamente. O chefe propõe tarefas, mas o aplicativo aplica dependências/limites e o usuário conserva permissões e aprovações. Abertura no Sintonia é requisito; abertura nos aplicativos originais exige validação própria. Escopo em [PRODUCT_SCOPE.md](PRODUCT_SCOPE.md).

## 008 — Assinaturas como padrão de acesso

Data: 08/10/2026. Estado: adotada; execução real a validar no M1.

Usar os CLIs locais com seus logins por assinatura. Não configurar API keys nem fazer fallback automático para cobrança de API. O adaptador deve conferir o modo declarado antes de uma tarefa real sem extrair credenciais, mantendo autenticação nos mecanismos dos provedores. Login declarado não comprova quota ou acesso a um modelo.

## 009 — Host Claude pelo protocolo de controle stdio

Data: 08/10/2026. Estado: implementada, com Write validada na instalação local.

Usar entrada e saída stream-json e o canal stdio do CLI, sem adicionar runtime Node/Python ou servidor MCP de autorização. Inicializar o canal antes do prompt, responder a can_use_tool por ID e devolver os parâmetros exibidos. O Sintonia não aplica sugestões de regras permanentes. As decisões esperam o usuário fora da leitura de stdout; cancelamento também retira a prévia da central.

Preservar configuração/extensões/hooks, plan para leitura e manual para escrita. O host decide somente pedidos que chegam a ele, pois regras/hooks podem resolver chamadas antes. Recusas locais bloqueiam a execução mesmo se omitidas no resultado. Sem suporte específico, AskUserQuestion/ExitPlanMode são recusadas. O escopo cobre um turno limitado, sem acompanhamento de trabalho autônomo após o resultado terminal.

Contrato conferido contra a [referência do CLI](https://code.claude.com/docs/en/cli-reference) e o [protocolo do SDK oficial](https://github.com/anthropics/claude-agent-sdk-python/blob/main/src/claude_agent_sdk/_internal/query.py). Implementação C# própria; comportamento real provado no Claude Code `2.1.277`, sem assumir compatibilidade de qualquer versão futura.

## 010 — Três a sete sessões no mesmo projeto

Data: 09/10/2026. Estado: implementada no núcleo e no WPF.

O usuário definiu mínimo de 3 e máximo de 7 vagas por projeto, com sessões Codex/Claude misturadas e tarefas atribuídas pelo chefe. O padrão é 3; contam somente execuções ativas, inclusive o chefe. Chat e fila compartilham teto global de 7 no banco, sem exclusividade por provedor.

O chefe propõe um plano revisável e o aplicativo distribui uma rodada das tarefas disponíveis quando o usuário inicia o grupo. Escrita direta no original é exclusiva; tarefas independentes em worktrees prontas e validadas podem escrever juntas. Cancelamento segura as vagas até encerramento e persistência. Permissões, revisão e publicação mantêm seus controles; acompanhamento automático do chefe e publicação Git concluída ainda estão pendentes. [Contrato e evidências](SESSION_CONCURRENCY.md).

## 011 — Canal de comunicação entre agentes e retirada do M5 de artes

Data: 09/10/2026. Estado: adotada por autorização explícita do usuário neste chat.

Contexto: o usuário questionou a necessidade do M5 de artes e biblioteca de assets, primeiro lembrando os recursos do Codex e do Claude e depois perguntando por que implementar no Sintonia um trabalho que os próprios agentes podem executar. Autorizou a retirada e reafirmou que o Sintonia é um canal de comunicação entre agentes das duas ferramentas, fazendo-os trabalhar em conjunto com várias sessões abertas nos projetos que forem necessários.

Decisão: retirar o marco de artes, a previsão de um adaptador de imagens próprio e os objetivos de biblioteca, galeria e exportação especializadas. Produzir imagens, criar variações, organizar arquivos e registrar informações pode ser solicitado nas tarefas dos agentes, conforme as ferramentas compatíveis de suas instalações. O Sintonia encaminha instruções, contexto e resultados, mantém projetos e sessões e acompanha o fluxo geral de entregas.

Justificativa: esses recursos especializados duplicariam responsabilidades dos agentes e ampliariam o produto além do propósito solicitado. Uma imagem, um documento e código são entregas a coordenar; o canal deve tratar o trabalho conjunto sem implementar um sistema de produção para cada tipo de arquivo. Uma interface especializada exige uma necessidade concreta e nova decisão de escopo.

Consequências: imagens continuam possíveis como entregas das tarefas, sem módulo próprio. A retirada não comprova geração de imagens pela integração nem paridade com os aplicativos originais; capacidades e extensões continuam exigindo validação. O escopo permite cadastrar projetos conforme a necessidade, mantendo contexto separado. Quantidade de projetos e sessões abertas é distinta de execuções ativas; os limites atuais de 3–7 por projeto e sete globalmente permanecem. M6/M7 conservam seus identificadores para preservar referências históricas. Documentos de escopo, contexto, arquitetura, roadmap e instruções foram alinhados; o próximo incremento de código continua sendo o M3 em andamento.
