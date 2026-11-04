# Baús e minérios para todos os jogadores

Item 1 de `proximos-investigacao.md`. **Nada disto foi testado em jogo, nem compilado**: foi escrito numa sessão sem SDK .NET. Rode `dotnet build src/SoulstoneCoop` antes de testar. Código em `src/SoulstoneCoop/Loot/ChestSync.cs`.

## Como o jogo faz (resumo)

- **Baú e minério são o mesmo objeto:** `InteractableRewardObject`. O tipo do ícone diz qual é (`MinimapIconType` 0x47 = baú, 0x48 = minério). O baú com guardiões é `InteractableGuardedRewardObject`.
- **Quem cria:**
  - `GameManagerInteractableGuardedRewardSpawnerComponent` (corrotina `SpawnReward`): `GetRandomRewardObject`, `ResourcesUtil.LoadAsync`, `GetPrefabInstance`, `InteractableGuardedRewardObject.Setup(tier)`;
  - `MapTask.CompleteTask` (0x45C350): `GetPrefabInstance(RewardTriggerObjectPrefab)` e `InteractableRewardObject.Setup(xp, prestígio, soulstones, minérios, powerUps)`.
  - As duas fontes só rodam no host: o `StopSpawning` desliga os dois spawners no convidado.
- **Abrir:** `InteractableOneTimeTrigger`.

  | Offset | Campo |
  |---|---|
  | 0x20 | `InteractionTime` |
  | 0x28 | `ColliderHolder` |
  | 0x48 | `InteractingEntity` |
  | 0x58 | `TimeInteracting` |
  | 0x5C | `State`: Idle 0, BeingTriggered 1, Triggered 2, Cancelled 3 |

  - `OnTriggerEnter` (0x456060) pega a `Entity` do rigidbody do colisor e só aceita `IsPlayer()`. `OnTriggerExit` limpa.
  - `OnUpdate` (0x4562C0): enquanto `InteractingEntity` não é nulo, `TimeInteracting += deltaTime`, barra de progresso, `State = 1`. Ao chegar a `InteractionTime`, `State = 2` e desliga o `ColliderHolder`. Sem ninguém, zera (ou desce aos poucos).
- **Depois:**
  - `InteractableTriggerObject.UpdateAll` (0x458CA0) roda o `OnUpdate`, anima (`OnInteraction`) e marca `ReadyToTrigger` no `State 2`.
  - `InteractableRewardObject.UpdateAll` (0x456A50) toca a voz no `State 1`. Com `ReadyToTrigger`, chama `ConsumeTrigger` (efeitos e som), `LootUtil.ProcessLoot` (e o loot de minério) e `RemoveRewardObject`.
  - O `DoUpdate` do baú com guardiões cria os guardiões enquanto alguém abre e os mata quando abre.

O proxy do convidado no host tem `IsPlayer() == false`, de propósito (sem Inventory), por isso nunca abria nada. O loot do baú já chegava a todos, pelo `LootDrop`.

## O que mudou

**Host**

- **Rastreio dos baús.** Postfix em `InteractableRewardObject.Setup` e em `InteractableGuardedRewardObject.Setup`. Cada baú ganha um id e vai para todos como `RewardSpawn`, com:
  - nome do prefab;
  - endereço (`Metadata.RewardObjectAddress`, se houver);
  - endereço da `MapTask` que o criou (prefix em `MapTask.CompleteTask`);
  - posição, rotação, progresso e quantos estão em cima.
- **Fim do baú.** Prefix em `RemoveRewardObject` manda `RewardDespawn`, com `opened` = `TriggerConsumed`. Ele vem logo depois do `ProcessLoot` da abertura, no mesmo canal ordenado. Rede de segurança: baú destruído ou inativo há mais de 2 s também conta como "sumiu".
- **Prefix no `InteractableOneTimeTrigger.OnUpdate`** (só nos baús rastreados), antes do código do jogo. Ele conta quem está em cima:
  - o host, se o `InteractingEntity` é o jogador local (o próprio jogo pôs);
  - mais cada convidado vivo que disse estar dentro (`RewardPresence`). Os convidados só contam com o `ColliderHolder` ativo: não antes do `DelayToEnable` nem depois de abrir.

  Com isso:
  - **só convidados em cima:** o proxy do primeiro vira `InteractingEntity`, e o timer do próprio jogo anda. Se não sobra ninguém, o proxy sai;
  - **2 ou mais em cima:** `TimeInteracting += deltaTime × (multiplicador − 1)`. O jogo soma o `deltaTime` normal logo depois.
