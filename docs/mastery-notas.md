# Modo Mastery em co-op (1.8.0)

Até a 1.7.0 o host podia começar uma run de Mastery e o convidado ia junto (`RunStart` leva modo, tier e nível), mas nada do mod conhecia o `MasteryGameManager` (`GameData.MasteryManager`, +0x190). Nunca foi testado. Código: `src/SoulstoneCoop/Modes/MasterySync.cs`.

## Como o modo funciona (GameAssembly 1.5.3.2)

Offsets lidos do binário (tabela de field offsets do `GameAssembly.dll` + `global-metadata.dat` v31).

- **Começo** (`<Start>d__48`): `StartMatch` e `SpawnMainBoss` → `SpawnMainBossRoutine` (`<SpawnMainBossRoutine>d__57`, 0x440D80):
  - `BossSpawnProcessor.SpawnSpecificBoss` (0x807350) → `_mainBossEntity` (+0x108); marca `IgnoredByEntitySearchQueries` e `InvulnerabilityTime = +inf` no chefe;
  - `EliminateSpecificEntitiesObjective(chefe)` → `_eliminateMainBossObjective` (+0x100) = `MainObjective` (+0xC0), também em `EliminationObjectives`;
  - `SpawnMasteryPillar` (pilares em `CreatedMasteryPillarsReference`, +0xA0).
- **Runa** (`MasteryBossSpawner`, campo +0x70 do manager): `OnTriggerStay` (0x45D980) soma tempo enquanto um jogador (`EntityRegistry.TryGetEntityByRigidbody` + `IsPlayer`) está dentro. Em `_currentTimer >= TimeToEnter`: `_triggered = 1`, evento `OnPlayerTriggerSpawn` (→ `SpawnBoss` → `SpawnBossRoutine` → `MasterySpawnBosses`, os lordes), e:
  - ciclo 0: o jogador **local** ascende (`GameManagerUtil.PlayerEntity.StartAscension`);
  - ciclo > 0: `CastMasterySkillAndResetPlayer` → `ResetPlayer` (nível, power-ups, buffs do jogador local);
  - ciclo < total − 1: `CreateLordsObjectivesAfterTrigger` → `EliminateEnemiesNonCumulativeObjective` em `ProgressObjectives` (+0xB0), `IsComplete` = `Current − Starting >= Total`, e o `UpdateAll` põe `Current = GameStats.BossesKilled` a cada frame.
- **`UpdateAll`** (0x42AEE0):
  - objetivo dos lordes completo → `CurrentMasteryCycle` (+0x124) +1, `StopSpawning`, apocalipse;
  - pilar com `ObjectiveAmountNeededToDestroy <= ciclo` → `CastDestroyPillarVfx` → `DestroyPillarRoutine` → `AdvanceCycle` → `GoToNextCycleRoutine` (rearma a runa, chefe especial, ascensões);
  - fase final: lê a vida do `_mainBossEntity` (NRE se for nulo);
  - todos os jogadores mortos → derrota; `MainObjective` completo → vitória (`OpenPortalAndObjective`).
- **Portal Home:** `MasteryGameManager.OnPlayerEnterPortal` (grava o progresso de Mastery no perfil, `SaveMasteryData`).

## O que quebrava no convidado

1. `SpawnSpecificBoss` devolve nulo no cliente (`ClientSpawnBlock`): a rotina do chefe dava NRE logo depois. Sem `MainObjective`, sem pilares, e o `UpdateAll` dando NRE a cada frame na fase final.
2. Runa: cada máquina disparava a sua. No convidado os lordes não nascem (bloqueados), o objetivo dos lordes ficava por conta dele, e o reset do jogador acontecia em hora diferente.
3. Os contadores de kill (`ObjectiveSync`) só valiam com `SurvivorsGameManager`: o objetivo dos lordes do convidado nunca completava, então o ciclo dele não andava.
4. Vitória do host no convidado e portal Home espelhado só conheciam Survivors e a sala do BossRush.

## O que a 1.8.0 faz

