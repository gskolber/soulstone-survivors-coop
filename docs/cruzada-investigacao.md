# Cruzada em co-op: investigação

O modo Cruzada (Crusade / Titan Hunt) era o único modo bloqueado em co-op (`RunSync.AllowFight`: `nt.crusade` no Lutar da aba Cruzada; `nt.crusade_menu` no Reiniciar). A etapa 1 (lobby e troca de mapas) está na 1.9.0: ver "Etapa 1 implementada" no fim. Este documento mapeia como o modo roda no jogo (GameAssembly 1.5.3.2, endereços RVA) e o que o co-op precisa para cada peça. Ferramentas usadas: tabela de offsets (`fo.py`), disassembly com nomes (`dis.py`), xrefs de chamada (`xref.py`) e corrotinas aninhadas (`nested.py`).

## Fluxo do modo

1. **Menu:** `MapSelectionCrusadeScreenPanel.OnFightPressed` (aba Cruzada). Escolhas: dificuldade (`_selectedDifficulty` → `Global.CrusadeDifficultyTier`) e nível dinâmico (`Global.CrusadeDynamicLevel`). `Global.CrusadeIsFinalBossMap` marca o mapa final. `MainUIManager.ChangeScene` para o lobby.
2. **Lobby** (`CrusadeLobbyGameManager`, `GameData.CrusadeLobbyManager` +0x1A8):
   - `<Start>d__25` (0x41FC50): `GameManagerUtil.SetupGameData`, `GetAndRemoveInitialData` → `CrusadeData` (`SetEndlessStats`), afixos do `CrusadeConfigSO`, `StartMatch`, jogador posto no ponto de spawn (`ModifiedCharacter.SetPosition`).
   - **Runas de sangue de titã** (`TitanBloodRune`, gatilho de ficar em cima, `TimeToEnter`): consomem o sangue de titã guardado; `CrusadeData.CurrentTitanBloodAmount`.
   - **Portal instável** (`OnPlayerEnterUnstablePortal` 0x416340): fecha o portal e roda `ShowMapSelectionUI`.
   - **Portal primordial** (`OnPlayerEnterPrimordialPortal` 0x416220): com sangue suficiente, `SetCrusadeEndlessData` + `LoadNextScene` direto (atalho para o chefe final).
3. **Escolha do mapa** (`ShowMapSelectionUI`, corrotinas `CrusadeLobbyGameManager <ShowMapSelectionUI>d__33` 0x41F9E0, `CrusadeGameManager d__108` 0x41F780, `GoblinCaveGameManager d__30` 0x434910):
   - `CrusadeMapSelectionPanel.Open(crusadeData, infinityReroll)` (0x5EF630). `FillOptions` (0x5ED390) sorteia opções de mapa (`_mapOptionsBucket`, `_mapIdsToIgnore`, `MapDefinition.IsCrusadeMapSelectionOption`, nomes de chefe por mapa `MapDefinition.CrusadeBossNames`); reroll (`OnRerollPressed`, `RerollCount`); botão de teleporte primordial.
   - A escolha (`CrusadeMapSelectionItem.OnPressed` → `RequestedSceneLoadForMapId`) volta para a corrotina: `GameManagerUtil.SetCrusadeEndlessData(powerUps, jogador)` (progressão do jogador para o próximo mapa, como o `SetEndlessData` do Endless) + `IngameCurrencyPanelReferences.CopyInventoryData` + `GameManagerUtil.LoadNextScene(mapa)`.
