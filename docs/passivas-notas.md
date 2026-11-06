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

## 1.3.1: marcador quando o prefab não está carregado

No teste, o jogo do Legionnaire não tinha a pedra do Demon Hunter carregada (`No prefab for slot 0's DemonicBindingStone`): o prefab fica num componente do personagem, não nos SOs carregados para todos. Nesse caso aparece um marcador no lugar: um anel no chão e um feixe curto, na cor do dono (`GhostOverhead.ColorFor`), com o material das linhas do caído (`DownedMarkers.LineMaterial`). Também é só visual.

## 1.4.1: a árvore do dono e o tamanho da área

No teste da 1.4.0 a área da Demon Hunter continuava só um anel pequeno no jogo do Legionnaire. Dois ajustes:

- **Prefab de verdade:** cada personagem tem a árvore de habilidades em `Resources` (`skill-tree-survivors-character/<char>-skill-tree/<char>-skill-tree-collection`, `<char>` = `CharacterName` do Hello em minúsculas). Os talentos dela referenciam os objetos do mini-jogo. Quando o prefab não é achado, o `Build` carrega a árvore do dono uma vez por personagem (`Resources.Load`, referência guardada na sessão) e procura de novo. Log: `Loaded <Char>'s skill tree (N assets) for slot N's objects`.
- **Segundo teste da 1.4.1:** a árvore carregou (`Loaded DemonHunter's skill tree`), mas a pedra não veio: o jogo carrega os objetos de mini-jogo por caminho, todos em `Resources` `powers/` (`powers/demonicbinding/demonicbindingstone`, `powers/cursedshrine/...`, `powers/callofthewild/...`, `powers/ghostship/...`, `powers/machinespirit/...`; uns 26 assets). Agora, na primeira falta, o `Build` carrega `powers/` inteiro uma vez (`Loaded the mini-game prefabs ('powers/', N assets)`), e só depois a pasta da árvore do dono (`Resources.LoadAll` de `skill-tree-survivors-character/<char>-skill-tree`, com os talentos em subpastas). O clone continua sem scripts: se o efeito do círculo depende do script da pedra para crescer ou animar, ele aparece no estado inicial do prefab.
- **Marcador no tamanho certo:** o dono mede a área do objeto (o maior collider: esfera, cápsula, caixa ou bounds; acima de 30 u é ignorado) e manda como `radius` no fim do `PassiveSpawn`. Leitores antigos param antes dele. Se o prefab ainda não aparecer, o marcador usa esse raio, com um segundo anel mais largo e fraco por dentro para ler como zona.

## 1.4.1: estado das partes (genérico, todos os heróis)

Terceiro teste: a pedra foi construída com o prefab de verdade (`Loaded the mini-game prefabs`, `built=1`), mas o círculo de runas não apareceu. O clone não roda scripts, e é o script da pedra que mostra e dimensiona o círculo (`DemonicBindingStone.CircleVfxTransform`, pelo raio `_demonicStoneRadius` do `DemonicBindingComponent`), além da barra de progresso (`ProgressBarRenderer`) e dos VFX de ativação/coleta.

Em vez de tratar herói por herói, o dono manda o estado de cada parte do objeto: `PassiveParts = 147` com hash do caminho da parte (FNV-1a, sem `(Clone)`), `activeSelf` e `localScale`, só o que mudou a 5 Hz e tudo junto a cada 3 s com o `PassiveSpawn`. A cópia indexa as próprias partes pelo mesmo caminho e aplica. O que o script instancia em tempo de execução fora do prefab não tem par (conta como `unmatched`). Parâmetros de material e de VFX Graph (o preenchimento da barra) não vão: ficam para um item próprio.

Log: `Layout (owner) '<nome>'` e `Layout (copy) '<nome>'` (partes, ligado/desligado, escala; até duas vezes por lado) e, na telemetria, `parts sent=N recv=N applied=N unmatched=N`.

## 1.5.0: espelhamento genérico (sem lista de tipos) e valores de material

Arquivo `Summons/PassiveObjectSync.Generic.cs`. A sonda mostrou que a maioria dos objetos de mecânica (navio, timão, imediato, baú do Pirata; pira e chama da Xamã; linha e cristal do Machinist) é `Instantiate` direto na raiz da cena, e só alguns efeitos vêm do pool (névoa do navio).

- **Dono (`ScanPowers`, 2 Hz, só com par conectado):** raízes de todas as cenas carregadas cujo nome (sem `(Clone)`) é um prefab de `powers/`, mais instâncias novas do pool (`ResourcesUtil.PrefabIdByInstanceId`) de um prefab de `powers/`. Raízes que não são de `powers/` ficam num conjunto por ponteiro (a varredura seguinte quase não custa). Viram `Owned` do tipo `255` (`PowersType`) com o nome do prefab, pelo mesmo `Track` dos patches de `Setup`; a cópia usa o prefab de `powers/` com esse nome. Pulados: o que um patch de `Setup` já rastreia, névoas de mundo (`WorldFog`: escureceriam a tela do outro; o `AscensionFx` já mostra a do navio), o que está sob um objeto nosso (`SoulstoneCoop_`) e um efeito que o `AscensionFx` acabou de tocar para um parceiro ali perto (`AscensionFx.WasReplayed`, 4 s, 6 u).
- **Materiais (`PassiveMaterials = 148`):** por renderer do objeto (até 24), as propriedades número/faixa/cor cujo nome sugere animação (fill, dissolve, fade, progress, alpha, opacity, amount, cutoff, intensity, emission, tint, visib, reveal, color; até 12 por renderer), lidas 5×/s (bloco de propriedades primeiro, senão o material) e mandadas quando mudam, tudo a cada 3 s. Chave: hash do caminho do renderer + hash do nome da propriedade. A cópia resolve o nome no próprio shader e aplica com `MaterialPropertyBlock` (sem vazar material).
- **Telemetria** (linha `[passives]` a cada 10 s): `scan found/foreign/fogsSkipped/roots`, `mats sent/recv/applied/unmatched`.
- **Fora:** animações (imediato cavando, ritualistas dançando), cristais giratórios do Machinist (`TemporaryRotatingSkill`, item próprio).