- **`MasteryState = 150`** (host → convidados, ReliableOrdered, na mudança + a cada 2 s): ciclo, total de ciclos, disparos da runa, net id do chefe principal, kills (inimigos, elites, chefes).
- **Chefe principal:** no convidado o `SpawnMainBoss` espera (`MasteryMainBossPatch`) até a marionete do chefe do host existir; aí roda, e o `SpawnSpecificBoss` devolve essa marionete. A rotina segue igual ao solo (objetivo, pilares). O objetivo do chefe **não é contado** no convidado (`MasteryMainObjectivePatch`): uma marionete apagada numa reconexão contaria como morte. A vitória vem do `RunEnd` do host (`CoopLife.ForceLocalVictory` → `MasterySync.ForceVictory`). Depois de uma reconexão, `_mainBossEntity` aponta para a marionete nova.
- **Runa:** é do host. No convidado o `OnTriggerStay` do jogo não roda (`MasteryRuneStayPatch`), a não ser quando o host disparou: cada disparo do host é repetido pelo próprio `OnTriggerStay` do jogo, com o colisor do jogador local e `_currentTimer = TimeToEnter`. Assim o convidado tem os mesmos lordes (marionetes), o mesmo objetivo e a sua ascensão / reset como no solo. Convidado caído: repete quando levantar. No host, um convidado vivo dentro da área da runa a enche como se fosse o host (chama `OnTriggerStay` com o colisor do host), a não ser que o próprio host já esteja nela.
- **Kills:** copiados do host para o `GameStats` do convidado, como o `ObjectiveSync` faz no Survivors. Rede de segurança: ciclo do convidado atrás do do host por 8 s → o objetivo dos lordes dele é completado (`Starting` baixado).
- **Vitória e portal:** `ForceLocalVictory`, `VictoryReached` e o F10 completam o `MainObjective` do Mastery; o portal Home do Mastery é espelhado (`MasteryHomePortalPatch`, `PortalSync.EnterHomePortal`).
- **Log:** linha `[mastery]` a cada 10 s nos dois lados (ciclo, runa, lordes, chefe, pilares, estado do host, contadores) e uma linha por passo do jogo (`CastMasterySkillAndResetPlayer`, `CreateLordsObjectivesAfterTrigger`, `AdvanceCycle`, `SpawnBoss`, `SpawnMasteryPillar`, `ActivateSpecialBossRoutine`).

## Primeiro teste (09/11/2026, Unholy Cathedral, Gold 1)

- Chefe principal no convidado: funcionou (`main boss = the host's (puppet 1)`, `SpawnSpecificBoss(Cthulhu) -> the host's main boss puppet`, `SpawnMasteryPillar` nos dois, `mainBoss=puppet`, sem NRE).
- Runa pelo convidado: não disparava. O host encheu a runa por 76 frames (0,7 de 1,5 s) e parou: o convidado estava na borda, e a checagem de posição do host usava o centro do jogador contra a caixa do colisor da runa. Na tela do convidado a barra não andava, então não dava para saber.
- Correção:
  - o convidado avisa o host pela própria física (`MasteryRunePresence = 151`, ligado/desligado, reenviado a cada 1 s enquanto ligado);
  - a barra do convidado enche como no solo, parando 0,1 s antes do fim (quem dispara é o host);
  - a checagem por posição no host continua como reserva, com 1,2 u de margem.

## Segundo teste (09/11/2026, mesma run, até o ciclo 1)

- Runa pelo convidado nas duas vezes: `slot 1 says it stands on the rune` → host dispara → `replayed the host's rune trigger #1/#2` no convidado. Ciclo 0: ascensão nos dois; ciclo 1: `CastMasterySkillAndResetPlayer` nos dois (convidado de volta ao nível 1).
- Lordes como marionetes no convidado, kills espelhados (`bossesKilled` igual nos dois), ciclo 0 → 1 e o primeiro pilar quebraram juntos (`AdvanceCycle` nos dois), sem a rede de segurança (`forced=0`).
- Fim: o host desistiu (teste interrompido). Ainda não visto: ciclos 2 a 4, chefe especial (`ActivateSpecialBossRoutine` não apareceu), luta com o chefe principal, vitória e portal Home.
- Duas NRE no convidado em `ECM2...CharacterAnimator.UpdateAll` (perde um frame de animação), durante a leva de lordes; não apareceu antes nos logs guardados. A observar.

