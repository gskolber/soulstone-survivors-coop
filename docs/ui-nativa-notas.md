# UI nativa: aba e painel de Co-op com peças do jogo

Pedido: "dá para melhorar o botão e o form do coop? ele está extremamente feio, tentar reutilizar componentes do jogo mesmo". Investigação em `docs/ui-nativa-investigacao.md`.

## O que mudou

**Aba "Co-op"** (`Ui/CoopUi.cs`, `TryCreateMenuTab`)
- Continua sendo um clone da aba Perfil, mas agora fica com as animações e o `EventTrigger` dela: os componentes de animação do clone entram no `MainMenuPanel.FadeAnimationGroup.Animations`, então o jogo desliza, esmaece, mostra e esconde a aba junto com as outras. Acabou o espelhamento manual de alpha/posição a cada frame.
- Pra caber, todas as abas andam meio passo (97,5) pra esquerda (início e alvo da animação) e a nossa fica um passo à direita do Perfil. O `HorizontalLayoutGroup`/`ContentSizeFitter` do holder é desligado (não é ele que posiciona as abas, e ganhar um filho o faria reposicionar tudo errado).
- Se a barra encostar na engrenagem de Opções (16:9 dá ~1%; 16:10/4:3 mais), o holder encolhe por inteiro (`FitTabBar`). Em ultrawide fica em 1.
- Estado selecionado: postfix em `MenuManager.AdjustSelectedButton`. Com o painel aberto, a aba Co-op fica `Menu_Tab_Select` e as outras normais; fechado, normal. Hover (troca de sprite), som de clique (`UIHighlight`) e de hover são os da própria aba.

**Painel** (`Ui/CoopUi.cs`, `BuildPanel`; peças em `Ui/GameLook.cs`)
- Canvas próprio com ordem 500 (acima do menu, abaixo de Opções 505 e Sair 502), escala igual à do jogo (1080 de altura).
- Fundo escurecido como nos pop-ups do jogo, deixando livre a barra do topo (abas, opções e sair continuam clicáveis; trocar de aba fecha o painel).
- Janela `Menu_Frame_NotTransparent` 1200x740 com o brilho `Yellow_Glow_01`, título em `Title_Frame`, `Close_Button` no canto (som `UIClose`). Abre/fecha com fade + slide de 0,15 s.
- Coluna esquerda: seção "Convidar pela Steam" (Convidar amigo (n/8) / Copiar código), seção "Conexão direta" (campo de IP + Colar, Entrar / Hospedar por IP) e a caixa de status (`Menu_Frame_Transparent`). Títulos de seção na faixa `UI_Frame_Title_list`.
- Coluna direita: "Jogadores (n/max)" e até 8 linhas `Craft_Background_List_Frame`: ícone do personagem (sprite 128 px do jogo dentro de `Skill_Tree_Character_BGFrame`), nome (host em dourado, "(você)" na sua linha), "Host · personagem" com o nome do personagem traduzido pelo jogo, onde está (no menu / carregando / na partida / trocando de fase / instável em vermelho) e ping colorido (verde < 80, amarelo < 150, vermelho). Vagas livres aparecem apagadas até o máximo da sessão. Fora do co-op, só a sua linha.
- Desconectar embaixo, no centro (some quando não há nada pra desconectar).
- Botões (`GameLook.Button`): clone da aba do menu (Image + Button com troca de sprite + `UIButtonSound` + `EventTrigger` do hover + label com `LabelReferences`), sem animações e sem localização, com a skin `Fight_Button1`→`Fight_Button` (principais: Convidar, Entrar) ou `Button`→`Button_Selected` (secundários). Som de clique `MenuClick`.
- Campo de IP: clone do campo de e-mail da newsletter (`MailingPanel.EmailInput`), com os eventos trocados e a moldura de foco (`SelectionHighlight`) esticada pro tamanho novo. Se não existir, cai no campo montado à mão (agora com `Achievement_Frame`).
- **Esc** (ou B do controle, ou o botão de sair do topo) com o painel aberto fecha só o painel: prefix em `QuitScreenPanel.Open` devolve `false` nesse caso.
- O foco do teclado no campo de IP continua pelo `GameLoopKeepSelectionPatch` (sem mudança).
- Se algum sprite ou modelo sumir numa atualização do jogo, o `GameLook` cai pra uGUI simples (cor sólida) em vez de quebrar.

