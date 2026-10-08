# Perfis reutilizáveis de função

A biblioteca local guarda padrões para conversas em projetos gerais: análise, desenvolvimento, revisão, documentação e funções definidas pelo usuário. É compartilhada entre projetos nesta instalação do Sintonia.

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

Testes do Core/Infrastructure cobrem normalização, limites, texto literal, duplicidade, revisões obsoletas, persistência, exclusão e migração com histórico preservado. O teste WPF `--profiles` percorre biblioteca, aplicação em dois projetos, retomada/reabertura, preservação de conversas/tarefas, edição/aplicação durante execução e chefia em leitura, sem erros de binding. Usa provedores de teste; não chama modelos reais.