4. **Mapa da Cruzada** (`CrusadeGameManager`, `GameData.CrusadeManager` +0x198, 214 métodos):
   - `<Start>d__94` (0x421050): `StartMatch`, `VoidKingRuneManager.Setup` (mapa final), componentes de spawn (elites por tempo, minerais, cristais de cura, tarefas de mapa `GameManagerMapTaskSpawnerComponent`, recompensas guardadas, inimigos de loot).
   - `<CreateObjectives>d__100` (0x41E7B0): `CollectKeyFragmentsObjective` = `SimpleObjectiveWithProgression` com `CrusadeConfigSO.GetRequiredKeyMapFragments`. Os fragmentos ficam no inventário do jogador (`InventoryData.CrusadeMapKeyFragments`); vêm das tarefas do mapa.
   - `UpdateAll` (0x4117E0): fragmentos completos → `OpenBossRoomObjective` e `CrusadeDoorObject.EnableDoorTrigger`; porta aberta → `EnterBossRoomObjective`; entrou na sala → `StopSpawning`, apocalipse, `CrusadePlayerBarrier` (prende os jogadores na arena), `SpawnMapBosses` → `KillLastBossObjective` (`EliminateSpecificEntitiesObjective`); chefes mortos → sangue de titã (`CollectableTitanBlood` → `OnTitanBloodCollected` → `OpenPortalAndObjective`).
   - **Porta** (`CrusadeDoorObject`, `UpdateAll` 0x449DD0): é um `InteractableOneTimeTrigger` (como baús e tarefas), com tempo de abertura e projéteis de fragmento voando até ela.
   - **Chefes** (`SpawnMapBosses` 0x4113F0 → `SpawnCrusadeBoss` 0x410ED0): `ResourcesUtil.Load` + `GetPrefabInstance` + `Entity.Setup` + `MasteryBossComponent.SetupWithTier` + `RegisterBossEntity`. **Não passa pelo `BossSpawnProcessor`**: o `ClientSpawnBlock` não bloqueia, e o convidado criaria chefes reais próprios.
   - **Portais de saída:** próximo mapa (`OnPlayerEnterPortal` 0x410110: `SaveCrusadeGameData` + `ShowMapSelectionUI`), Home (`OnPlayerEnterHomePortal` 0x40FFD0: fim da run), caverna dos goblins (`OnPlayerGoblinCavePortal` 0x4102B0: `SetCrusadeEndlessData` + `LoadNextScene`).
   - **Chefe final** (Void King, Arcane Weaver ou chefe aleatório; `FinalBossAddresses`): timelines de apresentação e de fim de estágio (`RequestPlayVoidKingTimeline`, `VoidKingStage1/2/3End...`, `ForceFinishFinalTimeline`), runas do Void King (`VoidKingRuneManager`, `DisableAllVoidKingRunes`), três objetivos (`KillVoidKingObjective`, `...Seated`, `...Giant`), créditos (`AttemptCallCredits`).
5. **Caverna dos goblins** (`GoblinCaveGameManager`, `GameData.GoblinCaveManager` +0x1B0): baús de recompensa (`RegisterChestObject` → `RewardChests`, `InteractableAutoRewardObject`), portal instável → `ShowMapSelectionUI`.
6. **Fim:** `SurrenderEndGame` (lobby/caverna), Home portal no mapa; `SaveCrusadeGameData` grava a progressão de Cruzada de cada jogador no perfil dele.

## O que o co-op precisa, por peça

