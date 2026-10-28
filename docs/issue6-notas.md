# Issue #6 (mobs comuns invisíveis no cliente) e #3 (maldições do host)

Testado com duas instâncias locais por IP (modo de debug `--coop-mob-shot`, `Ui/MobShot.cs`). Resultado no fim desta nota.

## #6: causa

- `EnemyPrefabsUtil.FillTierList` (0x6E1B80) recebe as variantes de um tier do mapa (`Enemy-Prefabs-Per-Map-Dungeon-Tier-01a..01d`) e fica com **uma**, sorteada com `RandomUtils.Range`. Isso vale para Tier1–5 e Elite1–5, em cada máquina por conta própria. Só a variante sorteada entra nas listas e é pré-carregada (`ResourcesUtil.Preload`).
- No Dungeon, o mob principal muda de variante para variante: `Skeleton-01` tem peso 10/14 na 01a, e na 01b/01c quem domina é `Skeleton-02`. Quando o host sorteia uma variante que o cliente não sorteou, a chave `Creeps/...` não está nas listas do cliente, e o prefab também não está carregado. O cliente marca `Unresolved prefab key` e descarta a marionete.
- Os grupos de elite (`FillGroupTierList`) não sorteiam, por isso elites e capitães apareciam. Quando o tier sobe (~2 min), os mobs novos caem mais vezes em listas que o cliente tem.
- As maldições não são a causa; a falha é aleatória. No Desert quase não aparece porque as variantes compartilham quase todos os mobs (Lizard e Beetle estão em quase todas).
- Hipótese descartada no caminho: `EnemyPrefabsPerMapSO.UseEnemiesFromRandomMaps`. Ela está desligada no Dungeon; só OverlordArena e SandscaleCamp a usam, e essas também ficam cobertas pela correção.

## #6: correção

- O `EnemySpawn` ganhou um bloco opcional com o `EnemyPrefabData` da lista do host: Type, IsElite e IsEndless. O host captura esses dados no prefix do `SpawnEnemyEntity`. **O formato do pacote mudou.**
- Cliente (`EnemyPrefabKeys.ResolveOutsideLists`): se a chave não está nas listas dele, carrega o prefab com `ResourcesUtil.Load<Entity>(endereço)` (o endereço da lista é um caminho de Resources). O `PrefabData` vem de `Global.EnemyPrefabs.PrefabDatas`, pela mesma regra do `TryGetPrefabDataByType` (0x4FEE20): o primeiro com o mesmo Type, IsElite e IsEndless. Tier, materiais e scaling ficam iguais aos do host.
- Log: uma linha `'<chave>' is not in our tier lists ...` por chave, e `outsideLists=N` no `[puppets]`.

## #3: causa e correção

- O patch no `AffixGameplayData..ctor` nunca rodou: o Il2CppInterop avisa `Failed to init IL2CPP patch backend ... using normal patch handlers`, e esse fallback não pega a chamada nativa do `SurvivorsGameManager.Start`. No log do cliente aparecia o `RunStart received`, mas nunca o `host affixes applied`. Cada cliente jogava com as maldições do próprio perfil, o que muda o contador de bosses, os stats etc.
- Agora o `RunSync.PrepareClientRun` coloca a lista do host em `MapProgression[mapa].SelectedAffixes`, liga os afixos se estiverem desligados e devolve tudo no `StartMatch` (ou ao sair da run). O patch do construtor saiu.
- No `StartMatch`, o cliente loga `run affixes match the host's` ou `DIFFER from the host's`, com a curse.
- Risco: se o jogo salvar o perfil durante o load, a seleção salva do cliente para aquele mapa fica igual à do host. A troca do `AffixesDisabled`, que já existia, tem o mesmo risco.
- Do #3 ficou de fora a checagem de totais de objetivo entre host e cliente, além do log `[objectives]` que já existe.

## Teste pendente

1. Dungeon, host e cliente na mesma run, de preferência várias vezes, porque o sorteio é aleatório. No cliente: mobs comuns desde o início; `outsideLists=` maior que 0 em algumas runs; `unresolved=-`.
2. Uma run normal (Desert, sem maldições): nada muda e `outsideLists=0` na maioria das vezes.
3. Cliente com maldições diferentes das do host no próprio perfil: o log `run affixes match the host's` aparece, o contador de bosses bate, e depois da run a seleção do cliente continua a dele.

## Resultado em jogo (duas instâncias locais)

- `Ui/MobShot.cs`, só por argumento. Host: `--coop-host --coop-mob-shot <pasta>\host [--coop-mob-shot-map <cena>] [--coop-mob-shot-affixes a,b|none]`. Cliente: `--coop-connect 127.0.0.1 --coop-mob-shot <pasta>\client [--coop-mob-shot-nofix]`. O host começa a run 15 s depois que o parceiro chega ao menu, os dois tiram prints aos 15 e 30 s e fecham aos 40 s. Aos 15 s cada lado loga as listas de tier que sorteou.
- **Causa confirmada:** numa das runs, o Tier3 e o Elite1 saíram diferentes entre host e cliente (cada um sorteou a sua variante).
- **4 runs no Dungeon:** o cliente teve ≈40–50 marionetes desde o início, com `unresolved=-`, e os prints de 30 s mostram os mesmos esqueletos nas mesmas posições. Em 40 s tudo fica no Tier1, que coincidiu nas 4, então `outsideLists=0`: o caminho novo ainda não foi exercitado (falta uma run de 2–3 min).
- **#3:** apareceu `run affixes match the host's`, mas o perfil é o mesmo nas duas instâncias locais. Falta testar com um cliente de maldições diferentes (PCs diferentes).
- **Achado à parte:** o cliente aceita o `RunStart` ainda na tela de título, sem o perfil carregado, e fica preso no loading (`Client has no progression`). O `MobShot` espera 15 s por isso.
