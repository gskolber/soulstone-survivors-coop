# Reentrada com a build (1.4)

Item 1 da lista de próximos (`proximos-investigacao.md`, seção 5). Continua atrás de `[Coop] AllowRejoin` (desligado por padrão), que só o host precisa ligar.

## Como o jogo já faz

Entre as fases do Endless o jogo leva a build do jogador para a cena seguinte:

- `GameModeBaseData.SetDataBasedOnPlayerEntity(player, endlessLevel)` grava nível, XP e XP necessário, poderes coletados, runas, banidos, slots travados, inventário, stats somados e tempo de partida;
- o objeto fica em `GameManagerUtil.InitialEndlessData`;
- na cena nova, o `Start` lê com `GetAndRemoveInitialData` e devolve tudo ao jogador com `SetupPlayerForMatch`.

## O que entrou

**`RunStart` (formato novo):** além do que já ia, leva o **id da run** (sorteado no Fight e no Reiniciar, mantido nas fases do Endless) e se o host tem `AllowRejoin` ligado.

**Convidado (`Lobby/RejoinProgress.cs`), numa run de host com `AllowRejoin`:**

- a cada 5 s, com a partida começada, preenche um `GameModeBaseData` a partir do próprio jogador (`SetDataBasedOnPlayerEntity`, com o `EndlessLevel` atual de `GameManagerUtil.BaseGameData`) e guarda na memória, marcado com o id da run;
- não grava enquanto está fora (morto) ou com a run acabando: fica a última build de antes disso.

**Na volta:** o host reenvia o `RunStart` a quem conecta no meio da run (fase 8). Se o id é o da build guardada, antes de carregar a cena o convidado:

- troca os afixos da build pelos do host;
- põe a build em `GameManagerUtil.InitialEndlessData`;
- mostra "De volta à partida com a sua build (nível N)".

O `Start` da cena faz o resto, como entre fases do Endless. Qualquer outra run (nova, reinício) descarta a build guardada.

## Limites

- Só memória: se o jogo do convidado fechou, a build se perde e ele volta como personagem novo.
- Perde até 5 s de progresso (o intervalo entre as gravações).
- Se o host trocou de fase do Endless enquanto o convidado estava fora, a build é da fase anterior: o `EndlessLevel` gravado fica uma fase atrás.
- O tempo de partida volta com a build. O relógio do convidado retoma de onde estava, não do tempo do host.
- Sem descompilador nesta sessão: o caminho do `Start` com `InitialEndlessData` numa run Survivor comum vem da investigação (`portais-investigacao.md`, seção 2) e ainda não foi visto em jogo.

## Roteiro de teste (duas janelas)

1. Na `.cfg` do A (host): `[Coop] AllowRejoin = true`. Os dois jogos leem a mesma `.cfg` no mesmo PC, então o B também fica com ela, o que não muda nada nele.
2. `tools\test-local.cmd`. A começa uma run; os dois sobem alguns níveis e pegam itens.
3. No log do B: `[rejoin] Client: keeping this run's build for a rejoin (run N, level L, ...)`.
4. Feche a conexão do B (Desconectar no painel Co-op, ou feche a janela do B e abra de novo pelo atalho; fechar o jogo perde a build, então para testar a build use Desconectar).
5. B conecta de novo. O A reenvia a run (`slot X connected mid-run - RunStart resent`) e o B carrega a mesma partida.
6. Esperado no B: `[rejoin] Client: rejoining run N with our build (level L, ...)`, o aviso na tela e o personagem com o nível, as skills e os itens de antes.
7. Confira os afixos (`run affixes match the host's`) e se o XP para o próximo nível está certo.
8. Se o personagem voltar novo mesmo com a linha do passo 6, o `Start` não usou o `InitialEndlessData` nesse modo: mande o log.

## 1.6.0: voltar à partida (reconexão automática e Lutar no menu)

Teste com amigo pela Steam: o convidado se rendeu (pausa, Desistir), voltou ao menu ainda conectado e não conseguia mais entrar: `AllowRejoin` vinha desligado e, mesmo ligado, a reentrada só acontecia ao conectar.

