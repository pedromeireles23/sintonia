# Sintonia

Aplicativo desktop para Windows, em C#/.NET, que centraliza projetos gerais e conversas do Codex e do Claude: software, documentação, pesquisa, análise e outras tarefas suportadas pelos provedores.

O usuário abre projetos, escolhe uma IA/modelo e acompanha as ferramentas trabalhando. Perfis reutilizáveis guardam funções, provedor/modelo padrão e instruções para sessões normais dos provedores. A chefia produz planos editáveis; a fila permite encaminhar, iniciar tarefas e revisar entregas. Integração Git e acompanhamento automático pela chefia são os próximos incrementos. Jogos são apenas um exemplo opcional de uso.

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

**M2 concluído: central real de projetos com perfis, planos e fila.** Cadastre pastas, escolha Codex/Claude e modelo, converse, acompanhe atividade e abra/retome sessões. Histórico e perfis SQLite sobrevivem à reabertura. Leitura permite duas IAs simultâneas, uma por provedor; conversas com escrita trabalham sozinhas no projeto até existir integração por worktrees. Build sem avisos/erros, 141 testes xUnit aprovados e fluxos WPF verificados.

Ambos os provedores leram uma amostra e responderam corretamente dentro da janela real, usando assinaturas. Retomada e interrupção também foram verificadas. Codex e Claude têm autorização por ação na central, preservando regras/hooks existentes. No Claude, uma prova real recusou a criação de um arquivo e autorizou outra na mesma sessão, conferindo o conteúdo. A função **Chefe do projeto** gera propostas estruturadas em leitura. **Revisar planos** permite editar e confirmar; **Fila de tarefas** encaminha o plano, inicia tentativas e registra aprovação ou ajustes das entregas. Integração Git está pendente.

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
- [Perfis de função](docs/FUNCTION_PROFILES.md).
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

Na central, clique em **Adicionar projeto**, escolha a pasta, IA e modelo e envie um pedido. **Nova conversa** permite escolher outra IA/modelo; a lista à direita abre o histórico e retoma a sessão ao enviar novamente. **Função e permissões** configura instruções e acesso de leitura/escrita. Quando Codex ou Claude pedirem autorização, confira a pasta e a prévia e escolha **Permitir esta ação** ou **Recusar**. As regras e os hooks dos provedores também podem permitir ou recusar ferramentas. **Cancelar** interrompe a conversa selecionada e suas decisões pendentes.

O host Claude aprova somente os parâmetros exibidos, sem criar regras permanentes. Em leitura, solicitações ao host são recusadas. Prévias incompletas ou acima de 64 mil caracteres e interações que exigem respostas especiais (`AskUserQuestion`/`ExitPlanMode`) também são recusadas. O fluxo atual cobre um turno até seu resultado terminal; trabalho autônomo em background após esse resultado ainda não tem acompanhamento.

O histórico fica em `%LOCALAPPDATA%/Sintonia/workspace.db`. Execuções sem resultado terminal são marcadas como interrompidas ao reabrir; nenhum pedido é reenviado automaticamente.

Abra **Perfis de função** para salvar configurações de análise, documentação, desenvolvimento ou outras funções. Na central, escolha o perfil em **Função e permissões** e use **Nova com perfil**. A ação preenche uma nova conversa em leitura, conserva a mensagem em rascunho e permite conferir os padrões antes de enviar. Salvar/aplicar não chama um modelo; editar ou excluir preserva conversas e tarefas já criadas. Modelo vazio usa o padrão da instalação. [Fluxo, edição e limites dos perfis](docs/FUNCTION_PROFILES.md).

![Perfis de função em tamanho mínimo — dados de teste, sem chamada de modelos](docs/images/sintonia-perfis.png)

Para planejar, selecione **Chefe do projeto** em **Função e permissões** e descreva o objetivo no chat. A chefia usa leitura e mantém a IA/modelo escolhido. Ao receber uma proposta válida, abra **Revisar planos**. Ajuste responsável, modelo, função, acesso recomendado, instruções, escopo, dependências e critérios; adicione ou remova tarefas conforme necessário. **Confirmar plano** registra sua aprovação para execução futura. Salvar ajustes como rascunho retira a aprovação anterior. O acesso recomendado de uma tarefa ainda depende das permissões do provedor quando houver execução.

![Revisão de plano em tamanho mínimo — dados de teste com provedor simulado](docs/images/sintonia-planos.png)

