# Issue #6 (mobs comuns invisíveis no cliente) e #3 (maldições do host)

Compilado, **sem teste em jogo** (não havia como entrar numa run sem clicar, e o jogo estava em uso por outras sessões).

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