- **Multiplicador:** `1 + [Coop] ChestOpenBonusPerPlayer × (jogadores − 1)`, no máximo ×3. Padrão 0,5: 2 jogadores ×1,5, 3 ×2, 4+ ×2,5 a ×3. Só o valor do host vale.
- **`RewardStates`** a cada 0,1 s (Sequenced): id, `TimeInteracting`, jogadores e multiplicador dos baús em uso (e por 1 s depois de qualquer mudança).
- **Reenvio.** Quando um convidado entra na run (`CoopSession.OnLobbyState` → InRun), recebe todos os baús ainda de pé, junto com o resync dos inimigos.

**Convidado**

- **Cópia do baú.** `Instantiate` do mesmo prefab na posição do host, depois `InteractableTriggerObject.Setup()` (colisor, atraso, visuais) e `MinimapManager.RequestIcon`. Ficam de fora o resto do `InteractableRewardObject.Setup` (rendimentos, som de spawn) e o pool do jogo: a cópia é destruída por nós.
- **Prefab, nesta ordem:**
  1. o endereço enviado;
  2. o `RewardTriggerObjectPrefab` da `MapTask` (`ResourcesUtil.Load<MapTask>`);
  3. um asset já carregado com o mesmo nome;
  4. o endereço nos `MapCollectionSO.RewardObjects` cujo último trecho é o nome.

  Sem prefab, o baú não aparece, mas abre igual: o convidado conta como "dentro" a até 2,2 u do ponto.
- **`InteractableRewardObject.UpdateAll` não roda no convidado** (prefix que pula). Assim:
  - não tem loot local, porque o loot vem do `ProcessLoot` do host;
  - não cria guardiões, que chegam como marionetes;
  - não tem remoção local.

  O convidado não tem outros baús: os spawners estão parados.
- **Prefix no `OnUpdate` da cópia:** escreve o progresso do host em `TimeInteracting`, sempre abaixo de 97% do `InteractionTime` para nunca abrir sozinho. Com alguém em cima (qualquer jogador), põe o jogador local em `InteractingEntity` para mostrar a barra e a animação. Entre pacotes, o progresso anda na velocidade do host.
- **`OnTriggerEnter/Exit` da cópia** só registram se é o nosso jogador (o código do jogo é pulado). Ao mudar, vai `RewardPresence` ao host.
  - Caído ou morto conta como fora.
  - A mais de 8 u também conta como fora, para o caso de o `OnTriggerExit` não chegar.
- **`RewardDespawn` aberto:** `State = Triggered`, colisor desligado e `ConsumeTrigger()` (efeitos e som). A cópia some depois do `DelayToStore`, entre 1 e 6 s. Não aberto: some na hora.
- **Limpeza:** cópias e rastreio somem na troca de cena (`PortalSync.OnLocalSceneGone`), quando o host sai da cena (`OnHostLeftScene`) e quando todos desconectam.

**Patches.** Todos à mão, em `ChestSync.ApplyPatches`, cada um no seu try/catch. Se um alvo mudar numa atualização do jogo, só aquela parte para (o log diz `Could not patch ...`). Assinaturas só com referências, `int` e `float`: nenhuma struct pequena por valor.

## Pacotes (130–139)

| Tipo | Direção | Conteúdo | Entrega |
|---|---|---|---|
| `RewardSpawn = 130` | host → convidados | id u16, nome, endereço, endereço da task (str), pos 3f, rot 4f, progresso f32, jogadores u8 | ReliableOrdered |
| `RewardDespawn = 131` | host → convidados | id u16, aberto u8 | ReliableOrdered |
| `RewardStates = 132` | host → convidados | n u8, n × (id u16, progresso f32, jogadores u8, multiplicador f32) | Sequenced, ~10 Hz em uso |
| `RewardPresence = 133` | convidado → host | id u16, dentro u8 | ReliableOrdered, na mudança |

