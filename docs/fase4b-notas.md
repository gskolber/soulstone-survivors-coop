# Fase 4B: XP e loot no cliente

Testado com duas instâncias no mesmo PC (Desert). A investigação está em `docs/fase4b-investigacao.md`. O código fica em `src/SoulstoneCoop/Loot/LootSync.cs`, com ganchos em `ClientPuppets`, `HostEnemySync`, `RemoteProxy`, `PacketRouter` e `CoopBehaviour`.

## Parte 1: o cliente recebe o loot do host (commit a25e89e)

O usuário confirmou que o cliente coleta XP e a barra sobe.

- **Host, prefix em `LootUtil.ProcessLoot`** (`LootSyncProcessLootPatch`, convive com o `HostLootProxyTeamPatch` da fase 3): cada chamada vira um pacote `LootDrop = 16`, ReliableOrdered, no mesmo canal do `EnemyDied`. Como o `ProcessLoot` roda dentro da morte e o `Died` sai depois, pelo polling, o loot de uma entidade sempre chega antes da morte dela.
  - Formato: `netId u16 | pos 3×f32 | xp i32 | prestige f32 | flags u8 | interval i32 | [6×i32 moedas] | itens (ItemId, chance, min, max) | power-ups (PlayerLevel, SpecialSource, flags)`. Ficou em 54 B por morte no teste.
  - O `netId` vem de `HostEnemySync.FindKilledAt(pos)`: a entidade rastreada já morta na posição exata. Ele só serve de fallback para a lista de itens.
  - Loot pessoal **não** é replicado: um prefix e um finalizer em `SoulstoneItem.OnCollect` ligam `InPersonalLoot`, porque o passo 0 do `RunBreakdown` (minerais da runa de quem coletou) roda síncrono lá dentro.
- **Cliente, replay** (`LootSync.OnLootDrop`): na hora, se a partida local já começou; senão vai pra uma fila.
  - Monta `PlayerProfileCurrencies`, `List<ItemDropRuntimeData>` (via `Global.ItemCollection.GetItemDefinitionById`) e `List<PowerUpRequest>`, e chama `LootUtil.ProcessLoot` com `ReplayingNetworkLoot`.
  - Se a construção da lista de itens falhar uma vez, passa a usar a `PossibleItems` da própria marionete.
- **Crédito de kill:** o `ClientPuppets.KillPuppet` não limpa mais a `AttackerList` e mata com `yieldLoot: true`. O próprio jogo roda os procs on-kill (SkillTree, Runes, PassiveSkills) e os recursos de classe para quem acertou a marionete aqui.
  - O loot da marionete é bloqueado no mesmo prefix do `ProcessLoot`, por `ApplyingNetworkDeath && !ReplayingNetworkLoot`.
  - `killCredits` no log `[puppets]` conta as mortes em que o jogador local estava na lista.
- **Bloqueios no cliente:** `LootDropperByHealthPercentComponent.UpdateAll` devolve false, porque toda instância no cliente é marionete de boss e o HP vem do snapshot. `SurvivorsGameManager.OnPowerUpCollected` também devolve false, porque o power-up de boss abriria o portal no mundo do cliente. A escolha do power-up continua.

## Parte 2: XP de equipe

Decisão do usuário: quando qualquer um coleta uma orb, os dois ganham o XP, e cada um mantém o próprio nível e os próprios upgrades. Ele confirmou que funcionou.

- **Por que não orb a orb:** no modo instanciado, as orbs vivem nos `NativeArray` do `XpCpuState`, sem id, e o `RemoveCompletedJob` compacta os índices. Não dá pra remover "a orb N" de fora.
- **Cada drop de XP existe num mundo só**, o do jogador **vivo** mais perto da posição (`LootSync.ClientOwnsXp`).
  - O cliente conta como vivo pelo `RemoteProxy.RemoteAlive`, que vem do `ClientState.dead`.
  - Se o cliente for o dono, o host zera o `experience` por `ref` no prefix (a orb não cai no host) e manda o XP no `LootDrop`.
  - Se o dono for o host, o `LootDrop` vai com `xp = 0`.
  - Prestige, moedas, itens e power-ups não mudaram: o jogo já dá um por jogador.