| Peça | Problema em co-op | Caminho | Já existe no mod |
|---|---|---|---|
| Lutar na aba Cruzada | Bloqueado | `RunStart` com modo Cruzada, `Global.CrusadeDifficultyTier`/`CrusadeDynamicLevel`, cena do lobby; tirar os bloqueios `nt.crusade` / `nt.crusade_menu` | `RunStart`, `RunSync` |
| Escolha do mapa | Cada máquina sorteia suas opções e cada jogador escolheria a sua | Só o host abre o painel; o convidado vê aviso ("o host está escolhendo"); a escolha do host vai como transição de cena; o convidado roda o próprio `SetCrusadeEndlessData` e `LoadNextScene(mapa do host)` | `PortalSync` faz isso para o Endless (`PortalTransition`, mapa do host) |
| Portais do lobby/mapa/caverna | Cada um entra no seu | Mesmo tratamento dos portais do Endless/BossRush: quem entra leva todos; host decide | `PortalSync`, `CoopLife` (Home) |
| Fragmentos de chave | Ficam no inventário de quem pegou | Ver de onde vêm (tarefas do mapa, já compartilhadas desde a 1.3.0) e espelhar o objetivo como o `ObjectiveSync` | `TaskObjectiveSync`, `ObjectiveSync`, `LootSync` |
| Porta da sala do chefe | Interação local | É `InteractableOneTimeTrigger`: o mesmo caminho dos baús (host autoritativo, presença do convidado, barra de progresso) | `ChestSync` |
| Barreira da arena | Liga quando o jogador local entra | Ligar no convidado quando o host liga (ou quando o próprio convidado entra) | — |
| Chefes do mapa | `SpawnCrusadeBoss` cria chefes reais no convidado | Bloquear `SpawnCrusadeBoss`/`SpawnMapBosses` no convidado (como `SpawnSpecificBoss` no Mastery); os chefes do host chegam como marionetes; objetivo `KillLastBossObjective` do convidado ligado às marionetes (como o chefe principal do Mastery) | `ClientPuppets`, `MasterySync` (modelo) |
| Sangue de titã | Coletável local, abre o portal | Host autoritativo; espelhar a coleta e o `OnTitanBloodCollected` | `LootSync` |
| Caverna dos goblins | Baús `InteractableAutoRewardObject` (o `ChestSync` diz que só existem aqui) | Estender o `ChestSync` | `ChestSync` |
| Chefe final (Void King) | Timelines e estágios rodam no host | Repetir no convidado como as fases do chefe do Mastery (`MasteryBossPhase`); timelines são `PlayTimelineAction`/`TimelineController` | `MasterySync` (fases) |
| Runas de sangue no lobby | Cada um tem o seu sangue | Por jogador (progressão própria): pode ficar local | — |

## Plano em etapas

1. **Lobby e troca de mapas** (médio): liberar o Lutar; `RunStart` de Cruzada; host escolhe o mapa (painel só no host), convidado segue; portais instável/primordial/próximo mapa/Home/caverna levando todos. Teste: lobby → mapa → portal → mapa, sem objetivos ainda.
2. **Mapa jogável** (grande): bloquear os chefes do convidado; porta pelo caminho dos baús; barreira; fragmentos e objetivos espelhados; sangue de titã; vitória do mapa.
3. **Caverna dos goblins** (pequeno a médio): baús automáticos no `ChestSync`.
4. **Chefe final** (grande): timelines e estágios do Void King / Arcane Weaver, créditos, fim da run.

## Perguntas para os primeiros testes (sondas)

- De onde vêm os fragmentos de chave (recompensa de tarefa? drop?) e se o `LootSync` já os entrega ao convidado.
- O `GameplayUIManager.CrusadeGameModeSetupCoroutine` e o painel de seleção num convidado que não escolhe.
- O que a `CrusadePlayerBarrier` faz com quem fica fora da arena (o parceiro).
- Se a `LoadNextScene` da Cruzada passa pelo mesmo `PortalSync` (patch de `LoadNextScene`/`MapCollectionSO`) ou precisa de patch próprio na corrotina `ShowMapSelectionUI`.

## Etapa 1 implementada (1.9.0): lobby e troca de mapas

Código: `Modes/CrusadeSync.cs`, `Lobby/RunSync.cs` (Lutar e `RunStart`), `Lobby/PortalSync.cs` (portal Home).

**Lutar.** O bloqueio `nt.crusade` saiu. O `OnFightPressed` da aba Cruzada (0x65E130) grava `Global.SelectedGameMode = Crusade` (+0x168 = 2), `CrusadeDifficultyTier` (+0x174, de `_selectedDifficulty`) e `CrusadeDynamicLevel` (+0x178, `_selectedDynamicLevel` quando `_maximumDynamicLevel > 0`) e carrega `_gameModeConfig.MapId` (`CrusadeConfigSO.MapId`, o lobby). O postfix novo do `CrusadeFightPatch` manda o `RunStart` com essa cena; o `RunStart` ganhou `CrusadeTier`, `CrusadeLevel` e `CrusadeFinal` (`Global.CrusadeIsFinalBossMap`, que o próprio mapa regrava no `Start`), e o convidado escreve os três no `Global` antes do `ChangeScene`. O `Start` do lobby (`<Start>d__25`) cria um `CrusadeData` novo a partir do `Global` quando não há `InitialData`, então os dois começam com a mesma dificuldade.

