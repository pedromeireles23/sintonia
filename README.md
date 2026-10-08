# Sintonia

Aplicativo desktop para Windows, em C#/.NET, que distribui tarefas entre sessões do Codex e do Claude.

O usuário abre um projeto, define funções como gameplay, sistemas e revisão, e acompanha as ferramentas trabalhando. Cada função acrescenta instruções e critérios de entrega a uma sessão normal do provedor. O Sintonia organiza tarefas, dependências, resultados e integração das mudanças.

## Objetivos

- Fazer Codex e Claude executarem tarefas reais no mesmo projeto.
- Preservar as skills, os plugins e as conexões MCP compatíveis com seus CLIs.
- Abrir ou retomar sessões conforme o trabalho for liberado.
- Separar alterações paralelas com Git e revisar entregas antes da integração.
- Adicionar geração de imagens e uma biblioteca de assets numa etapa posterior.
- Entregar um aplicativo utilizável e um projeto demonstrável em portfólio .NET.

## Stack escolhida

C# · .NET 10 · WPF · MVVM · SQLite · testes automatizados.

A comunicação com os provedores será feita por adaptadores. A primeira opção para Codex é App Server por entrada/saída padrão; para Claude, CLI com saída estruturada e um mecanismo de permissões validado.

## Estado atual

Repositório inicializado com contexto, arquitetura, roadmap e instruções de desenvolvimento. A implementação C# começa pelo marco M0. As integrações reais ainda precisam ser desenvolvidas e verificadas.

A demonstração Electron criada durante a pesquisa foi um experimento de fluxo; ela não é a implementação deste repositório. O produto será desenvolvido em C#/.NET.

## Documentação

- [CONTEXT.md](CONTEXT.md): decisões, requisitos e ponto de retomada.
- [ROADMAP.md](ROADMAP.md): marcos e critérios de aceite.
- [ARCHITECTURE.md](ARCHITECTURE.md): estrutura proposta e contratos.
- [CONTRIBUTING.md](CONTRIBUTING.md): ambiente, validação e commits.
- [AGENTS.md](AGENTS.md): instruções para assistentes de programação.
- [CLAUDE.md](CLAUDE.md): entrada de contexto para Claude Code.
- [Registro de desenvolvimento](docs/DEVELOPMENT_LOG.md).
- [Análise do Maestro](docs/MAESTRO_ANALYSIS.md).
- [Decisões técnicas](docs/DECISIONS.md).

## Executar

Requisitos: Windows e SDK do .NET 10. Os comandos serão habilitados pela solução criada no marco M0; atualmente não existe um aplicativo compilável neste repositório.

Os pré-requisitos de Codex e Claude serão diagnosticados pelo aplicativo. Encontrar um executável não confirma autenticação, assinatura ou quota disponível.

## Referência e autoria

O [Maestro](https://github.com/RunMaestro/Maestro) foi estudado como referência arquitetural. A implementação do Sintonia será independente; nenhum código do Maestro foi incorporado. A licença de distribuição do Sintonia será definida antes da primeira publicação de release.