**Faixa de avisos**: agora numa faixa `Menu_Frame_Transparent` (860 de largura). No menu fica abaixo da barra de moedas; com o painel aberto, embaixo da tela; na partida, onde já ficava.

**Textos novos** (8 idiomas, `Ui/LocTable.cs`): `ui.sec_steam`, `ui.sec_ip`, `ui.players`, `ui.you`, `ui.host_tag`, `ui.empty_slot`, `ui.alone`. Fontes por idioma como antes (`ApplyLanguage` troca a fonte de tudo que é nosso; verificado em russo).

## Modo de screenshot (debug)

`Ui/UiShot.cs`, só por argumento de linha de comando:

```
"Soulstone Survivors.exe" -screen-fullscreen 0 -screen-width 1600 -screen-height 900 --coop-ui-shot <pasta>
```

- Passa a tela inicial sozinho (botão Jogar), espera a aba Co-op aparecer, e salva `01-menu-tab`, `02-menu-tab-hover`, `03-panel-idle`, `04-panel-hover-focus` (campo de IP com foco), `05-panel-hosting`, `06-panel-connected` (host com 3 parceiros), `07-panel-client`, `08-panel-full` (8/8), `09-panel-error`, `10-panel-banner`, `11-menu-banner` (depois de simular o Esc). Os estados de rede são falsos (`CoopUi.DebugFake`), nada conecta.
- Grava `ui-dump.txt`: hierarquia de todos os canvas carregados (RectTransform, componentes, sprites, fontes, cores, transições de botão, sons, `EventTrigger`, listeners persistentes) + lista de sprites e fontes em memória.
- Fecha o jogo no fim (ou depois de 180 s).
- Opções: `--coop-ui-shot-tag <prefixo>` (nome dos arquivos), `--coop-ui-shot-lang <código>` (troca o idioma **do jogo** sem salvar nas preferências: `pt-BR`, `ru`, `zh`…), `--coop-ui-shot-gallery a,b,c` (desenha esses sprites numa grade com o nome e tira uma foto: pra escolher peças).

**Vídeo de demonstração:** `--coop-ui-demo --coop-ui-shot <pasta>` roda um roteiro fixo de ~13 s (aba com hover, painel abrindo, IP digitado, hospedar, 4 amigos entrando um a um, Esc fechando) com o relógio do jogo travado em 30 fps (`Time.captureFramerate`) e salva um PNG por quadro. Depois:

```
ffmpeg -framerate 30 -i frame_%04d.png -c:v libx264 -crf 20 -pix_fmt yuv420p -movflags +faststart coop-ui-demo.mp4
ffmpeg -framerate 30 -i frame_%04d.png -vf "fps=15,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128[p];[b][p]paletteuse=dither=bayer:bayer_scale=4" coop-ui-demo.gif
```

Testado assim: 1600x900 (en), 1280x720 (pt-BR), 1920x1080 (en), 2560x1080 ultrawide (ru).

## Roteiro de teste (com gente)

