# Passivas e "mini-jogos" do parceiro, só para ver

## O que muda

Os objetos da mecânica própria de cada personagem aparecem para os outros jogadores, mas ninguém mais consegue usá-los. São eles:

- os legionários caídos do Legionnaire;
- a pedra do Demon Hunter;
- fendas, fragmentos e orbes;
- santuários e portais torii;
- altares e pegadas;
- o tesouro do navio fantasma e o cristal do Machine Spirit;
- os orbes elementais;
- os cristais de mana e de alma do apocalipse.

## Como o jogo faz

Cada objeto vem de um `SkillTree*SO` aplicado ao jogador, e passa pelo próprio `Setup` quando é colocado. Ele só existe na máquina do dono, e só o dono interage com ele (gatilhos e coleta). Os tipos e as assinaturas do `Setup`, todas seguras para o trampolim (só referências, floats, ints e bools):

| Tipo | Setup |
|---|---|
| `FallenLegionnaireComponent` | `(float timeToRescue)` |
| `DemonicBindingStone` | `(float, float)` |
| `ArcanePowerRift` | `(float)` |
| `DivineInspirationShard` | `(float, float, float, DivineInspirationOrb)` |
| `DivineInspirationOrb` | `(float)` |
| `CursedShrineToriiGate` | `()` |
| `CursedShrineShrine` | `(float, float)` |
| `ChaosEnergyAltar` | `(VfxReferences)` |
| `CallOfTheWildFootprint` | `(bool)` |
| `GhostShipTreasure` | `(Entity, float, float, float)` |
| `MachineSpiritMineral` | `(Entity, float)` |
| `ElementalOverloadOrb` | `(int)` |
| `CollectableSecretObject` | `(Action, Func, Action)` |

`CollectableSecretObject` também é o coletável do próprio mapa. Por isso só são espelhados os prefabs `SkillTreeManaTalentSO.ManaCrystalPrefab` e `SkillTreeSoulHarvestTalentSO.ApocalypseSoulCrystalPrefab`.

## O que o mod faz (`Summons/PassiveObjectSync.cs`)

- **Dono:**
  - um postfix em cada `Setup` (aplicado à mão, um por tipo) registra o objeto;
  - `PassiveSpawn = 143` sai na hora e a cada 3 s;
  - `PassiveStates = 145` leva a posição dos que se mexeram, a ~10 Hz (Sequenced);
  - `PassiveDespawn = 144` sai quando o objeto fica inativo ou é destruído (conferido a cada 0,25 s).
- **Os outros:**
  - procuram um prefab carregado do mesmo tipo e nome (os assets de árvore de habilidade de todos os personagens ficam em memória);
  - montam um clone embaixo de um holder inativo e deixam só o visual com `StripToVisualOnly(..., keepEffects: true)`: renderers, partículas, VFX Graph, luzes e sprites, sem scripts, colisores ou rigidbody;
  - o clone desliza até a posição recebida;
  - tudo some ao trocar de cena ou quando o dono sai.

## Como testar

1. A joga de Legionnaire. Na tela de B aparecem os legionários caídos no mesmo lugar.
2. B passa por cima deles e nada acontece. A resgata, e eles somem na tela de B.
3. Repita com o Demon Hunter (a pedra) ou outro personagem com mini-jogo.
4. **Logs:**
   - de A: `[passives] Own <Tipo> #N '...'`;
   - de B: na telemetria `[passives] ... built=N missing=0`;
   - um `No prefab for ...` indica um tipo cujo prefab não está carregado no outro jogo.
