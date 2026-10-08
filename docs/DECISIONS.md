# Decisões técnicas

## 001 — Aplicativo Windows em .NET

Data: 08/10/2026. Estado: adotada.

Escolha: C#/.NET 10 com WPF e MVVM. O usuário escolheu uma interface visual para Windows e quer considerar o projeto para portfólio .NET. A demonstração Electron serviu à pesquisa e não define a stack do produto.

Consequências: reimplementar interface e regras em C#; usar componentes WPF; manter o núcleo independente da interface. Avalonia e WinUI permanecem alternativas para uma mudança futura explicitamente escolhida.

## 002 — Funções sobre sessões existentes

Estado: adotada.

Uma função é uma configuração de trabalho atribuída ao Codex ou ao Claude. As ferramentas mantêm seus próprios mecanismos de execução e extensões compatíveis. O Sintonia organiza tarefas e sessões; ambos os provedores precisam executar trabalho real.

## 003 — Execução local por adaptadores

Estado: adotada; transportes específicos a validar no M1.

Começar com instalações locais dos provedores, preservar seus perfis e não extrair credenciais. Investigar Codex App Server por stdio e Claude CLI estruturado. A arquitetura não exige chamadas diretas às APIs de modelos.

## 004 — Permissões e revisão explícitas

Estado: adotada.

Não exigir bypass global para o produto funcionar. Distinguir término de execução, aceite da entrega e integração. Worktrees não são isolamento de segurança. Se uma capacidade de aprovação não estiver disponível, mostrar a limitação e restringir a execução.

## 005 — Persistência e histórico de desenvolvimento

Estado: adotada.

SQLite será o armazenamento local do produto quando entrar o histórico real. Git será o histórico do desenvolvimento: commits pequenos ao concluir incrementos validados, contexto atualizado e sincronização normal com `origin`.

## 006 — Implementação independente

Estado: adotada.

O Maestro é uma referência arquitetural, não um fork. Seu código declara AGPL 3.0. Nenhum código foi incorporado; uma proposta futura de reutilização deve tratar licença e origem explicitamente.