1. Menu: a aba **Co-op** aparece no fim da barra junto com as outras (mesma animação de entrada), sem encostar na engrenagem. Passar o mouse: fica dourada e toca o som de hover; clicar: som de clique e o painel abre, com a aba Co-op dourada e Lutar normal.
2. Painel: clicar fora não fecha; **Esc** fecha (e *não* abre "Sair do jogo?"); o X fecha; clicar em outra aba (Personagem) fecha e vai pra ela; clicar de novo em Co-op fecha.
3. Campo de IP: clicar, apagar e digitar um IP (Delete/Insert não disparam atalhos), Colar, Entrar. A moldura dourada de foco envolve o campo.
4. Host por Steam: Convidar amigo → o botão vira "Convidar amigo (1/8)", Copiar código liga; a lista mostra você como Host. Quando o amigo entra, aparece a linha dele com personagem, "no menu" e ping.
5. Cliente: a lista mostra o host primeiro (dourado, "Host · personagem") e você com "(você)"; status "Conectado com…". Desconectar volta pro estado inicial e o botão some.
6. Durante a partida: o painel não aparece; a faixa de avisos continua no topo como antes.
7. Trocar o idioma nas Opções com o painel fechado e abrir de novo: textos e fonte no idioma novo (testar um de fonte diferente: russo/chinês/japonês).
8. Resoluções: 1280x720, 1920x1080 e uma ultrawide ou 16:10 (na 16:10 a barra de abas encolhe um pouco pra não bater na engrenagem).

## Limites conhecidos

- Num perfil novo, com abas ainda escondidas (`HandleButtonState`), as posições continuam as do jogo deslocadas meio passo; não testado.
- O hover simulado no modo de screenshot não acende o sprite (falta o `EventSystem` de verdade); no jogo com mouse acende.

## Aviso de nova versão (1.0)

`Ui/UpdateCheck.cs` + o pop-up em `Ui/CoopUi.cs` (`BuildUpdatePopup`).

- No `Plugin.Load`, uma `Task` faz um GET em `api.github.com/repos/gskolber/soulstone-survivors-coop/releases/latest` (`HttpClient`, timeout de 5 s, `User-Agent: SoulstoneCoop/<versão>`, `Accept: application/vnd.github+json`) e lê só `tag_name` e `html_url` (`System.Text.Json`). Qualquer falha (sem rede, rate limit, não-200, JSON ruim) vira uma linha de log `[update]` e nada mais. Drafts e prereleases nunca são "latest".
- Compara tirando o "v" e o sufixo `-pre`/`+build`, com `System.Version`. Só mostra se a última for maior que `PluginVersion` e diferente de `[Updates] SkippedVersion`.
- O resultado passa pra thread principal por um campo `volatile`, assim como a linha de log, que é escrita na thread principal. O pop-up aparece uma vez por execução, quando o menu principal fica parado por 1 s: nunca durante uma partida ou um carregamento, e não por cima do painel Co-op (espera ele fechar).
- O pop-up tem o mesmo estilo do painel (moldura, título, X). Os botões são "Abrir página" (`Application.OpenURL`, só se a URL começar com `https://github.com/gskolber/soulstone-survivors-coop/`; senão abre `.../releases/latest` do repositório), "Agora não" e "Não lembrar desta versão", que grava a tag em `[Updates] SkippedVersion` e é um botão só de texto (`GameLook.Style.Text`, como o Sim/Cancelar do pop-up de sair). Esc = Agora não (o mesmo prefix em `QuitScreenPanel.Open`).
- Config: `[Updates] CheckForUpdates` (padrão `true`; `false` = nenhuma requisição) e `[Updates] SkippedVersion`.
- Debug: `--coop-fake-latest 1.0.1` (ou `=1.0.1`) pula a rede e finge a última versão. `--coop-update-url <url>` troca o endpoint, pra testar falhas. No modo `--coop-ui-shot`, se o pop-up estiver aberto, sai primeiro o `00-update-popup.png` e depois ele é fechado pelo caminho do Esc.

Verificado com o modo de screenshot:
- `--coop-fake-latest 1.0.1` em en e pt-BR: o pop-up aparece, e o Esc fecha sem abrir o "Sair do jogo?".
- Sem fake, com rede: "latest is 0.9.2, this is 1.0.0 - up to date", sem pop-up.
- Endpoint inválido: `HttpRequestException ... - skipped`, sem pop-up.
- `SkippedVersion = v1.0.1` com fake 1.0.1: "the player skipped it", sem pop-up.

Não testado com clique: "Abrir página" (abrir o navegador) e a gravação do `SkippedVersion` pelo botão.
