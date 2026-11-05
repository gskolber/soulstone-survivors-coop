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

## Limites

- Os efeitos de ativação (VFX de uma vez só, a luz do mundo do `WorldLightFeedback`) e os sons não são enviados. As magias lançadas na ativação já passavam pelo `CastSync`, e as unidades invocadas pelo `SummonSync`.
- Uma skin de mod tem caminho de mod: os outros continuam vendo o modelo padrão.

## Como testar

1. A joga de Legionnaire e resgata centuriões até ascender.
2. Na tela de B, o fantasma de A troca para o modelo de ascensão, maior, com a mesma locomoção e animações de magia, e volta ao normal quando a ascensão acaba.
3. **Logs:**
   - de A: `Own ascension model '...' comes from '...'` e `Own models now: ...`;
   - de B: `Slot 0 model '...' via path '...': built ...` e `Slot 0 now shows '...'`.
