# Ascensão e troca de modelo do parceiro

## O problema

O fantasma do parceiro era montado uma vez, com a skin do personagem. Quando ele ascendia (por exemplo, o Legionnaire depois de resgatar os centuriões), o modelo dele mudava na máquina dele, mas os outros continuavam vendo o modelo normal.

## Como o jogo faz

- As ascensões e transformações trocam ou acrescentam um `PlayerPrefabReferences` embaixo da entidade do jogador:
  - `SiegeExpertComponent` (Legionnaire), `DemonicBindingComponent`, `DivineInspirationComponent`, `ArcanePowerComponent`, `ConcentrationComponent`;
  - montaria do `NaturesGuardianComponent`, robô do `MechatronComponent`, `MyrmidonFavorComponent`.
- O modelo de ascensão vem de `PlayerPrefabReferences.InstantiateAscensionModelRoutine(entity, originalModel, ascensionModelPath, out)` (MoveNext 0x8137D0), com `ResourcesUtil.Load(caminho)`.
- `SiegeExpertData.ActiveScaleFactor` também aumenta o jogador.

## O que o mod faz (`AscensionSync.cs`)

- **Quem envia** (os dois lados, a cada 0,25 s):
  - monta a lista dos modelos ativos do jogador local: nome, caminho em Resources (capturado num prefix do `InstantiateAscensionModelRoutine`) e escala;
  - junta a escala da própria entidade;
  - manda `ModelState = 142` quando muda e a cada 3 s (ReliableOrdered, repassado pelo host).
- **Quem recebe:**
  - o fantasma guarda o modelo dele (da skin) e monta cada outro modelo uma vez, com `VisualGhostFactory.BuildExtraModel` (holder inativo, armas do Hello quando o modelo tem `ItemFollowBoneUtil`, strip);
  - o prefab é procurado pelo caminho, depois em `Characters/<nome>`, depois entre os prefabs carregados com esse nome;
  - liga os modelos que o parceiro está usando e desliga os outros;
  - o `ModelAnimator` passa para o modelo visível, e `RemotePlayer.ModelVersion` sobe para que a locomoção (`PlayerSync`) e as animações de magia (`RemoteCaster`) peguem o animator novo;
  - a escala da entidade vai para a raiz do fantasma.
- Um modelo que não é encontrado deixa o normal ligado, e o log avisa uma vez: `No prefab for slot N's model '...'`.

## 1.3.1: o que faltava no primeiro teste

O teste com Legionnaire mostrou o fantasma igual ao normal. O log de quem ascendia dizia `Own models now: LegionnaireRegular@1,80`: a ascensão do Legionnaire **não troca o modelo**. Ela aumenta o mesmo modelo (`ActiveScaleFactor`), acrescenta o `ModelToAddOnActivatePrefab` e invoca os legionários.

- **Escala do modelo principal:** o receptor só aplicava escala aos modelos extras. Agora a escala enviada vale para o modelo principal também.
- **Objeto acrescentado:** `SiegeExpertComponent._modelOnActivate`. Quem envia junta, no `ModelState`, o nome dele, o nome do transform pai (um placeholder do modelo) e a pose local. Quem recebe acha o prefab em `SkillTreeSiegeExpertTalentSO.Data.ModelToAddOnActivatePrefab` (o SO fica carregado), deixa só o visual (com efeitos) e prende no transform de mesmo nome do modelo visível.
- **Legionários invocados:** o `SummonSync` não achava a skill que invoca (`FindSkillKey`): `TryCast` e `GetComponentInParent<Skill>` lançavam "type argument 'T' violates the constraint" nesses efeitos, então a chave ia vazia e ninguém conseguia montar o clone (`No prefab for the partner's EntitySummon 'SummonRagedLegionnaires-Summon' (skill '')`). Agora o efeito é reconhecido pela classe Il2Cpp (`IsAssignableFrom`) e embrulhado pelo ponteiro, e a skill é achada subindo pelos pais. O mesmo vale para o golem do caos e as criaturas do Demon Hunter.

## Efeitos de ativação (`AscensionFx.cs`)

