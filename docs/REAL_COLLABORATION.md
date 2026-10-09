# Prova real finita de colaboração com Git

Executada em 09/10/2026 por autorização do usuário. Dois turnos de trabalho por assinatura: um Codex `gpt-6.1-sol` e um Claude `claude-opus-5`. Sem inferência de planejamento, reenvios de tarefas, bypass global ou configuração de API. O plano de duas tarefas é fornecido e identificado pelo diagnóstico.

## Trabalho conferido

1. Codex criou `numbers.json` numa worktree: marcador aleatório, valores 11/14 e soma 25.
2. Diagnóstico conferiu arquivo/escopo/diff, aprovou somente esta entrega de teste, fez commit e registrou a entrega. A combinação separada passou por validação em processo real e publicação local.
3. Claude recebeu a tarefa dependente com resposta/revisão/commit publicados. Sua worktree foi criada da revisão integrada. Leu os dados e escreveu `verification.md` com marcador e `SUM=25`.
4. Uma única autorização Write foi concedida pelo host após conferir pasta, caminho e conteúdo integral esperados. Nenhuma permissão permanente foi criada. Segunda entrega também passou por diff, revisão, combinação, validação e publicação local.
5. Reabrir SQLite preservou as duas tarefas aprovadas/publicadas, uma tentativa por tarefa, sessões, medições e ausência de reservas ativas. Retomada explícita posterior reutilizou os dois runs concluídos, sem modelos ou nova publicação.

As aprovações de revisão/publicação pertencem ao diagnóstico exclusivo e conferem critérios fixos; não representam aprovação automática no produto ou nos projetos do usuário. A prova usa os serviços reais da aplicação; os fluxos WPF têm verificações separadas com provedores simulados. Não comprova sete modelos simultâneos, planejamento real Codex ou paridade geral de extensões.

## Tokens e limites

Codex informou **41.715 tokens parciais** (41.537 entrada, 178 saída). Claude informou **97.617 tokens do turno principal** (96.946 entrada, 671 saída). Soma persistida por run: **139.332**, sem somar cache/raciocínio outra vez. O run sintético do plano permanece como uma execução encerrada sem medição; não é apresentado como chamada real.

Limite de teste: 100.000 tokens informados, com reserva de 20.000 por início. Após o Codex, havia margem para o segundo início. Claude ultrapassou sua reserva; a execução foi conservada e o snapshot final bloqueia novos envios. Isso comprova a finalidade do [limite de admissão](TOKEN_BUDGET.md), sem prometer teto integral de consumo ou estimar quota de assinatura.

## Interrupção e caminhos

A primeira preparação recusou corretamente um vínculo divergente: neste ambiente do Codex desktop, a pasta lógica `%LOCALAPPDATA%/Sintonia/integrations` foi redirecionada para `AppData/Local/Packages/.../LocalCache/Local/Sintonia/integrations`. O Git informou o caminho físico. A pasta parcial e o registro NeedsAttention foram preservados.

O diagnóstico passou a usar raiz explícita de combinações dentro da sua pasta exclusiva, como já fazem os testes Git. A entrega Codex já concluída foi retomada sem chamada adicional; depois ocorreu o único turno Claude. Essa prova com modelos usou a raiz explícita, conservando as conferências de identidade.

Incremento posterior corrigiu a conferência da combinação pelo identificador físico de diretórios Windows. Um teste separado, sem modelos, percorreu criação no diretório padrão real redirecionado, reabertura, validação em processo e publicação no repositório exclusivo. Caminhos registrados e operações antigas em atenção permanecem intactos, sem replay. [Contrato e verificação](TASK_INTEGRATION_PREPARATION.md#diretórios-redirecionados-no-windows).

## Reprodução manual

`rtk proxy dotnet run --project tools/Sintonia.Diagnostics -c Release -- collaboration` cria outra pasta exclusiva em `artifacts/provider-probes`. Limites: uma tentativa e 90s por execução, no máximo os dois turnos de trabalho, Claude até quatro iterações e seis minutos por comando. Uma falha encerra a prova; não repetir modelos em CI/loops.

`collaboration-resume pasta` retoma explicitamente a prova existente: exige primeira entrega concluída e recusa runs de trabalho falhos/cancelados/interrompidos. Não reenvia runs concluídos nem republica entregas publicadas. Arquivos, worktrees e preparações parciais permanecem para inspeção. `verify-collaboration data/complete marcador` valida somente arquivos, sem provedores.

Evidência desta execução, local e ignorada pelo Git: `artifacts/provider-probes/Colaboração Git ação 658278b8-4e52-4285-b0ec-0f2934d9cb2f`. Guarda JSON/Markdown integrados, diffs, banco, runs, autorização e `evidence.json`. Nenhuma credencial é registrada. Os arquivos podem ser inspecionados sem chamar modelos.