**Troca de cena.** Toda troca de cena da Cruzada é `GameManagerUtil.SetCrusadeEndlessData(PowerUpData, PlayerEntity)` + `IngameCurrencyPanelReferences.CopyInventoryData()` + `GameManagerUtil.LoadNextScene(mapa, delay, false)`:
- corrotinas `ShowMapSelectionUI` do lobby (0x41F9E0), do mapa (0x41F780) e da caverna (0x434910): `CrusadeMapSelectionPanel.Open(crusadeData, infinityReroll)`, espera `RequestedSceneLoadForMapId`, esconde o painel e a HUD (`ForceHideGameplayElements`, `GameplayPanel.FadeAnimationGroupDesiredVisibility = 0`) e carrega;
- portal primordial do lobby (0x416220): mapa fixo (literal), depois do `ProcessOnPlayerEnterPortal`;
- portal da caverna dos goblins no mapa (0x4102B0).

O host manda o mapa num prefixo do `LoadNextScene` (só com `Global.SelectedGameMode == Crusade`, uma vez por cena, nunca para o menu): `CrusadeTransition = 160` (mapa, delay, clearLoad, se veio do painel, run e estágio). O convidado faz o que o jogo faz antes de carregar, com o próprio jogador (`SetCrusadeEndlessData`, `CopyInventoryData`, HUD escondida quando veio do painel) e chama `LoadNextScene` com o mapa do host. Se o painel do próprio convidado estiver aberto, só preenche `RequestedSceneLoadForMapId` e a corrotina dele segue. Cada um leva a própria build, sangue de titã e inventário, como no Endless. O `LoadNextScene` da Cruzada que o convidado chamasse por conta própria é recusado enquanto conectado (log `own LoadNextScene ... refused`).

**Portais do convidado.** Os portais instável (lobby e caverna), primordial (lobby), de saída e da caverna (mapa) do convidado não rodam o código do jogo: viram `CrusadePortalRequest = 161`, e o host roda o próprio portal (a escolha de mapa abre na tela do host, com o aviso `nt.crusade_pick`). Quando o painel do host abre, os convidados recebem `CrusadeChoosing = 162` ("O host está escolhendo o próximo mapa..."). Os botões do painel (mapa, reroll, teleporte primordial) são recusados num convidado conectado. O portal Home do mapa (`CrusadeGameManager.OnPlayerEnterHomePortal`) entra no espelhamento do `CoopLife`, como os outros mapas.

**Teclas de teste.** F7 no host, na Cruzada: abre a escolha de mapa (`ShowMapSelectionUI` do lobby, do mapa ou da caverna) sem o portal. Serve para testar mapa → mapa, já que o portal de saída só abre depois dos chefes (etapa 2).

**O que testar.**
1. Lutar na aba Cruzada com o convidado no menu: os dois entram no lobby; no log do convidado, `RunStart received: ... crusade=<tier>/<level>`.
2. Host entra no portal instável: painel abre no host, convidado vê o aviso; host escolhe; os dois carregam o mesmo mapa (`Host: LoadNextScene(...)` / `Guest followed the host`).
3. Convidado entra no portal instável: o painel abre no host (`walked into the Unstable portal`).
4. No mapa, F7: escolha de mapa no host, os dois vão para o próximo mapa; a build e o inventário de cada um continuam.
5. Desistir no lobby e no mapa: a run acaba para todos (host) ou só para o convidado.

Ainda não feito (etapas 2 a 4): fragmentos, porta e sala do chefe, chefes do mapa, sangue de titã e portais de saída abertos para todos; baús da caverna; chefe final. Reiniciar continua bloqueado na Cruzada (`nt.crusade_menu`).

### Primeiro teste (1.9.0)

Lutar, lobby, portal instável pelo convidado (abre a escolha no host) e lobby → mapa funcionaram. Dois problemas no mapa:

- **Marcadores de tarefa invisíveis no convidado.** As tarefas dos mapas da Cruzada vêm com `TaskMetadata.TaskAddress` vazio no prefab (log do host: `task ''`), e o convidado não tinha o prefab carregado (`no prefab for task marker`). O prefixo em `GameManagerMapTaskSpawnerComponent.SpawnTask(taskAddress)` (primeira tentativa) nunca rodou: o IL2CPP embutiu a fábrica da corrotina no `GetNewTask`. O endereço agora vem do registro do próprio spawner: `GameData.GameManagerComponents` → `GameManagerMapTaskSpawnerComponent.ActiveTasks` → `MapTaskInstance` cuja `TaskInstance` é a tarefa → `taskMetadata.TaskAddress` (`ChestSync.AddressOf`), usado no `TaskTriggerSpawn`, na recompensa e na cópia do anel. Se o spawner não tiver o registro, o endereço vem da lista de tarefas da coleção de mapas (`Global.MapCollection.MapTasks`, de onde o spawner sorteia), pelo nome do prefab (`MapTask-...`); o convidado faz a mesma busca a partir do nome do marcador (`Trigger-...` → `MapTask-...`) quando o host não manda endereço.
- **Host preso do lado de fora.** As tarefas com `UsePlayerBarrier` (`MapKillBossesInAreaTask`, `MapKillElitesInAreaTask`, `MapSurviveForSecondsTask`) ligam uma `CrusadePlayerBarrier` (anel de colisores em `_colliderHolder`) no `DoOnTrigger`. O convidado começou uma tarefa longe do host, e o anel subiu no host com o host fora dele.

  Em co-op o anel das tarefas não prende ninguém: no `EnableBarrier` do host os colisores são desligados (o anel fica só como marca da área), e o convidado levanta uma cópia também sem colisores (`CrusadeBarrier = 163`, com `free = 1`). Cada jogador pode fazer a sua tarefa ou entrar na do outro. No lugar do anel, a área vazia conta (`[Coop] TaskAreaEmptySeconds`, 5 s): sem nenhum jogador dentro do raio (medido pelos colisores), o tempo cai; com alguém dentro, para, e não volta a encher. Aviso para todos (`TaskAreaCountdown = 164`). Em 0 a tarefa volta ao começo:
  - inimigos da tarefa (`_eliminationObjective.EntitiesToEliminate`, `_specialEntities`) saem com `Entity.Recycle` (sem loot nem XP; chefe tirado da barra com `DeregisterBossEntity`); chefes que ainda saem da estátua (`SpawnBossRoutine`, no `JobRunner`) são varridos por 30 s;
  - objetivo com `Failed = true` (o convidado também marca o seu como falho: `FlagFailed` no `TaskObjective`);
  - anel desfeito (`DissipateBarrier`), ícone velho do minimapa removido;
  - `MapTask.StartTaskAtPosition` no lugar do marcador (guardado quando o marcador apareceu): `Stage = Spawned`, marcador novo, que volta para o convidado pelo `TaskTriggerSpawn`. O `DoOnTrigger` seguinte limpa as listas e cria objetivo e anel novos.

  Outros anéis (sala do chefe, etapa 2) continuam prendendo: quem está fora quando sobem é levado para dentro, e o convidado sem o anel na cena é mantido dentro pela posição.

### Segundo teste (1.9.0)

Tarefas simultâneas (uma por jogador) e F7 (os dois foram para o próximo mapa) funcionaram. Corrigido depois:

- **Linhas vermelhas no `LevelUpPanelManager.OnUpdate`** (`ArgumentOutOfRangeException` no indexador de `List`, todo quadro, nos dois lados): um popup de nível aberto (a sobreposição do co-op não pausa) quando a troca de mapa começou, depois do `SetCrusadeEndlessData`, até a cena velha sumir. Sozinho, a escolha de mapa pausa o jogo e não há popup aberto. Na Cruzada, durante a troca de cena, o `OnUpdate` do popup não roda (`CrusadeLevelUpTransitionPatch`); o nível pendente abre de novo no mapa seguinte.
- **`CharacterAnimator.UpdateAll` (NRE no convidado, uma por marionete)**: as marionetes dos tentáculos (`VoidTentacle-Elite`) não têm `Character` do ECM2, e o `UpdateAll` rodou antes do `Start` marcar `_isCharacterNull`. A marionete nova já sai com `_isCharacterNull = true` quando não tem `_character` (`ClientPuppets.MarkAnimatorsWithoutCharacter`).

