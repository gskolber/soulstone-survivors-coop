# Próximos itens: investigação no código do jogo

Levantamento feito depois da 1.1.0, para decidir a ordem do que falta (`roadmap.md`, "Próximos itens"). Nada aqui foi implementado nem testado.

**Fonte.** Assembly de interop `BepInEx/interop/Assembly-CSharp.dll` (classes, campos e assinaturas) e `GameAssembly.dll` do jogo 1.5.3.2. O endereço de cada método vem do `MethodAddressToToken.db` do interop: o token ali é o do assembly de interop, não o do `global-metadata.dat`. Quem chama quem saiu de uma varredura das chamadas diretas (`call`/`jmp rel32`) entre métodos conhecidos. Chamadas virtuais não aparecem por esse caminho, então "ninguém chama" pode ser só despacho por vtable. Os endereços são RVAs, no mesmo formato dos outros docs (`CastSkill` = 0x7F0D00).

## 1. Baús e minérios para o convidado

**Como o jogo faz.**
- O baú é um `InteractableRewardObject` (base dos baús comuns e do `InteractableGuardedRewardObject`, o baú com guardiões). Quem cria:
  - `GameManagerInteractableGuardedRewardSpawnerComponent` (`OnUpdate` 0x80E870 → `SpawnReward` 0x80ED50, pelo progresso do mapa);
  - `MapTask.CompleteTask` 0x45C350 → `InteractableRewardObject.Setup` 0x456800.
- A abertura é um `InteractableOneTimeTrigger` (`InteractionTime`, barra de progresso, `InteractingEntity`, `TimeInteracting`):
  - `OnTriggerEnter` 0x456060 chama **`Entity.IsPlayer`** e `UnitUtil.GetTargetTransform`;
  - `OnTriggerExit` 0x4561B0 solta.
- O estático `InteractableRewardObject.UpdateAll` 0x456A50 vê o gatilho consumido e chama `InteractableTriggerObject.ConsumeTrigger` e **`LootUtil.ProcessLoot`**. Ele também trata os minérios: chama `GameManagerMineralSpawnComponent.GetMineralPrefabAccordingToTier` e `VoiceOverManager.PlayMiningStart`.
- `InteractableGuardedRewardObject.DoUpdate` 0x454ED0 usa `GameManagerUtil.get_PlayerEntity`, ou seja, só o jogador local. `TrySpawnGuardians` 0x455660 cria elites por `EnemyPrefabsUtil.SpawnEnemyEntity`.

**Por que o convidado não abre.** O proxy do convidado no host foi feito de propósito com `IsPlayer() == false` (não tem Inventory; `RemoteProxy.cs`). Por isso o `OnTriggerEnter` ignora o proxy. No convidado o baú nem existe, porque o spawner é desligado. O loot já chega certo: o `ProcessLoot` do baú passa pelo `LootSyncProcessLootPatch` e é repetido no convidado.

**Plano.**
1. **Host:**
   - postfix em `InteractableRewardObject.Setup` e `InteractableGuardedRewardObject.Setup` manda `RewardSpawn` (id, endereço do prefab em `Metadata.RewardObjectAddress`, posição, rotação e tier);
   - `RemoveRewardObject` 0x4566F0 manda `RewardDespawn`.
2. **Convidado:** instancia o prefab só como visual: gatilho e colisor desligados, sem `UpdateAll` próprio. Mostra a barra de progresso com o valor que o host manda.
3. **Abrir:** o convidado diz que está dentro ou fora do raio (`RewardInteract`, id + bool). O host põe o proxy em `InteractingEntity`, como o `OnTriggerEnter` faria com um jogador, e o resto (tempo, `ConsumeTrigger`, `ProcessLoot`) segue igual.
   - Alternativa a testar: deixar o `OnTriggerEnter` aceitar o colisor do proxy. Depende de o proxy ter colisor e rigidbody que disparem o trigger.
4. **Guardiões:** já viram marionetes, porque nascem no host. Falta ver se o `DoUpdate` (que olha só o `PlayerEntity` do host) precisa considerar o proxy para acordar os guardiões.

**Esforço:** médio. **Risco:** baixo, porque o loot já funciona. **Ganho:** alto, porque hoje o convidado não abre nada em nenhuma run. Isso também é pré-requisito da Cruzada (portas, caverna dos goblins).

## 2. Mira contínua do channeling do parceiro

**Como está.** `CastSync` manda o `TargetFrontPosition` só no início, e o `RemoteCaster` monta o `GetTargetFrontPosition` como `_ => copy.Front` (`RemoteCaster.cs`, `TryGetFuncs`). O `copy.Front` só muda no próximo cast. `ChannelingSkill.OnUpdate` 0x516B00 consulta o func a cada frame, então basta o valor mudar.

**Plano.**
- O emissor já guarda o canal aberto (`CastSync.LocalChannels`). Para quem tem o func de front, ele chama esse func a ~10 Hz e manda `SkillChannelAim` (chave + vetor, Sequenced, por jogador).
- O receptor atualiza `copy.Front` / `copy.Ground`.

**Esforço:** pequeno. **Risco:** baixo (só visual). **Pacote:** novo, a partir do 120.

## 3. Ataques animados dos inimigos comuns no convidado

**Como está.** Só os rastreados com `ReplayCasts` (boss, elite, especial) mandam `EnemyCast` (`HostEnemySync.cs`). Replay completo para todo mob seria caro.

**Como o jogo anima.** `MeleeSkill.DoActivate` 0x5371A0 (e os outros tipos de skill) chama `SkillAnimationTrait.Activate(AnimatorComponent)` 0x55E710, que só faz `AnimatorComponent.CrossFade(AnimationName, Fade, AnimationLayer)` 0x5DCBF0. A assinatura só tem referências, então é seguro patchar (regra da 4C).

