# Cruzada em co-op: investigação

O modo Cruzada (Crusade / Titan Hunt) é o único modo bloqueado em co-op (`RunSync.AllowFight`: `nt.crusade` no Lutar da aba Cruzada; `nt.crusade_menu` no Reiniciar). Este documento mapeia como o modo roda no jogo (GameAssembly 1.5.3.2, endereços RVA) e o que o co-op precisa para cada peça. Ferramentas usadas: tabela de offsets (`fo.py`), disassembly com nomes (`dis.py`), xrefs de chamada (`xref.py`) e corrotinas aninhadas (`nested.py`).

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