- **`AllowRejoin` ligado por padrão.** (Configs antigas guardam `false`: mudar à mão.) Com ele o lobby da Steam fica aberto durante a run.
- **Lutar no menu (`RunJoinRequest = 34`):** convidado no menu, host numa run: o `AllowFight` do cliente pede para entrar (no máximo a cada 3 s) e mostra `nt.join_asked`. O host (`RunSync.OnJoinRequest`) esquece que já mandou a run para aquele slot e o `HostRejoinTick` manda o `RunStart` de novo; mesma run = build de volta (`RejoinProgress`). Com `AllowRejoin` desligado, o host vê `nt.join_wanted`.
- **Reconexão automática (`NetworkManager`):** conexão estabelecida que cai (não recusa, não o botão Desconectar) arma 45 s de tentativas a cada 6 s, no mesmo IP ou no mesmo lobby/host da Steam (`SteamLobby.ReconnectTo`, que entra de novo no lobby mesmo no meio da run: o host só aceita membros). Só ações do jogador cancelam (`CancelReconnect`: Desconectar, Entrar, Hospedar, Convidar).
- **Sem recarregar:** enquanto reconecta, `CoopLife.HostLostTick` não encerra a run (aviso `nt.reconnecting` com os segundos). De volta, o Hello/LobbyState InRun faz o host reenviar inimigos, baús e objetos (`ResyncToPeer`) e recriar fantasma e proxy; as marionetes antigas já tinham sido apagadas na queda. Se a janela acabar, a run termina como antes.
- **Limite conhecido:** se o host trocou de estágio (portal) ou reiniciou enquanto o convidado estava fora, o convidado volta na run antiga.
  - Conferido na 1.6.1: o `LobbyState` só leva o lugar e o `RunId` é o mesmo em todos os estágios, então nada percebe a diferença. O host vê o convidado `InRun` e o `ResyncToPeer` manda os inimigos e baús do estágio novo para o mapa velho. Com o host no menu, o convidado fica sozinho numa run parada (sem queda de conexão, o `HostLostTick` não age). Item 1 do `roadmap.md`.
  - Corrigido depois da revisão do PR #9: ver "Reconexão numa run velha" abaixo.

### Como testar

1. **Lutar no menu:** em B, pausa, Desistir; no menu, Lutar: B volta para a run de A com a build (`Host: B asked to join the run`).
2. **Queda no meio da run:** aperte **F1** na janela B (debug: derruba a conexão como uma falha de rede). B mostra "reconectando (Xs)" e continua jogando; em alguns segundos volta à mesma run sem recarregar, com inimigos e parceiro de novo (`Reconnected to the host (attempt N)` em B, `Host: resync to slot 1` em A).
3. **Pela Steam:** o mesmo com um amigo (a queda real: desligar o Wi-Fi alguns segundos).

### Primeiro teste da 1.6.0

- Reconexão (F1) e Lutar no menu funcionaram.
- A foto da build nunca tinha funcionado: `GameModeBaseData.SetDataBasedOnPlayerEntity` (0x4FF600) dá `Clear()` numa lista do objeto (+0x30) e o construtor simples a deixa nula (NRE a cada 5 s, `snapshot of our build`). `RejoinProgress.InitCollections` cria toda propriedade `List`/`Dictionary`/`HashSet` nula antes. Log: `keeping this run's build ... N empty collections created`.

### Segundo teste da 1.6.0: Desistir travava o convidado em Carregando

B desistiu e voltou ao menu; o host mandou o `RunStart` de novo na hora (`slot 1 connected mid-run - RunStart resent`): B tinha entrado na run enquanto o host ainda carregava, e o `NoteInRun` só anotava com o host `LocalInRun`. O `RunStart` chegou com a cena do menu acabando de abrir (a aba Co-op foi montada depois) e a troca de cena nunca terminou.

- Host: `NoteInRun` vale também com o host carregando, e `NoteLeftRun` (InRun -> Menu na mesma conexão) marca o jogador: quem sai da run por vontade própria só volta com Lutar.
- Cliente: um `RunStart` que chega com menos de 2 s de menu espera (`menu not settled yet - the RunStart waits` / `starting the held RunStart`), até 30 s.
- O engasgo do host na saída de B vinha em parte desse reenvio (run + ressincronização de inimigos de novo).

