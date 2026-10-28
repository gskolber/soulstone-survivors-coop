# Issue #5: painel de level-up coberto pelo HUD

Issue: "Level UP ui obstructed by player HUD". O painel compacto (overlay) ficava embaixo e o HUD do jogo
(barra de vida com chifres, slots de skill, ícone do dash, ultimate) desenhava por cima dos cards. O jogador também
reclamou do controle.

## Causa

- O HUD (`Gameplay-Panel-Survivors`) é um canvas com ordem **8**; o popup de level-up (`Level-Up-Panel-Survivors`)
  tem ordem **4**. Tudo do HUD que cai em cima do painel fica na frente.
- O canvas do jogo escala pela **largura** (`match=0`). Em 21:9 (2560x1080, o caso do print da issue) o canvas fica
  1920x810 em unidades, então o painel em escala 0.6 ocupa bem mais da altura e entrava fundo no HUD.

## O que mudou

- **Posição** (`Ui/LevelUpOverlay.cs`):
  - O topo do HUD de baixo é medido em jogo: os gráficos visíveis do `GameplayPanel.BottomHolder` (e das barras de
    vida, skills, artefato, nível e XP) na metade de baixo da tela. O contador de dash fica de fora, porque flutua
    perto do personagem. A medida é refeita a cada 0,5 s com o painel aberto e só sobe (um ícone de buff que aparece).
    Sem medida, a reserva é 1/4 da tela. O log mostra `HUD top at …`.
  - **Opção nova `[Coop] LevelUpPanelPosition`, padrão `Top`:** em cima, centralizada, acima do personagem (deixa
    8% da altura livre acima do meio da tela quando cabe). `Bottom` / `BottomLeft` / `BottomRight` agora ficam logo
    **acima** da barra de vida. Em qualquer posição o painel nunca entra no HUD de baixo e encolhe pra caber.
  - A chave antiga `LevelUpOverlayPosition` ficou sem uso, pelo mesmo motivo da issue #1: o BepInEx não troca um
    valor salvo, e `Bottom` estava salvo pra todo mundo. Quem tinha escolhido outra posição precisa escolher de novo.
  - **Centralizado nos cards**, não em cards + opções (os pontos de skill e o Reroll / Banir / Travar ficam pendurados
    à direita). A largura reservada é o dobro do lado maior, então nada sai da tela.
  - O losango de raridade e o ícone, que saem acima do card, entram na medida (antes o topo era cortado em `Top`).
  - A faixa de avisos do co-op (a dica do controle) desce pra logo abaixo do painel quando ele está no lugar dela
    (`CoopUi.RefreshBanner`).
- **Controle:**
  - Como antes, o stick esquerdo continua movendo, o **D-pad esquerda/direita** troca o card e o **D-pad baixo**
    esconde/mostra.
  - **Confirmar com o botão do jogo** (`Submit`: A no Xbox, X no PS) além do D-pad cima.
  - **Reroll / Banir / Travar / Pular no controle:** usam os botões que o próprio painel mostra (o jogo liga essas
    funções a ações genéricas `ButtonNorth`, `ButtonWest`, ombros e gatilhos; o mod lê o nome no glifo do painel,
    `GamepadSpriteAdjuster.Action`). O mod chama o `On…Pressed` do painel, como os botões do mouse.
  - Enquanto o painel está aberto e o controle é a entrada ativa, as ações de gameplay nesses botões ficam
    desligadas (pulo/dash no A, skills manuais nos botões da face...). Escondendo o painel (D-pad baixo), elas voltam.
  - O card destacado **cresce 8%** no controle, porque a moldura de seleção do jogo some no painel transparente. Ele
    já vem destacado ao abrir e a cada novo conjunto de cards.
  - Um controle ligado com o painel já aberto é reconhecido na hora (antes só valia o controle que existia quando o
    painel abriu).
  - O texto da dica agora cita o botão de confirmar (`{ok}`, nome que o Input System dá ao botão desse controle), nas
    8 línguas.
- **Teclado:** R (reroll), C (travar) e T (pular) não funcionavam no overlay. Eles "batiam" com atalhos de debug
  do jogo (Ctrl+R, Ctrl+T…), e a ação inteira era desligada. Agora as combinações com Ctrl/Alt/Shift não contam como
  conflito nas teclas do teclado.

## Verificação

Numa **cópia privada do jogo** (no scratchpad, pra não mexer na instalação compartilhada nem no `.cfg` do usuário),
com o modo de debug novo `--coop-ui-shot <pasta> --coop-ui-shot-levelup` (`Ui/LevelUpShot.cs`):

- O modo entra numa run sozinho (`SurvivorsDesert`), trata como co-op só pra política do level-up, sobe 4 níveis e
  tira os prints.
- Depois pluga um **controle virtual** (dispositivo XInput do Input System alimentado com eventos de estado). Com ele:
  D-pad direita 2x (destaque 1→2→3), A (escolhe o card 3, fila 4→3), botão de reroll do controle, D-pad baixo 2x
  (esconde/mostra) e, no teclado, R (rerolls 10→9).
- Resoluções testadas: 1920x1080, 1600x900 e 2560x1080, em EN e PT, em janela sem borda.
- **Antes/depois:** o "antes" é o master com o mesmo modo de prints (só a parte dos prints): em todas as resoluções a
  barra de vida e os slots desenham por cima dos cards, como no print da issue. Prints em
  `side-session-notes/…/issue5-shots/` (`before-*` = master, o resto = esta branch).

O que **não** foi verificado:

- Controle físico. O controle virtual não faz o jogo trocar sozinho de esquema (o `PlayerInput` não reage a ele), então
  o teste força `ActiveInputType = Gamepad`. Com controle de verdade quem troca é o próprio jogo.
- Co-op real com duas instâncias (host e cliente). O overlay é local e igual dos dois lados; a política (`Decide`) é
  a mesma da 0.9.2.
- A troca de skill (slots cheios), compacta, também não entrou nos prints.
- Em 1600x900 o rótulo "Pontos de habilidade" do painel encosta no texto do objetivo no canto de cima à direita
  (só encosta, os cards ficam livres).

## Roteiro de teste

1. Co-op em dupla, F6 pra subir de nível. O painel abre em cima, centralizado nos cards, sem nada do HUD por cima;
   a barra de vida e os slots continuam visíveis. No log: `HUD top at …` e `Overlay on the popup … Top, above the HUD at …`.
2. 21:9 (2560x1080): o mesmo. O painel fica menor (o canvas do jogo é mais baixo) e não encosta no HUD.
3. `LevelUpPanelPosition = Bottom`: o painel fica logo acima da barra de vida, sem entrar nela.
4. Teclado: R / F / C fazem reroll / banir / travar no overlay.
5. Controle: D-pad esquerda/direita (o card destacado cresce), A escolhe, Y / X / B (Xbox, os glifos do painel)
   fazem reroll/banir/travar, D-pad baixo esconde (e o A volta a pular/dar dash). O stick esquerdo move o tempo todo.
