# UI nativa: investigação da UI do menu

Base: dump da hierarquia feito pelo modo `--coop-ui-shot` (ver `ui-nativa-notas.md`) no menu do jogo 1.5.3.2, decompilação (Ghidra headless, `-readOnly`) de `MainMenuPanel`, `MenuManager.UpdateAll`, `AdjustSelectedButton`, `UIButtonSound` e `ButtonSelectionState`, e `tools/il2cpp_dump/dump.cs`.

## Como o menu é montado

- Tudo vive na cena `Menu-Survivors`, cada tela num canvas `ScreenSpaceOverlay` próprio com `CanvasScaler` de referência 1920x1080 e `match = 1` (a altura manda: são sempre 1080 unidades de altura e a largura acompanha o aspecto; 16:9 = 1920, 21:9 = 2520, 4:3 = 1440).
- Ordem dos canvas: menu 5, Fight/Character etc. 0, `Version-Panel` 501, `Quit-Panel` 502, `Options-Panel` 505, tooltips 550, avisos e loading 1001.
- `Menu-UI-Canvas-Survivors/MainMenu-Panel/Top-Menu-Holder` tem as 7 abas (`Fight-Button` … `History-Button` = "Perfil"). Cada aba: `Image` (`Menu_Tab` / `Menu_Tab_Select`), `Button` com troca de sprite, `CanvasGroup`, `DisableEnableAnimation` + `CanvasAlphaAnimation` + `RectTransformAnchoredPositionAnimation`, `UIButtonSound 'UIHighlight'` e um `EventTrigger` (PointerEnter/Select → `UIButtonSound.onSelect`, o som de hover). O texto tem `LabelReferences` (fonte por idioma) e `LocalizedLabel`.
- O holder tem `HorizontalLayoutGroup` + `ContentSizeFitter`, mas quem posiciona as abas em execução é a animação de entrada: as posições-alvo (−585 … 585, passo 195) estão nas `RectTransformAnchoredPositionAnimation`, com âncora (0.5, 1). Se o layout group rodar de novo (por exemplo ao ganhar um filho), ele reposiciona tudo com âncora no canto e a animação passa a mandar as abas para o lugar errado: foi o "Co-op 150 px acima da tela" do segundo teste da fase 6.
- `MainMenuPanel.FadeAnimationGroup` (`UIDeltaTimeAnimationGroup`) tem a lista `Animations` (`UIAnimation[]`) de todas as abas. É ela que faz o slide/fade quando o menu aparece ou some. É um array de `Il2CppObjectBase`: dá pra trocar por um maior.
- **Aba selecionada:** `MenuManager.UpdateAll` chama `AdjustSelectedButton(MainMenuState, MainMenuPanel)` (0x66A690) **todo frame**, que põe `ButtonSelectedSprite` na aba do estado atual e `ButtonRegularSprite` nas outras. Um postfix ali é o lugar certo pra pintar a nossa (enum de 4 bytes = int, sem struct pequeno por valor).
- `MainMenuPanel.HandleButtonState(button, ativo)` só liga/desliga uma aba e troca o `GameObject` da `DisableEnableAnimation` correspondente (abas ainda não liberadas).
- **Esc no menu:** `UpdateAll` lê a ação de cancelar do `InputManager` (+0x230) e, se Options e o formulário não estiverem abertos, chama `QuitScreenPanel.Open()` ("Sair do jogo?"). O botão de sair do topo faz o mesmo via `MainMenuPanel.QuitPressed`. Interceptar `QuitScreenPanel.Open` (sem argumentos) cobre Esc, B do controle e o botão.
- Sons: `UIButtonSound.OnPointerClick` chama `AudioUtil.PostEventFireAndForget(soundEvent, gameObject)` (Wwise). Eventos usados no menu: `UIHighlight` (132), `UICheckbox` (62), `MenuClick` (39), `UIClose` (10), `UIFightClick` (1). Hover: o `EventTrigger` → `UIButtonSound.onSelect`.

## Peças reaproveitáveis

| Peça | Onde | Sprites / componentes |
|---|---|---|
| Janela de pop-up | `Fight-Panel/.../Character-Resources-Holder`, `SplashScreen/.../Delete-Panel-Background` | `Menu_Frame_NotTransparent` (sliced), `Yellow_Glow_01` no topo |
| Título de janela | Quit, Options, Save, painel do personagem | `Title_Frame` (simple, ~491x94) |
| Fundo de diálogo / faixa | Quit, Splash erro | `Menu_Frame_Transparent` (sliced), `Black-Fade` preto ~0.9 |
| Botão principal | Lutar, Options Fechar, Mapa | `Fight_Button1` → `Fight_Button` (cinza → dourado), 676x140 |
| Botão secundário | Splash Voltar, Learn more, DamageBreakdown Fechar | `Button` → `Button_Selected` (escuro → dourado), 664x141 |
| Botão de lista | Slots de save | `Replace_Skill_Button` → `_HL` |
| Botão pequeno | Newsletter "Inscrever" | `Inspect_Background_Frame` → `Inspect_Selected_Frame` (+ `SelectionStateFeedback`) |
| Fechar | Newsletter, mods, saves | `Close_Button` → `Close_Button_HL`, `UIButtonSound 'MenuClick'`/`'UIClose'` |
| Abas laterais de texto | Options (General, Audio…) | `Button` ColorTint branco → `#FFC700`, ponteiro `UI_Objective_Check` |
| Campo de texto | `MenuManager.MailingPanel.EmailInput` | `Achievement_Frame` + `SelectionHighlight` (`Item_Frame_Select`) |
| Faixa de título de lista | Options `Divisor` | `UI_Frame_Title_list` |
| Linha de lista | Forja | `Craft_Background_List_Frame` (1024x120) |
| Moldura de ícone | Painel do personagem | `Skill_Tree_Character_BGFrame` (octógono) |
| Ícone do personagem | Tela de personagens | sprite 128x128 com o nome do `CharactersType` (`MonkeyKing`, `Barbarian`…); nome traduzido por `LocalizationManager.GetLocalizedStringOrDefaultToKey(nomeDoTipo)` |
| Checkbox | Options, painel do personagem | `Check_Options` / `Check_Options_Select` |

Os sprites estão carregados no menu: `Resources.FindObjectsOfTypeAll<Sprite>()` acha todos pelo nome (há nomes repetidos; os ícones de personagem vêm em 128 px e maiores).

## Como clonar com segurança

- `Object.Instantiate` remapeia as referências internas (inclusive alvos persistentes de `UnityEvent` e o `Transform` das animações) para o clone. O `EventTrigger` do clone chama o `UIButtonSound` do próprio clone: pode ficar.
- Remover: `LocalizedMonoBehaviour` (reescreve o texto), `UIAnimation`/`UIDeltaTimeAnimationGroup` de outra tela (ninguém as dirige no clone), `GamepadSpriteAdjuster` e filhos "Controller"/"Notification".
- `Button.onClick = new ButtonClickedEvent()` tira o listener persistente original (`RemoveAllListeners` não tira). No `TMP_InputField`, trocar os cinco eventos (`onEndEdit`, `onSubmit`, `onValueChanged`, `onSelect`, `onDeselect`).
- `SpriteState.pressedSprite` não tem setter no interop: copiar o struct e mudar só `highlightedSprite`/`selectedSprite`.
- `CanvasGroup` herdado de uma aba guarda o alpha do momento do clone: tirar (botões) ou deixar o grupo de animação do menu dirigir (a aba).
