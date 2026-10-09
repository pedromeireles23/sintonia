# Referência arquitetural: Maestro

Análise estática realizada em 08/10/2026 sobre o [commit 31b4eb3667f52cfa55cb924ff0c22d494f308cfb](https://github.com/RunMaestro/Maestro/tree/31b4eb3667f52cfa55cb924ff0c22d494f308cfb). O `package.json` identifica a versão 0.17.9. O Maestro não foi instalado ou executado nesta pesquisa; não se trata de auditoria completa de segurança.

## Componentes observados

| Componente | Evidência | Aplicação no Sintonia |
| --- | --- | --- |
| Desktop Electron e React | [Janela](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/app-lifecycle/window-manager.ts#L233) | Separar interface de execução; nossa interface será WPF |
| Definições e capacidades dos provedores | [definitions.ts](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/agents/definitions.ts#L160) | Adaptadores específicos com contratos comuns |
| Processos no Windows | [ChildProcessSpawner.ts](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/process-manager/spawners/ChildProcessSpawner.ts#L308) | Resolver executáveis, wrappers, espaços e comunicação por streams |
| Saída estruturada | [Codex](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/parsers/codex-output-parser.ts#L1), [Claude](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/parsers/claude-output-parser.ts#L1) | Normalizar mensagens, ferramentas, resultados e erros |
| Group Chat | [Participantes](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/group-chat/group-chat-agent.ts#L1) | Coordenar trabalho sobre sessões; cadastrar um participante não mantém necessariamente um processo ativo |
| Cue | [Motor](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/cue/cue-engine.ts#L1), [distribuição](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/cue/cue-dispatch-service.ts#L85) | Filas, eventos, distribuição para várias sessões e espera de várias entregas |
| Persistência de filas | [Queue persistence](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/cue/cue-queue-persistence.ts#L1) | Recuperar estado com registro explícito de perdas e falhas |
| Auto Run | [Máquina de estados](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/renderer/hooks/batch/batchStateMachine.ts#L43) | Estados distintos para execução, erro, parada e finalização |
| Git | [Worktrees](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/ipc/handlers/git.ts#L1062) | Separar alterações e integrar serialmente |

## Diferenças deliberadas

O Sintonia usa C#/.NET e WPF. Seu escopo é um canal de comunicação e coordenação entre sessões do Codex e do Claude em múltiplos projetos, com funções, tarefas e revisão. A prova das duas integrações precede automação extensa. Em 09/10/2026, o usuário retirou o marco de geração de imagens/biblioteca própria; esse trabalho cabe às tarefas dos agentes e às suas ferramentas compatíveis, conforme a decisão 011 de [DECISIONS.md](DECISIONS.md).

No commit analisado, Claude recebe `--dangerously-skip-permissions` e o modo de lote de Codex inclui `--dangerously-bypass-approvals-and-sandbox`. [Claude](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/agents/definitions.ts#L169), [Codex](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/src/main/agents/definitions.ts#L251). O Sintonia não adotará essas opções como padrão de funcionamento.

O modo de inicialização pode modificar capacidades. Preservar skills e plugins requer manter perfis e configurações compatíveis e verificar versões; recursos exclusivos de aplicativos desktop não são garantidos por usar seus CLIs.

## Fontes das integrações

- [Codex App Server](https://learn.chatgpt.com/docs/app-server).
- [Claude programático](https://code.claude.com/docs/en/headless).
- [Plugins locais do Codex](https://developers.openai.com/plugins/build/plugins).
- [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/).

O Maestro declara [AGPL 3.0](https://github.com/RunMaestro/Maestro/blob/31b4eb3667f52cfa55cb924ff0c22d494f308cfb/LICENSE). A pesquisa orientou ideias; seu código não foi copiado para este repositório.
