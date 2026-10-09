# Compatibilidade de skills, plugins e MCPs

Comparação realizada em 09/10/2026 no projeto Sintonia, com Codex CLI `0.162.0-alpha.2` e Claude Code `2.1.277`. As consultas usam os executáveis e as configurações nativas que os adaptadores encontram no PATH. Nenhum prompt ou turno de modelo foi enviado. Autenticação permanece nos mecanismos dos provedores; o Sintonia não lê arquivos de credenciais nem altera a configuração global.

## Consulta reproduzível

Na pasta do projeto a inspecionar, usar o diagnóstico já compilado, ou executar a partir deste repositório:

```powershell
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release -- extensions Codex
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release -- extensions Claude
```

O prazo total é de 60 segundos por provedor. Os processos drenam stdout/stderr de forma assíncrona e são encerrados com seus filhos ao terminar ou cancelar. A consulta Claude não envia mensagem `user` e desativa a persistência de sessão; o Codex não inicia thread/turno. A inicialização nativa pode iniciar MCPs, carregar hooks e acessar serviços conforme a configuração existente. Não há chamadas explícitas de ferramentas MCP, instalação de plugins, alteração de permissões ou tentativas automáticas de login.

O JSON projeta somente tipo, nome, habilitação, escopo conhecido, estado conhecido, modalidade de autenticação, quantidade de ferramentas, vínculo com plugin quando disponível e indicação de erro. Não inclui configurações MCP, URLs, argumentos, ambientes, headers, descrições de ferramentas, identidade da conta, caminhos ou mensagens internas de erro. `null` significa metadado indisponível; zero ferramentas significa catálogo vazio retornado. `DiscoveryFailed` registra erro informado pelo provedor (`toolsError` ou `error`), sem expor seu texto. Campos/estados novos não são presumidos válidos; valores de estado desconhecidos ficam indisponíveis.

Erros de protocolo, respostas inválidas, cursor repetido, mais de vinte páginas MCP ou cancelamento impedem apresentar a consulta como completa. Ausência de uma categoria é descrita nos avisos: plugins do Codex não são enumerados; comandos Claude não são uma enumeração completa de skills. Os snapshots locais projetados ficam em `artifacts/extension-probes`, ignorados pelo Git. São fotografias deste ambiente, não fixtures ou configuração padrão do produto.

## Contratos e alcance

| Recurso esperado | Codex App Server | Claude Code por stdio |
| --- | --- | --- |
| Skills pessoais/do projeto fornecidas pelo CLI | `skills/list`, com `forceReload`, nomes, habilitação, escopo e proprietário quando informado | `initialize` informa comandos disponíveis; o evento `system/init` das execuções também informa skills quando presente |
| Skills desabilitadas | Mantidas no inventário estruturado; excluídas da lista de capacidades ativas | Não inferidas a partir da lista de comandos |
| Plugins instalados/habilitados | Inventário completo indisponível no contrato estável adotado; proprietário de skill/MCP pode aparecer sem catálogo de plugins | `plugin list --json` informa instalações e habilitação; não prova a carga de cada componente |
| MCPs e ferramentas anunciadas | `mcpServerStatus/list`, todas as páginas, `toolsAndAuthOnly`; separa autenticação, catálogo e estado de sessão quando disponível | Controle `mcp_status`, com estado de conexão e quantidade de ferramentas quando disponível |
| Perfis/instruções | Modelo e instruções adicionais do Sintonia, preservando configuração nativa | Modelo e `--append-system-prompt`, preservando configuração nativa |
| Aprovação de ação | Host para comandos/arquivos com prévia; elicitation MCP é recusado | Host `can_use_tool`, com prévia; controles especiais não suportados são recusados |
| Recursos exclusivos do aplicativo desktop | Sem paridade presumida de UI, sessões, bibliotecas ou ferramentas do host | Sem paridade presumida com Claude Desktop/Cowork, navegação ou interfaces dos plugins |

