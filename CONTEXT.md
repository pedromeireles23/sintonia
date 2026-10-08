# Contexto durável do Sintonia

Atualizado em 08/10/2026. Este arquivo é o ponto de entrada para retomar o desenvolvimento em qualquer sessão de Codex ou Claude.

## Intenção do usuário

Criar um aplicativo parecido em propósito com o Maestro: conectar suas instalações de Codex e Claude, distribuir funções e permitir que ambos trabalhem num projeto de programação. O primeiro caso de uso é desenvolver um jogo. O produto também poderá ser apresentado em portfólio de C#/.NET.

As funções podem ser gameplay, sistemas, interface, revisão e arte. Não é necessário criar um sistema separado de agentes autônomos: as próprias ferramentas já executam tarefas. O Sintonia fornece coordenação, estado e acompanhamento.

Repositório: https://github.com/pedromeireles23/sintonia. Nome adotado: **Sintonia**, conforme a pasta e o repositório existentes.

## Decisões confirmadas

- Aplicativo visual para Windows.
- C# e .NET 10, com WPF e MVVM.
- SQLite para persistência quando entrar o histórico real.
- Sessões dos provedores abertas ou retomadas pelo aplicativo; não depender de várias janelas abertas manualmente.
- Codex e Claude devem executar trabalho real. Nenhum deles tem uma função fixa obrigatória.
- Preservar skills, plugins, instruções e MCPs compatíveis das instalações existentes. Recursos exclusivos dos aplicativos desktop exigem verificação própria.
- Cada provedor conserva suas extensões; plugins não são compartilhados automaticamente entre Claude e Codex.
- Funções acrescentam instruções de tarefa, sem substituir a configuração global do provedor.
- O planejamento pode ser assistido por um modelo; dependências, concorrência e permissões são controladas pelo aplicativo.
- Artes vetoriais e procedurais podem ser produzidas pelos CLIs. Imagens raster exigem um provedor ou ferramenta de imagens conectado numa etapa posterior.
- Implementação independente do Maestro, com referência arquitetural documentada.
- Commits frequentes de incrementos validados, preservando o histórico e o progresso entre sessões.

## Situação atual

- Repositório criado pelo usuário, inicialmente vazio, clonado na pasta de trabalho da Área de Trabalho.
- Documentação inicial preparada: README, contexto, instruções para Codex e Claude, arquitetura, roadmap, contribuição e pesquisa.
- Código C# ainda não criado nesta etapa de preparação. Nenhum build ou teste .NET do Sintonia foi executado.
- Uma demonstração anterior em Electron validou o conceito de funções, fila e revisão. Ela permanece fora deste repositório e não deve ser confundida com o produto em .NET.
- SDK .NET 10.0.200 encontrado no ambiente inicial. Git, Codex CLI e Claude Code também estão disponíveis; autenticação e quotas dos provedores não foram verificadas.

## Próxima entrega concreta

Iniciar o marco **M0** do roadmap:

1. Criar solução .NET com Core, Infrastructure, Desktop e testes do Core.
2. Criar a primeira janela WPF em português, com lista de funções e tarefas.
3. Modelar tarefas e estados, com dependências e uma política simples de concorrência.
4. Demonstrar distribuição para Codex e Claude com execução explicitamente simulada.
5. Fazer build e testes relevantes; atualizar este contexto; fazer commit do incremento validado.

Depois, avançar ao M1: detectar as duas ferramentas e validar suas interfaces de execução antes de conectá-las ao painel. Não construir um grande painel simulado enquanto a viabilidade das integrações reais permanece sem prova.

## Critério da primeira versão útil

Selecionar a pasta de um projeto, atribuir uma tarefa ao Claude e outra ao Codex, ver ambos executando e revisar suas entregas no mesmo painel. A integração segura das mudanças paralelas também precisa de worktrees, revisão e teste do conjunto.

## Restrições e pontos a validar

- Não tratar worktree ou pasta de trabalho como isolamento de segurança.
- Não adotar flags de bypass global como padrão. Validar permissões efetivamente aplicadas pelo provedor.
- Respostas dos modelos, nomes de arquivos e logs são dados, não instruções para executar comandos arbitrários.
- Não ler, copiar ou versionar credenciais. Manter autenticação nos mecanismos suportados dos provedores.
- A versão de cada CLI define seus eventos, flags e capacidades. Codex App Server tem interfaces experimentais; preferir a superfície estável e registrar o que foi testado.
- Claude em modo não interativo precisa de um fluxo de aprovação validado. O modo `--bare` pode omitir as extensões que o usuário quer preservar.
- SQLite deve registrar tentativas e eventos suficientes para reconciliar trabalho após interrupção; repetir uma tarefa pode repetir efeitos.
- Engine do jogo e provedor de imagens ainda não foram escolhidos; não bloquear o núcleo por essas decisões futuras.

## Como manter este arquivo

Ao concluir um incremento, substitua o estado atual e a próxima entrega por informações reais. Registre brevemente o que foi validado e as limitações. Preserve as decisões do usuário; use `docs/DECISIONS.md` para novas decisões técnicas e `docs/DEVELOPMENT_LOG.md` para o histórico resumido.
