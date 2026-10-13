# Modelos e reasoning na central

A central mostra, para o projeto e a IA selecionados, o catálogo de modelos da instalação, o padrão configurado, a versão confirmada pela sessão e o nível de reasoning. Em conversas comuns, modelo e reasoning podem mudar entre turnos.

![Central com padrão da instalação, versão em uso e reasoning — catálogo e provedores de teste, sem chamadas de modelos](images/sintonia-modelos.png)

## Painel de modelo

- **IA:** escolhida em uma conversa nova. Depois do primeiro envio, fica fixa porque a sessão nativa pertence ao provedor.
- **Modelo:** "Padrão da instalação" não envia um modelo e segue a configuração do provedor. A lista mostra nome, identificador, marcadores de padrão da instalação/catálogo, níveis anunciados e descrição. O campo também aceita um identificador digitado, e o provedor confere o acesso no envio.
- **Reasoning:** "Padrão" não envia um nível. Os demais itens são os níveis anunciados no catálogo para o modelo escolhido. Com "Padrão da instalação" no Codex, valem os níveis do modelo configurado.
- **Padrão da instalação:** no Codex, mostra modelo, reasoning e origem da configuração efetiva (usuário, perfil, projeto, configuração administrada ou pacote), sem expor camadas, caminhos ou valores brutos. No Claude, mostra a entrada `default` do catálogo e sua descrição. Nessa consulta, o CLI não informa a versão exata nem o reasoning atual.
- **Em uso nesta conversa:** mostra a versão informada pelo provedor ao conectar a sessão. O Codex confirma o nível efetivo. No Claude, o nível aparece como solicitado porque o CLI não o confirma.

A lista de conversas mostra a versão e o nível de cada sessão. Na lista de reasoning, cada nível tem a descrição do catálogo:

![Lista de reasoning com níveis e descrições de teste](images/sintonia-reasoning.png)

## Consulta do catálogo

- A consulta é automática ao abrir um projeto ou trocar a IA. Ocorre uma vez por provedor e pasta enquanto o aplicativo está aberto; **Atualizar** repete a consulta.
- Somente metadados são lidos. Codex: `model/list` e `config/read`, sem o inventário de extensões. Claude: `auth status` e `initialize`. Nenhuma mensagem ou turno é enviado ao modelo.
- O prazo é de 60 segundos, e fechar a central cancela e aguarda a consulta. Uma falha aparece como "Consulta indisponível", sem apagar o catálogo anterior nem a escolha atual.
- Sem um projeto cadastrado não há consulta, porque o Codex lê a configuração da pasta.

## Troca entre turnos

- Em conversas comuns, modelo e reasoning podem mudar entre envios. A mesma sessão nativa é retomada com a nova escolha, e o painel avisa "Nova escolha vale no próximo envio" até o envio.
- A escolha fica bloqueada durante a execução e em conversas de tarefas da fila, que seguem o plano aprovado. O banco também recusa essas alterações.
- A escolha do usuário (por exemplo, o alias `opus`) é salva separadamente da versão confirmada (por exemplo, `claude-opus-5`). Assim, retomadas continuam usando a escolha original.

## Regras dos provedores

- **Codex:** o nível é enviado em `thread/start`/`thread/resume` e em `turn/start`. Antes do turno, o adaptador confere o suporte no catálogo e a confirmação do nível pela sessão. Se algo falhar, recusa sem iniciar o turno.
- **Claude:** usa `--model` e `--effort`. O nível exige um modelo explícito (não "Padrão da instalação" nem `default`). Na resposta de `initialize`, o adaptador confere se o modelo anuncia o nível antes de enviar a mensagem.

## Persistência

O schema 17 acrescenta `selected_model` a `conversation_model_settings`, junto com a escolha explícita ou padrão, o nível solicitado e o nível confirmado. Históricos do schema 16 recebem a coluna sem perda de dados. Em conversas antigas sem esse registro, a escolha passa a ser o último modelo salvo, como antes.

## Validação e limites

- Testes de Infrastructure cobrem escolha entre turnos sem substituição pela versão confirmada, envio sem modelo pelo padrão da instalação, migração do schema 16, bloqueio durante a execução e regras de reasoning do Claude (modelo explícito, nível anunciado e `--effort`), além dos casos anteriores do Codex.
- O fluxo WPF `--models` usa catálogos e provedores de teste. Ele cobre o padrão da instalação, as listas, a troca entre turnos, o Claude sem reasoning implícito, o bloqueio durante a execução, a falha de consulta e a reabertura, com zero erros de binding e capturas normal/mínima.
- Uma consulta real de metadados do Claude Code em 13/10/2026 listou `default` (Sonnet 5), `sonnet`, `opus`, `claude-fable-5-1[1m]` e `haiku`, com níveis por modelo, sem prompt. Uma consulta anterior do Codex confirmou `gpt-6.1-sol`/`xhigh` na configuração do usuário pelas mesmas leituras. Nesta sessão, o CLI Codex não estava no PATH, então `InspectModelsAsync` não foi exercitado com a instalação real.
- Nenhum turno real foi enviado com nível escolhido ou com troca de modelo entre turnos; esses fluxos foram verificados com provedores de teste. Perfis de função e propostas da chefia ainda não guardam reasoning.