- **Compartilhar:** um prefix em `ExperienceComponent.AddExperience` (`LootSyncAddExperiencePatch`) soma o que o jogador **local** coletou e manda `XpShare = 17` (f32), ReliableOrdered, a cada 0,1 s.
  - O `AddExperience` é o funil das orbs nos dois modos do `XpDropper`, do `ExperienceProjectile` e dos cheats.
  - O valor vai **bruto**, antes do stat de XP. Quem recebe chama `AddExperience` com a flag `ApplyingSharedXp`, que evita eco, e aplica o stat de XP dele próprio. A runa de XP é aplicada no drop pelo jogador do mundo onde a orb caiu.
- **Medido no teste:** os dois lados terminaram as janelas de 5 s com o **mesmo XP** (`1254+0` e `1518+0` nos dois logs).
  - `xpShareSent` de um lado bateu com `xpShareRecv` do outro.
  - O host mandou de 480 a 1440 de XP por janela para orbs do cliente e ficou com 360 a 720.
  - `recv = replayed`, `errors = 0` e `blockedPuppetLoot` igual às mortes, então não houve drop em dobro.
  - Nenhum erro de patch nem de trampoline.
- **Jogador morto:** o `XpShare` recebido por um jogador local morto **não é aplicado**. Fica retido (`xpShareHeld`) e entra quando ele reviver; se a partida acabar, é descartado (`xpShareDiscarded`). Motivo: o `AddExperience` num morto pode abrir o level-up, prender o `LevelUpTime` em 0 e congelar os dois pela pausa sincronizada. **Não testado em jogo; validar junto com a 4A.** Quando o estado "caído" (`ReviveOnDeath`) existir, ele entra no mesmo check do `LootSync.ApplyShareIn`.
  - **Conferido no código (1.6.1):** o caído já entra nesse check, e o XP que o próprio jogador caído pega também fica retido (`OnLocalAddExperience`, telemetria `ownXpHeldWhileDown`). Tudo entra quando ele levanta: revive, vitória ou portal (o revive do portal roda antes da troca de cena). Só é descartado se a run acaba com ele caído.

## Telemetria `[loot]`, a cada 5 s

- **Host:** `sent` / `bytes` / `xp` (XP enviado), `items`, `powerUps`, `skippedPersonal`, `captureErrors`, `xpDropsToClient` / `xpDropsToHost` (contagem e XP).
- **Cliente:** `recv` / `replayed` / `pending` / `dropped`, `builtItems` / `itemFallbacks` / `errors`, `blockedPuppetLoot`, `blockedPortal`.
- **Os dois:** `xpShareSent` / `xpShareRecv` (soma e pacotes), `xpShareApplied` / `xpShareHeld` / `xpShareDiscarded`, nível (`_experience+_experienceToAdd`), `GameStats` (inimigos/elites/bosses) e `RunesComponent.KillCount`.

## O que ainda não foi exercitado

- **Itens e power-ups:** no Desert inicial vieram `items=0` e `powerUps=0`. A lista `List<ItemDropRuntimeData>` montada pelo interop e o fallback pela marionete ainda não rodaram. O bloqueio do portal (`blockedPortal`) também não.
- **Procs on-kill do cliente:** `killCredits` sobe, mas `runeKillCount` ficou em 0 porque o personagem não tinha runa de kill. Falta testar com runa ou classe que use kill (Rage, SoulHarvest, cura após kills).
- **O próprio level-up do cliente:** no teste da parte 1 só o host subiu de nível (a pausa congelou o cliente certinho). Na parte 2 os níveis andam juntos.
- **Baús** só existem no host (o spawner é desligado no cliente). O cliente recebe a parte dele quando o host abre, mas não consegue abrir.

## Pacotes

`LootDrop = 16` e `XpShare = 17`. As próximas fases começam no **18**.
