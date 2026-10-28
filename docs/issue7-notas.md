# Issue #7: painel do grupo na partida (notas)

Pedido do jogador: ver no HUD a vida, o nível, os revives e a barra de XP dos aliados, pra saber se alguém caiu e como
vai a run de cada um.

## O que aparece

Painel no canto superior esquerdo (`Ui/PartyHud.cs`), uma linha por parceiro (até 7), em ordem de slot:

- faixa fina na cor do jogador (a mesma do anel e do nome do fantasma), ícone do personagem dentro da moldura
  `Skill_Tree_Character_BGFrame`, fundo `Craft_Background_List_Frame` (as peças das linhas do painel Co-op);
- nome da Steam na cor do jogador (vermelho se caído/morto), com "instável" em vermelho quando a conexão está calada;
- **revives restantes**: ícone `Death_Guard` do próprio HUD do jogo + "x2". Some com 0;
- **nível** ("Nv 23", dourado);
- **barra de vida** com "182 / 210" por cima (verde > 50%, amarelo > 25%, vermelho; tons escuros pros números brancos aparecerem);
- **barra de XP** fina embaixo: progresso **daquele jogador** até o próximo nível dele;
- **estado**: vivo; **CAÍDO** (barra vermelho-escura); **REVIVENDO 45%** (barra âmbar enchendo com o progresso do
  revive); **MORTO** (linha apagada, ícone cinza); **Desconectado** (apagada, fica 30 s); fora da partida ("no menu",
  "carregando", "trocando de fase", linha apagada).

### O que é "revive" e "XP" neste jogo

- **Revives = cargas de Death Guard** (`InventoryData.DeathGuards`), a "segunda chance" do jogo: um golpe letal gasta uma
  carga e o jogador volta com 50% da vida. Vêm da árvore de habilidades (`SkillTreeDeathGuardSO`) e de runas. O HUD do
  jogo mostra o mesmo número (`DeathGuardPanelReferences.ChargeCount`). Só depois de acabarem é que o jogador fica caído
  (estado co-op da fase 4A). O revive por proximidade do co-op não tem limite e não é contado.
- **XP é de equipe, nível é de cada um**: todo orbe soma pros dois, mas cada lado aplica o próprio stat de XP, então os
  níveis podem se separar. A barra mostra `_experience / _requiredExperience` do `ExperienceComponent` de cada jogador.

## Rede

