# Desenvolvimento e contribuição

## Ambiente

Windows e SDK .NET 10. WPF é a interface escolhida. Visual Studio com ferramentas de desktop .NET é uma opção de desenvolvimento; comandos de build e teste devem funcionar pelo SDK.

No ambiente do usuário, os comandos de terminal dos assistentes devem começar com `rtk`. Para comandos sem filtro próprio, usar `rtk proxy`. A aplicação não deve depender do RTK para funcionar.

## Solução e validação

A solução contém Core, Infrastructure, Desktop, testes xUnit do núcleo e de processos, um diagnóstico limitado e um executável de verificação WPF. O último exige uma sessão Windows com acesso à interface gráfica; não é executado por `dotnet test`. Os testes de processos usam `Sintonia.ProcessFixture`, sem chamadas a modelos.

Validação:

```powershell
rtk proxy dotnet restore Sintonia.sln --locked-mode
rtk proxy dotnet build Sintonia.sln --no-restore
rtk proxy dotnet test Sintonia.sln --no-build
rtk proxy dotnet run --project tests/Sintonia.Desktop.SmokeTests --no-build
rtk proxy powershell -NoProfile -File tests/Sintonia.Desktop.SmokeTests/verify-startup.ps1
rtk proxy dotnet run --project src/Sintonia.Desktop
```

O teste WPF verifica a criação da janela nativa, bindings, concorrência demonstrativa, bloqueio antes da revisão, cinco aprovações e sessões. Salva capturas em `artifacts/ui-smoke` para inspeção visual. Todo o fluxo usa provedores simulados.

## Regras de código

- C# com nullable habilitado e contratos claros.
- Separar UI, regras de distribuição e acesso a recursos externos.
- Usar async e cancelamento para operações de processos, Git e armazenamento.
- Adicionar testes que exercitem comportamentos relevantes e falhas; evitar testes que apenas espelham propriedades ou markup.
- Fixar dependências restauradas e documentar versões de provedores testadas.
- Atualizar o README quando mudar a instalação ou o uso real.

## Commits

Fazer commits pequenos de incrementos coerentes e validados. Usar prefixos como `feat`, `fix`, `test`, `docs` e `chore`, com escopo quando ajudar. Revisar exatamente o que será incluído; preservar alterações de outras sessões.

Ao fechar um incremento, atualizar `CONTEXT.md`, o marco correspondente no `ROADMAP.md` e o registro resumido em `docs/DEVELOPMENT_LOG.md`. Sincronizar normalmente com o repositório do projeto. Não usar force push ou reescrever histórico compartilhado.

## Portfólio e releases

Documentar o problema resolvido, decisões e validação. Screenshots devem corresponder à versão demonstrada; simulações devem ser identificadas. Definir licença antes de publicar releases. Não adicionar credenciais ou arquivos pessoais ao repositório.
