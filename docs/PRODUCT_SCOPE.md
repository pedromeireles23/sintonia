# Sintonia como central de projetos

Direção solicitada pelo usuário em 08/10/2026. Este documento descreve funcionalidades planejadas; a implementação atual continua sendo o M0 simulado.

## Experiência desejada

O Sintonia será uma central geral de trabalho com Codex e Claude Code. O jogo é o primeiro caso de uso, não um projeto fixo nem uma limitação do aplicativo.

| Área | Comportamento planejado |
| --- | --- |
| Projetos | Adicionar pastas locais, alternar projetos e preservar instruções, conversas, funções e tarefas de cada um. |
| Chat | Conversar numa interface central sobre o projeto selecionado, acompanhar respostas e ferramentas e enviar novas mensagens à sessão correspondente. |
| Provedor e modelo | Escolher Codex ou Claude e um modelo disponível para a instalação/conta. Identificar o provedor/modelo usados em cada sessão. |
| Funções | Configurar instruções, escopo e provedor/modelo padrão por função; atribuir coordenação, implementação ou revisão conforme a necessidade do projeto. |
| Coordenação | Escolher uma sessão como chefe do projeto para propor tarefas, responsáveis, dependências e critérios de entrega, e acompanhar os resultados. |
| Sessões | Listar sessões do Sintonia por projeto, estado, provedor, modelo e função; clicar para abrir a conversa, inspecionar eventos e continuar quando houver suporte. |

As sessões dos dois provedores pertencem ao mesmo projeto lógico. Quando escreverem em paralelo, o fluxo de Git deve separar as alterações e integrar de forma serial. Adicionar outra pasta não deve misturar instruções ou histórico entre projetos.

## Chat central e conversas nativas

O chat é uma interface do Sintonia sobre as sessões nativas dos provedores. Cada sessão mantém seu identificador, histórico, modelo, função e diretório compatível. O Sintonia conserva os vínculos e as mensagens necessárias ao acompanhamento, sem assumir memória compartilhada entre Codex e Claude.

Trocar de provedor deve criar ou selecionar uma sessão compatível e permitir levar um resumo explícito do contexto. Não apresentar a troca como se os dois provedores compartilhassem integralmente a mesma conversa. Trocas de modelo dentro do mesmo provedor dependem das capacidades verificadas do adaptador.

O Codex documenta catálogo de modelos, criação, leitura, listagem e retomada de threads. Esses métodos permitem construir o seletor e a navegação, mas precisam de validação na versão instalada; um modelo listado não é prova de acesso bem-sucedido. Fonte: [Codex App Server](https://learn.chatgpt.com/docs/app-server).

O Claude Code documenta escolha de modelo, sessão com ID e retomada explícita. O catálogo mostrado pelo Sintonia precisa respeitar as opções verificadas da instalação e da conta. Fonte: [referência do Claude CLI](https://code.claude.com/docs/en/cli-reference).

Abrir uma sessão **dentro do Sintonia** é parte do produto. Retomá-la em um terminal pode ser oferecido quando validado, com transferência de controle para impedir dois executores alterando a mesma conversa ao mesmo tempo. Abrir a sessão nos aplicativos gráficos originais exige uma interface suportada e uma prova específica. Não prometer importação ou controle de todas as conversas já abertas fora do Sintonia.

## Chefe do projeto

A chefia é uma função configurável numa sessão normal de Codex ou Claude, com modelo escolhido pelo usuário. Não exige uma identidade especial nem um novo runtime de agentes.

Fluxo planejado: objetivo no chat → proposta estruturada do chefe → revisão/edição do plano → distribuição para sessões de trabalho → resultados e revisão → acompanhamento no chat central.

A proposta deve definir objetivo, escopo, responsável, dependências e critérios de entrega. O Sintonia valida identificadores, dependências, diretórios e limites; não executa texto livre do chefe como comandos de controle. A coordenação pode recomendar aprovação, mas não substitui decisões de permissão, aprovação das entregas ou integração reservadas ao usuário.

## Assinaturas

O padrão solicitado é usar os logins das assinaturas nos CLIs locais. Não configurar cobrança por chave de API nem migrar para esse modo automaticamente. Antes de uma execução real, o adaptador deve conferir o modo de autenticação sem expor segredos e recusar um modo incompatível com essa escolha.

Na consulta local realizada nesta conversa, Codex informou login ChatGPT; Claude informou login `claude.ai` e assinatura Pro. Isso comprova o estado declarado pelos CLIs naquele momento, não validade de uma inferência futura, quota disponível ou acesso a todo modelo anunciado. Nenhuma tarefa real do Sintonia foi enviada.

## Ordem de implementação

1. Concluir M1 com provas reais limitadas dos dois provedores, eventos, permissões e retomada.
2. M2: cadastro de projetos, chat com provedor/modelo, sessões navegáveis e persistência local.
3. M3: separar e integrar escrita paralela com Git.
4. M4: função de chefe e distribuição assistida com plano revisável, limites e acompanhamento.

A base existente de funções, dependências, tentativas e concorrência será evoluída. Não ampliar a demonstração para apresentar essas integrações como prontas.
