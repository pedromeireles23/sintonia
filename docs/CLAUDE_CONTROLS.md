# Controles especiais do Claude

Estado em 13/10/2026: o host recusa explicitamente elicitation MCP e ExitPlanMode. Contratos publicados conferidos; comportamento de recusa, streaming e ciclo de vida verificado com processos de teste. Não houve nova chamada de modelo ou prova de emissão real desses controles pelo CLI neste incremento. [Perguntas AskUserQuestion](CLAUDE_QUESTIONS.md) conservam seu fluxo e sua prova real anteriores.

## Contratos e comportamento

| Solicitação recebida pelo host | Resposta do Sintonia | Resultado local |
| --- | --- | --- |
| can_use_tool / AskUserQuestion | Callback de perguntas e answers, conforme o contrato existente | Depende da resposta; não autoriza escrita |
| can_use_tool / ExitPlanMode | behavior = deny; nenhum callback de aprovação/pergunta | Blocked, mesmo se o CLI anunciar sucesso |
| Controle elicitation | Resposta success correlacionada por request_id, com somente action = decline | Blocked e diagnóstico específico |
| Controle desconhecido | Resposta error correlacionada, com mensagem fixa | Blocked e diagnóstico genérico |
| Envelope inválido, identificador duplicado ou controle após resultado terminal | Falha de protocolo e encerramento do processo | Não produz execução concluída |

A [referência oficial TypeScript](https://code.claude.com/docs/en/agent-sdk/typescript#options) separa `onElicitation` de `canUseTool` e define recusa automática para elicitation sem handler. Os [tipos publicados do SDK 0.3.220](https://unpkg.com/@anthropic-ai/claude-agent-sdk@0.3.220/sdk.d.ts) declaram o controle `elicitation` com `mcp_server_name`, `message` e campos opcionais `mode`, `url`, `elicitation_id` e `requested_schema`. A resposta de elicitation é distinta da decisão booleana de uma ferramenta. A conferência desses contratos não comprova que o CLI instalado emitirá todos eles.

O host responde `decline` sem interpretar o formulário, abrir URL, devolver `content`, modificar permissões ou chamar os callbacks de perguntas/aprovação. Isso inclui modos form/url, modo omitido, campos incompletos ou modo futuro: nenhum valor fornecido pelo servidor é aceito. URLs, mensagens, schemas e identificadores internos de elicitation não são interpolados nos eventos nem na resposta; a correlação usa somente o request_id do envelope. [Hooks nativos de elicitation](https://code.claude.com/docs/en/hooks#elicitation) podem resolver a interação antes do host e continuam preservados.

`ExitPlanMode` é uma saída do planejamento, não uma aprovação de entrega do Sintonia. Seu [contrato atual](https://code.claude.com/docs/en/agent-sdk/typescript#exitplanmode) aceita entrada vazia e mantém `allowedPrompts` como campo depreciado/ignorado. O host recusa ambas as formas em leitura e escrita, com diagnóstico específico, sem alterar o acesso da conversa. A chefia continua usando propostas estruturadas, revisão de planos e confirmação pelo usuário. Nenhum suporte de saída de planejamento ou de trabalho em background foi acrescentado.

As recusas são registradas localmente antes da resposta. Um resultado final success, inclusive sem permission_denials do CLI, permanece Blocked. Uma Write posterior conserva o acesso capturado e exige sua própria decisão em escrita; em leitura é negada. Elicitation não herda uma aprovação de Write pendente e não bloqueia a leitura dos eventos. Limites existentes de IDs, quantidade de pedidos, cancelamento e encerramento permanecem aplicados.

## Validação

29 casos novos com adaptador real e processo local de teste: form/url/modo omitido/futuro/entrada incompleta; leitura/escrita; ausência de callbacks ou dados devolvidos; diagnóstico sem conteúdo nativo; ExitPlanMode vazio/com allowedPrompts; controle desconhecido; subtype ausente/inválido e IDs vazios/excessivos; duplicação/pedido após resultado terminal; Write posterior e intercalada com streaming. O processo confere a resposta correlacionada e anuncia sucesso sem repetir a negativa, para verificar que o host conserva Blocked.

Seleção Release: 189 casos aprovados, incluindo os 29 novos e 160 regressões de protocolo, perguntas, diagnósticos, persistência, fila e concorrência. Os 29 novos também passaram em Debug, sem falhas/ignorados. Builds Debug/Release sem avisos/erros; WPF --questions Release aprovado com processos de teste e zero erros de binding. Release local atualizado em artifacts/app, com abertura/encerramento normal conferidos. As suítes de Git não afetadas não foram repetidas integralmente.

```powershell
rtk proxy dotnet build Sintonia.sln -c Release --no-restore
rtk proxy dotnet test tests/Sintonia.Infrastructure.Tests -c Release --no-build --filter "FullyQualifiedName~ClaudeUnsupportedControlTests"
rtk proxy dotnet run --project tests/Sintonia.Desktop.SmokeTests -c Release --no-build -- --questions artifacts/control-question-smoke-release
```

Nenhum SDK/runtime novo, schema, formulário de elicitation ou configuração global foi acrescentado. M1 conserva as lacunas de emissão real das recusas, outros MCPs/autenticações e acompanhamento de background. Próximo incremento: definir uma prova local de elicitation com servidor MCP de teste, investigando primeiro um caminho sem inferência antes de qualquer turno real.
