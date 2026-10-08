# Contexto durável do Sintonia

Atualizado em 08/10/2026. Este arquivo é o ponto de entrada para retomar o desenvolvimento em qualquer sessão de Codex ou Claude.

## Intenção do usuário

Criar um aplicativo parecido em propósito com o Maestro: conectar suas instalações de Codex e Claude, distribuir funções e permitir que ambos trabalhem num projeto de programação. O primeiro caso de uso é desenvolver um jogo. O produto também poderá ser apresentado em portfólio de C#/.NET.

As funções podem ser gameplay, sistemas, interface, revisão e arte. Não é necessário criar um sistema separado de agentes autônomos: as próprias ferramentas já executam tarefas. O Sintonia fornece coordenação, estado e acompanhamento.

O usuário ampliou a direção para uma **central geral de projetos**: cadastro de várias pastas, chat central com escolha de provedor/modelo, função de chefe que propõe trabalho a outras sessões e lista de sessões com abertura/retomada. O jogo é o primeiro caso de uso. Escopo e limites em [docs/PRODUCT_SCOPE.md](docs/PRODUCT_SCOPE.md).

Repositório: https://github.com/pedromeireles23/sintonia. Nome adotado: **Sintonia**, conforme a pasta e o repositório existentes.

## Decisões confirmadas

- Aplicativo visual para Windows.
- C# e .NET 10, com WPF e MVVM.
- SQLite para persistência quando entrar o histórico real.
- Sessões dos provedores abertas ou retomadas pelo aplicativo; não depender de várias janelas abertas manualmente.
- Usar os logins por assinatura das instalações locais como padrão; não configurar chaves de API nem mudar automaticamente para cobrança de API.
- Conversas e sessões pertencem a projetos e preservam provedor/modelo, função e diretório. Não assumir memória compartilhada entre as duas IAs.
- Chefia configurável para Codex ou Claude: propõe tarefas e acompanha entregas; regras de execução, permissões e aprovações continuam controladas pelo Sintonia/usuário.
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
- M0 concluído: solução .NET com Core, Infrastructure, Desktop WPF/MVVM, testes xUnit e verificação executável da janela no Windows.
- Núcleo com estados, aprovação explícita de dependências, limites global/por provedor, reserva atômica, cancelamento e histórico de tentativas em memória. 17 testes automatizados aprovados.
- Demonstração M0 em português com quatro funções de provedor editável, cinco tarefas dependentes, entregas, eventos e sessões. Seus adaptadores são **simulados**, sem CLI/modelo ou alterações de arquivos. A central real é uma janela separada e passou a ser a principal.
- Build sem avisos/erros. Teste da janela nativa percorreu duas execuções simultâneas, cinco aprovações e sessões, sem erros de binding. Capturas revisadas em tamanho normal e mínimo; ações de revisão permanecem acessíveis com rolagem.
- A janela principal agora é a central real: múltiplos projetos, chat, escolha de IA/modelo, funções, permissões, atividade, cancelamento e abertura/retomada de conversas. SQLite guarda histórico em `%LOCALAPPDATA%/Sintonia/workspace.db`. Demonstração M0 disponível separadamente e identificada.
- Uma demonstração anterior em Electron validou o conceito de funções, fila e revisão. Ela permanece fora deste repositório e não deve ser confundida com o produto em .NET.
- SDK .NET 10.0.200. Login ChatGPT no Codex e `claude.ai`/Pro no Claude conferidos antes dos turnos. Ambos leram uma amostra com soma/marcador, produziram a resposta correta e retomaram a mesma sessão mantendo contexto. Modelos usados: `gpt-6.1-sol` e `claude-opus-5`. Isso comprova acesso naquele momento, sem garantir quota futura.
- Preparação do M1: diagnóstico .NET limitado a versão/ajuda detectou Codex CLI `0.162.0-alpha.2` e Claude Code `2.1.277`, consultando executáveis nativos sem interpretar wrappers de shell. 11 testes de Infrastructure aprovados (28 testes xUnit no total), inclusive streams simultâneos, timeout/filho e cancelamento. Detalhes e fontes em [docs/PROVIDER_PROBES.md](docs/PROVIDER_PROBES.md).
- M1: contratos de conversa, processo com streams assíncronos limitados, cliente stdio e adaptadores reais com checagem de assinatura, retomada, resultados finais e cancelamento. Isolamento de leitura do Codex bloqueou escrita; interrupção real verificada nos dois provedores. Extensões listadas sem alterar configuração. 28 testes de Infrastructure + 17 de Core aprovados.
- O isolamento elevado do Codex instalado falhou com `apply deny-read ACLs`; o cliente usa o modo oficial `unelevated` somente no próprio processo, sem bypass ou alteração global. Claude usa plan para leitura e manual/recusa de prompts para escrita; host de autorização do Claude ainda pendente.

## Próxima entrega concreta

M2 em andamento: `SqliteWorkspaceStore` e `WorkspaceChatService` preservam projetos, conversas, identificadores nativos, tentativas, checkpoints de texto e eventos. Recuperação marca tentativas interrompidas sem reenvio. Escrita é exclusiva no projeto; leitura admite duas IAs, uma execução por provedor. Janela WPF conectada e comprovada com um turno real de leitura de cada provedor, resultado correto e histórico salvo. Teste WPF com provedores de teste validou dois projetos, autorização, duas IAs em leitura simultânea, retomada, cancelamento e reabertura, sem erros de binding; 55 testes xUnit passam. Executável Release pronto em `artifacts/app/Sintonia.Desktop.exe` (gerado localmente, sem versionar binários).

Completar o host de permissões do Claude e verificar negativas/autorização real antes de anunciar equivalência. Evoluir chefia de instruções de planejamento para proposta estruturada validada e tarefas distribuídas com revisão; a função de chefe atual só propõe pelo chat. Worktrees/revisão/integração Git continuam pendentes. Não executar escrita paralela no mesmo projeto.

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
