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
