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
