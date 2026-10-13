# Prova local de elicitation Claude sem modelo

Estado em 13/10/2026: uma elicitation de formulário foi emitida pelo Claude Code **2.1.277** e recusada com `decline`, sem prompt ou turno de modelo. Servidor MCP local confirmou a recusa e a ausência de dados. A prova usa o canal de controle `mcp_call` e a mesma serialização de resposta do host; não é uma execução de conversa/fila nem uma prova de autorização de ferramentas genéricas. [Comportamento do host](CLAUDE_CONTROLS.md).

## Rota e escopo

Os [tipos publicados do SDK 0.3.220](https://unpkg.com/@anthropic-ai/claude-agent-sdk@0.3.220/sdk.d.ts) descrevem `mcp_call` como chamada direta de ferramenta MCP pelo subprocesso, sem turno de modelo ou checagem de permissão de ferramenta. É um canal confiado ao chamador. O diagnóstico restringe a chamada a uma ferramenta própria de teste; a central e `IConversationProvider` não oferecem essa rota como executor genérico.

`elicitation Claude` cria uma pasta exclusiva, confere versão/login por assinatura, inicializa stdio em plan e consulta uma vez o estado MCP. Configuração de processo contém somente o servidor stdio `sintonia_elicitation`, executado pelo próprio diagnóstico, com `--strict-mcp-config` e sem persistência de sessão. Instalações, regras/hooks e configurações globais não são modificados; hooks nativos que resolvessem a interação antes do host fariam faltar a solicitação esperada e a prova não passaria.

Um único `mcp_call` invoca `mcp__sintonia_elicitation__request_format` com um nonce exclusivo. O servidor anuncia uma ferramenta local, admite uma chamada e pede somente um formato fictício por `elicitation/create`. Não contém cliente de rede nem executa shell ou acessa projetos do usuário; escreve somente a auditoria no diretório da prova. O [ciclo de vida MCP](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle) e a [elicitation de formulário](https://modelcontextprotocol.io/specification/2025-11-25/client/elicitation) definem negociação de capacidades, pedido e resposta usados pelo servidor.

O diagnóstico exige servidor, mensagem, modo e schema exatos. IDs repetidos, solicitação extra, autorização/ferramenta inesperada, resposta não correlacionada, evento de turno ou arquivo extra interrompem a prova. Resposta usa `ClaudeStdioSession.CreateUnsupportedControlResponse`, também usado na central: somente `action = decline`, sem `content`. O servidor confere isso antes de retornar o marcador de recusa. Prazo de 90 segundos, limite externo de três minutos, um envio de ferramenta e nenhum retry/reconnect/reenvio. Pasta usada ou servidor reiniciado na mesma pasta são recusados.

## Evidência real

| Conferência | Resultado |
| --- | --- |
| CLI / protocolo MCP | 2.1.277 / 2025-11-25 |
| Estado do diagnóstico / servidor | Completed / Completed |
| Chamadas MCP / elicitation exata / respostas decline | 1 / 1 / 1 |
| Mensagens de usuário enviadas / eventos de turno observados | 0 / 0 |
| Dados devolvidos ao servidor / controles inesperados | nenhum / 0 |
| Resultado da ferramenta | marcador conferido, 68 caracteres e hash SHA-256 |

Artefatos locais ignorados em `artifacts/provider-probes/Elicitation Claude cf5824ed-d48d-4232-aeed-a29e2f68c5c8`: `elicitation-evidence.json` e `local-server-evidence.json`. Guardam contagens, versão/protocolo, estado/ação, ausência de dados, tamanho/hash/critério e categoria de falha. Não guardam mensagem/schema, nonce, corpos das respostas, stderr, identidade de conta ou credenciais. Não há medição/estimativa de quota da assinatura.

`verify-elicitation` passou após o encerramento, sem CLI, rede ou modelos. Confere a consistência entre os dois artefatos; não reobtém o conteúdo cujo hash foi registrado. Nenhuma chamada MCP real foi repetida após o sucesso.

```powershell
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release --no-build -- elicitation Claude
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release --no-build -- verify-elicitation "pasta da prova"
```

## Validação e limites

18 casos novos Debug/Release com processos de teste: recusa/correlação, pasta usada, escopo do lançamento, cliente sem capacidade form, resposta accept, servidor/controle/schema/ID/resultado divergentes, duplicação, evento de turno, conteúdo/arquivo inesperado, interrupção e cancelamento. Seleção Release final de 207 casos aprovada, incluindo os 18 novos e 189 regressões. Um teste antigo excedeu cinco segundos na primeira seleção com build Debug simultâneo; seu grupo de dez passou isoladamente e os 207 passaram depois sem compilação concorrente. Nenhum prazo de teste foi alterado.

Builds Debug/Release sem avisos/erros; WPF --questions Release passou com processos de teste e zero erros de binding. Release local atualizado em artifacts/app, abertura/encerramento normal conferidos; 228 referências locais válidas em 35 documentos e diff sem erros de whitespace. Suítes de Git não afetadas não foram repetidas integralmente. Nenhum SDK/runtime, schema ou formulário novo; o servidor é ferramenta do diagnóstico.

A prova confirma emissão e recusa form pelo canal direto, com a mesma resposta do host. O estado Blocked de uma conversa/fila continua coberto pelos testes existentes; não há inferência nem run real nessa rota. Não comprova URL mode, escrita externa, autenticações de outros MCPs ou paridade desktop. Uma prova posterior própria confirmou [recusa ExitPlanMode real](CLAUDE_PLAN_EXIT.md) em um envio em leitura, com resultado Blocked. Próximo incremento: prova de recusa URL mode com MCP local, buscando a mesma rota sem modelo já comprovada em form.
