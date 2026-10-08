# Propostas de chefia

A função **Chefe do projeto** usa uma sessão normal de Codex ou Claude, com IA/modelo escolhido. O serviço força leitura e não oferece autorização para elevar ações durante planejamento. Instruções adicionais do projeto permanecem preservadas; o contrato do plano é acrescentado somente ao pedido enviado.

## Formato

Uma resposta pode trazer explicação em português e exatamente um bloco `sintonia-plan` com JSON. Outros blocos/texto livre não são importados como propostas. Exemplo de conteúdo:

```json
{
  "schemaVersion": 1,
  "title": "Formulário do portal",
  "objective": "Enviar solicitações por um formulário acessível.",
  "tasks": [
    {
      "id": "task-1",
      "title": "Implementar formulário",
      "functionName": "Interface",
      "provider": "Codex",
      "model": null,
      "access": "WorkspaceWrite",
      "instructions": "Criar rótulos, campos e botão de envio.",
      "scope": ["src/"],
      "dependencies": [],
      "acceptanceCriteria": ["O botão pode ser acionado pelo teclado."]
    },
    {
      "id": "task-2",
      "title": "Revisar formulário",
      "functionName": "Revisão",
      "provider": "Claude",
      "model": null,
      "access": "ReadOnly",
      "instructions": "Revisar navegação e critérios da entrega.",
      "scope": ["src/"],
      "dependencies": ["task-1"],
      "acceptanceCriteria": ["Registrar problemas concretos ou confirmar os critérios."]
    }
  ]
}
```

Todos os campos são obrigatórios; `model` pode ser null para o padrão da instalação. Disponibilidade do modelo ainda será verificada ao executar. Acesso é uma recomendação para revisão, sem conceder permissões.

## Validação e limites

- Versão 1, JSON até 128 mil caracteres, resposta até 256 mil e 1–20 tarefas.
- IDs únicos até 32 caracteres ASCII (letras, números, hífen/sublinhado); dependências existentes, sem repetição, autorreferência ou ciclos.
- Títulos até 120, objetivo/instruções até 8.000, função até 80 e modelo até 120 caracteres.
- Por tarefa, 1–12 caminhos de escopo até 240 caracteres e 1–8 critérios até 1.000 caracteres.
- Escopo relativo ao projeto; '.' indica todo o projeto. Caminhos absolutos, '..', curingas, streams e nomes reservados comuns do Windows são recusados. A validação é lexical: não resolve symlinks/junctions e não constitui sandbox.
- Campos extras, duplicados, ausentes e enumerações incompatíveis são recusados. Texto de instruções permanece dado, sem ser interpretado como comando pelo importador.

## Revisão e histórico

**Revisar planos** abre propostas do projeto. O usuário edita responsáveis/modelos, funções, acesso recomendado, instruções, escopo, dependências e critérios; adiciona ou remove tarefas; valida e salva o rascunho ou confirma o plano. Se remover uma tarefa ainda referenciada, é preciso ajustar suas dependências antes de salvar. IDs existentes permanecem estáveis.

Schema 2 acrescentou proposals ao banco da central; schema 3 acrescenta a fila e preserva o histórico anterior. Cada proposta mantém projeto e run de origem imutáveis; a resposta original do chat também permanece intacta. Apenas respostas concluídas e válidas são importadas. Reabrir a revisão recupera uma resposta salva antes de uma interrupção da importação, sem repetir inferência nem sobrescrever edições. Importação é idempotente por run.

As revisões têm estado Draft/Approved e número de revisão. Salvar ajustes como rascunho retira a aprovação anterior. Uma edição com número antigo é recusada, preservando a versão salva e as alterações locais na janela; reverta e atualize a lista antes de tentar novamente. Trocar de plano com alterações não salvas fica desabilitado; fechar pede decisão sobre descartá-las.

Confirmar um plano não chama provedores, inicia tarefas, aprova entregas ou integra Git. A [Fila de tarefas](TASK_QUEUE.md) oferece encaminhamento e início explícitos. Uma proposta encaminhada fica preservada, sem edição; alterações de planejamento exigem outra proposta. Escrita paralela no mesmo projeto continua indisponível.
