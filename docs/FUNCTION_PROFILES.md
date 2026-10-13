# Perfis reutilizáveis de função

A biblioteca local guarda padrões para conversas em projetos gerais: análise, desenvolvimento, revisão, documentação e funções definidas pelo usuário. É compartilhada entre projetos nesta instalação do Sintonia.

## Funções do catálogo e perfis iniciais

O catálogo de funções (`WorkspaceFunctionCatalog`) é o mesmo na central e no editor. Cada função tem um resumo de uma linha e instruções prontas:

| Função | Resumo | Provedor sugerido no perfil inicial |
| --- | --- | --- |
| Conversa | Perguntas e conversa livre com o contexto do projeto. | sem perfil inicial |
| Chefe do projeto | Planeja o trabalho e propõe tarefas para revisão. | Claude |
| Desenvolvimento | Implementa mudanças pequenas e validadas. | Codex |
| Interface | Cria e ajusta telas com foco em usabilidade e acessibilidade. | Claude |
| Testes | Escreve e executa testes do comportamento pedido. | Codex |
| Revisão | Revisa entregas e aponta problemas concretos. | Claude |
| Documentação | Escreve e atualiza a documentação do projeto. | Claude |
| Análise | Investiga questões e recomenda um caminho. | Codex |
| Personalizada | Instruções definidas por você para a tarefa. | sem perfil inicial |

- Na primeira abertura com o schema 18, a biblioteca recebe um perfil por função do catálogo (exceto Conversa e Personalizada), com nome igual ao da função, instruções preenchidas e modelo vazio (padrão da instalação). O provedor sugerido é só um ponto de partida; edite à vontade.
- A criação acontece uma única vez por banco (`workspace_markers`). Perfis excluídos ou renomeados depois não são recriados, e um perfil existente com o mesmo nome é preservado.
- No editor, **Novo perfil** já vem com a função Desenvolvimento e suas instruções. Trocar a função preenche as instruções do catálogo somente se o campo estiver vazio ou ainda tiver o texto de outra função; texto escrito pelo usuário não é substituído. O resumo da função aparece abaixo do campo.
- Na central, a lista de funções mostra o resumo de cada uma, e escolher uma função preenche as instruções da conversa, como antes.

## Criar e aplicar

1. Abra **Perfis de função** na central e use **Novo perfil**.
2. Informe nome, função, provedor padrão, modelo opcional e instruções adicionais. A função aceita texto personalizado.
3. Use **Salvar perfil**. Salvar não chama um modelo.
4. Selecione um projeto, abra **Função e permissões**, escolha o perfil e use **Nova com perfil**.
5. Confira modelo, instruções e acesso; envie o pedido pelo chat.

**Nova com perfil** prepara outra conversa em leitura e conserva a mensagem digitada. Não altera a conversa selecionada nem interrompe uma execução ativa. A sessão nativa só é criada ao enviar. Um modelo vazio usa o padrão da instalação; um identificador informado depende da disponibilidade e do acesso confirmados pelo provedor.

O perfil não armazena acesso de escrita. Essa escolha fica no painel da conversa e continua sujeita às regras/hooks e às autorizações do provedor. A função **Chefe do projeto** permanece em leitura: o serviço acrescenta o contrato de proposta ao pedido, mantendo as instruções adicionais originais no histórico.

## Edição e histórico

Editar ou excluir um perfil não muda os parâmetros copiados para conversas ou tarefas existentes. Retomar uma conversa recupera sua função e suas instruções, mesmo que o perfil tenha mudado ou sido excluído. A fila usa a definição da tarefa confirmada no plano; não consulta automaticamente um perfil pelo nome da função.

Salvar ou reverter libera a troca de perfil. Atualizar a lista e criar outro perfil ficam desativados enquanto há alterações locais. Fechar o editor solicita confirmação para descartar um rascunho. Excluir exige confirmação e um perfil sem alterações locais.

Cada edição incrementa a revisão. Salvar/excluir uma revisão antiga é recusado; o rascunho local permanece disponível. Reverter e atualizar permite ler a versão atual antes de refazer as alterações. Se o perfil selecionado na central mudou em outra janela, aplicar atualiza a lista e pede nova conferência antes de preencher o rascunho.

## Dados e limites

| Campo | Regra |
| --- | --- |
| Nome | Obrigatório, até 80 caracteres, único após normalização Unicode e comparação sem distinção de maiúsculas |
| Função | Obrigatória, até 80 caracteres; texto personalizado aceito |
| Provedor | Codex ou Claude |
| Modelo | Opcional, até 120 caracteres; vazio usa o padrão da instalação |
| Instruções | Até 8.000 caracteres, preservadas literalmente; vazio aceito |
| Chefia | Limite menor para instruções adicionais, mostrado no editor e calculado com espaço para o contrato de planejamento |

Nome, função e modelo não aceitam caracteres de controle; espaços nas extremidades são removidos. As instruções conservam quebras de linha e texto literal. O schema 4 acrescenta `function_profiles` a `%LOCALAPPDATA%/Sintonia/workspace.db`, preservando projetos, conversas, propostas, tarefas e execuções anteriores.

Perfis não substituem configurações globais, skills, plugins ou MCPs do Codex/Claude. Não sincronizam automaticamente com outros computadores. O cenário demonstrativo M0 conserva seus perfis em memória separadamente.

## Validação

Testes do Core/Infrastructure cobrem normalização, limites, texto literal, duplicidade, revisões obsoletas, persistência, exclusão e migração com histórico preservado, além do catálogo (resumos, perfis iniciais válidos e substituição só de texto vazio ou do catálogo) e da criação única dos perfis iniciais, que não recria perfis excluídos nem altera nomes existentes. O teste WPF `--profiles` percorre biblioteca, aplicação em dois projetos, retomada/reabertura, preservação de conversas/tarefas, edição/aplicação durante execução e chefia em leitura, sem erros de binding. Usa provedores de teste; não chama modelos reais.
