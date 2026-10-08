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
