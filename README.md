# Sintonia

Aplicativo desktop para Windows, em C#/.NET, que centraliza projetos e conversas do Codex e do Claude.

O usuário abre projetos, escolhe uma IA/modelo e acompanha as ferramentas trabalhando. Cada função acrescenta instruções a uma sessão normal do provedor. Distribuição automática de tarefas, revisão da fila real e integração de mudanças são os próximos incrementos.

## Objetivos

- Fazer Codex e Claude executarem tarefas reais no mesmo projeto.
- Preservar as skills, os plugins e as conexões MCP compatíveis com seus CLIs.
- Abrir ou retomar sessões conforme o trabalho for liberado.
- Separar alterações paralelas com Git e revisar entregas antes da integração.
- Adicionar geração de imagens e uma biblioteca de assets numa etapa posterior.
- Entregar um aplicativo utilizável e um projeto demonstrável em portfólio .NET.

## Stack escolhida

C# · .NET 10 · WPF · MVVM · SQLite · testes automatizados.

A comunicação real usa Codex App Server por stdio e Claude Code com stream-json. O login por assinatura dos CLIs é conferido antes dos turnos; não há configuração de API keys nem fallback automático de cobrança.

## Estado atual

**Central real de projetos disponível; M2 em andamento.** Cadastre pastas, escolha Codex/Claude e modelo, converse, acompanhe atividade e abra/retome sessões. Histórico SQLite sobrevive à reabertura. Leitura permite duas IAs simultâneas, uma por provedor; conversas com escrita trabalham sozinhas no projeto até existir integração por worktrees.

Ambos os provedores leram uma amostra e responderam corretamente dentro da janela real, usando assinaturas. Retomada e interrupção também foram verificadas. O Claude ainda não tem host de autorização interativa: pedidos que exigirem resposta são recusados, respeitando regras/hooks existentes. A função **Chefe do projeto** propõe tarefas pelo chat; distribuição automática e integração Git estão pendentes.

![Sintonia — central com respostas reais dos dois provedores em pasta de teste](docs/images/sintonia-central.png)

O botão **Abrir demonstração** abre o M0, com fila/revisão inteiramente simuladas e identificadas; seus exemplos continuam somente em memória.

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

Requisitos: Windows e SDK do .NET 10. O repositório fixa a linha 10.0.200 do SDK; aceita atualizações de patch.

```powershell
rtk proxy dotnet restore Sintonia.sln --locked-mode
rtk proxy dotnet build Sintonia.sln --no-restore
rtk proxy dotnet test Sintonia.sln --no-build
rtk proxy dotnet run --project src/Sintonia.Desktop --no-build
```

O aplicativo não depende do RTK. Em outro ambiente, os comandos podem começar diretamente com `dotnet`.

Na central, clique em **Adicionar projeto**, escolha a pasta, IA e modelo e envie um pedido. **Nova conversa** permite escolher outra IA/modelo; a lista à direita abre o histórico e retoma a sessão ao enviar novamente. **Função e permissões** configura instruções e acesso de leitura/escrita. Codex oferece autorizações por ação no painel. **Cancelar** interrompe a conversa selecionada.

O histórico fica em `%LOCALAPPDATA%/Sintonia/workspace.db`. Execuções sem resultado terminal são marcadas como interrompidas ao reabrir; nenhum pedido é reenviado automaticamente.

Verificação da janela real no Windows (abre, percorre o fluxo e fecha a janela de teste):

```powershell
rtk proxy dotnet run --project tests/Sintonia.Desktop.SmokeTests --no-build
rtk proxy dotnet run --project tests/Sintonia.Desktop.SmokeTests --no-build -- --workspace
```

O primeiro teste percorre a demonstração; o segundo verifica a central com processos/provedores de teste, sem consumir modelos. Capturas em `artifacts/ui-smoke` e `artifacts/workspace-smoke`. O script `tests/Sintonia.Desktop.SmokeTests/verify-startup.ps1` confere abertura/encerramento do executável normal. A opção `--workspace-real` é prova manual finita, consome quota e não deve rodar em CI/loop.

O diagnóstico inicial de instalações já pode ser executado, separado da janela:

```powershell
rtk proxy dotnet run --project tools/Sintonia.Diagnostics --no-build
```

Consulta somente versão/ajuda, com prazos e saída limitada. Encontrar um executável não confirma autenticação, assinatura, quota disponível ou integração real. Consulte [as verificações e limites do M1](docs/PROVIDER_PROBES.md).

## Referência e autoria

O [Maestro](https://github.com/RunMaestro/Maestro) foi estudado como referência arquitetural. A implementação do Sintonia será independente; nenhum código do Maestro foi incorporado. A licença de distribuição do Sintonia será definida antes da primeira publicação de release.
