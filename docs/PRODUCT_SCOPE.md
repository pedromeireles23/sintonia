# Sintonia como canal de comunicação entre agentes

Direção solicitada pelo usuário em 08/10/2026. A central real já oferece projetos, chat, sessões persistentes e propostas de chefia para revisão. Este documento também descreve a distribuição e a integração ainda planejadas; a demonstração M0 permanece separada e simulada.

Reafirmação em 09/10/2026 neste chat: o Sintonia é um canal de comunicação entre agentes de IA do Codex e do Claude para trabalharem em conjunto com várias sessões abertas nos projetos que o usuário precisar. O usuário autorizou retirar o M5 de artes e biblioteca, pois a produção e organização dos arquivos já podem ser tarefas dos agentes com suas ferramentas. Decisão e justificativa em [DECISIONS.md](DECISIONS.md), item 011.

## Experiência desejada

O Sintonia conecta sessões de Codex e Claude Code, encaminha instruções, contexto e resultados e acompanha o trabalho conjunto. Desenvolvimento, documentação, pesquisa, análise e produção de arquivos são tarefas executadas pelos agentes com as ferramentas disponíveis. O usuário cadastra projetos conforme sua necessidade, mantendo as conversas e o trabalho de cada projeto separados.

| Área | Comportamento planejado |
| --- | --- |
| Projetos | Adicionar pastas locais, alternar projetos e preservar instruções, conversas, funções e tarefas de cada um. |
| Chat | Conversar numa interface central sobre o projeto selecionado, acompanhar respostas e ferramentas e enviar novas mensagens à sessão correspondente. |
| Provedor e modelo | Escolher Codex ou Claude e um modelo disponível para a instalação/conta. Identificar o provedor/modelo usados em cada sessão. |
| Funções | Configurar instruções, escopo e provedor/modelo padrão por função; atribuir coordenação, implementação ou revisão conforme a necessidade do projeto. |
| Coordenação | Escolher uma sessão como chefe do projeto para propor tarefas, responsáveis, dependências e critérios de entrega, e acompanhar os resultados. |
| Sessões | Listar sessões do Sintonia por projeto, estado, provedor, modelo e função; clicar para abrir a conversa, inspecionar eventos e continuar quando houver suporte. |

As sessões dos dois provedores pertencem ao mesmo projeto lógico. Quando escreverem em paralelo, o fluxo de Git deve separar as alterações e integrar de forma serial. Adicionar outra pasta não deve misturar instruções ou histórico entre projetos.

Pedido atualizado em 09/10/2026: limite configurável de **3 a 7 sessões simultâneas por projeto**, padrão 3, incluindo o chefe enquanto executa. Provedores podem repetir e misturar livremente. Chat e fila compartilham até sete execuções globais. Seletor WPF e início em grupo do plano aprovado implementados; escrita paralela requer worktrees distintas e validadas. [Fluxo e limites](SESSION_CONCURRENCY.md).

A quantidade de projetos cadastrados é distinta desses limites de execução: o escopo não define uma quantidade fixa de projetos. Ter várias sessões abertas não significa manter todas executando ao mesmo tempo. O teto atual de sete execuções globais permanece uma limitação da implementação.

## Responsabilidades do canal e dos agentes

| Responsável | Escopo |
| --- | --- |
| Codex e Claude | Executar as tarefas e produzir ou organizar código, documentos, imagens e outros arquivos com suas ferramentas compatíveis. |
| Sintonia | Abrir/retomar sessões, encaminhar contexto e resultados, acompanhar tarefas e entregas, preservar histórico e aplicar dependências, concorrência e decisões do usuário. |

O M5 de artes e biblioteca de assets foi retirado do roadmap. Geração de imagens, seleção de um provedor de imagens próprio, galeria e exportação especializadas duplicariam trabalho que pode ser solicitado aos agentes e acrescentariam um módulo fora do propósito confirmado. Arquivos visuais seguem o fluxo geral de entregas; uma interface especializada só poderá entrar por necessidade concreta e nova decisão de escopo.

Preservar as extensões dos agentes não garante que toda ferramenta disponível em seus aplicativos originais funcione na integração. Geração de imagens dentro do Sintonia ainda não foi validada; a retirada do marco não declara essa capacidade concluída.