## Quinto teste (09/11/2026): introdução e segunda parte só no host

- F7 levou ao chefe nos dois lados (`ActivateSpecialBossRoutine` sem NRE), e a segunda parte começou no host (`26.270.266`, limiar #1). Mas só o host viu a introdução ("Mh'thaeus, the Heretic") e só ele foi puxado para a arena do vazio: o convidado ficou na catedral.
- Causa: as fases do chefe (`MasteryBossComponent`: `InitialPhase` = introdução, `IntermediatePhases[i]` em cada limiar de vida, `FinalPhase`) são `PhaseTransition` com ações (`TeleportEntityToTargetTransformAction`, câmera, `PlayTimelineAction`, faixa com o nome...) que a IA do chefe roda no host. A marionete do convidado não tem IA rodando.
- Correção: `MasteryBossPhase = 152` (host → convidados: tipo e índice da fase). Postfix em `PhaseTransition.StartAction` no host reconhece as fases do chefe principal; o convidado roda a mesma `PhaseTransition` no `MasteryBossComponent` da marionete (com `Setup(marionete)` antes, se preciso). O host loga as ações de cada fase.
- Lordes ainda chegaram depois dos 10 s (fila reabastecida): depois da janela o F7 continua matando só os lordes (entidades de chefe que não são o principal) e esvaziando a fila, a cada 1 s.
- A observar: canalizar o chefe na arena só mostra o efeito de quem canaliza (cada um vê só o próprio).

## Sexto teste (09/11/2026): introdução e arena nos dois; morte do chefe trava o convidado

- Introdução e fase #0 (arena do vazio) tocadas no convidado (`played the host's main boss phase intro / #0`). Ações da fase final no host: `StopEntityAction, StopEntityTeamAction, SetCombatStateAction, KillAllEntitiesAction, PlayEntityAnimationAction, SetGameplayInterfaceVisibilityAction, SetEntityCameraTargetAction, ..., MoveEntityToPositionAction, ..., ResetCameraAction, StopVisualEffectAction, ResumeEntityTeamAction, SetGameplayInterfaceVisibilityAction, PlayPlayerEntityVoiceOverAction`.
- No convidado a marionete do chefe foi reciclada antes da fase final (`#1 survived the network death - recycling instead`: os limiares de vida seguram o dano da morte da rede). A fase final rodou numa marionete já reciclada: a rotina parou no meio, e o `ResetCameraAction` / `ResumeEntityTeamAction` / interface de volta nunca vieram (convidado parado).
- Correção: `ClientPuppets.KillPuppet` não mata nem recicla o chefe principal do Mastery (`MasterySync.KeepForCutscene`); a fase final roda nele, e a troca de cena depois do portal o leva.
- Canalizar o chefe: ainda só aparece para quem canaliza. Falta saber o que é (habilidade do herói, objeto da arena ou efeito da ascensão forçada pelo `SetPlayersAscensionsStateAction`).

## Sétimo teste (09/11/2026): morte do chefe ok; convidado não foi para a arena

- Cena da morte do chefe nos dois (o chefe guardado para a cena final funcionou).
- A fase #0 (arena) foi tocada no convidado, mas ele ficou na catedral, parado em (-4,48, 13,11); no 6º teste a mesma fase o levou para (143,92, 133,91). Sem exceção no log. Suspeita: alguma ação da fase travou no meio no convidado (entre o `StopEntityTeamAction` e o `ChangeSceneAction` / `ResumeEntityTeamAction`), por exemplo o `MoveEntityTeamToCenterAction`, que espera cada entidade do time chegar ao centro.
- Agora: 12 s depois de cada fase do chefe, os dois lados logam o `State` da `PhaseTransition` e a posição (o convidado, a distância até o host). Se depois de uma fase intermediária o convidado ainda estiver a mais de 60 u do host, ele é levado para perto do host (`ModifiedCharacter.Teleport`) e volta a andar (`MovementMode.Walking`).

## Oitavo teste (09/11/2026): por que o convidado às vezes não vai para a arena

- A arena é uma cena aditiva. O `ChangeSceneAction` (rotina `<DoExecute>d__4`, 0x492CA0) espera o `Delay`, abre a tela de carregamento (`LoadingScreenManager.PushAndWaitUntilVisible`), carrega a cena por nome (aditiva) e então teleporta cada `GameManagerUtil.PlayerEntities` para **posição do jogador − posição da entidade da ação + deslocamento fixo**. A entidade é a do `Setup` da transição: o chefe. Antes, o `TeleportToSpawnPositionAction` da mesma fase leva o chefe para o ponto onde ele nasceu (`_spawnPosition`, gravado no `Setup`).
- No host isso fecha: o chefe está no ponto de nascimento, e o jogador cai na arena. No convidado, a marionete do chefe segue a posição do host a cada snapshot. Se o carregamento da cena demora mais no convidado (as duas janelas no mesmo PC, uma sem foco), o chefe do host já foi para a arena, a marionete foi junto, e a conta "jogador − chefe + deslocamento" devolve o convidado para perto da catedral. No 6º teste os carregamentos terminaram juntos e deu certo; no 7º e no 8º, não. Por isso parecia aleatório.
- Correção: enquanto uma fase do chefe roda no convidado (`State` Waiting/Ongoing, até 25 s), a marionete do chefe principal não segue a posição do host (`MasterySync.HoldBossPuppet` no começo do `ClientPuppets.DriveMovement`); fica onde a própria fase a pôs. Acabada a fase, volta a seguir o host. A fase só recebe `Setup` de novo se o `Setup` da marionete não tiver rodado (um `Setup` novo gravaria a posição atual do chefe como ponto de nascimento).
- A reserva dos 60 u continua.

## Nono teste (09/11/2026): tudo funcionou

- Convidado levado para a arena (`Boss phase #0 12 s later: state=Completed, me at (138.35, 0.00, 129.35), 4 u from the host`), introdução, arena, morte e vitória nos dois. Logs sem erro nem exceção.
- Restos: no convidado o chefe morto continuava com a aparência de vivo (no host ele fica cinza) e com a barra de vida no topo. Causa: a marionete era guardada sem morrer. Agora ela morre do jeito do jogo, como no host (os limiares de vida são limpos antes, para o dano da morte passar), e só não é reciclada por nós; se ainda assim sobreviver, a barra é tirada (`DeregisterBossEntity`) e ela fica para a cena.
- O aviso "Local player has not cast for N s" aparecia durante as cenas do chefe (o jogo desliga o combate): não sai mais enquanto uma fase do chefe roda (`MasterySync.BossCutsceneRunning`).

## Limites

- Testado num PC (duas janelas); falta pela Steam.
- Efeito de canalizar o chefe na arena: cada jogador só vê o próprio. Origem ainda não identificada.
- Ciclos 1 a 3 jogados de verdade só até o ciclo 1 (2º teste); os testes seguintes pularam para o chefe com F7.
- O perfil do convidado precisa ter o Mastery do personagem dele? O `RunStart` manda o tier e o nível do host (o convidado entrou sem problema nos testes, com o mesmo perfil).

## Teclas de teste (host, `[Debug] TestKeys`)

- **F7** (numa partida de Mastery): vai direto para a luta com o chefe principal. Mata todos os inimigos menos o chefe, para os spawns (inclusive os lordes na fila: `BossSpawnProcessor._pendingBosses = 0`), tira o objetivo dos lordes, desliga uma runa armada, põe o ciclo no último e esconde todos os pilares menos um (com `ObjectiveAmountNeededToDestroy = 0`). No frame seguinte o `UpdateAll` do jogo quebra esse pilar: `DestroyPillarRoutine` → `AdvanceCycle` → `GoToNextCycleRoutine` → `ActivateSpecialBossRoutine` (0x428C60), que acorda o chefe (`BossSpawnProcessor.ActiveSpecialBoss` 0x804EA0 troca o `InvulnerabilityTime` infinito pelo normal). O convidado faz o mesmo do lado dele quando o `MasteryState` traz o bit "pulou para o chefe".
  - Versões anteriores: enchia a runa ciclo a ciclo, depois terminava um ciclo por toque. No 3º teste o ciclo terminou certo, mas a runa do ciclo seguinte (o "portal") trouxe mais 9 lordes e mais os 9 que ainda estavam na fila; os jogadores morreram.
  - 4º teste: o chefe acordou, mas `ActivateSpecialBossRoutine` deu NRE nos dois lados no `DisruptThePillarsObjective` nulo (o jogo o cria no fim do `SpawnBossRoutine` da primeira runa, que o pulo cortou). Sem o resto da rotina: sem a introdução do chefe no convidado, sem o rastreador e com `IgnoredByEntitySearchQueries` ainda ligado (as skills não miravam o chefe), e os lordes já na fila continuaram chegando (`HighPrioritySpawnQueue`). Agora o pulo cria o objetivo igual ao do jogo (`SimpleObjectiveWithProgression("MasteryDisruptRitualObjective", 0, pilares)`), esvazia a fila de chefes e por 10 s mata o que ainda aparecer (menos o chefe principal).
  - Segunda parte da luta: as fases do chefe dependem de `HealthComponent.HealthThresholds` (lista de `HealthThreshold`, `Min` em vida absoluta). O `ChangeEntityHealth` para a vida no `Min` do limiar atual e liga `ThresholdChanged`; o `MasteryBossComponent.DoOnUpdate` (0x7C7360), no estado `Intermediate`, então roda o `PhaseTransition` da fase (o portal). No 4º teste o primeiro golpe do F8 parou em 26,27 M de 43,78 M: era o limiar. Depois do pulo, o F7 espera o `CurrentState` do chefe virar `Intermediate` (fim da introdução), mais 3 s, e dá um golpe grande: o jogo faz o resto. Sem `Intermediate` em 40 s, golpeia mesmo assim.
- **F8** (Mastery): mata o chefe principal (zera o `InvulnerabilityTime`, que vem infinito, e bate até 12 vezes: um golpe só tirou ~40% dos 43,7 M de vida no teste); a vitória vem do próprio jogo. Se ele continuar vivo 1 s depois, completa o objetivo dele (como o F10).
- Fora do Mastery, F7/F8 seguem como antes (especiais, objetivo de kills).

## Como testar (duas janelas, `tools\test-local.cmd`)

1. No host (A), aba Mastery, um personagem com Mastery liberado; Lutar. B entra junto.
   - Nos dois: `[mastery] Mastery match (...)`. Em B: `main boss held until the host's arrives` e depois `main boss = the host's (puppet N)`, e `SpawnMasteryPillar`. Sem NRE de `SpawnMainBossRoutine`.
   - B vê o chefe principal e os pilares no mesmo lugar que A.
2. **Runa com o host:** A fica na runa até disparar. Em A: `Host: rune triggered (#1, cycle 0)`; em B: `Guest: replayed the host's rune trigger #1`. Os dois ascendem; os lordes aparecem nos dois.
3. **Runa com o convidado:** no ciclo seguinte, só B fica na runa. Em A: `a guest stands on the rune`, e a runa dispara. Os dois resetam (nível / upgrades) como no solo.
4. **Ciclo:** matar os lordes. Nas linhas `[mastery]`, o `cycle` de B acompanha o de A (`host=c..`); um pilar quebra nos dois (`pillars=1/N`). Se aparecer `lords objective completed here`, o ciclo do B atrasou e a rede de segurança agiu (anotar).
5. **Fim:** matar o chefe principal (ou F10 em A). Vitória nos dois, portal Home nos dois; quem entra leva o outro.
6. Mandar `LogOutput.log` (A) e `LogOutput.1.log` (B).
