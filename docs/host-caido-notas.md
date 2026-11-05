# Host caído com convidado vivo

Revisão do item "host morto com cliente vivo" (`fase3-notas.md`). O host nunca fica "morto de verdade" com alguém vivo: a morte final só vem quando todos caem (derrota) ou quando ele fica sozinho (regra solo). O caso real é o **host caído** (`ReviveOnDeath`) enquanto um convidado continua jogando.

## O que já estava coberto (fase 4A e fase 9)

- O host caído sai do `EntityRegistry`. Os inimigos que o perseguiam largam ele (`CoopLife.DropEnemiesTargeting`), e o `RefreshTarget` acha o proxy do convidado.
- Os seekers vão para o jogador vivo mais perto (`RetargetSeeker`, patch 13).
- O spawn é redirecionado (patch 7): com o host caído, todos os pontos de spawn são sorteados em volta dos convidados vivos. Isso vale para inimigos comuns, elites, bosses, void hunters, cristais de cura e minérios, porque todos passam por `GetRandomSpawnPointOutsideOfView`.
- O reciclador (patch 8) poupa o que está perto de qualquer convidado vivo.
- XP retido enquanto caído (`LootSync`).
- A câmera do host segue o convidado, então o "fora da tela" do spawn é relativo a ele.

## O que faltava

1. **Inimigos parados em volta do corpo do host.** O reciclador do jogo (`EntityRegistry.DefaultContinuousRecycler(playerEntity, ...)`, 0x424690) mede a distância até o jogador do host, que agora é um corpo no chão. Os inimigos a menos de 35 u do corpo nunca eram reciclados, mesmo sem ninguém vivo por perto. Se o convidado está longe, eles não o acham (a busca de alvo tem alcance), ficam parados e ocupam o limite de unidades vivas que o spawner confere (`GetCurrentActiveEntitiesCount`). Resultado: menos inimigos chegando ao convidado.
   - **Correção (`RemoteProxy.HostDownSweep`):** com o host caído há mais de 3 s e algum convidado vivo, uma vez por segundo, recicla até 25 inimigos que estão a menos de 35 u do corpo e a mais de 35 u de todo convidado vivo. É a mesma regra do reciclador do jogo, só que em volta dos jogadores vivos.
   - Ficam de fora: os não recicláveis (`CanBeRecycledByDistance`), os bosses e os mortos.
   - O `Entity.Recycle` já manda o `EnemyDespawn` aos convidados.
2. **Referência do spawn com o host no chão.** O patch 7 só redireciona quando o ponto de referência é o host, e a comparação era em 3D a até 2 u. Ela passa a ser no plano (sem a altura) a até 2,5 u, porque os chamadores passam a raiz ou um placeholder do jogador (`UnitUtil.GetTargetTransform`). A telemetria `spawnNotHostRef` conta, com o host caído, os spawns que não foram reconhecidos como do host.

## Ainda não tratado (inofensivo)

- Coisas presas a `PlayerEntities[0]` continuam no corpo do host: `EnvironmentalDamageOnTriggerEnterFollowPlayerSkill`, os meteoros do deus e a fuga do goblin de loot. O host caído está invulnerável, então elas só deixam de pressionar o convidado. Com o host de pé, elas também nunca vão atrás dos convidados.
- Baús e tarefas novos continuam sorteados pelos pontos do mapa em volta do host (`FillPossibleSpawnPointQuery`), mas agora qualquer jogador pode abrir ou começar.

## Como testar

`tools\test-local.cmd`, depois:

1. Leve o convidado (B) a uns 60 u do host (A).
2. Na janela de A, aperte F9 (golpe letal no jogador local): A cai.
3. Depois de uns 3 s, o log de A mostra `[proxy] Host down: N enemies left around the host's body with no living player near recycled.`, e a telemetria do proxy mostra `hostDown=True sweepRecycled=N`.
4. Em B, os inimigos em volta do corpo de A somem, e as ondas continuam chegando em volta de B: `spawnRedirects` sobe e `spawnNotHostRef` fica em 0 ou perto disso.
5. B volta e revive A: o sweep para e tudo volta ao normal.
