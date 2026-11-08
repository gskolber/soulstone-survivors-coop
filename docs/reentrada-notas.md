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
