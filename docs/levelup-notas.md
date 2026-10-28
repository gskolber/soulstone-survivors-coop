# Level-up sem pausa: notas

Investigação em `docs/levelup-investigacao.md`.

- **0.8.0:** level-up sem pausa depois da build completa, com o painel do jogo em tela cheia (feedback da 0.7.0).
- **0.8.1 / 0.9.0:** **painel compacto (overlay)** embaixo da tela, dá pra andar e lutar escolhendo (feedback da 0.8.0: "fica paia sem pausar").
- **0.9.2 (issue #1):** em co-op, **toda** escolha de upgrade usa o overlay e **nada pausa o tempo** de ninguém. Os modos que pausavam saíram. Overlay no controle com o D-pad. **Compilado, não testado em jogo.**

## O que vale na 0.9.2

- **Em co-op** (você e pelo menos mais um jogador na run), cada escolha de upgrade abre no painel compacto e o tempo não para pra ninguém, nem pra quem escolhe. Sozinho, o jogo pausa como sempre.
- **Quais escolhas:** todas as da partida passam pelo mesmo popup (`LevelUpPanelManager`, fila `PowerUpRequests`). Achei no descompilado quatro fontes de pedido:
  - level-up (`ExperienceComponent.LevelUp`, inclusive vários na fila);
  - power-up de elite, boss e baú no chão (`CollectablePowerUpRequest.DoOnReachedTarget`);
  - escolha inicial da partida (`GameManagerUtil.StartMatch`, que também chama o `OpenPopup`);
  - `MasteryGameManager.ResetPlayer`.
  - Reroll, banish, lock e skip são botões desse mesmo popup, e a troca de skill (`ReplaceSkillsAnimation`) é parte dele. Não existe outra tela de escolha na partida: `RuneScreen…`, `SkillSelectionPanel` e `MapSelection…` são do menu.
- **`[Coop] LevelUpPause` saiu.** Era o problema da issue: quem veio da 0.8 tinha `NotWhenFullBuild` salvo no `.cfg`, e o BepInEx não troca um valor salvo quando o padrão muda. Com a opção removida, o valor velho não tem mais efeito. A linha antiga pode continuar no `.cfg` (o BepInEx guarda chaves órfãs), mas é ignorada.
- **Escape de debug:** `[Debug] LevelUpFullPanel = true` usa o painel grande do jogo (também sem pausa, e o personagem fica parado). Os outros veem "Fulano escolhendo upgrade (sem pausa)". É só pra comparar ou se o overlay quebrar em alguma máquina.
- **Rede (`PauseSync`):** o mod **não manda mais `LevelUp`** como motivo de pausa. Com o overlay o estado é `None`; no painel grande de debug, `Choosing`. Só o Esc (`Pause`) congela os outros. Um `LevelUp` recebido de uma versão antiga vira só aviso, nunca congela. O Esc continua como na fase 9: qualquer um pausa e o host despausa (fechando o próprio menu ou com F4).
- **3+ jogadores:** cada um escolhe o próprio upgrade no próprio jogo, sem mandar nada que afete os outros.
- **Opções do painel (`[Coop]`):** `LevelUpOverlayScale` (0.6), `LevelUpOverlayOpacity` (0.8) e `LevelUpPanelPosition` (`Top` / `Bottom` / `BottomLeft` / `BottomRight`). Desde a issue #5 o padrão é `Top`, o painel nunca entra no HUD de baixo e a chave antiga `LevelUpOverlayPosition` não vale mais (ver `docs/issue5-notas.md`).

## Painel compacto (overlay)

- **Visual** (`Ui/LevelUpOverlay.cs`): é o painel do próprio jogo, sem recriar nada. A cada frame, depois do `LevelUpPanelManager.OnUpdate`:
  - o grupo do popup (`PowerUpAnimation`) é reduzido, levado pra borda escolhida e fica semitransparente (`CanvasGroup`);
  - as imagens em tela cheia dentro dele (o fundo escuro) são desligadas;
  - a troca de skill recebe o mesmo tratamento e, se for um grupo separado, fica empilhada ao lado dos cards.
- **HUD visível:** um postfix no `GameplayUIManager.HasOverlayPanel` ignora o popup.
- **Movimento:** o mod religa o mapa de gameplay que o `SwitchToUIMap` desliga e resolve os conflitos por ação, pelas teclas em que cada uma está ligada:
  - ação de UI na mesma tecla de uma de gameplay é desligada;
  - as teclas de opção do jogo (`SelectPowerup1..7`), o menu de pausa e o cancelar ganham do gameplay;
  - as teclas do overlay (1-4, numpad, Tab e o D-pad do controle) também ganham, do gameplay e das outras ações de UI;
  - os botões do mouse do gameplay só ficam desligados com o cursor em cima do painel.
- **Teclado e mouse:** 1-4 (ou o numpad) escolhem, Tab esconde e mostra, e o clique funciona. Reroll, banish, lock e skip usam as teclas do jogo, se não baterem com o gameplay, ou os botões no mouse.
- **Controle (gamepad), novo na 0.9.2:**
  - o **stick esquerdo continua movendo** o personagem (a navegação de UI que usava o stick fica desligada);
  - os cards ficam no **D-pad**: **esquerda/direita** movem o destaque do próprio jogo (o mesmo índice e a mesma seleção que a navegação dele usa), **cima** escolhe e **baixo** esconde/mostra;
  - ações de gameplay ligadas no D-pad ficam desligadas enquanto o painel está aberto;
  - com o controle ativo, a faixa de avisos mostra a dica do D-pad; escondido, mostra "Level up! (N) - D-pad baixo mostra".
  - Reroll, banish, lock e skip no controle só funcionam se os botões do jogo pra isso não baterem com o gameplay (ombros e gatilhos costumam bater). Se baterem, ficam desligados no overlay e dá pra usar o mouse.
- **Textos:** os textos visíveis do overlay ficam juntos no topo do `LevelUpOverlay.cs` (classe `Texts`, comentário `// i18n`).
- **Diagnóstico:** na primeira abertura da sessão, o log mostra a hierarquia do popup (`Popup hierarchy:`), onde o painel foi posto (`Overlay on the popup …`) e o que foi desligado de input (`Overlay input:`).

## Base (continua valendo)

- **Sem pausa:** o postfix de `TimeUtil.SetTime` recalcula o `timeScale` sem o fator `LevelUpTime`. Esc, death guard, câmera lenta etc. continuam valendo.
- **Telemetria `[levelup]`:** `Popup opened: coop=… -> pause (solo) | NO PAUSE (overlay) | NO PAUSE (full panel, debug)`, `Policy changed…`, `Popup closed after X s …`.
- **Atalho de debug F6** (co-op, na partida): +1 nível no jogador local; **Shift+F6** dá +5. Não é XP de equipe.

## Riscos

1. **Escolha inicial da partida:** o `StartMatch` abre o popup antes de todo mundo estar na run. Até lá ele pausa só o jogo de quem abriu, como sozinho, e vira overlay quando os outros chegam (`Policy changed…` no log). Se a espera de carregamento (`RunSync`) segurar o tempo, isso continua igual.
2. **Controle:** os caminhos dos botões do D-pad são lidos do controle atual (`Gamepad.current`). Com dois controles ligados, só o atual vale. Não deu pra testar com controle de verdade.
3. **Layout às cegas** (desde a 0.8.1): se o painel ficar estranho, o log da hierarquia mostra o que ajustar.
4. **Teclas:** a resolução é por ação inteira. Uma ação de UI que divide tecla com o gameplay some inteira enquanto o painel está aberto (fica no log).
5. **Clique no painel:** com o cursor em cima, o botão do mouse do gameplay fica desligado (quem usa clique pra andar para enquanto o cursor está no painel).
6. **Outras telas que ainda pausam:** a dica do tutorial (`TutorialHintPanelReferences`) e o aviso de visibilidade (`VisibilityInformationPanel`) escrevem no `TimeUtil.PauseTime`, ou seja, contam como Esc e congelam todos. Não são escolha de upgrade, então ficaram fora desta issue.

## Roteiro de teste (0.9.2)

Todos com a 0.9.2. Pra testar o caso da issue, **deixe a linha antiga `LevelUpPause = NotWhenFullBuild` no `.cfg`** de quem veio da 0.8.

1. **Primeiro nível em co-op:** comecem uma run em dupla e peguem o primeiro level-up. O painel abre pequeno embaixo, ninguém congela e quem escolhe continua andando. No log: `Popup opened: coop=True -> NO PAUSE (overlay)`, mesmo com o `NotWhenFullBuild` velho no `.cfg`.
2. **Fila:** aperte **Shift+F6** (5 níveis) e escolha os 5 com 1-4 enquanto anda. Ninguém pausa em nenhum momento.
3. **Elite, boss ou baú:** matem um elite ou um boss (F8 no host chama o boss) e peguem o power-up do chão. A escolha abre no mesmo painel compacto, sem pausa.
4. **Reroll / banish / lock / skip:** use os botões do painel (mouse) no meio de uma escolha. Nada pausa.
5. **Troca de skill:** com os slots cheios, pegue uma skill nova. A troca aparece compacta, escolha o slot com 1-4.
6. **Esc continua:** com ou sem o painel aberto, Esc num dos jogadores congela todos. O host fecha o próprio menu (ou aperta F4) e todos voltam.
7. **Controle:** com um controle, abra um level-up (F6). O stick esquerdo move o personagem, **D-pad esquerda/direita** troca o card destacado, **D-pad cima** escolhe e **D-pad baixo** esconde e mostra. A faixa mostra a dica do D-pad.
8. **3+ jogadores:** com três instâncias, dois sobem de nível ao mesmo tempo. Cada um escolhe o seu e o terceiro não vê pausa nem aviso.
9. **Sozinho:** numa run sem co-op, o level-up pausa como no jogo original.
10. **Debug:** `[Debug] LevelUpFullPanel = true` mostra o painel grande sem pausa (personagem parado) e os outros veem "… escolhendo upgrade (sem pausa)". Volte pra `false`.
11. Mande o `LogOutput.log` de todos (linhas `[levelup]` e `[pause]`).
