# Perfis reutilizáveis de função

Cada projeto tem sua equipe de perfis: análise, desenvolvimento, revisão, documentação e funções definidas pelo usuário. Provedor, modelo e instruções podem variar por projeto. A biblioteca compartilhada anterior fica preservada como modelo para a primeira configuração de cada equipe; editar um projeto não modifica os demais.

## Chat principal e delegação

Ao selecionar um projeto, a central abre seu **Chefe do projeto**. O vínculo persiste ao reabrir o aplicativo; uma conversa de chefia anterior é reaproveitada quando existe. **Chat do chefe** volta à conversa principal, e **Nova conversa** permite abrir conversas independentes.

1. Abra **Equipe e funções** no projeto selecionado.
2. Edite um perfil de função **Chefe do projeto**, escolhendo Codex ou Claude, modelo opcional e instruções. Salve.
3. Use **Usar como chefe do projeto**. Na mesma IA, a sessão/histórico continua; trocar a IA cria outra conversa principal e preserva a anterior. Uma chefia em execução precisa terminar antes da troca.
4. Configure os demais perfis desta equipe e descreva um objetivo no chat principal.
5. O chefe recebe os perfis do projeto e um resumo das tarefas/resultados salvos. Ele responde a dúvidas normalmente; objetivos de trabalho podem gerar uma proposta em **Revisar planos**.
6. Confira responsáveis, modelos, instruções, acessos, escopos, dependências e critérios. **Confirmar e iniciar equipe** salva a aprovação, encaminha o plano e inicia uma rodada das tarefas disponíveis. **Só confirmar plano** salva sem executar.

Essa confirmação é a escolha do usuário para a delegação. Tarefas aguardam vagas/dependências; permissões por ação e revisão das entregas permanecem com o usuário. Escrita paralela exige worktrees preparadas; escrita direta da fila continua exclusiva. Não há repetição, aprovação ou publicação automáticas. Outras rodadas são iniciadas pela fila, após revisar as entregas e publicar integrações quando necessário. A fila mostra a entrega de tarefas delegadas e permite seu cancelamento.

O contexto automático contém até 20 perfis de trabalho e 20 tarefas recentes, trechos de resultado de até 1.200 caracteres e até 60.000 caracteres no total, respeitando o limite do pedido. Ele não é salvo como mensagem digitada nem cria turnos adicionais. O plano deve copiar as instruções relevantes para cada tarefa; o usuário pode conferir/editar a proposta. Alterar um perfil não muda tarefas já confirmadas.

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

1. Selecione o projeto, abra **Equipe e funções** e use **Novo perfil**.
2. Informe nome, função, provedor padrão, modelo opcional e instruções adicionais. A função aceita texto personalizado.
3. Use **Salvar perfil**. Salvar não chama um modelo.
4. Selecione um projeto, abra **Função e permissões**, escolha o perfil e use **Nova com perfil**.
5. Confira modelo, instruções e acesso; envie o pedido pelo chat.

**Nova com perfil** prepara outra conversa e conserva a mensagem digitada. Não altera a conversa selecionada nem interrompe uma execução ativa. Confira o acesso no painel antes de enviar; a chefia permanece em leitura. A sessão nativa só é criada ao enviar. Um modelo vazio usa o padrão da instalação; um identificador informado depende da disponibilidade e do acesso confirmados pelo provedor.

O perfil não armazena acesso de escrita. Essa escolha fica no painel da conversa e continua sujeita às regras/hooks e às autorizações do provedor. A função **Chefe do projeto** permanece em leitura: o serviço acrescenta o contrato de proposta ao pedido, mantendo as instruções adicionais originais no histórico.

## Edição e histórico

Editar ou excluir um perfil não muda os parâmetros copiados para conversas ou tarefas existentes. Retomar uma conversa recupera sua função e suas instruções, mesmo que o perfil tenha mudado ou sido excluído. A fila usa a definição da tarefa confirmada no plano; não consulta automaticamente um perfil pelo nome da função.

Salvar ou reverter libera a troca de perfil. Atualizar a lista e criar outro perfil ficam desativados enquanto há alterações locais. Fechar o editor solicita confirmação para descartar um rascunho. Excluir exige confirmação e um perfil sem alterações locais.

Cada edição incrementa a revisão. Salvar/excluir uma revisão antiga é recusado; o rascunho local permanece disponível. Reverter e atualizar permite ler a versão atual antes de refazer as alterações. Se o perfil selecionado na central mudou em outra janela, aplicar atualiza a lista e pede nova conferência antes de preencher o rascunho.

## Dados e limites

| Campo | Regra |
| --- | --- |
| Nome | Obrigatório, até 80 caracteres, único dentro do projeto após normalização Unicode e comparação sem distinção de maiúsculas |
| Função | Obrigatória, até 80 caracteres; texto personalizado aceito |
| Provedor | Codex ou Claude |
| Modelo | Opcional, até 120 caracteres; vazio usa o padrão da instalação |
| Instruções | Até 8.000 caracteres, preservadas literalmente; vazio aceito |
| Chefia | Limite menor para instruções adicionais, mostrado no editor e calculado com espaço para o contrato de planejamento |

Nome, função e modelo não aceitam caracteres de controle; espaços nas extremidades são removidos. As instruções conservam quebras de linha e texto literal. O schema 4 acrescenta `function_profiles` a `%LOCALAPPDATA%/Sintonia/workspace.db`, preservando projetos, conversas, propostas, tarefas e execuções anteriores.

O schema 20 acrescenta `project_profile_libraries`, `project_function_profiles` e `project_chiefs`. A primeira configuração copia os modelos compartilhados uma única vez por projeto. Exclusões posteriores não recriam perfis; um perfil de chefia inicial é fornecido se o modelo compartilhado tiver sido excluído. O vínculo do chefe aponta à conversa, sem chave mutável para as instruções do perfil.

## Remover um projeto

A lixeira junto a **PROJETOS** remove o projeto selecionado da lista após confirmação. A pasta, os arquivos, worktrees e histórico ficam preservados. Adicionar a mesma pasta recupera o cadastro, a equipe e o chat principal. A remoção recusa execuções e operações Git ativas, inclusive reservas em outra instância do banco. O schema 19 guarda o cadastro removido em `removed_projects`; reservas posteriores recusam projetos removidos.

Perfis não substituem configurações globais, skills, plugins ou MCPs do Codex/Claude. Não sincronizam automaticamente com outros computadores. O cenário demonstrativo M0 conserva seus perfis em memória separadamente.

## Validação

Testes do Core/Infrastructure cobrem normalização, limites, texto literal, duplicidade por projeto, revisões obsoletas, persistência, exclusão, isolamento das equipes e migração com histórico preservado. O teste WPF `--profiles` cobre aplicação, retomada/reabertura e preservação de conversas/tarefas. `--project-team` cobre o chat principal, troca de chefia/projeto, contexto e resultados, confirmação antes de delegar, dependências, atualização/cancelamento pela fila, lixeira e restauração. São provedores de teste, sem modelos reais. Geração real de planos com a equipe por projeto ainda não foi exercitada; as provas anteriores de planos e fila continuam delimitadas em seus próprios guias.
