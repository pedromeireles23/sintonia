# Recusa real de ExitPlanMode

Estado em 13/10/2026: prova real finita concluída com Claude Code **2.1.277** / **claude-opus-5** por assinatura. Uma chamada ExitPlanMode foi recusada pelo host em leitura, seu resultado correlacionado anunciou erro e o adaptador devolveu **Blocked**. Nenhum callback de aprovação/pergunta foi chamado e nenhuma entrada inesperada surgiu na pasta. A recusa continua sendo o comportamento do produto; a prova não acrescenta aprovação ou execução de planos. [Contratos do host](CLAUDE_CONTROLS.md).

## Escopo e critérios

`plan-exit Claude` cria pasta exclusiva, confere versão/login e initialize sem prompt antes de um único envio real. Prazo de 90 segundos por execução e três minutos para o comando inteiro, até duas iterações internas do CLI, sem retry/reenvio. Ferramentas disponíveis restritas a ExitPlanMode, MCP vazio e regra ask apenas nos processos do diagnóstico. Acesso enviado ao adaptador é ReadOnly; configurações, regras/hooks globais e autenticação nativa são preservados, sem bypass.

O [contrato da ferramenta](https://code.claude.com/docs/en/agent-sdk/typescript#exitplanmode) permite entrada vazia; o pedido usa `{}` e manda encerrar após a negativa, sem escrever plano, executar proposta, fazer perguntas ou chamar outras ferramentas. A [avaliação oficial de permissões](https://code.claude.com/docs/en/agent-sdk/permissions#how-permissions-are-evaluated) permite que regras/hooks resolvam pedidos antes do host. A prova exige o diagnóstico de recusa produzido pelo host, portanto uma negativa anterior sozinha não satisfaz o aceite.

Aceitação exige um envio, uma chamada ExitPlanMode, um diagnóstico específico do host, uma negativa local, zero callbacks de aprovação/pergunta, resultado de ferramenta correlacionado com is_error true e mensagem da recusa, e Outcome Blocked com somente ExitPlanMode entre as negativas. Chamadas/pedidos extras, resultado ausente/divergente/truncado, término Completed, cancelamento e arquivo inesperado interrompem a prova. O texto final do modelo não é usado como autorização ou comprovação da negativa.

Uma pasta usada impede novo envio. Falha conserva estado Interrupted e contagens/sessão/modelo/tokens observados quando disponíveis, com categoria segura, sem reenvio automático. `verify-plan-exit` confere consistência do artefato sem CLI/modelos; não reobtém o conteúdo cujo hash foi registrado.

## Evidência de 13/10/2026

| Conferência | Resultado |
| --- | --- |
| Estado do diagnóstico / resultado da execução | Completed / Blocked |
| Envios / chamadas ExitPlanMode | 1 / 1 |
| Diagnósticos de recusa do host / negativas locais | 1 / 1 |
| Callbacks de aprovação / perguntas | 0 / 0 |
| Resultado correlacionado | erro, 62 caracteres, hash SHA-256 e recusa do host encontrados, sem truncamento |
| Tokens informados do turno principal | 16.205 entrada + 229 saída = 16.434 |

Cache read (8.020) e cache write (8.181) já estão incluídos na entrada, sem soma duplicada. Não estimar quota da assinatura por essa medição. Artefato ignorado pelo Git em `artifacts/provider-probes/Saída planejamento Claude 450fea43-7cfb-4268-ad1d-a762f531ff9e/plan-exit-evidence.json`. Guarda contagens, versão, sessão/modelo, tokens informados, estado/negativas, tamanho/hash/critério e categoria de falha. Não guarda corpo da resposta/ferramenta, stderr, identidade de conta ou credenciais. Verificador posterior aprovado; nenhum envio real repetido após sucesso.

```powershell
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release --no-build -- plan-exit Claude
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release --no-build -- verify-plan-exit "pasta da prova"
```

## Validação e limites

22 casos novos Debug/Release: adaptador real com processo de teste, recusa/resultados sem depender da negativa no resultado do CLI, pasta usada e artefato alterado, leitura, limite de iterações/ferramentas, chamada/diagnóstico/negativa ausentes ou divergentes, callbacks inesperados, resultado permitido/incorreto/truncado/extra, execução Completed, cancelamento, arquivo extra e falha OAuth sem mensagens internas. Seleção Release final: 229 casos aprovados, 22 novos e 207 regressões, sem falhas/ignorados; builds Debug/Release sem avisos/erros. Adaptadores, schema e UI preservados; alterações restritas ao diagnóstico/testes/documentação. Suítes de Git/WPF não afetadas não foram repetidas neste incremento. Release local reconferido com abertura/encerramento normal, 239 referências locais válidas em 36 documentos e diff sem erros de whitespace.

A prova cobre uma recusa pelo adaptador/host reais em leitura; não usa o formulário WPF nem demonstra isolamento do sistema operacional, aprovação de saída ou execução de planos. Contratos de entrada vazia/allowedPrompts e escrita conservam testes de processo anteriores. Chefia do Sintonia continua propondo tarefas estruturadas para revisão explícita. URL mode, outros MCPs/autenticações e acompanhamento de background permanecem parciais. Próximo incremento: prova de recusa URL mode com MCP local, buscando a mesma rota sem modelo já comprovada em [elicitation form](CLAUDE_ELICITATION.md).