## Chat central e conversas nativas

O chat é uma interface do Sintonia sobre as sessões nativas dos provedores. Cada sessão mantém seu identificador, histórico, modelo, função e diretório compatível. O Sintonia conserva os vínculos e as mensagens necessárias ao acompanhamento, sem assumir memória compartilhada entre Codex e Claude.

Trocar de provedor deve criar ou selecionar uma sessão compatível e permitir levar um resumo explícito do contexto. Não apresentar a troca como se os dois provedores compartilhassem integralmente a mesma conversa. Trocas de modelo dentro do mesmo provedor dependem das capacidades verificadas do adaptador.

O Codex documenta catálogo de modelos, criação, leitura, listagem e retomada de threads. Esses métodos permitem construir o seletor e a navegação, mas precisam de validação na versão instalada; um modelo listado não é prova de acesso bem-sucedido. Fonte: [Codex App Server](https://learn.chatgpt.com/docs/app-server).

O Claude Code documenta escolha de modelo, sessão com ID e retomada explícita. O catálogo mostrado pelo Sintonia precisa respeitar as opções verificadas da instalação e da conta. Fonte: [referência do Claude CLI](https://code.claude.com/docs/en/cli-reference).

Abrir uma sessão **dentro do Sintonia** é parte do produto. Retomá-la em um terminal pode ser oferecido quando validado, com transferência de controle para impedir dois executores alterando a mesma conversa ao mesmo tempo. Abrir a sessão nos aplicativos gráficos originais exige uma interface suportada e uma prova específica. Não prometer importação ou controle de todas as conversas já abertas fora do Sintonia.

## Chefe do projeto

A chefia é uma função configurável numa sessão normal de Codex ou Claude, com modelo escolhido pelo usuário. Não exige uma identidade especial nem um novo runtime de agentes.

Fluxo planejado: objetivo no chat → proposta estruturada do chefe → revisão/edição do plano → distribuição para sessões de trabalho → resultados e revisão → acompanhamento no chat central.

Proposta, revisão e despacho explícito estão implementados: chefia executa em leitura e planos são validados e vinculados à resposta de origem no SQLite. Confirmar não inicia sessões; encaminhar preserva a revisão e cria tarefas. A fila permite iniciar tentativas, revisar resultados, solicitar ajustes e aprovar entregas para liberar dependências. O acompanhamento automático pela chefia e a integração Git permanecem pendentes. Formato em [PLAN_PROPOSALS.md](PLAN_PROPOSALS.md) e fluxo em [TASK_QUEUE.md](TASK_QUEUE.md).

A proposta deve definir objetivo, escopo, responsável, dependências e critérios de entrega. O Sintonia valida identificadores, dependências, diretórios e limites; não executa texto livre do chefe como comandos de controle. A coordenação pode recomendar aprovação, mas não substitui decisões de permissão, aprovação das entregas ou integração reservadas ao usuário.

## Assinaturas

O padrão solicitado é usar os logins das assinaturas nos CLIs locais. Não configurar cobrança por chave de API nem migrar para esse modo automaticamente. Antes de uma execução real, o adaptador deve conferir o modo de autenticação sem expor segredos e recusar um modo incompatível com essa escolha.

Nas consultas locais, Codex informou login ChatGPT; Claude informou login `claude.ai` e assinatura Pro. Turnos reais limitados comprovaram leitura/retomada nos dois, autorização de Write e geração de proposta no Claude. Isso comprova acesso naquele momento, não quota futura ou acesso a todo modelo anunciado. Evidências em [PROVIDER_PROBES.md](PROVIDER_PROBES.md).

## Ordem de implementação

1. Concluir M1 com provas reais limitadas dos dois provedores, eventos, permissões e retomada.
2. M2: cadastro de projetos, chat com provedor/modelo, sessões navegáveis e persistência local.
3. M3: separar e integrar escrita paralela com Git.
4. M4: função de chefe e distribuição assistida com plano revisável, limites e acompanhamento.

A base existente de funções, dependências, tentativas e concorrência será evoluída. Não ampliar a demonstração para apresentar essas integrações como prontas.