**Plano.**
- **Host:** postfix em `SkillAnimationTrait.Activate`. Se o `AnimatorComponent` é de um inimigo rastreado sem `ReplayCasts`, enfileira (netId, hash do nome, camada, fade).
- Manda em lote por frame (Sequenced), só para inimigos a menos de N u de algum convidado.
- **Convidado:** `CrossFade` no `AnimatorComponent` da marionete. O nome vem de uma tabela hash → nome montada com os `SkillAnimationTrait` do prefab, ou vai direto como string curta.

**Esforço:** pequeno a médio. **Risco:** baixo (sem efeito, sem dano). **Atenção:** volume com 200+ mobs; o limite por inimigo e por distância resolve.

## 4. Auras e escudos sempre ligados do parceiro

**O que é "sempre ligado".**
- `AuraSkill` **passa** pelo `CastSkill`: `Skill.Activate` 0x5619B0 → `AuraSkill.DoActivate` 0x5099C0 → `SkillAuraEffectContinuous/Instant.Activate`. Então já deveria ser replicada pela 4C; vale confirmar em jogo.
- `RotatingShieldSkill` **não tem `DoActivate`**: o `OnUpdate` 0x55D830 cria os projéteis sozinho (`ResourcesUtil.GetPrefabInstance`, `StaticProjectile.Setup`, `SubProjectileEmitter.Setup`) com base em `BaseProjectiles`, `ExtraProjectiles` e `RotationSpeed`. Nunca passa pelo `CastSkill`, por isso o parceiro não vê.

**Plano.**
- O emissor manda a lista de skills ativas do tipo "sem cast" quando ela muda. Gancho em `SkillCasterComponent.AddSkill` 0x7EFBF0 / `RemoveSkill` 0x7F2DD0, mais os `SkillModifiers` relevantes (contagem de projéteis).
- O receptor adiciona a skill no lançador remoto (`RemoteCaster`), cujo dano, status e empurrão já são bloqueados. O `OnUpdate` dela roda sozinho seguindo o fantasma.
- Para os modificadores, chamar `OnSkillModifiersChanged` com os valores recebidos.

**Esforço:** médio. **Risco:** médio: o `RemoteCaster` hoje só toca cópias por cast, e uma skill "viva" nele é caminho novo.

## 5. Reentrada de verdade (nível, skills e itens)

> **Feito na 1.4** (atrás de `AllowRejoin`): `reentrada-notas.md`. O plano abaixo foi seguido, com o id da run no `RunStart` para não aplicar a build numa run diferente.

**Achado principal.** O jogo já sabe salvar e devolver o estado do jogador no meio da run: é o que ele faz entre as fases do Endless (`portais-investigacao.md`, seção 2).
- `GameModeBaseData.SetDataBasedOnPlayerEntity` 0x4FF600 grava nível, XP e XP necessário, poderes coletados (`CollectedPowerUps`), runas (`SelectedRunes`), banidos, slots travados, inventário (`InventoryComponent.SaveMatchInventoryData`), stats somados e tempo de partida.
- `GameManagerUtil.InitialEndlessData` guarda isso entre cenas. `GetAndRemoveInitialData` 0x6ECEF0 lê, e `SetupPlayerForMatch` 0x6F46E0 devolve tudo ao jogador na cena nova.

**Plano.**
1. **Convidado, na queda (ou ao sair com a reentrada ligada):** chama `SetDataBasedOnPlayerEntity` no próprio jogador e guarda o `GameModeBaseData` na memória.
2. **Na volta:** antes do `RunStart` da reentrada carregar a cena, põe esse objeto em `GameManagerUtil.InitialEndlessData`. O `SurvivorsGameManager.Start` faz o resto.
3. **Cuidados:**
   - o `Start` também lê do `EndlessData` os afixos e o `EndlessLevel`; é preciso manter os do host e não somar fase;
   - o host guarda o slot do jogador (desenho da `fase8-notas.md`, item 5);
   - se o jogo do convidado fechou, o estado se perde. Para sobreviver a isso, teria que serializar o objeto, e o jogo só serializa no perfil.

**Esforço:** médio a grande. **Risco:** médio (mexe no início da run). **Ganho:** alto.

## 6. Cruzada

É um modo à parte, com muitas peças próprias:
- `CrusadeLobbyGameManager` (cena de lobby);
- `CrusadeMapSelectionPanel` / `MapSelectionCrusadeScreenPanel` (escolha de mapa com opções sorteadas);
- `CrusadeGameManager` (214 métodos): sangue de titã, fragmentos de chave, sala do boss, `CrusadeDoorObject`, `CrusadePlayerBarrier`, `GoblinCaveGameManager` com `RewardChests`, as timelines do Void King, `SaveCrusadeGameData`.

Depende do item 1 (portas e baús são interativos) e do trabalho de portais. **Esforço:** grande. Fica por último.

## Ordem sugerida

| # | Item | Esforço | Por quê |
|---|---|---|---|
| 1 | Baús e minérios para o convidado | médio | Única limitação que tira algo do convidado em toda run; base para a Cruzada |
| 2 | Mira do channeling | pequeno | Ganho rápido, mesmo canal de casts |
| 3 | Ataque animado dos inimigos comuns | pequeno a médio | O que mais se nota visualmente; só animação |
| 4 | Escudos giratórios do parceiro | médio | Visual; caminho novo no lançador remoto |
| 5 | Reentrada completa | médio a grande | O jogo já tem o salvar/devolver; falta o desenho do slot no host |
| 6 | Cruzada | grande | Depende do 1 e dos portais |

Os itens 2, 3 e 4 mexem só em visual e podem sair juntos numa versão.
