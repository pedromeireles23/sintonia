# Preparação das integrações — M1

Verificado em 08/10/2026, no Windows desta sessão. A preparação inicial abaixo registra detecção/ajuda. A seção final registra as provas reais posteriores; M1 continua com limitações de permissões/extensões.

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

Nessa preparação inicial nenhuma inferência foi realizada. O painel WPF continua simulado até o incremento de conexão à UI.

## Provas reais posteriores — 08/10/2026

- Schema estável gerado pelo executável instalado; handshake initialize/initialized e account/read comprovados. Login ChatGPT exigido antes de cada turno, modelProvider openai e política efetiva conferidos. Catálogo retornou sete modelos e inventário de skills/MCPs, sem habilitar opt-in experimental.
- Codex `gpt-6.1-sol` e Claude `claude-opus-5`: cada um leu amostra.txt em caminho com espaços/acentos, confirmou soma 25 e marcador SINTONIA-7341. Segundo turno, na mesma sessão, recuperou ambos sem ferramentas. Evidências locais ignoradas em artifacts/provider-probes.
- Interrupção real após primeiro delta de texto comprovada nos dois provedores, em uma chamada finita cada. Adaptadores encerram processos/filhos e reportam cancelamento; não tratam saída parcial como sucesso.
- Codex: sandbox elevado falhou em `apply deny-read ACLs`. Override local `windows.sandbox="unelevated"` usa o isolamento oficial por token restrito, sem modificar configuração global. command/exec readOnly leu a amostra e negou Set-Content: exit não zero e arquivo ausente. Política on-request e sandbox readOnly/workspaceWrite; autorizações recebem apenas accept por ação mediante callback explícito, nunca bypass geral. Fonte: [sandbox Windows](https://developers.openai.com/codex/windows/).
- Claude: print/stream-json com verbose/partial messages, stdin UTF-8, session-id/resume. Leitura usa plan; escrita usa manual + permission-prompts none, respeitando regras/hooks existentes. Falta host interativo Claude: ações que dependerem de resposta são recusadas e resultado com permission_denials fica bloqueado. Não equivale a isolamento do sistema operacional. Fonte: [execução programática](https://code.claude.com/docs/en/headless).
- Claude carregou quatro plugins e skills; pixellab e Claude Docs conectaram, diversos conectores exigem autenticação e Google Calendar/Gmail de plugins falharam. Codex anunciou skills e MCPs locais. Inventário não comprova funcionamento de cada ferramenta nem paridade desktop.
- 45 testes automáticos sem modelos: 17 Core e 28 Infrastructure. Incluem protocolo, respostas correlacionadas, prompts extensos/acentos, retomada, assinatura incompatível, política insegura, evento adicional, resultado ausente/falha, cancelamento, recusa e autorização explícita.

Comandos opcionais e finitos (real/interrupt consomem a quota da assinatura):

```powershell
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -- handshake
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -- sandbox readOnly
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -- real Codex
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -- real Claude
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -- interrupt Codex
rtk proxy dotnet run --project tools/Sintonia.Diagnostics -- interrupt Claude
```

Não executar as provas reais em loops/CI. Próximo incremento: conectar projetos/chat/sessões persistentes e completar host de permissões Claude.
