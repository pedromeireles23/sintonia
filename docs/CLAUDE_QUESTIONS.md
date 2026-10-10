# Perguntas de escolha do Claude

Estado em 10/10/2026: contrato, adaptador stdio e formulário WPF implementados na central e na fila. Verificados com o adaptador real e um processo local de teste; nenhum modelo ou rede neste incremento. A emissão de `AskUserQuestion` pelo CLI/modelo real ainda precisa de uma prova finita. M1 conserva essa lacuna.

## Uso

Quando o Claude encaminha perguntas ao host, aparece **Claude aguarda sua resposta**, com o nome da conversa/tarefa e sua pasta efetiva. Escolha uma opção, marque várias quando indicado, ou preencha **Outra resposta**. Texto livre substitui as opções daquela pergunta; uma nova seleção limpa o texto livre. Nada vem selecionado, e **Enviar respostas** só fica habilitado quando todas as perguntas têm resposta válida.

**Recusar perguntas** envia negativa e a execução fica bloqueada, mesmo que o CLI anuncie sucesso. Cancelar a conversa/tarefa, fechar a central ou receber retirada do CLI remove o formulário e invalida seus comandos. Respostas antigas não são reaproveitadas nem reenviadas ao reabrir.

Perguntas e autorizações compartilham a espera serial da central, inclusive entre sessões/projetos. A pasta e a conversa exibidas pertencem à solicitação capturada, mesmo ao navegar. Na fila, responder não aprova a entrega: o término continua sujeito à revisão. Durante uma pergunta da tarefa ativa, o formulário de revisão indisponível cede espaço, mantendo **Cancelar execução** acessível.

## Contrato

O [contrato oficial de entrada do usuário](https://code.claude.com/docs/en/agent-sdk/user-input) encaminha `AskUserQuestion` por `can_use_tool`. Cada pergunta contém `question`, `header`, `options` e `multiSelect`; opções contêm `label`, `description` e, opcionalmente, `preview`. O retorno permitido acrescenta `answers` a `updatedInput`, com o texto exato da pergunta como chave e rótulo, lista de rótulos ou texto livre como valor.

`ConversationRequest.QuestionHandler` é separado de `PermissionHandler`. O adaptador preserva os outros campos da entrada e substitui somente `answers`, sem `updatedPermissions` ou regras permanentes. Pode responder em leitura e na chefia; isso não habilita escrita. O callback não bloqueia stdout, e retirada/cancelamento/término interrompem a espera sem emitir resposta tardia.

Limites locais: entrada de até 64 mil caracteres; uma a quatro perguntas, com duas a quatro opções cada; pergunta/descrição até 2.000 caracteres, cabeçalho até 12 sem controles, rótulo até 200, prévia até 10 mil e texto livre até 4.000. Perguntas/rótulos repetidos, propriedades JSON duplicadas, seleção desconhecida/duplicada, múltiplas escolhas em pergunta simples ou texto junto de seleção são recusados. O texto livre mantém espaços, quebras de linha e caracteres literais. Prévias são texto, sem executar HTML.

Sem callback, com entrada/resposta inválida, recusa ou falha do callback, o host nega a pergunta. Não encaminha a operação para aprovação booleana. O schema SQLite permanece 15; o formulário é transitório, sem tabela de respostas. Histórico/eventos/resultados continuam usando os contratos existentes. `ExitPlanMode`, elicitation e acompanhamento de background após resultado terminal permanecem sem fluxo específico.

## Validação e retomada

32 casos novos: 15 Core e 17 Infrastructure. Seleção Release aprovada em 216 casos (84 Core + 132 Infrastructure), incluindo protocolo, resultados de ferramentas, persistência, fila, concorrência e os diagnósticos anteriores. Builds Debug/Release sem avisos/erros. Casos não afetados de Git não foram repetidos integralmente.

WPF `--questions` aprovado em Debug e Release: escolha simples/múltipla, texto literal, envio incompleto, recusa/retomada, streaming durante espera, duas sessões concorrentes, cancelamento em espera/decisão, retirada nativa, leitura sem autorizar Write, fila/revisão, fechamento e reabertura. Regressões Release `--workspace`, `--queue`, `--chief-plan` e `--sessions` aprovadas, zero erros de binding. Capturas normal/mínima revisadas; a fila conserva rolagem e botões no tamanho mínimo.

```powershell
rtk proxy dotnet build Sintonia.sln -c Release --no-restore
rtk proxy dotnet test tests/Sintonia.Core.Tests -c Release --no-build
rtk proxy dotnet test tests/Sintonia.Infrastructure.Tests -c Release --no-build --filter "FullyQualifiedName~ClaudeQuestionTests|FullyQualifiedName~ConversationProviderTests|FullyQualifiedName~ClaudeToolResultTests|FullyQualifiedName~WorkspaceTests|FullyQualifiedName~WorkspaceTaskTests|FullyQualifiedName~WorkspaceConcurrencyTests|FullyQualifiedName~ClaudeMcpPermissionProbeTests|FullyQualifiedName~ClaudeToolPermissionProbeTests"
rtk proxy dotnet run --project tests/Sintonia.Desktop.SmokeTests -c Release --no-build -- --questions artifacts/question-smoke-release
```

Os comandos acima usam simulação/processos de teste e não instanciam modelos. Próximo incremento: diagnóstico opt-in com uma pergunta real delimitada, um turno, prazo e iterações limitados, resposta exata conferida e parada sem retry. Não declarar compatibilidade real de `AskUserQuestion` antes dessa evidência. [Provas anteriores](PROVIDER_PROBES.md) e [diferenças de extensões](PROVIDER_EXTENSIONS.md).
