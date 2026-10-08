# Desenvolvimento e contribuição

## Ambiente

Windows e SDK .NET 10. WPF é a interface escolhida. Visual Studio com ferramentas de desktop .NET é uma opção de desenvolvimento; comandos de build e teste devem funcionar pelo SDK.

No ambiente do usuário, os comandos de terminal dos assistentes devem começar com `rtk`. Para comandos sem filtro próprio, usar `rtk proxy`. A aplicação não deve depender do RTK para funcionar.

## Primeiro incremento

Criar `Sintonia.sln`, `Sintonia.Core`, `Sintonia.Infrastructure`, `Sintonia.Desktop` e os testes necessários. Não executar comandos para uma solução inexistente nem descrever a base como compilada antes de verificar.

Após a criação da solução, validação prevista:

```powershell
rtk proxy dotnet restore Sintonia.sln
rtk proxy dotnet build Sintonia.sln --no-restore
rtk proxy dotnet test Sintonia.sln --no-build
rtk proxy dotnet run --project src/Sintonia.Desktop
```

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