O App Server documenta paginação de MCPs e descoberta de skills. Também declara `plugin/list` em desenvolvimento e orienta a não usá-lo em clientes de produção; este incremento não habilita opt-in experimental. [Contrato oficial Codex](https://learn.chatgpt.com/docs/app-server).

Claude documenta `plugin list --json` e seus estados de habilitação. A inicialização e o controle `mcp_status` seguem os contratos do Agent SDK, sem acrescentar esse SDK como dependência do Sintonia. Comandos de inicialização e estado MCP são metadados distintos de execução de uma skill. [Plugins Claude](https://code.claude.com/docs/en/plugins/cli-reference), [referência do SDK](https://code.claude.com/docs/en/agent-sdk/python), [protocolo stdio oficial](https://github.com/anthropics/claude-agent-sdk-python/blob/main/src/claude_agent_sdk/_internal/query.py).

## Comparação Codex observada

O catálogo de skills oferecido à conversa Codex desktop desta sessão continha 32 entradas. O App Server usado pelo Sintonia retornou 13 habilitadas: 12 em comum e uma adicional. A comparação usa os nomes daquele catálogo e a resposta nativa; não presume que o catálogo injetado nesta conversa seja a configuração efetiva de outro cliente.

| Grupo esperado nesta conversa | Resultado no App Server |
| --- | --- |
| `imagegen`, `openai-docs`, `skill-creator`, `skill-installer` | As quatro presentes |
| `computer-use:computer-use`, `documents:documents`, `pdf:pdf`, `presentations:Presentations` | As quatro presentes |
| `spreadsheets:Spreadsheets`, `spreadsheets:excel-live-control`, `template-creator:template-creator`, `visualize:visualize` | As quatro presentes |
| As 15 skills `codex-security:*` do catálogo | Ausentes neste inventário de skills; o MCP `codex-security` anunciou ferramentas separadamente |
| `plugin-management:plugin-management`, `sites:sites` | Ausentes neste inventário |
| `work-pets:create-pet`, `work-pets:pets`, `work-pets:update-pet` | Ausentes neste inventário |
| Entrada adicional | `review-agent` |

As 15 entradas de segurança esperadas são `assess-patch-risk`, `attack-path-analysis`, `deep-security-scan`, `define-security-policy`, `finding-discovery`, `fix-finding`, `propose-security-hardening`, `security-diff-scan`, `security-scan`, `threat-model`, `track-findings`, `triage-finding`, `validation`, `verify-fix` e `vulnerability-writeup`, todas com prefixo `codex-security:`. A diferença de descoberta está comprovada; sua causa e a possibilidade de carregar cada skill em outro cliente não foram determinadas. Nenhuma skill foi copiada/injetada para mascarar a diferença.

| MCP anunciado | Ferramentas | Autenticação informada | Erro de descoberta |
| --- | ---: | --- | --- |
| `code-review` | 0 | `unsupported` | Não |
| `codex-security` | 47 | `unsupported` | Não |
| `codex_app` | 0 | `unsupported` | Não |
| `codex_apps` | 255 | `bearerToken` | Não |
| `cua_repl` | 3 | `unsupported` | Não |
| `node_repl` | 4 | `unsupported` | Não |
| `unityMCP` | 0 | `unknown` | Sim |

O estado de conexão de sessão veio indisponível para os sete MCPs; nenhuma thread foi criada. `unsupported` é a modalidade de autenticação reportada, não uma recusa de execução ou de compatibilidade do servidor. O catálogo de 255 ferramentas de `codex_apps` não comprova acesso a cada conta/serviço, nem funcionamento fora do contexto do host desktop. `unityMCP` exige diagnóstico específico antes de anunciar ferramentas utilizáveis. Skills de segurança ausentes e MCP de segurança anunciado são informações independentes.

## Comparação Claude observada

O comando nativo listou três plugins habilitados, todos sincronizados de claude.ai: `browser-use@synced`, `design@synced` e `engineering@synced`. A inicialização informou 82 comandos. Apareceram os sete comandos `design:*`, dez `engineering:*`, `unity-mcp-orchestrator` e treze `anthropic-skills:*`, além de comandos nativos. Essas contagens não representam 82 skills, nem quatro plugins instalados: comandos internos e recursos embutidos podem aparecer sem registro em `plugin list`.

| Componentes esperados dos plugins/configuração nativa | Resultado desta inicialização |
| --- | --- |
| `design` e `engineering` | Comandos com os respectivos namespaces presentes; MCPs correspondentes descobertos, com as limitações abaixo |
| `browser-use` | Plugin habilitado; MCP descoberto com conexão pendente |
| `unity-mcp-orchestrator` | Comando presente; nenhum MCP Unity anunciado nesta consulta Claude |
| Recursos `anthropic-skills:*` | Treze comandos presentes; sem inferir um registro de plugin adicional |
| `pixellab` e Claude Docs | Descobertos com conexão pendente na fotografia inicial |

| Estado MCP inicial | Quantidade | Servidores |
| --- | ---: | --- |
| `needs-auth` | 13 | Design: Slack, Figma, Linear, Asana, Atlassian, Notion, Intercom; Engineering: GitHub, PagerDuty, Datadog; claude.ai: Google Drive, Gmail, Google Calendar |
| `failed` | 4 | Google Calendar e Gmail de cada um dos plugins Design/Engineering |
| `pending` | 3 | Browser-use, Pixellab e claude.ai Claude Docs |

Quantidade de ferramentas veio indisponível para os vinte servidores. Não há polling ou reconexão automática: `pending` não é tratado como conectado ou com falha. Uma prova histórica registrou quatro plugins no evento de execução e Pixellab/Claude Docs conectados; esta fotografia usa outros contratos e outro instante, e não substitui a prova histórica nem demonstra perda desses recursos.

## Limites e próximo passo

Instalação, habilitação, descoberta, conexão e uso efetivo são evidências diferentes. Os adapters continuam usando o CLI nativo e seus recursos existentes; não traduzem plugins entre provedores nem recriam interfaces desktop. Configurações extras de sessão como `--plugin-dir`/`--mcp-config` não têm seleção própria na interface do Sintonia. A enumeração completa de plugins Codex e de skills Claude continua indisponível neste diagnóstico, com avisos explícitos.

A comparação de M1 está registrada, incluindo as diferenças encontradas. Não foi comprovado o funcionamento das ferramentas anunciadas, a causa das vinte skills Codex ausentes, nem a resolução das autenticações/falhas MCP. O próximo incremento de M1 é uma prova real finita de negativa/autorização Claude para Bash/Edit, com arquivos e efeitos verificáveis; depois, uma prova MCP delimitada conforme servidor disponível. Recursos que exigem `AskUserQuestion`, `ExitPlanMode`, elicitation, controles especiais ou acompanhamento de background permanecem incompatíveis/parciais na integração atual. [Provas e permissões existentes](PROVIDER_PROBES.md).

Validação: quinze novos casos de inventário e 57 regressões de protocolo/uso aprovados em Release, total de 72. Cobrem páginas adicionais, cursor repetido, limite de páginas, valores futuros, disabled skills/plugins, distinção comando/skill, projeção sem detalhes internos, recusa de controle, saída antecipada e cancelamento. As consultas reais de metadados foram repetidas uma vez após finalizar os campos projetados; nenhum modelo chamado.
