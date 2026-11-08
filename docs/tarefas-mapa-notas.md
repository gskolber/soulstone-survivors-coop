# Tarefas do mapa para todos

Antes, só o host conseguia começar uma tarefa do mapa (os marcadores de objetivo: sobreviver X segundos, matar elites ou bosses numa área). No convidado o marcador nem aparecia, porque o `GameManagerMapTaskSpawnerComponent` só roda no host. Agora o marcador aparece para todos, e qualquer jogador vivo em cima dele começa a tarefa. Código em `src/SoulstoneCoop/Loot/ChestSync.cs`, junto com os baús (`baus-notas.md`).

## Como o jogo faz

- `MapTask.StartTaskAtPosition` (0x45CDA0):
  - pega uma instância de `TaskTriggerObjectPrefab` do pool e guarda em `_taskTriggerObjectInstance` (+0x70);
  - chama `InteractableTriggerObject.Setup`;
  - põe `Stage = Spawned` (+0x78);
  - pede o ícone do minimapa: `MinimapIconType.TaskEasiest..TaskHardest` (50–54) conforme `TaskMetadata.Difficulty` (2–5 → 51–54, o resto → 50);
  - cria o ícone flutuante (`WorldSpaceMapTaskIcon`).
- O marcador tem o mesmo `InteractableOneTimeTrigger` dos baús: `OnTriggerEnter` só aceita `IsPlayer()`, e o `OnUpdate` soma `TimeInteracting` enquanto há `InteractingEntity`.
- `MapTask.UpdateAll` (0x45D040), para cada tarefa:
  - roda o `DoOnUpdate` da subclasse;
  - se o marcador está com `ReadyToTrigger` (+0x80) e sem `TriggerConsumed` (+0x81), chama `ConsumeTrigger` e `OnTriggerInteracted`.
- `OnTriggerInteracted` (0x45CBF0):
  - toca o som de início;
  - devolve o marcador ao pool;
  - chama o `DoOnTrigger` da subclasse: timer, barreira (só Crusade), bosses ou elites, objetivo no painel.
- `MapTaskCompletionStage`: None 0, Spawned 1, Triggered 2, Completed 3, Failed 4.
- O progresso das tarefas não depende de onde o host está:
  - as de sobreviver são só um timer (`TimerObjective`);
  - as de matar contam os inimigos da própria lista (`EliminateSpecificEntitiesObjective`).
- `CompleteTask` cria a recompensa com `InteractableRewardObject.Setup`, que a sincronização de baús já cobre.

## O que o mod faz

- **Host:**
  - `MapTask.OnEnable` e `OnDisable` (postfix) guardam as tarefas existentes. O `StartTaskAtPosition` não é patchado porque recebe dois structs por valor (regra do `fase4c-notas.md`).
  - A cada frame, ele confere cada tarefa. Se ela está com `Stage == Spawned` e o marcador está ativo, o marcador vira um "baú" rastreado (`Chest.IsTask`), com chave pela `MapTask`, e sai `TaskTriggerSpawn`.
  - Quando o estágio muda ou o marcador some, sai o `RewardDespawn`: `opened = 1` se começou (Triggered/Completed), `0` se foi cancelada ou sumiu.
  - O resto é o dos baús: `RewardPresence` do convidado, o proxy do convidado vira `InteractingEntity` quando só convidados estão em cima, o bônus por jogador (`[Coop] ChestOpenBonusPerPlayer`) e o `RewardStates` com o progresso. Quando o tempo enche, o próprio `MapTask.UpdateAll` do host começa a tarefa.
- **Convidado:**
  - Ele monta uma cópia do `TaskTriggerObjectPrefab` da tarefa (`ResourcesUtil.Load<MapTask>(endereço)`). Se não achar, procura um prefab carregado com o mesmo nome.
  - Depois faz `Setup()` e pede o ícone do minimapa pela dificuldade.
  - A barra mostra o progresso do host, limitada a 97%, e a cópia nunca dispara sozinha. O `OnTriggerEnter`/`Exit` da cópia só anota se o jogador local está dentro.
  - No despawn com `opened`, ele toca o `ConsumeTrigger` e remove a cópia.
  - Sem prefab, ele mostra só o ícone do minimapa e a tarefa começa por distância (2,2 u).

## Pacote

`TaskTriggerSpawn = 134` (host → convidados, ReliableOrdered), com este formato:

```
id u16 | nome str | endereço da tarefa str | dificuldade u8 | pos 3f | rot 4f | progresso f32 | jogadores u8
```

Os ids são os mesmos dos baús, então `RewardDespawn`, `RewardStates` e `RewardPresence` servem para os dois.

## Limites

- O ícone flutuante (`WorldSpaceMapTaskIcon`) não é copiado; o convidado vê o marcador e o ícone do minimapa.
- O painel de objetivos do convidado recebe o timer ou o contador da tarefa pelo `Loot/TaskObjectiveSync.cs` (`TaskObjective = 135`, 2 Hz). O host lê `_timerObjective` / `_eliminationObjective` da tarefa em andamento; o convidado cria o mesmo tipo de objetivo, com o mesmo id (o texto é o do jogo), e o completa quando a tarefa acaba (ou após 5 s sem notícia).
- O som de início da tarefa só toca no host.
- Corrigido depois da revisão do PR #9: um convidado que caía, desistia ou tinha o proxy recriado em cima do marcador deixava o proxy velho no `InteractingEntity`, e a tarefa começava sozinha (bosses incluídos). Detalhes em `baus-notas.md`, "proxy velho no `InteractingEntity`".

## Como testar

`tools\test-local.cmd`, depois:

1. Ande com o convidado (B) até um marcador de tarefa. Ele aparece no mesmo lugar nas duas janelas, com o ícone no minimapa.
2. Fique só com B em cima: a barra enche nas duas janelas e a tarefa começa no host (timer no painel do host, elites ou bosses aparecem para os dois).
3. Em outra tarefa, os dois em cima: enche mais rápido.
4. Ao completar, a recompensa aparece para os dois (baú ou minério, como já era).
5. No log do host:
   - `Host: task marker #N '...' (..., difficulty D, task '...') at ...`;
   - depois `Host: task marker #N '...' started (guests on it: 1)`;
   - na telemetria, `tasks=… taskMarkers=… tasksStarted=…`.
6. No log de B: `Client: task marker #N '...' shown at ...`.