Muda o formato de pacote: host e convidados precisam da mesma build. Para publicar, suba o `PluginVersion`, e o handshake recusa a mistura.

## Configuração nova

`[Coop] ChestOpenBonusPerPlayer` (padrão `0.5`, de 0 a 2): velocidade extra por jogador a mais em cima do baú ou minério. `0` = mesma velocidade de sozinho. Só o valor do host conta.

## Telemetria

- Linha `[chests]` a cada 10 s:
  - **host:** `tracked`, `spawnsSent`, `despawnsSent`, `statesSent`, `presenceRecv`, `proxyDriven`, `boostedFrames`;
  - **convidado:** `mirrors`, `spawnsRecv`, `built`, `buildFailures`, `fallbacks`, `presenceSent`, `opened`;
  - nos dois: `errors`.
- **Logs de uma vez:**
  - `Host: #id 'nome' (tipo) at ...`;
  - `Client: prefab 'nome' found (via)`;
  - `Client: #id ... shown`;
  - `Host: #id ... opened (guests on it: n)`;
  - `Client: InteractableRewardObject.UpdateAll skipped`;
  - `Patches: 8 applied, 0 failed`.

## Riscos (do maior pro menor)

1. **Instanciar o prefab fora do pool** no convidado: algum `Awake` pode esperar o pool. Se `building #id` der erro, o baú ainda abre pela distância.
2. **Proxy em `InteractingEntity`:** nada do jogo que eu vi lê esse campo como jogador (`OnUpdate` só testa nulo; voz e guardiões usam o `PlayerEntity` do host). Se algum baú especial ler, o log de erro aponta.
3. **Prefab não achado** (`buildFailures`): mais provável no minério de `MapTask` e em mapas de DLC. O baú fica invisível no convidado, mas abre.
4. **Guardiões:** o `DoUpdate` do baú com guardiões só olha o jogador do host para a voz. Os guardiões nascem quando o estado vira 1, o que agora também acontece com só convidados em cima.

## Primeiro teste (duas janelas, F3)

**O que funcionou** (log do convidado):
- os 8 patches entraram;
- o baú do F3 (`Interactable-Guarded-Chest-Cavern`) apareceu no B, achado pelo caminho dos `MapCollectionSO`;
- o B mandou presença ao entrar e ao sair;
- os guardiões nasceram como marionetes;
- nenhum erro `[chests]`.

