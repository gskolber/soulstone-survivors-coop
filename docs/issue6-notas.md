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

- O patch no `AffixGameplayData..ctor` nunca rodou: o Il2CppInterop avisa `Failed to init IL2CPP patch backend ... using normal patch handlers`, e esse fallback não pega a chamada nativa do `SurvivorsGameManager.Start`. Cada cliente jogava com as maldições do próprio perfil (bosses a mais, objetivo `x/15` diferente).
- Trocar a lista no perfil (a primeira versão desta correção) não é seguro: o jogo enfileira os objetos do perfil e os serializa depois, numa thread própria (`StandalonePlayerDataProvider.RequestFileWrite` → `ProcessFileRequests`), então um save podia pegar a lista do host.
- Agora há um postfix em `PlayerProfileMapProgression.TryGetMapProgressionById`: enquanto a run do host carrega (`LocalLoading` ou `StageTransition`, mesmo mapa), o cliente devolve uma **cópia avulsa** da progressão daquele mapa com a lista do host e os afixos ligados. A cópia não está em lista nenhuma do perfil, então nunca é salva, e não há nada para devolver em nenhuma saída (menu, desconexão, fechar o jogo no meio do load). Isso também funciona quando o cliente nunca jogou o mapa.
- No `StartMatch`, o cliente loga `run affixes match the host's` (todas as que o host mandou estão presentes; o jogo acrescenta as dependências, ex. `EmpoweredBosses-01`).

## Teste pendente

- Com um amigo de verdade (PCs diferentes): Dungeon por alguns minutos, com maldições diferentes nos dois perfis.

## Resultado em jogo (duas instâncias locais)

- `Ui/MobShot.cs`, só por argumento. Host: `--coop-host --coop-mob-shot <pasta>\host [--coop-mob-shot-map <cena>] [--coop-mob-shot-affixes a,b|none]`. Cliente: `--coop-connect 127.0.0.1 --coop-mob-shot <pasta>\client [--coop-mob-shot-nofix]`. O host começa a run 15 s depois que o parceiro chega ao menu, os dois tiram prints aos 15 e 30 s e fecham aos 40 s. Aos 15 s cada lado loga as listas de tier que sorteou.
- **Causa confirmada:** numa das runs, o Tier3 e o Elite1 saíram diferentes entre host e cliente (cada um sorteou a sua variante).
- **4 runs no Dungeon:** o cliente teve ≈40–50 marionetes desde o início, com `unresolved=-`, e os prints de 30 s mostram os mesmos esqueletos nas mesmas posições. Em 40 s tudo fica no Tier1, que coincidiu nas 4, então `outsideLists=0`: o caminho novo ainda não foi exercitado (falta uma run de 2–3 min).
- **#3:** apareceu `run affixes match the host's`, mas o perfil é o mesmo nas duas instâncias locais. Falta testar com um cliente de maldições diferentes (PCs diferentes).
- **Achado à parte:** o cliente aceita o `RunStart` ainda na tela de título, sem o perfil carregado, e fica preso no loading (`Client has no progression`). O `MobShot` espera 15 s por isso.

### Segunda rodada (perfis separados)

- `--coop-profile-dir <pasta>` (debug, `Ui/MobShot.cs`): cada instância grava o save na própria pasta (`StandalonePlayerDataProvider.PersistentDataPath`), semeada com uma cópia do save real. O save real não foi tocado.
- **#3:** o host com 6 maldições (incluindo `EmpoweredBosses-02` e `SimultaneousBosses`) e o cliente com as próprias 4 (ou nenhuma). O cliente jogou com as do host: as mesmas 7 maldições, `curse=10`, `extraBosses=2`, objetivo `0/15` nos dois (sem a correção o cliente teria `0/5`). Depois de três runs fechadas no meio, o save do cliente continuava com as 4 maldições dele (`Profile affixes ... [HealingDampening-01,ExplosiveGoblins-01,ResilientElites-01,ResilientBosses-01]`).
- **Contadores:** numa run de 200 s, bosses pendentes e objetivo iguais nos dois lados (`pendingBosses=3`, `main=0/15`). No fim, `main` 2/15 no host e 1/15 no cliente, a 2 s um do outro: é o atraso normal do `ObjectiveSync`.
- **#6, caminho novo:** `--coop-mob-shot-forget Creeps/SewerRat` tira esse endereço das listas do cliente. Resultado: `'Creeps/SewerRat' is not in our tier lists ...: loaded from Resources, prefabData=True (type 47)`, `outsideLists=1`, `unresolved=-`. `--coop-mob-shot-tier <n>` força o tier do host (o Tier3 saiu diferente nos dois lados, mas os mobs dele estavam em outras listas do cliente).
- **Bugs vistos, fora do escopo** (para outras tarefas): marionetes de mob comum deslizando sem animar a caminhada (às vezes; talvez com a janela do cliente em segundo plano); bosses invisíveis no cliente durante o estado de entrada, antes de começarem a agir; o cliente aceita o `RunStart` ainda na tela de título e trava no loading.