### 1.6.1: foto da build desligada

O `SetDataBasedOnPlayerEntity` (0x4FF600) é a passagem de estágio do jogo, não uma leitura: chama `InventoryComponent.SaveMatchInventoryData` (grava no perfil, `PlayerDataUtil.BeginBulkWriteWithAutoFinish`), entrega o `InventoryData` vivo do jogador à foto e dá ao jogador um `InventoryData` novo e vazio (InventoryComponent+0x20). Até a 1.6.0 ele dava NRE na primeira linha e não fazia nada; com a correção da 1.6.0 o convidado tinha o inventário zerado e o perfil gravado a cada 5 s, e o painel de nível quebrou (`LevelUpPanelManager.SetupPopup` NRE, cartas "Behemoth Behemoth" sem preencher, sem como subir de nível). `RejoinProgress.SnapshotEnabled = false`.

Sem perda prática: uma queda reconecta na mesma run sem recarregar (a build fica). Só "Desistir e depois Lutar" volta como personagem novo. Uma foto sem efeitos colaterais pediria montar o `GameModeBaseData` à mão a partir dos componentes (nível, XP, power-ups, runas, inventário).

Depois do merge do PR #9: o `Lobby/RejoinProgress.cs` foi removido (era só esse caminho desligado; nada mais lia o id da run nem o `AllowRejoin` guardados ali) junto com a chave `nt.rejoin_build`. O `RunStart` continua levando o id da run e o `AllowRejoin` (formato igual). Uma foto nova, só de leitura, começa do zero (item 2 do `roadmap.md`).

### 1.6.1: o host não pausa mais por causa do convidado

- **Barreira de carregamento:** o host congelou 41 s enquanto B voltava com Lutar (`Others still loading - holding our match`). A barreira agora só espera quem começou a carregar antes da partida do host começar (`CoopSession.AnyRemoteLoadingWithUs`, `RemotePlayer.ChangingSince`): começo de run, portal, reinício. Quem entra depois carrega sozinho.
- **Painel de nível:** com B fora da run (desistiu) ou caído da conexão, o host voltava à pausa do solo com o painel aberto (`Policy changed ... -> pause (solo)`). `LevelUpPolicy` mantém as regras do co-op enquanto houver parceiro conectado (na run ou no menu) e por 60 s depois da última queda.

## Depois da revisão do PR #9

### Reconexão numa run velha

O "limite conhecido" da 1.6.0: o host pegou um portal, reiniciou ou foi ao menu enquanto o convidado reconectava, e o convidado voltava `InRun` no mapa velho.

- **Qual partida do host:** `RunSync.RunParams` ganhou `Stage` (0 no Lutar e no Reiniciar, +1 a cada portal Endless/BossRush, em `OnHostStageChanged`). `RunSync.LocalMatch` dá (id da run, estágio):
  - no host, o da run em que ele está (`_hostRun`);
  - no convidado, o que ele carregou (`RunStart`, `PortalTransition` de Reiniciar) ou seguiu (portal);
  - id 0 = nenhuma, ou não se sabe.
- **Host sozinho no portal ou no Reiniciar:** sem parceiro conectado não há `StageTransition`, e o `RunSync.OnLocalRunLeft` apagava o `_hostRun` na troca de cena. Por isso o parceiro que reconectava (ou entrava depois, fase 8) não recebia nada. Agora `_hostSceneChange` mantém a run (id e estágio) até o `StartMatch` seguinte.
- **Pacotes (mudam o formato: mesma build dos dois lados):**
  - `LobbyState`: lugar u8 | id da run i32 | estágio i32. O host repassa o de cada convidado para quem chega (`SendOthersTo`), com os mesmos campos.
  - `PortalTransition` (Endless/BossRush): tipo u8 | mapa | id da run i32 | estágio i32, o estágio para onde o portal leva. O convidado só adota no `ClientFollow`, quando o `LoadNextScene` foi marcado. Se não consegue seguir, continua com o estágio velho.
  - `RunStart` e `PortalTransition` (Reiniciar): o estágio vem depois do `Rejoin`.
  - O bot (`tools/TestClient`) repete no seu `LobbyState` a partida do host.
