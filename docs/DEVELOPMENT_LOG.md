# Registro de desenvolvimento

Registro resumido de incrementos e pontos de retomada. Consulte `git log` para hashes e cronologia exata dos commits.

## 08/10/2026 — Preparação do repositório

- Repositório inicialmente vazio clonado na pasta existente do usuário.
- Nome Sintonia adotado conforme pasta e repositório.
- Stack definida como C#/.NET 10, WPF e MVVM.
- Contexto, roadmap, arquitetura e instruções para Codex e Claude criados.
- Política: commits pequenos de incrementos validados e atualização do contexto ao avançar.
- Validação: revisão documental; nenhum build ou teste .NET ainda, pois a solução está pendente.
- Próximo incremento: M0, solução .NET e primeiro painel WPF com demonstração identificada.

## 08/10/2026 — Solução e núcleo do M0

- Projetos Core, Infrastructure, Desktop e testes criados com .NET 10, nullable e warnings tratados como erros. Dependências NuGet registradas em lockfiles.
- Política pura de dependências/concorrência e coordenador com reserva atômica, aprovação, cancelamento, falha e novas tentativas preservadas em memória.
- Cancelamento conserva a vaga até a tentativa terminar; entregas em revisão não liberam dependentes. Troca de provedor não modifica tentativas ativas.
- Validação: 17 testes xUnit aprovados; build da solução. WPF ainda contém somente a janela base, sem painel ou integração real.
- Próximo incremento: painel MVVM em português e adaptadores explicitamente simulados.

## 08/10/2026 — Painel WPF e conclusão do M0

- Painel MVVM em português com funções editáveis, fila, entrega/eventos e histórico de sessões por tentativa. Simulação sinalizada na janela, sessões, eventos e resultados.
- Dois adaptadores simulados com atraso assíncrono/cancelável. O fluxo aprova cinco entregas e distribui as dependentes; nenhum CLI, modelo ou arquivo do jogo é utilizado.
- Validação: build sem avisos/erros, 17 testes do núcleo e teste executável da janela nativa com o fluxo completo e zero erros de binding. Capturas revisadas em tamanho normal e mínimo; painel de revisão com rolagem e ações acessíveis.
- Limites: estado somente em memória, cenário fixo, sem abertura de projeto, persistência ou integrações reais. Rejeição devolve a tarefa à fila; instruções de ajuste entram com o fluxo real.
- Próximo incremento: preparação limitada do M1 com detecção e conferência das interfaces instaladas; depois prova real pequena de cada provedor.

## 08/10/2026 — Preparação limitada do M1

- Diagnóstico .NET consulta somente versão/ajuda, sem shell, credenciais ou inferência. Detecção distingue ausência, layout incompatível e falha. Streams simultâneos limitados e cancelamento/timeout com encerramento da árvore do processo.
- Detectados Codex CLI `0.162.0-alpha.2` nativo e Claude Code `2.1.277` nativo por instalação npm. Interfaces anunciadas e fontes oficiais registradas em `docs/PROVIDER_PROBES.md`; flags não foram apresentadas como integração pronta.
- Validação: 11 testes de Infrastructure e 17 do Core aprovados, incluindo argumentos literais, saída excessiva, falha, timeout com filho e cancelamento. Build da solução e diagnóstico real limitado a versão/ajuda.
- Limites: nenhuma inferência, handshake, parsing nativo, retomada, negativa de permissão ou extensão foi validada. O painel continua simulado e o M1 está parcial.
- Próximo incremento: handshake stdio sem turno, contratos/parsing com fixtures e uma prova real pequena de cada provedor com permissões verificadas antes de conectar à janela.

## 08/10/2026 — Direção de central geral de projetos

- Registrado o pedido de múltiplos projetos, chat com provedor/modelo, chefia configurável e abertura/retomada de sessões no Sintonia. M2/M4 ajustados; M1 continua o próximo incremento de código.
- Registrado o padrão de usar assinaturas pelos CLIs, sem fallback automático para API keys. Comandos de status consultados na conversa anterior informaram ChatGPT no Codex e `claude.ai`/Pro no Claude, sem exposição de identidade/segredos ou inferência.
- Fontes oficiais confirmam interfaces para escolha de modelo e retomada; capacidades na versão instalada ainda exigem prova. Abertura nos aplicativos gráficos originais não foi prometida como recurso validado.
- Validação: revisão documental, links e consistência entre escopo, contexto, arquitetura, decisões e roadmap. Nenhuma alteração de código ou teste sem relação com a documentação.
- Próximo passo: concluir a prova limitada das integrações por assinatura; depois implementar projetos/chat/sessões persistentes.

## 08/10/2026 — Conversas reais e retomada por assinatura

- Contrato de conversa e adaptadores Codex/Claude com eventos, identificador nativo, modelo, login por assinatura obrigatório, limites, resultado final, falha/bloqueio e cancelamento. Prompts via protocolo/stdin, sem interpretação de shell.
- Provas reais finitas: cada provedor leu a amostra, conferiu soma 25/marcador e retomou a mesma conversa sem reler arquivos. Modelos `gpt-6.1-sol`/`claude-opus-5`. Interrupção após início do stream verificada em ambos. Amostras/evidência local ignoradas em artifacts.
- Sandbox elevado Codex falhou nas ACLs; modo oficial unelevated, restrito ao processo do cliente, permitiu leitura e bloqueou escrita. Nenhuma configuração global alterada ou bypass adotado. Claude carregou skills/plugins/MCPs; alguns servidores precisam de autenticação ou falharam.
- Validação: build isolado para preservar janela demonstrativa aberta, sem avisos/erros; 45 testes xUnit (17 Core + 28 Infrastructure), com correlação, texto extenso/acento, falha, recusa, autorização explícita, retomada e interrupção simulada. Chamadas reais não integram a suíte automática.
- Limites: autorização interativa do Claude e equivalência completa de extensões pendentes. A janela permanece demonstrativa até o próximo incremento.
- Próximo passo: cadastro/alternância de projetos, chat central e sessões com histórico SQLite e retomada na janela.
