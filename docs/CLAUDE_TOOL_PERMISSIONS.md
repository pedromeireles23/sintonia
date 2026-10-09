# Prova real de permissões Bash/Edit

Em 09/10/2026, o host do Sintonia recusou e autorizou ações reais de Bash e Edit no Claude Code `2.1.277`, modelo `claude-opus-5`, por assinatura. Foram quatro turnos: dois por ferramenta, retomando a mesma sessão em cada par. O callback recebeu a prévia, conferiu parâmetros e respondeu por ação, sem regras permanentes ou bypass.

## Resultado observado

| Etapa | Prévia recebida | Decisão | Resultado do adaptador | Conferência de arquivos |
| --- | ---: | --- | --- | --- |
| Bash: negar | Uma, com comando exato | Recusar | Blocked | `negado.txt` e `permitido.txt` ausentes |
| Bash: permitir | Uma, com comando exato | Autorizar uma ação | Completed | Somente `permitido.txt`, bytes UTF-8 do marcador exato; `negado.txt` ausente |
| Edit: negar | Uma, com caminho/strings exatos | Recusar | Blocked | Os dois arquivos iniciais preservados byte a byte |
| Edit: permitir | Uma, com caminho/strings exatos | Autorizar uma ação | Completed | Só `permitido.txt` alterado para o marcador; `negado.txt` continua original |

Bash executou um comando fixo `rtk proxy python -c ...` que escreve somente um marcador no arquivo previsto. Para Edit, o prompt exige leitura prévia do seu arquivo e troca única de uma string conhecida. Pastas com espaços/acentos e os efeitos foram conferidos pelo diagnóstico; ele não executa o comando no lugar do agente. Foram duas recusas e duas autorizações, sem pedido adicional, repetição de ação, troca de modelo ou configuração global. Nenhuma inferência de planejamento.

O arquivo `Bash-evidence.json` registra as duas etapas Bash, e `Edit-evidence.json` as etapas Edit. Incluem estado, contagens, outcomes, sessão/modelo, hashes SHA-256 e tokens informados pelo adaptador. Cada etapa termina com uma conferência da lista de entradas e dos bytes dos arquivos, não apenas do texto produzido pelo modelo. Os arquivos recusados continuam verificáveis após os turnos autorizados.

Tokens informados pelo turno principal: 19.304/24.430 no par Bash e 28.559/37.521 no par Edit, total 109.814. Cache já está incluído na contagem de entrada; não é somado outra vez. São medições reportadas nesta prova, sem conversão para quota/custo ou garantia de contabilizar todo trabalho externo. [Contrato das medições](RUN_TOKEN_USAGE.md).

Pasta local da prova concluída, ignorada pelo Git: `artifacts/provider-probes/Bash Edit Claude 37534168-52b6-4d8a-bbe3-6ced1a2e0553`. A primeira preparação criou outra pasta e parou na consulta de login, antes de enviar qualquer prompt: uma opção de ferramentas com vários argumentos consumia os argumentos `auth/status`. O prefixo agora termina com a opção de valor único `--max-turns 3`; ambas as combinações de flags passaram na consulta de autenticação sem modelos. A pasta inicial permanece preservada, com estado Interrupted e sem etapas concluídas.

## Execução opcional e finita

```powershell
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release -- permissions Claude BashEdit
```

Esse comando consome a assinatura: no máximo quatro turnos, três iterações por turno e 90 segundos por envio, com prazo total de seis minutos. Cada invocação cria uma pasta exclusiva; não reaproveita uma pasta já existente. Falha na negativa interrompe antes da autorização. Qualquer divergência, cancelamento ou falha encerra o comando, conservando evidências parciais/arquivos, sem retry ou reenvio automático. Não executar em loop, teste automatizado ou CI.

As regras `ask` são fornecidas somente aos processos do diagnóstico. Bash recebe apenas a ferramenta nativa Bash; Edit recebe Read/Edit. MCPs são retirados das ferramentas utilizáveis nessa prova por `--disallowedTools mcp__*`. As demais personalizações nativas continuam carregando; as restrições de ferramentas do teste não são opções padrão dos adaptadores do produto. `--settings` e `--max-turns` seguem a [referência oficial do CLI](https://code.claude.com/docs/en/cli-reference); a ordem deny/ask/allow é descrita na [documentação de permissões](https://code.claude.com/docs/en/permissions).

A autorização confere provedor, ferramenta, pasta efetiva e todos os parâmetros relevantes. Edit precisa apontar para o arquivo previsto, com strings exatas e sem substituição global. Bash precisa conter o comando literal previsto, sem background, desativação de isolamento, parâmetros desconhecidos ou timeout acima de dez segundos. Campos JSON duplicados são recusados. Um pedido divergente é negado e não conta como prova bem-sucedida. Esse callback específico aprova somente ações fornecidas pelo próprio diagnóstico na sua pasta; não aprova tarefas de projetos do usuário.

## Conferir novamente sem modelos

```powershell
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release -- verify-permissions "artifacts/provider-probes/Bash Edit Claude 37534168-52b6-4d8a-bbe3-6ced1a2e0553"
```

O verificador lê evidências/arquivos, confere etapas, contagens, sessão/modelo, hashes da negativa, conteúdo final e ausência de entradas inesperadas. Não instancia provedores, retoma sessões ou envia prompts. Foi executado após a prova, com aprovação. Conserva os arquivos para conferência posterior; alteração de artefato faz a verificação falhar.

## Validação e limites

Dezoito testes novos usam um provedor simulado e efeitos locais após o callback, sem shell, rede ou modelos. Exercitam recusa/autorização, retomada, pasta já existente, efeito após recusa, prévia divergente, pedido duplicado, flags de background/isolamento, timeout/troca de parâmetros e artefatos alterados. O projeto de testes compila o mesmo arquivo fonte do diagnóstico. Também passaram 39 regressões do protocolo de conversa, total de 57 casos selecionados em Release. Os dezoito novos passaram novamente após finalizar os parâmetros Bash. Builds Debug/Release sem avisos ou erros.

A prova exercita os adaptadores e o host reais; a UI WPF conserva as provas anteriores com processos de teste. Não demonstra isolamento do sistema operacional no Claude nem funcionamento de qualquer comando/edição arbitrários. Configurações e hooks existentes podem decidir antes do host; ausência da prévia esperada faz o diagnóstico falhar. Uma prova posterior confirmou recusa/autorização de leitura pública pelo [Microsoft Learn MCP](CLAUDE_MCP_PERMISSIONS.md). Demais MCPs, controles especiais como AskUserQuestion/ExitPlanMode, elicitation e acompanhamento de background continuam parciais/não suportados. M1 conserva essas lacunas. Próximo incremento: perguntas de escolha do Claude na central, inicialmente com processos de teste. [Comparação de extensões](PROVIDER_EXTENSIONS.md) e [provas anteriores](PROVIDER_PROBES.md).
