# Preparação das integrações — M1

Verificado em 08/10/2026, no Windows desta sessão. Este registro comprova **detecção e ajuda local**, não execução de modelos ou conclusão do M1.

## Diagnóstico reproduzível

```powershell
rtk proxy dotnet run --project tools/Sintonia.Diagnostics
```

O diagnóstico consulta somente `--version` e ajuda. Cada processo tem prazo de 10 segundos, leitura simultânea/assíncrona de stdout e stderr e retenção máxima de 64 Ki caracteres por stream. O programa inteiro tem prazo de 25 segundos e aceita cancelamento. Não imprime configuração, ambiente, credenciais nem stderr bruto. Não verifica login ou quotas.

O resolvedor usa entradas absolutas do PATH e não executa wrappers de shell. Suporta executáveis nativos, o layout npm nativo observado do Claude e scripts npm conhecidos via Node.js. Um wrapper diferente recebe diagnóstico de incompatibilidade; não é executado por tentativa. Instalações fora desses layouts precisarão de suporte explícito.

## Resultado observado

| Provedor | Versão | Instalação efetivamente consultada | Interface anunciada |
| --- | --- | --- | --- |
| Codex | `0.162.0-alpha.2` | Executável nativo da instalação local do Codex | `app-server --listen`, `--stdio`; comando rotulado experimental pela ajuda |
| Claude Code | `2.1.277` | Executável nativo em `node_modules/@anthropic-ai/claude-code/bin/claude.exe`, resolvido a partir do wrapper npm | `--print`, `--input-format`, `--output-format`, `--resume`, `--session-id`, `--append-system-prompt`, `--permission-mode`, `--permission-prompts`, `--max-budget-usd` |

As consultas retornaram código 0. A ajuda do Claude anuncia `stream-json` nos formatos de entrada/saída e `host`/`none` para respostas a permissões. Esses fluxos **ainda não foram exercitados**. A ajuda menciona `--permission-prompt-tool` na explicação de `host`; o uso dessa opção depende de uma prova do fluxo.

## Documentação e próxima prova

O Codex documenta a inicialização por `initialize` seguido de `initialized`, mensagens por linha e respostas correlacionadas por ID. A API tem campos experimentais que exigem opt-in; a primeira prova deve permanecer na superfície estável e validar os contratos contra a versão instalada. Nada desse protocolo foi enviado nesta preparação. Fonte: [Codex App Server](https://learn.chatgpt.com/docs/app-server).

O Claude documenta `--append-system-prompt`, saída estruturada, sessão explícita, retomada e modos de permissão. A próxima prova deve acrescentar instruções da função ao contexto do provedor e tratar pedidos/negativas, sem bypass global. Flags presentes na ajuda não comprovam que os eventos, o host de permissões ou a retomada funcionem no Sintonia. Fonte: [referência do CLI](https://code.claude.com/docs/en/cli-reference).

O modo `--bare` omite descobertas automáticas de contexto e extensões e altera o uso da autenticação. Não foi usado nesta preparação e não será o padrão do produto, pois o usuário quer preservar suas extensões compatíveis. A documentação atual já descreve versões posteriores à instalada; comportamentos específicos precisam ser verificados localmente. Fonte: [execução programática](https://code.claude.com/docs/en/headless).

Próximo incremento verificável:

1. Validar handshake stdio do Codex, sem iniciar turno, e gerar/conferir o schema da versão instalada.
2. Implementar parsing limitado de eventos com fixtures de sucesso, erro, interrupção, eventos desconhecidos e pedidos de permissão.
3. Preparar uma pasta de teste e fluxo de permissões do Claude, preservando a configuração compatível. Conferir modelos/acesso sem ler credenciais.
4. Fazer uma tarefa pequena por provedor com limite de tempo/tentativas e, no Claude, orçamento quando aplicável; verificar resultado e negativas. Testar retomada e cancelamento antes de conectar os adaptadores reais à janela.
5. Comparar as extensões esperadas com as efetivamente carregadas. Recursos exclusivos dos aplicativos desktop permanecem sem garantia.

## Validação automatizada desta preparação

11 testes de Infrastructure, usando somente arquivos e processos de teste: detecção nativa/npm, wrapper incompatível, ausência, falha de lançamento, versões/ajuda sintéticas, argumentos literais com espaços/acentos/sintaxe de shell, falha com stderr, inundação simultânea dos dois streams, timeout com encerramento de filho e cancelamento pelo chamador. Os 17 testes do Core também passaram.

Nenhuma inferência real, sessão nativa, configuração de permissões, autenticação, skill, plugin ou MCP foi validado. O painel WPF continua inteiramente simulado.
