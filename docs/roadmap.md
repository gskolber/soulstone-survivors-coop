# Roadmap do Soulstone Coop

Fonte única das próximas fases e pendências. Detalhes técnicos de cada fase ficam nos `docs/faseN-*.md`.

## Feito

| Fase | O que entrega | Commit |
|---|---|---|
| Base | Dois jogos (host/cliente) conectam e sincronizam a posição dos jogadores; o remoto é um fantasma só visual | `b4d8ba2` |
| 0 | Mapa de spawn, IA, dano e XP com pontos de patch | `fd9fb6a` |
| 1 | Cliente sem inimigos próprios, pausa e level-up sincronizados | `2b06a95` |
| 2 | Inimigos do host replicados como marionetes no cliente | `e56ab67` |
| 3 | Dano nos dois sentidos, com proxy aliado do cliente no host | `15b0d45`, `0cf1274` |
| 4C | Cada jogador vê as magias do outro (só visual) | `e7a0261` |
| 4B | Loot e XP no cliente, crédito de kill, XP de equipe | `a25e89e`, `47c4287` |
| 4A | Caído, reviver, derrota só quando os dois caem, vitória/portal Home pros dois; fantasma suavizado e animado, câmera de espectador | `ad9a096` |
| 4D | Bosses, VoidPresence, void hunters e pilares como marionetes; avisos de área dos ataques de boss, elite e especiais | `91b4851` |
| 6 | Aba Co-op no menu, conexão por IP, run começando junto, handshake com versão/nome/personagem | `70dff33` |
| 7 | Convite pela Steam, transporte P2P pelo relay da Steam | `a068c13` |
| Pacote | Script que gera o zip pra instalar em outro PC, com LEIA-ME | `5f09811` |
| **0.8.0** | Tudo abaixo junto, ainda sem teste em jogo (roteiro em `teste-0.8.0.md`) | `7452dec` |
| 5 | Fantasma com o personagem, skin e arma do parceiro; nome da Steam, barra de vida, anel e marcador no minimapa | `56b5309` |
| 8 | Simulação de rede, correção do MTU do LiteNetLib, queda do host e do cliente, aviso de conexão instável, reentrada (experimental) | `958d3f9` |
| Pendências | God Meteors no cliente, status do proxy chegando no cliente, números de dano das réplicas, proxy fora das buscas por time, código morto removido | `bb7481e` |
| 4D, correções | Contador de bosses do cliente parado em 0/N; golpe duplo da estátua do deus no replay | `03f5232`, `4625503` |
| Level-up | Com a build completa, o upgrade não congela a sessão (`[Coop] LevelUpPause`) | `6c7ce5f` |
| Portais | Portais Endless e de desafio e o Reiniciar, com os dois indo juntos | `4aec2b3` |
| **0.9.0** | Tudo abaixo junto (roteiro em `teste-0.9.0.md`) | `26e37dd` |
| 5, correções | Fantasma animado de novo, com arma; lançador das réplicas colado no fantasma; dash do parceiro | `568db38`, `c8876dc` |
| 5, item 5 | Summons, companheiros e a puma do Sentinel do parceiro como clones visuais | `568db38` |
| Level-up | Painel compacto e semitransparente pra jogar enquanto escolhe; padrão `Overlay` | `c263e20`, `efd4d89` |
| 9 | Até 8 jogadores: slots, transportes multi-peer, repasse pelo host, lobby de 8, regras com N, escala de inimigos opcional | merge da `fase9-oito` |
| **0.9.1** | Correções da revisão da fase 9: quem desistiu não é puxado pela vitória/portais do host, Desconectar do host no meio da run, reentrada não puxa quem desistiu, escala de inimigos só sobe limites | `dcd88fa` |
| **0.9.2** | Tudo abaixo junto (roteiro em `teste-0.9.2.md`) | `cfd120b` |
| Level-up | Toda escolha de upgrade no painel compacto, sem pausa em co-op; `[Coop] LevelUpPause` removido (#1) | `9289c2d` |
| 5, dash | Rastro do dash no fantasma do parceiro, na cor do personagem (#2) | `f6ca3b8` |
| i18n | Textos do mod no idioma do jogo de cada jogador (en e pt-BR à mão; es, fr, de, ru, zh, ja automáticos, a revisar) | `964e991` (`i18n-notas.md`) |
| **1.0.0** | Tudo abaixo junto; jogada por duas pessoas pela Steam | `0c6067c` |
| UI nativa | Aba e painel Co-op feitos com peças da UI do próprio jogo (`ui-nativa-notas.md`) | `227a58c` |
| Atualização | Aviso no menu quando sai versão nova (`[Updates] CheckForUpdates`) | `c9db3b5` |
| **1.1.0** | Tudo abaixo junto | `fc520cb` |
| #7 | Painel do grupo no HUD: vida, nível, XP, revives e estado de cada parceiro (`issue7-notas.md`) | `f16b5fa`, `3df1ac2` |
| #5 | Painel de upgrade fora do HUD, centrado nas cartas, com controle completo (`issue5-notas.md`) | `a961ab8` |
| #6, #3 | Convidado acha inimigos fora das próprias listas de tier; maldições (afixos) vêm do host (`issue6-notas.md`) | `0ff2f28`, `97e8f72` |
| **1.2.0** | Tudo abaixo junto; testado com duas janelas (`tools\test-local.cmd`); nunca publicada sozinha (entra na 1.3.0) | `95e39f0` |
| Baús | Baús e minérios para todos: espelho no convidado, proxy abre, mais rápido com mais gente em cima (`baus-notas.md`) | — |
| Reviver | Barra de reviver cai devagar longe do caído; log do host a cada 2 s enquanto alguém está caído | — |
| 4C, mira | Mira contínua do channeling do parceiro (`canal-mira-notas.md`) | — |
| 4D, comuns | Ataque animado dos inimigos comuns no convidado (`inimigos-ataque-notas.md`) | — |
| 4C, escudos | Escudos giratórios do parceiro (`sempre-ligadas-notas.md`) | — |
| Ferramentas | `tools\test-local.cmd` (duas janelas conectadas no mesmo PC) e F3 (baú na frente do host) | — |
| **1.3.0** | Tudo abaixo junto, com a 1.2.0 | — |
| Tarefas | Marcadores de tarefa do mapa para todos; qualquer jogador começa a tarefa (`tarefas-mapa-notas.md`) | — |
| Host caído | Inimigos parados em volta do corpo do host caído são reciclados; referência do spawn no plano (`host-caido-notas.md`) | — |
| Ascensões | Invocações de ascensão (centurião do Legionnaire, demônio do Demon Hunter) não são mais repetidas nos outros: travavam as magias deles; vigia que avisa no log quando o jogador local para de lançar (`invocacoes-ascensao-notas.md`) | — |
| Olhar | Inimigos perto de um jogador olham para ele no convidado (`inimigos-olhar-notas.md`) | — |
| Caído | Raio de reviver 3,5 u; anel no chão com o progresso e linhas até os aliados vivos (`caido-marcadores-notas.md`) | — |
| Ascensão | Modelo de ascensão / transformação e escala do parceiro no fantasma (`ascensao-notas.md`) | — |
| Passivas | Objetos das mecânicas de cada personagem visíveis para todos, sem interação (`passivas-notas.md`) | — |
| Tarefas | Timer e contador da tarefa no painel do convidado (`tarefas-mapa-notas.md`) | — |
| Perigos | Meteoros do deus se revezam entre os jogadores vivos (`perigos-notas.md`) | — |
| Desempenho | Reciclador, snapshot, marionetes e vigia com menos trabalho por frame (`desempenho-notas.md`) | — |
| **1.3.1** | Correções do teste da 1.3.0 e os itens abaixo; nunca publicada sozinha (entra na 1.4.0) | — |
| Ascensão | Escala do modelo principal, objeto acrescentado (Legionnaire), legionários e criaturas invocados visíveis (chave da skill no `SummonSync`), efeitos de ativação (VFX, som, luz do mundo) (`ascensao-notas.md`) | — |
| Skins de mod | Skin de mod do parceiro quando o mesmo mod está instalado; companheiros de arma (`ascensao-notas.md`) | — |
| Passivas | Marcador no lugar quando o prefab do outro personagem não está carregado (`passivas-notas.md`) | — |
| Olhar | Só inimigo parado olha para o jogador: sem moonwalk, a Morte volta à velocidade normal (`inimigos-olhar-notas.md`) | — |
| **1.4.0** | Tudo abaixo junto, com a 1.3.1 | — |
| Reentrada | Quem caiu da run volta com nível, upgrades, runas e itens (`RunStart` com id da run; build guardada a cada 5 s; atrás de `AllowRejoin`) (`reentrada-notas.md`) | — |
| Baús | Voz de "abrindo baú" / "minerando" do personagem do convidado (`baus-notas.md`) | — |
| Desempenho | Falhas de busca de prefab (invocações, passivas, inimigos, ascensão) não se repetem a cada spawn; material dos marcadores reaproveitado (`desempenho-notas.md`) | — |
| Ascensão | Troca de skin com o parceiro ascendido apaga os modelos velhos; acréscimo do Legionnaire procurado de novo se o componente chega depois (`ascensao-notas.md`, `desempenho-notas.md`) | — |
| **1.9.0** | Cruzada: Lutar liberado (`RunStart` leva dificuldade, nível dinâmico e mapa final); o host escolhe o mapa e todos vão (`CrusadeTransition = 160`, prefixo do `GameManagerUtil.LoadNextScene`); portais do convidado viram pedido ao host (`161`, `162`); tarefas do mapa com marcador no convidado, anel que não prende e reinício da área vazia (`163`, `164`, `[Coop] TaskAreaEmptySeconds`); sala do chefe: porta, fragmentos, objetivos, apresentação e chefes do host (`165`, `167`, `168`); lobby: runas e portal (`169`, `170`); painéis do mod escondidos em cenas de chefe; F7 (escolha de mapa) e F8 (porta do chefe) (`cruzada-investigacao.md`) | — |
| **1.8.0** | Modo Mastery: chefe principal do host no convidado, runa do host (convidado na runa enche a do host), kills e ciclos, vitória e portal Home (`MasteryState = 150`, `MasteryRunePresence = 151`); fases do chefe (introdução, arena, morte) tocadas no convidado (`MasteryBossPhase = 152`); F7/F8 no Mastery (`mastery-notas.md`) | — |
| **1.7.0** | Merge do PR #9 no upstream; reconexão numa run/estágio velho, convidado que cai ainda conta como vivo/caído por 45 s, pacotes repassados com limites (`reentrada-notas.md`) | `757b942` |
| **1.6.2** | Teclas de teste (F1-F3, F5-F11, Insert, Delete, PageDown) só com `[Debug] TestKeys` ou `--coop-test-keys` (o `tools/test-local` passa); antes valiam em toda sessão co-op, e Insert/Delete/PageDown até no solo | — |
| **1.6.1** | Correções do teste da 1.6.0 (`fb7d33f`) | — |
| Esquiva | Esquiva por dash para convidados: o proxy no host dá dash junto (`pendencias-notas.md`) | — |
| Reentrada | Foto da build desligada (zerava o inventário do convidado, gravava o perfil e quebrava o painel de nível); o host não congela para quem volta no meio da run nem pausa o painel de nível quando um convidado sai ou cai (`reentrada-notas.md`) | — |
| Passivas | Drops de herói em `collectables/` (orbes yin-yang do Wukong, almas do navio, orbes elementais...) espelhados como `powers/` (`passivas-notas.md`) | — |
| **1.6.0** | Volta à partida, animações dos objetos, caído deitado (`c39db1e`) | — |
| Reentrada | `AllowRejoin` ligado por padrão; Lutar no menu pede para entrar na run do host (`RunJoinRequest = 34`); reconexão automática de 45 s sem recarregar (`reentrada-notas.md`) | — |
| Passivas | Estados de `Animator` dos objetos de mecânica (`PassiveAnims = 149`) (`passivas-notas.md`) | — |
| Caído | Corpo deitado de costas em todas as telas (`caido-marcadores-notas.md`) | — |
| **1.5.0** | Espelhamento genérico das mecânicas de todos os heróis (`passivas-notas.md`, "1.5.0") (`a99e610`) | — |
| Passivas | Objetos de `Resources` `powers/` achados por varredura (raízes da cena + pool), tipo `255` com o nome do prefab; valores animados de material (`PassiveMaterials = 148`): barras, dissolves, fades | — |
| **1.4.1** | Correções do teste da 1.4.0 (`fa31e86`, `abaf7bf`) | — |
| Morte | Agente que anda mira ≥ 1 u à frente, sem `StopMovement`, `stoppingDistance` 0, sem freio e na velocidade medida do host; limiar de movimento 0,6 → 0,2 u/s (`inimigos-olhar-notas.md`) | — |
| Olhar | Inimigo em movimento sempre vira para onde anda, mesmo depois de um ataque replicado ter trocado o modo de rotação; modo esperado reaplicado a cada checagem (`inimigos-olhar-notas.md`) | — |
| Passivas | Estado das partes (ligado/escala) do objeto do dono aplicado na cópia, genérico para todos os heróis (`PassiveParts = 147`) (`passivas-notas.md`) | — |
| Passivas | Prefabs de mini-jogo de `Resources` `powers/` e árvore de habilidades do dono carregados sob demanda para achar o prefab de verdade; marcador no tamanho da área real (raio enviado no `PassiveSpawn`) (`passivas-notas.md`) | — |

## Agora

A **1.7.0** é a versão publicada (upstream `gskolber`, com o PR #9). A **1.8.0** (modo Mastery, `mastery-notas.md`) está no fork, testada em um PC (duas janelas, nove testes até a run completa) e indo como PR para o upstream.

### Modos de jogo em co-op

| Modo | Estado |
|---|---|
| Survivors | Funciona; o mais jogado |
| Endless (portal roxo) | Funciona: o host sorteia o mapa e todos vão (`portais-notas.md`); testado num PC |
| BossRush (portal vermelho, `SurvivorsBossRoomGameManager`) | Espelhado (chefes, vitória, portais); só testado forçando com F10 + Shift+F5 num PC |
| Reiniciar | Funciona para todos |
| Mastery (`MasteryGameManager`) | 1.8.0: chefe principal (introdução, arena, morte), runa, ciclos, pilares, vitória e portal sincronizados; testado num PC. Falta: efeito de canalizar o chefe do parceiro na arena |
| Crusade (lobby, caverna dos goblins) | 1.9.0: lobby (runas, portal), troca de mapas (o host escolhe, todos vão), tarefas do mapa (uma por jogador, reinício da área vazia), sala do chefe (objetivos, porta, apresentação, chefes); testada num PC. Falta: sangue de titã e portais de saída depois do chefe, caverna, chefe final (`cruzada-investigacao.md`) |

## Próximos itens

Em ordem de prioridade.

| # | Item | O que o código mostra | Esforço |
|---|---|---|---|
| 1 | Mastery pela Steam e o efeito de canalizar o chefe na arena (cada um só vê o próprio) | Roteiro em `mastery-notas.md`; falta saber de onde vem o efeito de canalizar | pequeno a médio |
| 2 | BossRush e Endless chegando de verdade (sem F10/F5), pela Steam | Só testados forçados num PC | pequeno (teste) |
| 3 | Desistir e voltar com Lutar (ou voltar depois de o host trocar de estágio) mantém a build | Desligada na 1.6.1 (e o `RejoinProgress` removido depois do PR #9): a foto usava `GameModeBaseData.SetDataBasedOnPlayerEntity` (0x4FF600), que é a passagem de estágio do jogo (grava o perfil, troca o `InventoryData` do jogador). Precisa de uma foto própria, só de leitura (nível, upgrades, runas, itens), e de reaplicar sem o caminho de estágio (`reentrada-notas.md`) | médio |
| 4 | Power-ups "ao esquivar" do convidado | Desde a 1.6.1 a esquiva acontece no proxy do convidado, no host; o `PassiveSkillsComponent` do convidado (`HasSkillOnDashDodge`, `SkillOnDashDodgePowerUpSO`) nunca fica sabendo. Caminho: o host avisa quando um hit no proxy sai com `IsDashDodged`, e o convidado dispara o próprio efeito (`pendencias-notas.md`, "1.6.1") | pequeno a médio |
| 5 | Modo Crusade: depois do chefe do mapa, caverna, chefe final | Na 1.9.0: lobby, troca de mapas, tarefas e sala do chefe. Faltam o sangue de titã e os portais de saída para todos, os baús da caverna dos goblins e o chefe final; plano em `cruzada-investigacao.md` | médio a grande |

**Problema conhecido, parado por decisão:** cristais giratórios do Machinist no parceiro. NRE mais adiante no `DoActivate` da réplica, depois do pool da projectile skill; o `FieldDump` mostrou os campos checados no começo todos preenchidos (`sempre-ligadas-notas.md`).

**Para olhar quando houver caso em jogo:** orbes de cura de `HealthProjectile.FireProjectileToAllEnemies` não chegam ao convidado; afixos do jogador não passam para o proxy (`pendencias-notas.md`, "Achados para depois").

### Conferido e fora da lista

- **XP pego caído:** não se perde. Fica retido (`LootSync.OnLocalAddExperience` soma em `_shareIn`) e entra quando o jogador levanta: revive, vitória ou portal (`ReviveLocal("victory")` / `ReviveLocal("portal")`, este antes da troca de cena). Só é descartado se a run acaba com ele caído, quando o XP já não vale nada.
- **Custo da varredura do `ScanPowers`:** nenhum problema medido. Raízes que não são de `powers/` ficam num cache por ponteiro; a linha `[passives]` mostra os números se precisar.
- **Reconexão sem recarregar:** feita na 1.6.0 (o README ainda a listava como próxima).

## Limites por definição

- Skin de mod do parceiro só aparece se você tem o mesmo mod (não há de onde carregar). Efeitos de ascensão de outro personagem: nenhum caso de `isn't loaded here` nos testes da 1.5/1.6.
- Cristais giratórios do Machinist no parceiro: deixado como problema conhecido (veja acima).

## Ainda não vistos em jogo (de fases anteriores)

- Barra de boss sumindo ao matar, invocações de boss, minérios e cristais de cura, vida u16 no snapshot (`fase4d-notas.md`).
- O cliente revivendo o host e o efeito ao levantar (`fase4a-notas.md`).
- Itens e power-ups via `ProcessLoot`, bloqueio do portal do boss no cliente, procs on-kill com runa de kill, XP retido de jogador morto (`fase4b-notas.md`).
- Patch 13 (PlayerSeekerAI) nunca exercitado (`fase3-notas.md`).
- Conexão por IP na fase 6: recusa por versão (`--coop-fake-version`), volta ao menu e segunda run (`fase6-notas.md`).

## Regras que valem pra qualquer fase

- Nunca criar um segundo Entity de jogador. Toda Entity nova passa pelo pipeline do jogo (pool + `Entity.Setup`), e nada é clonado do jogador antes do `StartMatch` local.
- Todo patch com try/catch: um NRE dentro de um `UpdateAll` derruba o frame inteiro.
- Não patchar método com struct de 1/2/4/8 bytes por valor na assinatura (`fase4c-notas.md`).
- Texto flutuante do jogo (`OverheadCombatTextPanelManager.RequestCombatTextWithString`) aceita no máximo 16 caracteres; acima disso dá erro e o texto não aparece.
- Ao subir a versão, mudanças de formato de pacote quebram a compatibilidade: o handshake recusa versões diferentes.
- Enquanto o usuário testa uma versão fixa, `%USERPROFILE%\.soulstone-no-deploy` trava o deploy (ou use `-p:SkipDeploy=true`).
- Texto visível ao jogador sai da tabela `Ui/LocTable.cs` (`Loc.T` / `Loc.Msg`), nunca literal no código; pela rede vai a `Loc.Msg` (chave + parâmetros), nunca o texto traduzido (`i18n-notas.md`).
- Pacotes de rede: veja o enum `PacketType` em `src/SoulstoneCoop/Net/PacketRouter.cs` (a fase 9 usa 100–104). Itens novos começam no 120.
