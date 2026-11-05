# Marcadores do jogador caído

## O que mudou

- **Raio de reviver:** passou de 2,5 u para **3,5 u** (`CoopLife.ReviveRadius`, distância no plano, decidida pelo host).
- **Anel no chão** em volta de cada jogador caído, com esse raio. Ele pulsa em vermelho, e um arco verde por cima enche com o progresso do reviver (`CoopLife.ProgressOf`, a conta do host).
- **Linhas:**
  - do caído até o jogador vivo mais perto (quem provavelmente vem reviver);
  - na tela de quem está vivo, de cada aliado caído até ele, para achar um corpo fora da tela.
- O próprio caído vê o anel dele e as linhas até os aliados.
- Os textos flutuantes ("CAÍDO", "REVIVE xx%") continuam.

## Como é feito (`Life/DownedMarkers.cs`)

- São `LineRenderer`s do mod, sem Entity nem colisor, embaixo de um objeto mantido entre cenas.
- O anel e o arco ficam deitados no chão (`LineAlignment.TransformZ`, objeto girado -90° em X). As linhas ficam viradas para a câmera.
- **Material:** vem do primeiro shader que o build tiver (`Sprites/Default`, `Universal Render Pipeline/Particles/Unlit`, `Universal Render Pipeline/Unlit`, `Unlit/Color`). Se nenhum existir, vem de uma cópia do material de um `TrailRenderer` do jogo. Sem nenhum, os marcadores desligam e só os textos ficam. O log diz qual foi usado: `Downed markers ready (material ..., shader ...)`.
- Rodam a partir do `CoopLife.Tick`, só em partida co-op e fora do fim de run.

## Como testar

1. Com `tools\test-local.cmd`, aperte F9 na janela de A.
2. Nas duas janelas aparece um anel vermelho em volta de A, com uma linha até B.
3. B entra no anel (já conta perto da borda, a 3,5 u): o arco verde enche nas duas janelas. Saindo, o arco esvazia devagar.
4. Ao reviver, anel e linhas somem.
5. Repita com B caindo: na tela de A, a linha sai do corpo de B até A.

## 1.4.1: corpo afundando no chão, marcadores mais visíveis

No teste do Pirata, o corpo de B (caído, em ragdoll) afundou no chão na máquina dele: o host recebia `y` = -73, -306, -718... até -3903 em um minuto. Anel, linha e tudo mais eram desenhados lá embaixo, invisíveis (não era nada só do host: os marcadores rodam em todas as telas).

- **Primeira tentativa (falhou):** congelar os `Rigidbody` do jogador achou só 1 corpo e o corpo continuou afundando; depois do revive o jogador levantou a -1500. Os ossos do ragdoll ficam fora do objeto do jogador (`RagdollComponent.RagdollRootParent`), atravessam o chão deste mapa e o `LateUpdateAll` do ragdoll arrasta a raiz junto.
- **Correção:** o jogador caído não entra mais em ragdoll. `EnterDownedVisuals` guarda a posição, zera `_gravity` e a velocidade do `ModifiedCharacter` e põe `MovementMode.None`; `HoldDownedBody` usa o `Teleport` do personagem se algo ainda o descer mais de 1 u; `ReleaseDownedBody` (no revive e em toda saída do estado caído, via `RestoreCamera`) devolve a gravidade e teleporta de volta se estiver abaixo do ponto da queda. O corpo fica de pé, parado; anel, pilar e "CAÍDO" marcam o estado.
- **F9 com o jogador já caído** passava pela invulnerabilidade e matava de verdade só naquela máquina (fim de run local, host ainda o via caído): agora é ignorado.
- `DownedMarkers.Collect`: um caído mais de 3 u abaixo do jogador local é desenhado na altura do jogador local (cobre builds antigas do outro lado).
- Mais visível no meio dos inimigos: um pilar vermelho pulsante de 9 u sobre o corpo, anel 0,24 u, arco 0,34 u e linhas 0,18 u.

### Como testar

1. Caia com B longe de A (ou F9 em A para o host): o pilar e a linha aparecem nas duas telas, no chão.
2. Fique caído uns 30 s: o pilar não some. Se aparecer `Downed body sank`, a trava agiu.
3. Reviva: o jogador levanta normalmente.