- **Janelas:** em cada componente de mecânica (Legionnaire, Demon Hunter, ArcanePower, DivineInspiration, Concentration, Mechatron, MyrmidonFavor, NaturesGuardian, SoulHarvest, CursedShrine, GhostShip, MachineSpirit, ElementalOverload, AncestralRitual, Mana, Rage, Reanimation, ChaosEnergy, CallOfTheWild), os métodos sem parâmetro `Activate`, `ActivateAscension`, `FinishOrClearAscension`, `FinishAscension`, `Ride`, `Unride`, `ForceAscensionToActivate` e o `UpdateAll` estático ganham um prefix e um finalizer que abrem e fecham uma janela.
- **O que é capturado dentro da janela:**
  - `SkillUtil.RunOneShotVfx` (por `VfxReferences` ou por caminho);
  - `SkillUtil.RunFireAndForgetVfxReference`;
  - `AudioUtil.PostEventFireAndForget` (posição ou objeto);
  - `WorldLightProcessor.ChangeLight`.
  Cada chamada sai como `AscensionFx = 146`, com a posição relativa ao jogador e se segue o jogador, no máximo 30 por segundo.
- **Quem recebe:**
  - roda o mesmo helper no fantasma (o VFX pelo prefab carregado com esse nome, ou pelo caminho; o som no fantasma), até 45 u de distância;
  - a luz do mundo passa por um `WorldLightProcessor` próprio por parceiro, montado com os valores recebidos (cores, temperatura, intensidades, duração), e volta ao original no fim da ascensão, quando o parceiro sai ou na troca de cena.
- Um VFX de outro personagem que não está carregado aqui não aparece (o log avisa uma vez: `Effect '...' isn't loaded here`).

## Skin de mod (`ModSkinLoader`, em `VisualGhostFactory.cs`)

Se este jogo tem a mesma skin de mod instalada (ela está na nossa coleção), o fantasma nasce com a skin padrão e carrega a de mod como o jogo faz: `ModManager.InstantiateCharacterSkinRoutine`, assíncrono, embaixo de um holder inativo. Quando ela chega, é limpa como a padrão e toma o lugar dela. A espera máxima é de 30 s. Sem o mod instalado, continua a padrão.

## Companheiros de arma

`CompanionComponent.AddWeaponCompanion` carrega o companheiro de outra pasta de Resources. Um prefix em `AddCompanionType(data, path)` guarda essa pasta, e o `SummonSync` passa a encontrar o caminho (`res`) desses companheiros, então os outros conseguem montá-los.

## Limites

- Os VFX e sons de ativação de um personagem cujos assets não estão carregados no jogo de quem recebe não aparecem.
- Uma skin de mod que só o parceiro tem: os outros veem a padrão.

## Como testar

1.3.1, além do de baixo:

- Legionnaire ascendendo: na tela do outro, o fantasma cresce, ganha o objeto acrescentado, os legionários invocados aparecem, e o flash, o som e a luz do mundo acontecem.
- Logs:
  - de quem recebe: `attachment '...' built`, `'SummonRagedLegionnaires-Summon' is summoned by skill '...'` (no log do dono) e `[ascension] effects ... played=N`;
  - de quem envia: `Effect capture: N mechanic windows, 6/6 effect hooks`.


1. A joga de Legionnaire e resgata centuriões até ascender.
2. Na tela de B, o fantasma de A troca para o modelo de ascensão, maior, com a mesma locomoção e animações de magia, e volta ao normal quando a ascensão acaba.
3. **Logs:**
   - de A: `Own ascension model '...' comes from '...'` e `Own models now: ...`;
   - de B: `Slot 0 model '...' via path '...': built ...` e `Slot 0 now shows '...'`.

## 1.4

- **Troca de skin com o parceiro ascendido:** o `ModSkinLoader` troca o modelo principal e chama `AscensionSync.InvalidateGhost`. Antes, isso só esquecia os modelos de ascensão e os acréscimos já montados, que continuavam embaixo do fantasma (o modelo de ascensão ficava ligado junto com o novo). Agora eles são destruídos e montados de novo no tick seguinte.
- **Acréscimo do Legionnaire:** o `SiegeExpertComponent` do jogador local era procurado uma vez só por entidade. Se a primeira busca viesse antes da árvore de talentos montar o componente, o acréscimo nunca mais era enviado. Agora a busca se repete a cada 5 s enquanto ele não aparece.
- **Modelo de ascensão que não existe aqui:** a falha da busca vale por 20 s (antes, cada fantasma novo varria todos os `PlayerPrefabReferences` carregados de novo).
