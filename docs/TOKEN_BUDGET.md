# Limite de tokens informados por projeto

## Contrato

O limite é opcional e desativado por padrão. Soma o último snapshot válido de cada execução do projeto desde seu cadastro, incluindo chat, chefia, fila, tentativas canceladas e falhas. Dados antigos sem medição continuam explicitamente ausentes; cache e raciocínio não são somados novamente. Não há estimativa monetária ou conversão em quota da assinatura.

Quando ativado, cada novo início reserva uma margem configurável (padrão de 1.000 tokens). Essa margem é uma regra de admissão escolhida pelo usuário, não previsão de consumo nem limite enviado ao modelo. A parcela ainda não informada é `max(0, reserva - tokens observados)` enquanto o run está ativo. A admissão exige `informados + reservas pendentes + nova reserva <= limite` na mesma transação que cria o run/tentativa. Stores e provedores compartilham esse saldo no banco do projeto.

Uma execução pode ultrapassar sua reserva; a contagem recebida é conservada e novos inícios ficam bloqueados. O teto não interrompe execuções ativas. Encerramento libera somente a reserva restante, sem apagar tokens; ausência de telemetria não vira consumo zero garantido. Recuperação marca runs interrompidos, mantém checkpoints e deixa de contar sua reserva ativa. Alterar limite/reserva não altera reservas já gravadas. Ativar o limite durante uma execução antes sem reserva não cria uma previsão retroativa.

Limite/reserva aceitam inteiros de 1 a 1.000.000.000.000; reserva não supera limite ativo. Configuração tem a mesma revisão dos limites de concorrência/tentativas/prazo. Histórico não é reiniciado ao editar ou desativar. Somatórios acima de `long.MaxValue` saturam e recusam admissão com limite ativo.

## Persistência e validação

Schema 15 acrescenta `project_token_limits` e `run_token_reservations`, preservando schema 14/histórico anterior. `GetProjectTokenBudgetAsync` lê configuração, medições e reservas num snapshot transacional. Bloqueio ocorre antes de consumir tentativa ou chamar provedor. Nenhuma quota adicional é consultada.

Quinze casos novos verificam concorrência entre stores, checkpoints repetidos, reserva/consumo sem duplicação, excesso durante execução, estados terminais, recuperação, configuração alterada/obsoleta, migração, isolamento por projeto, overflow e bloqueio de chat/fila sem inferência/tentativa. Provedores simulados, sem modelos. Interface WPF pendente neste incremento.