### Terceiro teste (1.9.0)

Cada janela foi para um mapa diferente: o `Modes/CrusadeSync.cs` tinha perdido, numa edição minha, todos os patches de portal e de troca de mapa (o arquivo foi cortado logo depois dos patches da barreira), e cada um escolheu o próprio mapa. Arquivo refeito com os 14 patches. Os marcadores continuavam com `task ''` no host: a busca no `ActiveTasks` do spawner não achou a tarefa; agora há a busca na coleção de mapas, nos dois lados.

### Quarto teste (1.9.0) e F8

Lobby, troca de mapa, tarefas (marcadores no convidado, uma tarefa por jogador, reinício da área vazia) e F7 funcionaram.

**F8 (teclas de teste, host, num mapa da Cruzada):** pula para a sala do chefe. O host recebe os fragmentos de chave do mapa (`InventoryData.CrusadeMapKeyFragments` até `CrusadeConfigSO.GetRequiredKeyMapFragments`; o próprio `UpdateAll` do mapa cria o `OpenBossRoomObjective` e liga o gatilho da porta) e todos vão para a porta (`CrusadeDebugJump = 166`). Ficar na porta a abre; entrar na sala sobe a barreira e chama os chefes, como no solo.

**Porta da sala do chefe (`CrusadeDoorState = 165`):** o estado aberto/fechado da porta segue o do host em todas as máquinas: o host manda, a 4 Hz quando muda e a cada 3 s, quais `GameObject`s da `CrusadeDoorObject` estão ativos (`DoorColliderHolder` 1, `BossRoomObstaclesHolder` 2, `ArenaObstaclesHolder` 4), e o convidado liga/desliga os seus. A porta do convidado nunca abre sozinha (os fragmentos são dele), então sem isso ele ficaria fora da sala. A barreira da sala (não é de tarefa) prende: quem está fora quando ela sobe é levado para dentro, e o convidado liga a barreira da própria cena.

Ainda da etapa 2: o `SpawnMapBosses` está embutido no `UpdateAll` (sem chamadores diretos) e o `SpawnCrusadeBoss` recebe structs por valor (não dá para patchear com segurança); se um convidado juntar os próprios fragmentos e entrar na sala, o jogo dele cria chefes de verdade. O objetivo `KillLastBossObjective`, o sangue de titã e os portais de saída ainda são só do host.

### Quinto teste (1.9.0): sala do chefe com F8

