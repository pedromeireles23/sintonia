# Perguntas de escolha do Claude

Estado em 13/10/2026: contrato, adaptador stdio e formulário WPF implementados na central e na fila, verificados com processos de teste. Após o usuário renovar o login por assinatura, uma prova real finita confirmou emissão de pergunta simples, resposta Detalhes pelo callback e resultado final em leitura. A tentativa OAuth interrompida de 10/10 está preservada. Escolha múltipla/texto livre e interação WPF com modelo real conservam apenas as provas de processo/UI; M1 ainda tem lacunas de outros controles e MCPs.

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

Os comandos acima usam simulação/processos de teste e não instanciam modelos. [Provas anteriores](PROVIDER_PROBES.md) e [diferenças de extensões](PROVIDER_EXTENSIONS.md).

## Diagnóstico real finito

`question Claude` prepara uma pasta exclusiva e admite somente um envio, com prazo de 90s e três iterações internas no máximo; o diagnóstico inteiro tem prazo de três minutos. Sem retry/reenvio. Conferência de assinatura e initialize sem prompt precedem a prova. A consulta `question-ready Claude` executa somente essas duas verificações; sucesso de metadados não garante renovação OAuth ou execução do modelo.

Somente a ferramenta de pergunta é solicitada no conjunto de ferramentas nativas; MCPs ficam fora dos processos da prova. Regra ask, ferramentas e configuração vazia MCP são parâmetros de processo, sem alterar configurações globais. Acesso continua em leitura, sem bypass. Limites/ferramentas seguem a [referência oficial do CLI](https://code.claude.com/docs/en/cli-reference). O host confere uma pergunta exata sobre formato, duas opções e a pasta, respondendo exclusivamente **Detalhes**. Pedido divergente/duplicado ou ferramenta extra cancela. Aprovação exige uma chamada, resposta do callback, resultado correlacionado sem erro/truncamento e JSON final com a escolha exata. A prova não usa a UI WPF nem demonstra escolha múltipla/texto livre reais; estes conservam as provas de processo/UI anteriores.

Artefato `question-evidence.json` registra estado/tentativa, contagens, sessão/modelo quando observados, tokens informados ou ausência, tamanho/hash/critério do resultado e código de falha. Não guarda texto do modelo, resultado de ferramenta, stderr, identidade de conta ou credenciais. Uma pasta já usada impede novo envio. `verify-question` confere somente consistência do artefato, sem modelos; não reobtém o conteúdo cujo hash foi registrado.

```powershell
# Metadados somente; não inicia modelos.
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release --no-build -- question-ready Claude
# Opt-in: um único envio real. Não executar em loop.
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release --no-build -- question Claude
# Leitura de evidência; não chama modelos.
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release --no-build -- verify-question "caminho da pasta da prova"
```

Tentativa de 10/10/2026 com Claude Code **2.1.277**: um envio, estado **Interrupted**, zero pedidos de pergunta, respostas ou chamadas de ferramentas, sem resultado/tokens informados. Uma leitura restrita à conversa nativa dessa pasta identificou erro de renovação OAuth; só a categoria foi projetada em `failure-diagnosis.json`. Mensagens e valores nativos não foram copiados. A evidência fica em `artifacts/provider-probes/Pergunta Claude 0d5361fa-3393-4fdd-8f35-18c66b036d9b`, ignorada pelo Git. Nenhum prompt foi reenviado. Não atribuir zero consumo, quota esgotada ou modelo indisponível a campos ausentes.

Assinatura e initialize em leitura passaram na consulta posterior sem modelos; a prova continuou interrompida e seu verificador recusou o artefato. O adaptador identifica erro explícito OAuth/refresh/failed em `result.errors`, inclusive com exit code diferente de zero, e retorna mensagem em português com `AuthenticationRefreshFailed`, sem expor a mensagem interna. Isso não verifica a validade futura do login nem refaz autenticação. A retomada exigia `rtk proxy claude auth login --claudeai` concluído pelo usuário e uma nova pasta; essa nova prova foi autorizada e concluída em 13/10/2026, abaixo.

Validação do incremento de 10/10/2026: 28 casos novos, aprovados em Debug/Release; seleção Release com 214 casos (84 Core + 130 Infrastructure), sem falhas/ignorados. Cobrem ausência/divergência/duplicação, autorização inesperada, ferramenta/resultado extra, erro/truncamento, resposta final incorreta, cancelamento, arquivo inesperado, pasta já usada, metadados sem prompt e classificação OAuth sem detalhes internos. Builds Debug/Release sem avisos/erros; WPF `--questions` Release e abertura/encerramento do Release local aprovados, zero erros de binding. Não repetidas integralmente suítes de Git ou fluxos não afetados. Nenhuma nova chamada real naquele incremento.

## Prova real concluída em 13/10/2026

Claude Code **2.1.277**, modelo **claude-opus-5**, login por assinatura renovado pelo usuário. `question-ready Claude` passou sem prompt; `question Claude` criou uma pasta exclusiva e concluiu um único envio, sem retry ou novas chamadas após o sucesso. O adaptador/host reais receberam exatamente a pergunta, opções e pasta esperadas, responderam **Detalhes** e conferiram o JSON final com essa escolha.

| Conferência | Evidência |
| --- | --- |
| Estado / resultado | Completed / Completed |
| Envios / chamadas de ferramenta | 1 / 1 |
| Perguntas exatas / respostas do callback | 1 / 1 |
| Autorizações de escrita | 0 |
| Resultado correlacionado | 140 caracteres, hash SHA-256, pergunta/resposta encontradas, sem erro anunciado ou truncamento |
| Resposta final | JSON com somente formato = Detalhes |
| Tokens informados do turno principal | 17.617 entrada + 477 saída = 18.094 |

Cache read (8.531) e cache write (9.082) já estão incluídos na entrada; não são somados novamente. A medição não representa saldo da assinatura. Nenhuma entrada inesperada foi criada na pasta da prova. Evidência projetada em `artifacts/provider-probes/Pergunta Claude 94ae0c62-9285-4ef1-b90f-a54a06a1b2d5/question-evidence.json`, ignorada pelo Git; a pasta Interrupted anterior permanece preservada.

`verify-question` passou depois sem rede/modelos. Build Release da solução sem avisos/erros; 208 referências locais válidas em 33 documentos, consistência e diff revisados. Código, schema e UI não foram alterados, e as suítes automatizadas/WPF não foram repetidas neste incremento. Esta prova usa resposta fixa pelo callback; escolha múltipla, texto livre, recusa/cancelamento e interação WPF com modelo real conservam as provas de processo/UI anteriores. `ExitPlanMode`, elicitation, acompanhamento de background e demais MCPs/autenticações continuam parciais/não suportados.
