# Roadmap do Soulstone Coop

Fonte única das próximas fases e pendências. Detalhes técnicos de cada fase ficam nos `docs/faseN-*.md`.

## Feito

| Fase | O que entrega | Commit |
|---|---|---|
| Base | Dois jogos (host/cliente) conectam e sincronizam a posição dos jogadores; o remoto é um fantasma só visual | `b4d8ba2` |
| 0 | Mapa de spawn, IA, dano e XP com pontos de patch | `fd9fb6a` |
| 1 | Cliente sem inimigos próprios, pausa e level-up sincronizados | `2b06a95` |
| 2 | Inimigos do host replicados como marionetes no cliente | `e56ab67` |
| 3 | Dano nos dois sentidos, com proxy aliado do cliente no host | `15b0d45`, `0cf1274` |
| 4C | Cada jogador vê as magias do outro (só visual) | `e7a0261` |
| 4B | Loot e XP no cliente, crédito de kill, XP de equipe | `a25e89e`, `47c4287` |
| 4A | Caído, reviver, derrota só quando os dois caem, vitória/portal Home pros dois; fantasma suavizado e animado, câmera de espectador | `ad9a096` |
| 4D | Bosses, VoidPresence, void hunters e pilares como marionetes; avisos de área dos ataques de boss, elite e especiais | `91b4851` |
| 6 | Aba Co-op no menu, conexão por IP, run começando junto, handshake com versão/nome/personagem | `70dff33` |
| 7 | Convite pela Steam, transporte P2P pelo relay da Steam | `a068c13` |
| Pacote | Script que gera o zip pra instalar em outro PC, com LEIA-ME | `5f09811` |
| **0.8.0** | Tudo abaixo junto, ainda sem teste em jogo (roteiro em `teste-0.8.0.md`) | `7452dec` |
| 5 | Fantasma com o personagem, skin e arma do parceiro; nome da Steam, barra de vida, anel e marcador no minimapa | `56b5309` |
| 8 | Simulação de rede, correção do MTU do LiteNetLib, queda do host e do cliente, aviso de conexão instável, reentrada (experimental) | `958d3f9` |
| Pendências | God Meteors no cliente, status do proxy chegando no cliente, números de dano das réplicas, proxy fora das buscas por time, código morto removido | `bb7481e` |
| 4D, correções | Contador de bosses do cliente parado em 0/N; golpe duplo da estátua do deus no replay | `03f5232`, `4625503` |
| Level-up | Com a build completa, o upgrade não congela a sessão (`[Coop] LevelUpPause`) | `6c7ce5f` |
| Portais | Portais Endless e de desafio e o Reiniciar, com os dois indo juntos | `4aec2b3` |
| **0.9.0** | Tudo abaixo junto (roteiro em `teste-0.9.0.md`) | `26e37dd` |
| 5, correções | Fantasma animado de novo, com arma; lançador das réplicas colado no fantasma; dash do parceiro | `568db38`, `c8876dc` |
| 5, item 5 | Summons, companheiros e a puma do Sentinel do parceiro como clones visuais | `568db38` |
| Level-up | Painel compacto e semitransparente pra jogar enquanto escolhe; padrão `Overlay` | `c263e20`, `efd4d89` |
| 9 | Até 8 jogadores: slots, transportes multi-peer, repasse pelo host, lobby de 8, regras com N, escala de inimigos opcional | merge da `fase9-oito` |
| **0.9.1** | Correções da revisão da fase 9: quem desistiu não é puxado pela vitória/portais do host, Desconectar do host no meio da run, reentrada não puxa quem desistiu, escala de inimigos só sobe limites | `dcd88fa` |
| **0.9.2** | Tudo abaixo junto (roteiro em `teste-0.9.2.md`) | `cfd120b` |
| Level-up | Toda escolha de upgrade no painel compacto, sem pausa em co-op; `[Coop] LevelUpPause` removido (#1) | `9289c2d` |
| 5, dash | Rastro do dash no fantasma do parceiro, na cor do personagem (#2) | `f6ca3b8` |
| i18n | Textos do mod no idioma do jogo de cada jogador (en e pt-BR à mão; es, fr, de, ru, zh, ja automáticos, a revisar) | `964e991` (`i18n-notas.md`) |
| **1.0.0** | Tudo abaixo junto; jogada por duas pessoas pela Steam | `0c6067c` |
| UI nativa | Aba e painel Co-op feitos com peças da UI do próprio jogo (`ui-nativa-notas.md`) | `227a58c` |
| Atualização | Aviso no menu quando sai versão nova (`[Updates] CheckForUpdates`) | `c9db3b5` |
| **1.1.0** | Tudo abaixo junto | `fc520cb` |
| #7 | Painel do grupo no HUD: vida, nível, XP, revives e estado de cada parceiro (`issue7-notas.md`) | `f16b5fa`, `3df1ac2` |
| #5 | Painel de upgrade fora do HUD, centrado nas cartas, com controle completo (`issue5-notas.md`) | `a961ab8` |
| #6, #3 | Convidado acha inimigos fora das próprias listas de tier; maldições (afixos) vêm do host (`issue6-notas.md`) | `0ff2f28`, `97e8f72` |
| **1.2.0** | Tudo abaixo junto; testado com duas janelas (`tools\test-local.cmd`) | — |
| Baús | Baús e minérios para todos: espelho no convidado, proxy abre, mais rápido com mais gente em cima (`baus-notas.md`) | — |
| Reviver | Barra de reviver cai devagar longe do caído; log do host a cada 2 s enquanto alguém está caído | — |
| 4C, mira | Mira contínua do channeling do parceiro (`canal-mira-notas.md`) | — |
| 4D, comuns | Ataque animado dos inimigos comuns no convidado (`inimigos-ataque-notas.md`) | — |
| 4C, escudos | Escudos giratórios do parceiro (`sempre-ligadas-notas.md`) | — |
| Ferramentas | `tools\test-local.cmd` (duas janelas conectadas no mesmo PC) e F3 (baú na frente do host) | — |

## Agora

A **1.2.0** está pronta (`PluginVersion` subido, notas em `releases/1.2.0.md`, README atualizado). Os itens dela foram testados com duas janelas no mesmo PC (`tools\test-local.cmd`). Antes de publicar com `tools\release.ps1`, falta uma partida pela Steam com um amigo (checklist em `teste-1.2.0.md`).

## Próximos itens

Em ordem de prioridade. Detalhes e endereços em `proximos-investigacao.md` (seção indicada).

| # | Item | Esforço | Origem |
|---|---|---|---|
| 1 | Reconexão completa no meio da run, mantendo nível, skills e itens (hoje a reentrada experimental entra como personagem novo). O jogo já salva e devolve isso entre fases do Endless (`GameModeBaseData`) | médio a grande | `fase8-notas.md`, §5 |
| 2 | `MapTask` que qualquer jogador pode começar: o gatilho de início é um `InteractableTriggerObject`, como o dos baús (`ChestSync` serve de modelo) | pequeno a médio | `baus-notas.md` |
| 3 | Modo Crusade (começa num lobby e troca de mapa no meio; portas e baús agora têm base) | grande | `fase6-notas.md`, `portais-notas.md`, §6 |
| — | Esquiva por dash nos hits que o host calcula no proxy (mexe no balanceamento) | — | `pendencias-notas.md` |
| — | Host morto com cliente vivo: rever proxy e spawn | — | `fase3-notas.md` |
| — | Skin de mod do parceiro vira a skin padrão; modelo de transformação (Ascension) e companheiros visuais da arma não aparecem no fantasma | — | `fase5-notas.md` |
| — | Voz de "abrindo baú" do personagem do convidado não toca | — | `baus-notas.md` |

## Ainda não vistos em jogo (de fases anteriores)

- Barra de boss sumindo ao matar, invocações de boss, minérios e cristais de cura, vida u16 no snapshot (`fase4d-notas.md`).
- O cliente revivendo o host e o efeito ao levantar (`fase4a-notas.md`).
- Itens e power-ups via `ProcessLoot`, bloqueio do portal do boss no cliente, procs on-kill com runa de kill, XP retido de jogador morto (`fase4b-notas.md`).
- Patch 13 (PlayerSeekerAI) nunca exercitado (`fase3-notas.md`).
- Conexão por IP na fase 6: recusa por versão (`--coop-fake-version`), volta ao menu e segunda run (`fase6-notas.md`).

## Regras que valem pra qualquer fase

- Nunca criar um segundo Entity de jogador. Toda Entity nova passa pelo pipeline do jogo (pool + `Entity.Setup`), e nada é clonado do jogador antes do `StartMatch` local.
- Todo patch com try/catch: um NRE dentro de um `UpdateAll` derruba o frame inteiro.
- Não patchar método com struct de 1/2/4/8 bytes por valor na assinatura (`fase4c-notas.md`).
- Texto flutuante do jogo (`OverheadCombatTextPanelManager.RequestCombatTextWithString`) aceita no máximo 16 caracteres; acima disso dá erro e o texto não aparece.
- Ao subir a versão, mudanças de formato de pacote quebram a compatibilidade: o handshake recusa versões diferentes.
- Enquanto o usuário testa uma versão fixa, `%USERPROFILE%\.soulstone-no-deploy` trava o deploy (ou use `-p:SkipDeploy=true`).
- Texto visível ao jogador sai da tabela `Ui/LocTable.cs` (`Loc.T` / `Loc.Msg`), nunca literal no código; pela rede vai a `Loc.Msg` (chave + parâmetros), nunca o texto traduzido (`i18n-notas.md`).
- Pacotes de rede: veja o enum `PacketType` em `src/SoulstoneCoop/Net/PacketRouter.cs` (a fase 9 usa 100–104). Itens novos começam no 120.
