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
