# Validação configurável por projeto

O núcleo permite salvar até dez comandos por projeto. Cada comando tem nome do critério, caminho absoluto de um executável `.exe`, lista de argumentos literais, pasta relativa e prazo de 1 a 600 segundos. Nenhuma stack é obrigatória; o usuário pode configurar ferramentas de código, documentação ou outros arquivos. Lista vazia representa ausência de validação configurada.

Configurações ficam no SQLite (schema 8), separadas por projeto e com revisão para recusar alterações concorrentes. Limpar comandos também aumenta a revisão. Parâmetros são copiados antes de salvar ou executar; mudanças posteriores na lista do chamador não alteram essa cópia.

`ValidationCommandRunner` inicia o executável diretamente, sem montar uma linha de shell. Wrappers `.cmd`/`.ps1` não são executados automaticamente; quando necessário, configure explicitamente o executável do interpretador e cada argumento. A pasta deve existir dentro do checkout e não usar links/junções ou `..`. O executável deve existir e ter caminho direto. Não há instalação/restauração automática de dependências nem interpretação de respostas dos modelos.

Stdout e stderr são lidos simultaneamente, com até 64 Ki caracteres por stream. Código zero significa que o comando passou; código diferente, timeout e cancelamento têm estados distintos. Saída truncada é identificada. Timeout/cancelamento encerram a árvore do processo e preservam a saída recebida. Variáveis Git herdadas são retiradas somente do processo. Diagnósticos existentes continuam com limite de 60 segundos; os comandos de validação admitem até dez minutos.

Comandos usam as permissões locais do usuário. A pasta combinada separa arquivos, mas não é um sandbox. Argumentos e saídas são dados locais persistíveis: não inclua senhas ou outros segredos. Arquivos ignorados da origem não são copiados para a combinação.

## Estado deste incremento

Configuração persistente e runner implementados/testados. O editor WPF e a operação completa com reserva, conferência da árvore e histórico de validação da combinação ainda estão pendentes. O aplicativo não apresenta um resultado de integração validada a partir deste runner isolado; publicação e dependentes permanecem pendentes.

Validação: build completo sem avisos/erros; 18 testes selecionados aprovados (13 novos de configuração/runner e cinco regressões de processos). Cobrem isolamento por projeto, revisão/reabertura, migração do schema 7, argumentos literais, pasta relativa, streams, saída excessiva, falha, timeout/cancelamento com processo filho e entradas ausentes. Processos de teste, sem modelos.