### Como testar (1.5.0)

Em A, F2 em cada herói e use a mecânica; olhe em B:
1. **Demon Hunter:** o círculo de runas e a barra enchendo ao capturar.
2. **Machinist:** o mineral aparece e a barra de mineração anda.
3. **Pirata:** o navio, o timão e o imediato; a barra de escavação do tesouro.
4. **Xamã:** a pira e a chama.
5. **Logs:** em A, `[passives] Own powers #N '<nome>'`; em B, `built=N` e `mats ... applied=N` com `unmatched` baixo. Nenhum objeto duplicado (uma cópia de cópia apareceria como `Own powers` em B).

## 1.6.0: animações dos objetos (`PassiveAnims = 149`)

O imediato cavando, o baú abrindo e os ritualistas dançando têm `Animator` próprio, movido pelo script, que a cópia não roda. O dono manda, por animator (hash do caminho, até 8) e camada (até 3), o `fullPathHash` do estado atual, o tempo normalizado e a `speed`, quando o estado muda e tudo a cada 3 s. A cópia (o `StripToVisualOnly` mantém o `Animator`) faz `Play` do estado se ainda não estiver nele (`HasState` antes). Telemetria: `anims sent/recv/applied/unmatched`.

## Sonda `powers/` (diagnóstico, só log)

`Summons/PowersProbe.cs`, ligada com `[Debug] PowersProbe = true` (desligada por padrão desde a 1.5.0). Não manda nada nem muda o jogo. Serve para desenhar o espelhamento genérico (qualquer coisa que o herói local gera a partir de `Resources` `powers/`) e as barras de progresso a partir de dados reais:

- `[probe] powers/ prefabs (N): ...`: os prefabs de mini-jogo do jogo;
- `[probe] Pooled instance of '<nome>' ... registered`: instância criada pelo pool (`ResourcesUtil.PrefabIdByInstanceId`, lido 1×/s quando cresce, objeto por `Resources.InstanceIDToObject`);
- `[probe] '<nome>' is live and NOT from the pool`: achada por `GameObject.Find("<nome>(Clone)")` a cada 3 s sem estar no pool;
- `[probe] '<nome>' ON #n (pooled|NOT pooled) at ... parent ... root components [...] N parts: ...` e `off after Xs`;
- `[probe] Material '<nome>' <renderer> (...): <propriedade> a -> b via material|property block after Xs`: primeira mudança de cada número/cor de shader (uma vez por prefab, renderer e propriedade, nos primeiros 90 s de cada ativação).

Desligar antes de publicar (ou trocar o padrão para `false`).

## Como testar

1. A joga de Legionnaire. Na tela de B aparecem os legionários caídos no mesmo lugar.
2. B passa por cima deles e nada acontece. A resgata, e eles somem na tela de B.
3. Repita com o Demon Hunter (a pedra) ou outro personagem com mini-jogo. Na 1.4.1 a pedra deve aparecer com o modelo do jogo; se não, um anel do tamanho da área.
4. **Logs:**
   - de A: `[passives] Own <Tipo> #N '...'`;
   - de B: na telemetria `[passives] ... built=N missing=0`;
   - um `No prefab for ...` indica um tipo cujo prefab não está carregado no outro jogo.
   - 1.4.1: `Loaded <Char>'s skill tree for slot N's objects` no jogo de quem vê, e depois `built=N` em vez de `missing`.

## Tecla F2 (debug): mecânica do herói sem desbloquear

`MechanicDebug.cs`, em qualquer janela, dentro de uma partida: carrega a árvore do personagem local (`skill-tree-survivors-character/<char>-skill-tree`) e aplica, pelo `SkillTreeItemSO.Apply(entity, 1)` do jogo, cada nó `SkillTree<X>SO` que adiciona `<X>Component` ao jogador (DemonicBinding, GhostShip, AncestralRitual, MachineSpirit, CursedShrine, ArcanePower, CallOfTheWild, ChaosEnergy, DivineInspiration, ElementalOverload, Rage, Reanimation, YinYang, Mechatron, MyrmidonFavor, NaturesGuardian, RoguePower, Concentration, CompanionHunger), menos os que o jogador já tem. Só nesta run, só nesta máquina; nada vai para o perfil. Log: `[debug] F2: applied '<nó>' (...): <X>Component present|NOT present after Apply`.

## 1.6.0: prefabs descarregados entre partidas

Na segunda partida da mesma sessão, todos os objetos do parceiro viraram marcador (`No prefab ... powers`): o jogo descarrega assets sem uso na troca de cena e os prefabs de `powers/` foram junto. `ReloadPowersIfStale` recarrega a pasta (e os ids de prefab do dono) quando algum sumiu, antes da varredura e antes de desistir para o marcador.