- **Host:** um `LobbyState InRun` com outra run ou outro estágio (os dois ids conhecidos) deixa o jogador `StaleRun`:
  - `RemotePlayer.InRun` fica falso para ele: sem fantasma, proxy, pausas, contagem de vida ou escala;
  - sem `ResyncToPeer` e sem `NoteInRun`;
  - quando ele chega ao menu, também não tem `NoteLeftRun`. Assim o `HostRejoinTick` manda a run atual (com `AllowRejoin`), ou o Lutar pede.
- **Convidado (`CoopSession.ClientStaleRunTick`):** na partida, fora de transição, com o `LobbyState` do host já recebido nesta conexão. Se o host está no menu, ou `InRun` noutra run/estágio, por 1 s seguido, chama `CoopLife.BeginStaleRun`:
  - aviso `nt.stale_run`;
  - 4 s depois (e com o painel de nível/pausa fechado), `Surrender` como quem sai sozinho (`LeftAlone`);
  - não age se a partida já acabou ali (morto, desistiu, vitória com o portal aberto);
  - do menu, a reentrada de sempre traz o convidado para a run atual do host, como personagem novo (a foto da build está desligada desde a 1.6.1).
- **Limites:**
  - nesses ~5 s, o que o host transmite do estágio novo (inimigos) ainda chega ao mapa velho do convidado;
  - um reinício do host sozinho com `AllowRejoin` desligado não tem id (`_hostRun` nulo): o host não marca nada, como antes, e o convidado só age se o host estiver no menu.

### "Todos caídos" com um convidado reconectando

O host contava só quem estava conectado. Se um convidado caía da conexão (45 s de reconexão no lado dele), o host:
- com outros convidados, podia perder por "todos caídos" com quem sobrou;
- sozinho, morria na hora se já estivesse caído (`OnPeerDisconnected`). Se levasse um golpe fatal depois, morria como no solo, sem cair.

Agora (`CoopLife.NoteAway`, no `PeerDisconnected`, antes do `Players.Left`), um convidado que estava na run e não estava morto de verdade conta como estava, vivo ou caído, por `NetworkManager.ReconnectWindow` (45 s):
- entra na conta de "todos caídos" do `HostTick`;
- o `HostTick` e o `TryArm` (cair em vez de morrer) continuam mesmo sem ninguém conectado;
- sai quando um jogador entra na run do host (`LobbyState InRun` com a mesma run e estágio: quase certo que é ele, porque o `Hello` vem depois);
- sai também quando a janela acaba: se o host está caído e sozinho, aí ele morre de verdade;
- não vale quando é o host que para de hospedar (`Transport` nulo), nem numa partida nova (`Reset`).

Custo: um convidado que sai de propósito com Desconectar no meio da run também é esperado por até 45 s. Quem desiste vai ao menu antes e não conta.

### Como testar (duas janelas, `AllowRejoin = true`, `[Debug] TestKeys = true`)

1. **Portal durante a reconexão:** A e B numa run Survivors. Em B, F1 (derruba a conexão). Em A, F10 (vitória) e F5 (portal Endless) antes que B volte. B reconecta. Esperado:
   - em A: `Slot 1 ... InRun run X/0 (STALE ...)`, sem `resync to slot 1`;
   - em B: `Stale run after a reconnect: the host is in run X/1, ours is X/0`, o aviso e, em ~5 s, o fim da partida;
   - B vai ao menu; em A, `connected mid-run - RunStart resent`; B carrega o estágio novo.
2. **Host no menu:** o mesmo, mas A desiste e volta ao menu enquanto B reconecta. B encerra a partida e fica no menu.
3. **Reconexão comum:** F1 em B sem A mudar nada. B volta na mesma partida, sem `STALE`, como antes.
4. **Todos caídos:** A caído (F9), F1 em B (B vivo). A continua caído e não perde; log `dropped out of the run (alive)`. B volta e revive A. Repita sem B voltar (feche a janela de B): A morre ~45 s depois (`didn't come back in time`).