O host entrou e viu a apresentação do chefe (Bro'Kenth); o convidado ficou do lado de fora, atrás da grade, com o objetivo ainda em "Coletar fragmentos 0/15", e a porta dele não tocou a animação. Não houve barreira (`CrusadePlayerBarrier`): a arena fecha com os próprios `GameObject`s da porta (`ArenaObstaclesHolder` + `DoorColliderHolder`, estado 0 → 5).

Como o mapa avança (`CrusadeGameManager.UpdateAll`, 0x4117E0):
- `CollectKeyFragmentsObjective.Current` = `InventoryData.CrusadeMapKeyFragments` do jogador local (limitado ao total); completo → `OpenBossRoomObjective` + `CrusadeDoorObject.EnableDoorTrigger`.
- `OpenBossRoomObjective.Completed` = `DoorTrigger.State == 2`; depois `EnterBossRoomObjective.Completed` = `BossRoomTrigger.State == 2`.
- Sala fechada e `KillLastBossObjective == null`: na primeira vez toca a apresentação (`_hasPlayedPresentation`, `_isPlayingPresentation`, `_unlockGameplayTime`); quando o jogo destrava (pelo menos um quadro depois), cria o `KillLastBossObjective` e cria os chefes (código do `SpawnMapBosses` embutido aqui).
- `CrusadeDoorObject.UpdateAll` (0x449DD0) toca a porta pelos estados dos gatilhos: 0→1 `OnStartInteracting`, 1→0 `OnStopInteracting`, →2 `OnStartOpening` + `_timeToOpen`, fragmentos voando de `InteractingEntity` enquanto em 1, `OnCompleteOpening`; o mesmo para fechar com o `BossRoomTrigger`.
- `EliminateSpecificEntitiesObjective.IsComplete` = `Current >= Total`; `RunEliminateSpecificEntitiesObjective` só soma.

O que o co-op faz agora (`CrusadeMapState = 167`, `CrusadeTriggerPresence = 168`):
- **Fragmentos:** o convidado recebe os fragmentos do host (prefixo do `UpdateAll`), então os objetivos dele seguem os do host e o gatilho da porta dele liga sozinho.
- **Porta e entrada da sala:** no convidado, os dois gatilhos (`DoorTrigger`, `BossRoomTrigger`) não rodam o `OnUpdate`: pegam o estado e o tempo do host. A `CrusadeDoorObject` dele toca abertura e fechamento como no host, e o `UpdateAll` dele toca a apresentação do chefe. Os fragmentos voando só saem quando o próprio convidado está na porta. Convidado em cima da porta ou da entrada conta no host pelo proxy (como nos baús).
- **Puxar para a arena:** quando a sala fecha (estado 2 do `BossRoomTrigger`), quem está do lado errado da porta (produto escalar com a direção porta → entrada) vai para logo depois da entrada. Vale para o host e para o convidado.
- **Chefes:** antes do `UpdateAll` do convidado criar os próprios chefes (apresentação já tocada, `KillLastBossObjective` vazio), o objetivo vira uma cópia do do host (mesmo id, total e atual). Os chefes que o convidado vê são as marionetes do host, e o objetivo completa com o do host.
- **Linhas vermelhas no convidado** (`SkillChargingTimeTraitRoundArea.Activate`, NRE nas habilidades carregadas do Sandscale General): o `Activate` lê `Skill.CasterEntity` e `Skill.RadiusTrait`. A réplica agora põe a marionete como `CasterEntity` quando falta, e não repete a habilidade de área sem `RadiusTrait` (log `has a charged area but no RadiusTrait`).

### Lobby e cenas de chefe (1.9.0)

**Lobby (`CrusadeLobbyState = 169`, `CrusadeRunePresence = 170`).** As runas de sangue de titã (`TitanBloodRune`: `OnTriggerEnter`/`Exit` do jogador local liga `IsBeingInteracted`; o `Update` enche `_currentTimer` até `TimeToEnter` e acende a runa com `RuneActivateVfx`, `RuneActive`; todas acesas → o `UpdateAll` do lobby abre o portal primordial) só respondiam ao jogador local, e só fechava o portal instável de quem entrava.
- Host: uma runa está "em uso" enquanto qualquer jogador está nela (o convidado avisa pela `CrusadeRunePresence`; prefixo do `Update`).
- Convidado: as runas mostram as do host (em uso e enchimento antes do próprio `Update`; acesa no host → o `Update` dele completa o último pedaço, para os efeitos de ativação tocarem nele também).
- O portal instável do convidado fecha (`PortalObject.ClosePortal(false)`) quando o do host foi usado (`GameData.EnterPortalObjective.Completed`).

**Cenas de chefe (`Ui/Cutscene.cs`).** Enquanto uma cena de chefe toca, os painéis do mod (painel do grupo, nome e vida do parceiro, anéis de caído e o popup de nível) ficam escondidos, como a HUD do jogo:
- `GameplayUIManager.ForceHideGameplayElements` (fora das trocas de cena);
- apresentação do chefe da Cruzada (`CrusadeGameManager._isPlayingPresentation`, ligado na apresentação e desligado ao destravar o jogo ou pular a timeline);
- fases do chefe principal do Mastery (`MasterySync.BossCutsceneRunning`).

O popup de nível fica parado (o `OnUpdate` não roda e o `Canvas` dele é desligado) e abre de novo quando a cena acaba.