O [formato de propostas e seus limites](docs/PLAN_PROPOSALS.md) descreve validação e histórico. Propostas inválidas não viram tarefas; a resposta original do chat permanece disponível. Planos são separados por projeto e edições simultâneas não sobrescrevem revisões mais novas.

Abra **Fila de tarefas**, escolha um plano aprovado e use **Encaminhar à fila**. Essa ação preserva a revisão e cria as tarefas, sem executar modelos. Selecione uma tarefa disponível e use **Iniciar tentativa**. Confira resposta, arquivos e critérios; **Aprovar entrega** libera dependências e **Solicitar ajustes** registra o pedido para uma nova tentativa na mesma sessão. Autorizações aparecem no próprio painel. Cancelar não desfaz arquivos já alterados; confira o projeto antes de tentar novamente.

![Fila e revisão em tamanho mínimo — dados de teste com provedores simulados](docs/images/sintonia-fila.png)

Cada tarefa tem até três tentativas explícitas. Planos encaminhados ficam preservados; mudanças de planejamento exigem outra proposta. Histórico e estados sobrevivem à reabertura. [Fluxo e limites da fila](docs/TASK_QUEUE.md). A prova real finita da fila executou um turno de leitura por provedor, verificou contexto da dependência e reabriu o banco. Escrita pela fila e permissões visuais foram testadas com provedores simulados; a autorização real dos adaptadores tem evidências separadas.

Verificação da janela real no Windows (abre, percorre o fluxo e fecha a janela de teste):

```powershell
rtk proxy dotnet run --project tests/Sintonia.Desktop.SmokeTests --no-build
rtk proxy dotnet run --project tests/Sintonia.Desktop.SmokeTests --no-build -- --workspace
rtk proxy dotnet run --project tests/Sintonia.Desktop.SmokeTests --no-build -- --proposals
rtk proxy dotnet run --project tests/Sintonia.Desktop.SmokeTests --no-build -- --queue
rtk proxy dotnet run --project tests/Sintonia.Desktop.SmokeTests --no-build -- --profiles
```

O primeiro teste percorre a demonstração; os demais verificam a central, os planos, a fila e os perfis com provedores de teste, sem consumir modelos. Capturas em `artifacts/ui-smoke`, `artifacts/workspace-smoke`, `artifacts/proposal-smoke`, `artifacts/queue-smoke` e `artifacts/profile-smoke`. O script `tests/Sintonia.Desktop.SmokeTests/verify-startup.ps1` confere abertura/encerramento do executável normal. A opção `--workspace-real` é prova manual finita, consome quota e não deve rodar em CI/loop.

O diagnóstico inicial de instalações já pode ser executado, separado da janela:

```powershell
rtk proxy dotnet run --project tools/Sintonia.Diagnostics --no-build
```

Consulta somente versão/ajuda, com prazos e saída limitada. Encontrar um executável não confirma autenticação, assinatura, quota disponível ou integração real. Consulte [as verificações e limites do M1](docs/PROVIDER_PROBES.md).

A prova opcional `rtk proxy dotnet run --project tools/Sintonia.Diagnostics --no-build -- permissions Claude` consome quota da assinatura: faz no máximo dois turnos em pasta exclusiva de `artifacts/provider-probes`, recusa uma escrita e permite outra com caminho/conteúdo conferidos. Não deve rodar em CI/loop.

A prova opcional `rtk proxy dotnet run --project tools/Sintonia.Diagnostics --no-build -- plan Claude` faz um turno de planejamento em leitura e valida duas tarefas, ambos os provedores e uma dependência. Consome quota; fora de CI/loop. A mesma opção aceita Codex, cuja geração real de plano ainda não foi exercitada neste incremento.

A prova opcional `rtk proxy dotnet run --project tools/Sintonia.Diagnostics --no-build -- queue` faz no máximo dois turnos reais em leitura, um por provedor, com plano fornecido pelo diagnóstico em pasta exclusiva. Verifica resposta, revisão, contexto de dependência e reabertura SQLite; para na primeira falha, sem novas tentativas. Consome quota e não deve rodar em CI/loop.

## Referência e autoria

O [Maestro](https://github.com/RunMaestro/Maestro) foi estudado como referência arquitetural. A implementação do Sintonia será independente; nenhum código do Maestro foi incorporado. A licença de distribuição do Sintonia será definida antes da primeira publicação de release.
