# Prova real de permissão MCP de leitura pública

Em 09/10/2026, o adaptador e o host do Sintonia recusaram e autorizaram uma leitura real pelo MCP público do Microsoft Learn, com Claude Code `2.1.277`, modelo `claude-opus-5`, por assinatura. Dois turnos, mesma sessão/modelo, uma solicitação por turno e nenhum reenvio. A conferência usa o resultado correlacionado da ferramenta, além da decisão do host e do resultado final do provedor.

## Escopo e resultado

Servidor de sessão `sintoniaLearn`, endpoint público `https://learn.microsoft.com/api/mcp`, ferramenta `mcp__sintoniaLearn__microsoft_docs_fetch`. Único parâmetro autorizado: `url` exatamente igual a `https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/task-cancellation`. O serviço e o transporte HTTP estão na [referência oficial Microsoft Learn](https://learn.microsoft.com/en-us/training/support/mcp-developer-reference); o parâmetro `url` consta no [repositório oficial](https://github.com/MicrosoftDocs/mcp).

| Etapa | Pedidos / autorizações | Resultado do adaptador | Resultado correlacionado da ferramenta |
| --- | --- | --- | --- |
| Recusar | 1 / 0 | Blocked | Erro nativo, 62 caracteres, sem o conteúdo esperado |
| Autorizar | 1 / 1 | Completed | 7.261 caracteres, sem truncamento, com título e referências da página esperada |

Cada pedido teve a URL/parâmetros conferidos. As duas etapas anunciaram uma única chamada de ferramenta. A negativa retornou `is_error: true`. Na autorização, o campo veio ausente; a prova não o converte em uma garantia de sucesso: exige também o conteúdo esperado e conclusão do turno, sem negativas. O conteúdo observado incluía o título “Task cancellation” e os identificadores `CancellationTokenSource` e `OperationCanceledException`; esses critérios não foram fornecidos como resposta esperada ao modelo. Não houve pesquisa adicional ou ferramenta de escrita.

Tokens informados pelo turno principal: 13.757 e 18.019, total 31.776, com cache já incluído na entrada. Sem conversão para quota/custo ou soma duplicada de cache. [Contrato de medições](RUN_TOKEN_USAGE.md).

Pasta concluída, ignorada pelo Git: `artifacts/provider-probes/MCP Claude 04a61e9d-9ea9-450f-b15e-1a4af6659719`. O resumo `mcp-evidence.json` guarda contagens, etapas, outcomes, sessão/modelo/tokens, identificador/nome da ferramenta, erro informado, tamanho, hash SHA-256 e critério de conteúdo. Não guarda corpos das respostas, texto do modelo, configurações nativas, credenciais ou stderr. O SHA-256 autorizado foi `D704BD8A0AEF549C5882942989911BDB9B171390E747E7CD61C113B253AA4C37`.

## Diagnóstico opcional e finito

```powershell
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release -- permissions Claude Mcp
```

O comando usa a assinatura: no máximo dois envios, três iterações por envio, 90 segundos por envio e seis minutos no total. Antes dos prompts, confere assinatura e inicializa o MCP sem modelo, com prazo de 50 segundos e até três observações de estado separadas por dez segundos, no mesmo processo, sem reconexão. Exige somente o servidor público e a ferramenta esperada conectados. Na consulta real, o servidor passou de pending a connected e anunciou três ferramentas de leitura. Falha antes disso encerra sem enviar prompt. Não executar em loop, CI ou testes automatizados.

O processo da prova recebe `--mcp-config` com esse endpoint, `--strict-mcp-config`, ferramentas nativas vazias e as duas outras ferramentas do servidor em `--disallowedTools`. A regra `ask` corresponde somente à ferramenta escolhida. A configuração é fornecida em argumentos ao processo, sem editar as instalações existentes. Essas restrições pertencem ao diagnóstico; a integração padrão continua carregando as personalizações nativas. A [referência do CLI](https://code.claude.com/docs/en/cli-reference) e as [regras MCP](https://code.claude.com/docs/en/permissions#mcp) descrevem essas opções.

O callback recusa ferramenta/provedor/pasta divergentes, URL diferente, campos extras, JSON inválido ou duplicado, e qualquer pedido adicional. Só autoriza o primeiro pedido exato da segunda etapa. Falha na recusa impede o segundo turno; qualquer outra divergência, erro ou cancelamento para conservando a pasta e eventual evidência parcial. Não há retry, reenvio ou retomada automática de prova parcial.

## Observação e conferência posterior

O adaptador Claude aceita um observador opcional de resultados. Sem observador, conserva o comportamento anterior e não interpreta/persiste os corpos de `tool_result`. Com observador, o parser correlaciona chamadas e resultados por `tool_use_id`, ignora histórico sem correlação, resultados repetidos e mensagens de subagentes, e extrai somente texto. Limites: 1.024 chamadas, 512 caracteres por nome/identificador e 128.000 caracteres por resultado, com truncamento explicitamente informado. Conteúdo fica em memória no callback; na prova é imediatamente convertido no resumo acima. Não há mudança no histórico SQLite ou UI WPF.

```powershell
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release -- verify-mcp "artifacts/provider-probes/MCP Claude 04a61e9d-9ea9-450f-b15e-1a4af6659719"
```

O verificador confere a consistência das duas etapas, contagens, resultados e retomada, sem rede, provedores ou modelos. Foi aprovado após a prova. Como os corpos não são persistidos, não recalcula o hash do conteúdo original nem refaz a consulta pública; a conferência de conteúdo aconteceu durante a execução.

## Validação e limites

Passaram 85 casos selecionados Release: 28 novos de diagnóstico/observação e 57 regressões do protocolo/Bash/Edit. Usam simulação ou processos locais de teste, sem rede/modelos. Cobrem URL/JSON divergentes, duplicação, ausência do host/resultado, erro ou página incorreta, truncamento, ferramenta extra, retomada divergente, parada sem retry, correlação, subagentes, limites e preservação do comportamento padrão. Builds Debug/Release sem avisos/erros. A seleção foi reexecutada após finalizar os limites; a prova real usou somente os dois turnos registrados.

A prova cobre uma ferramenta de um servidor público, por meio do adaptador/host reais. Não demonstra funcionamento dos MCPs autenticados instalados, escrita externa, ferramentas arbitrárias ou paridade desktop. A UI WPF conserva suas provas anteriores com processos de teste. Regras/hooks podem decidir antes do host; a ausência da prévia esperada faz o diagnóstico falhar. `AskUserQuestion`, `ExitPlanMode`, elicitation e acompanhamento de background permanecem parciais/não suportados. Próximo incremento: perguntas de escolha do Claude na central, começando pelo contrato e processos de teste sem modelos. [Compatibilidade](PROVIDER_EXTENSIONS.md) e [Write/Bash/Edit](CLAUDE_TOOL_PERMISSIONS.md).
