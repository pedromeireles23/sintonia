# Instruções de desenvolvimento do Sintonia

Estas instruções se aplicam a todo o repositório. Orientações e autorizações diretas do usuário prevalecem.

## Começar e retomar

1. Ler `CONTEXT.md`, `ROADMAP.md`, `ARCHITECTURE.md` e `docs/DEVELOPMENT_LOG.md` antes de implementar.
2. Conferir branch, alterações existentes e commits recentes. Preservar trabalho do usuário e de outras sessões.
3. Continuar pelo próximo incremento verificável do marco atual. Não reiniciar o projeto ou repetir trabalho já concluído.

## Produto e implementação

- Produto: aplicativo visual Windows em C#/.NET 10, WPF e MVVM. Electron não é a stack do produto.
- Escopo: canal de comunicação e coordenação entre agentes do Codex e do Claude, com várias sessões em múltiplos projetos. A produção dos arquivos cabe aos agentes e às suas ferramentas; usar o fluxo geral de entregas. O M5 de artes/biblioteca foi retirado por decisão do usuário em 09/10/2026; justificativa na decisão 011 de [docs/DECISIONS.md](docs/DECISIONS.md).
- Interface e comunicação com o usuário em português. Identificadores de código consistentes em inglês.
- Codex e Claude devem executar trabalho real. Uma função é uma configuração de tarefa, não exige criar outra identidade de agente.
- Preservar perfis, skills, plugins e MCPs suportados pelos provedores. Documentar incompatibilidades; não prometer paridade com todos os recursos dos aplicativos desktop.
- Identificar claramente dados e execuções demonstrativas. Não apresentar mocks como integração concluída.
- Não usar opções de bypass global de permissões como padrão para executar provedores.
- Não copiar código do Maestro sem decisão explícita sobre reutilização e licença.
- Preferir arquitetura simples, contratos claros e incrementos pequenos. Não adicionar dependências ou camadas sem necessidade concreta.
- Neste ambiente, prefixar comandos de terminal com `rtk`; usar `rtk proxy` para comandos sem suporte direto. Nunca expor credenciais ao verificar configuração.

## Validação

- Executar build e verificações adequadas antes de declarar o incremento concluído.
- Testar regras relevantes: dependências, concorrência, falha, cancelamento, revisão e parsing de eventos.
- Para mudanças somente de documentação, revisar consistência, links e estado descrito; não executar testes sem relação com a mudança.
- Ler stdout e stderr dos processos de forma assíncrona; manter interface responsiva e tratar encerramento de processos filhos.
- Testes automatizados usam provedores simulados ou processos de teste. Testes reais de modelos precisam de limites e de um propósito verificável; nunca disparar loops de chamadas pagas.

## Commits e progresso — requisito do usuário

**Fazer commits conforme o desenvolvimento avançar. Não acumular vários marcos em um único commit no fim.**

- Ao concluir um incremento coerente e validado, revisar o diff, atualizar o contexto necessário e fazer um commit local.
- Usar mensagens descritivas, por exemplo `docs: define project context`, `feat(core): add task dependency scheduling` ou `fix(claude): handle interrupted output`.
- Adicionar somente os arquivos do próprio incremento. Não incluir alterações alheias, arquivos pessoais, credenciais, saídas temporárias ou binários de build.
- Nunca fazer force push, reescrever histórico compartilhado, descartar mudanças ou apagar diretórios de trabalho para resolver um problema de sincronização.
- Sincronizar commits normais com o `origin` deste projeto ao fechar incrementos validados, respeitando proteções de branch e instruções do usuário. Se autenticação, rede ou políticas impedirem o push, preservar os commits locais e informar o motivo.
- Antes de publicar, conferir o destino: `pedromeireles23/sintonia`. Não alterar remotes para contornar uma falha.
- Ao encerrar uma sessão, atualizar `CONTEXT.md` e `docs/DEVELOPMENT_LOG.md` com o que funciona, como foi validado, limitações e próximo passo concreto.
- Não repetir todo o roadmap no registro. `git log` é a fonte dos hashes e da cronologia dos commits.

## Colaboração

Quando houver autorização para trabalho paralelo, definir escopos e diretórios de trabalho, coordenar alterações compartilhadas e integrar de forma serial. Worktrees separam alterações; não são um sandbox de segurança.