**Pacote novo `PartyStatus = 125`** (faixa 125–129 reservada pra issue #7), nos dois sentidos, ReliableOrdered:
`nível u16 | progresso de XP u8 (0..255) | death guards u8`. Sai na mudança (nível, cargas, ou XP mudando ≥ ~1%),
no máximo a cada 0,25 s (4 Hz), e a cada 2 s como refresco; na hora pra quem conecta e quando nasce um fantasma novo
(nova run). O host repassa o de cada cliente aos outros (`RelayMethod`, envelope `Relayed`). `Life/PartyStatus.cs`.

O resto já existia e é só lido:

| Dado | De onde |
|---|---|
| vida, vida máxima, caído, morto | `PartnerHealth` (50), do próprio jogador, ≤ 5 Hz |
| progresso do revive | `CoopLife.ProgressOf(slot)`: no host, a conta dele; no cliente, o pacote `CoopLife` (18) |
| nome, personagem | `Hello` (30) → `RemotePlayer.Info` |
| na partida / menu / carregando | `LobbyState` (31) → `RemotePlayer.Place` |
| instável | `NetworkManager.IsUnstable(slot)` |
| desconectado | `Players.Left` com o jogador na run (ou na run nos últimos 20 s: fechar o jogo manda "voltei ao menu" antes de a conexão cair) |

## Detalhes da UI

- Fica sob a raiz do Co-op UI (montada no menu), num **canvas aninhado com `overrideSorting` e ordem −90**: abaixo de
  todos os canvases do jogo (level-up, pausa, tela de fim, que ficam sempre por cima) e acima das plaquinhas de nome
  dos fantasmas (−100). A troca de fonte por idioma do `CoopUi.ApplyLanguage` pega o painel também.
- Tamanho em unidades de 1080 de altura: linhas de 360x54, a partir de y = 64 (abaixo da linha de versão do jogo);
  7 linhas terminam em ~466, logo acima dos contadores de gema do jogo (~480). CanvasGroup com alpha 0,9; sem raycast.
- Some com `[Coop] PartyHud = false`, fora de uma run, na troca de fase, sem parceiros e quando o jogo esconde o
  próprio HUD (`GameplayUIManager.ForceHideGameplayElements`).
- **Ícones**: os de 128 px do menu são descarregados quando a cena da run carrega (só sobra o de 268 px do personagem
  em jogo). O painel guarda os 23 (`CharactersType`) em Images inativas montadas no menu, e assim eles continuam
  carregados na partida.
- Textos atualizados a 10 Hz, só quando mudam. Log `[party]` a cada 5 s com o que cada linha mostra e o próprio
  nível/XP/cargas, mais `sent/recv` do pacote.
- Textos novos (8 idiomas, `Ui/LocTable.cs`): `party.level`, `party.down`, `party.reviving`, `party.dead`,
  `party.gone`. Reusa `place.*` e `st.unstable_tag`.

## Config

`[Coop] PartyHud` (padrão `true`). Linha nova na tabela de opções do README.

## Verificação (duas instâncias por IP no mesmo PC)

Modo de debug novo, `Ui/PartyShot.cs`, só por argumento:

```
host:    "Soulstone Survivors.exe" -screen-fullscreen 0 -screen-width 1600 -screen-height 900 --coop-host --coop-party-shot <pasta>\host [--coop-ui-shot-lang pt-BR]
cliente: "Soulstone Survivors.exe" -screen-fullscreen 0 -screen-width 1600 -screen-height 900 --coop-connect 127.0.0.1 --coop-party-shot <pasta>\client
```

Abra o cliente **só depois** que o host chegar ao menu (`Menu ready` no log): uma janela Unity sem foco não carrega até
o mod ligar o `runInBackground`. O host passa da tela de título, espera o parceiro no menu, começa uma run no Desert
(como o cliente faz no `RunStart`), tira os prints e fecha. No roteiro, os dois ficam invulneráveis; o cliente, a partir
de 10 s, toma o golpe do F9 até cair (as cargas de Death Guard vão primeiro), espera o revive e fecha o jogo.
Prints: `01` 2 jogadores, `02` 7 linhas falsas em todos os estados, `03`–`05` parceiro caído/revivendo, `06` depois,
`07` desconectado; o cliente tira os seus (`c01`–`c04`). Também grava `run-ui-dump.txt` (hierarquia da UI da run).
`--coop-party-shot-map <cena>` troca o mapa.

`--coop-party-shot-release` (nos dois, com `-popupwindow` e `--coop-name Mira` no cliente): prints pras notas de
versão, `release-1..3`, com três parceiros (Mira = o cliente de verdade, Tobias revivendo, Kael com pouca vida) e nada
de debug na tela.

Visto nos prints (1280x720, 1600x900, 1920x1080; pt-BR no host, en no cliente):

- linha real do parceiro com ícone, nome, vida com números, nível e "x2" de revives; o cliente vê o host igual;
- **cargas de revive ao vivo**: `rev=2 → 1 → 0` em cada F9 (e nos golpes dos inimigos), e só depois disso "CAÍDO";
- **CAÍDO → REVIVENDO 29%/45%** com a barra âmbar, e de volta ao normal depois do revive (o host chegou perto);
- **MORTO** (na rodada em que os dois caíram) e **Desconectado** quando o cliente fechou o jogo;
- **8 jogadores** (7 linhas falsas): cabem acima dos contadores de gema em 900p e 1080p;
- a **tela de derrota** do jogo e o **painel de level-up compacto** ficam por cima do painel, sem sobreposição;
- nível e XP do parceiro batem com o log dele (`me lv=… xp=…` de um lado = linha do outro).

## Limites

- Não testado com 3+ instâncias reais nem pela Steam (o repasse é o mesmo do `PartnerHealth`).
- A posição é fixa no canto superior esquerdo. Em telas muito altas (4:3, 16:10) sobra espaço, mas não testei.
- O "MORTO" real só existe no fim da run (derrota) ou para quem ficou caído quando o parceiro saiu; o resto do tempo um
  aliado fora de combate é "CAÍDO".