**Sem conclusão:**
- A cópia do baú no B ficou até o fim (`mirrors=1`, `opened=0`): o B nunca recebeu `RewardDespawn`. Ou ninguém abriu aquele baú, ou o host não mandou. Falta o log do host: a janela A foi reaberta e o `LogOutput.log` foi reescrito.
  - Agora o `test-local.ps1` guarda os logs anteriores em `dist\test-local\before-<hora>\` antes de abrir o jogo.
- **Jogador caído não revivido:** o B caiu (estado "caído" do co-op, não morte de verdade: `killed=False`, `revivable`) e ficou assim até o fim, sem nenhum `Revive` do host. O proxy de quem cai já sai das buscas de alvo (`IgnoredByEntitySearchQueries`) e perde o colisor, então isso não é a causa.
  - O VoidPresence (afixo "VoidPresence" na run, o imortal que persegue) passou a perseguir o A, que tinha 0 Death Guard. A explicação provável: o A não conseguia ficar 3 s a 2,5 u do B.
  - Para confirmar, o host agora loga a cada 2 s, enquanto alguém está caído: `Host: slot N down ... nearestAlly=slot M at X u ... progress=P`.
- **XP de quem está caído:** guardado de propósito e aplicado ao reviver (`xpShareHeld` chegou a 39.059 no B). Um popup de level-up com o jogador caído congelaria os dois jogos. Se ninguém revive, essa XP se perde.

## Fora do escopo (próximo)

- Os gatilhos de **iniciar** uma `MapTask` (o objeto da tarefa no mapa) continuam só no host: o convidado não começa uma tarefa sozinho. O mesmo mecanismo serve, porque também é um `InteractableTriggerObject`.
- A voz de "abrindo baú" do personagem do convidado não toca.
- `InteractableAutoRewardObject` (caverna dos goblins, Cruzada) não é rastreado.

## Roteiro de teste

Host A e convidado B na mesma build, mapa Survivors, perto um do outro.

**Sem amigo e sem esperar** (`Loot/ChestDebug.cs`). **Atalho:** `tools\test-local.cmd` (duplo clique) faz tudo abaixo sozinho: compila, instala, cria e depois apaga o `steam_appid.txt`, abre A hospedando e B conectando, e no fim copia os logs para `dist\test-local\<data-hora>\`. O build vai para `dist\test-local\build.log`.
- **Dois jogos no mesmo PC, por IP** (como na `fase9-notas.md`, seção A). Atalhos do `Soulstone Survivors.exe`:
  - A: `-screen-fullscreen 0 -screen-width 960 -screen-height 540 --coop-name A`;
  - B: `-screen-fullscreen 0 -screen-width 960 -screen-height 540 --coop-name B --coop-connect 127.0.0.1`.

  O A hospeda pela aba Co-op (Hospedar por IP). Os dois leem o mesmo `.cfg` e a mesma DLL. Logs: `LogOutput.log` (A) e `LogOutput.1.log` (B).
- **F3 na janela do A** (só o host): cria um baú ou minério a 5 u na frente dele, pelo pool e pelo `Setup` do próprio jogo (o mesmo caminho de um spawn normal).
  - Cada F3 pega o próximo prefab da lista: os baús com guardiões dos `MapCollectionSO.RewardObjects` e as recompensas das `MapTask` (baús e minérios).
  - O log mostra a lista (`F3 list #n`) e o que nasceu (`F3: spawned '...' (tipo) ... i/n`).
- **Com duas janelas:** os personagens ficam parados quando a janela não está em foco. Para o passo 4, ponha o A em cima do baú, troque de janela e leve o B.

1. **Início:** abrir a run e jogar 1 min. No log dos dois, procurar `[chests] Patches: 8 applied, 0 failed`. No B, `InteractableRewardObject.UpdateAll skipped` (aparece na primeira vez que o jogo roda).
2. **Baú aparece:** quando aparecer um baú (ícone no minimapa do A), o B vê o mesmo baú no mesmo lugar, com ícone no minimapa. Log do A: `Host: #1 '...'`. Log do B: `prefab '...' found (...)` e `Client: #1 ... shown`.
3. **Só o convidado abre:** B fica em cima e A longe.
   - O B vê a barra subir e o baú se mexer, e o A vê a mesma barra.
   - O baú abre nos dois, e o loot cai para os dois (como antes).
   - O B vê o efeito de abertura e o baú some.
   - No A: `opened (guests on it: 1)`, `proxyDriven > 0`.
4. **Os dois juntos:** A e B em cima de outro baú. Abre mais rápido que sozinho (×1,5 com o padrão), e `boostedFrames > 0` no A. Com `ChestOpenBonusPerPlayer = 0` no A, a velocidade volta à de sozinho.
5. **Sair no meio:** B começa a abrir e sai. A barra para e cai como sozinho, nos dois.
6. **Baú com guardiões:** B começa a abrir sozinho. Os elites aparecem nos dois (marionetes no B) e morrem quando o baú abre.
7. **Minério:** um minério de `MapTask` (completar uma tarefa no mapa). O B vê e consegue minerar. Se o B logar `no prefab for ...`, minerar perto do ponto ainda funciona; anote o nome.
8. **Entrar depois:** com `[Coop] AllowRejoin = true`, o B sai e volta na run. Os baús ainda de pé aparecem para ele (`Host: n chest(s) resent to slot 1`).
9. **Portal Endless e Restart:** os baús da fase anterior somem no B (`chest copies cleared`), e os da nova aparecem.
10. Mandar `LogOutput.log` dos dois.
