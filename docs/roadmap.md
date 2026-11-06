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
| **1.2.0** | Tudo abaixo junto; testado com duas janelas (`tools\test-local.cmd`); nunca publicada sozinha (entra na 1.3.0) | `95e39f0` |
| Baús | Baús e minérios para todos: espelho no convidado, proxy abre, mais rápido com mais gente em cima (`baus-notas.md`) | — |
| Reviver | Barra de reviver cai devagar longe do caído; log do host a cada 2 s enquanto alguém está caído | — |
| 4C, mira | Mira contínua do channeling do parceiro (`canal-mira-notas.md`) | — |
| 4D, comuns | Ataque animado dos inimigos comuns no convidado (`inimigos-ataque-notas.md`) | — |
| 4C, escudos | Escudos giratórios do parceiro (`sempre-ligadas-notas.md`) | — |
| Ferramentas | `tools\test-local.cmd` (duas janelas conectadas no mesmo PC) e F3 (baú na frente do host) | — |
| **1.3.0** | Tudo abaixo junto, com a 1.2.0 | — |
| Tarefas | Marcadores de tarefa do mapa para todos; qualquer jogador começa a tarefa (`tarefas-mapa-notas.md`) | — |
| Host caído | Inimigos parados em volta do corpo do host caído são reciclados; referência do spawn no plano (`host-caido-notas.md`) | — |
| Ascensões | Invocações de ascensão (centurião do Legionnaire, demônio do Demon Hunter) não são mais repetidas nos outros: travavam as magias deles; vigia que avisa no log quando o jogador local para de lançar (`invocacoes-ascensao-notas.md`) | — |
| Olhar | Inimigos perto de um jogador olham para ele no convidado (`inimigos-olhar-notas.md`) | — |
| Caído | Raio de reviver 3,5 u; anel no chão com o progresso e linhas até os aliados vivos (`caido-marcadores-notas.md`) | — |
| Ascensão | Modelo de ascensão / transformação e escala do parceiro no fantasma (`ascensao-notas.md`) | — |
| Passivas | Objetos das mecânicas de cada personagem visíveis para todos, sem interação (`passivas-notas.md`) | — |
| Tarefas | Timer e contador da tarefa no painel do convidado (`tarefas-mapa-notas.md`) | — |
| Perigos | Meteoros do deus se revezam entre os jogadores vivos (`perigos-notas.md`) | — |
| Desempenho | Reciclador, snapshot, marionetes e vigia com menos trabalho por frame (`desempenho-notas.md`) | — |
| **1.3.1** | Correções do teste da 1.3.0 e os itens abaixo; nunca publicada sozinha (entra na 1.4.0) | — |
| Ascensão | Escala do modelo principal, objeto acrescentado (Legionnaire), legionários e criaturas invocados visíveis (chave da skill no `SummonSync`), efeitos de ativação (VFX, som, luz do mundo) (`ascensao-notas.md`) | — |
| Skins de mod | Skin de mod do parceiro quando o mesmo mod está instalado; companheiros de arma (`ascensao-notas.md`) | — |
| Passivas | Marcador no lugar quando o prefab do outro personagem não está carregado (`passivas-notas.md`) | — |
| Olhar | Só inimigo parado olha para o jogador: sem moonwalk, a Morte volta à velocidade normal (`inimigos-olhar-notas.md`) | — |
| **1.4.0** | Tudo abaixo junto, com a 1.3.1 | — |
| Reentrada | Quem caiu da run volta com nível, upgrades, runas e itens (`RunStart` com id da run; build guardada a cada 5 s; atrás de `AllowRejoin`) (`reentrada-notas.md`) | — |
| Baús | Voz de "abrindo baú" / "minerando" do personagem do convidado (`baus-notas.md`) | — |
| Desempenho | Falhas de busca de prefab (invocações, passivas, inimigos, ascensão) não se repetem a cada spawn; material dos marcadores reaproveitado (`desempenho-notas.md`) | — |
| Ascensão | Troca de skin com o parceiro ascendido apaga os modelos velhos; acréscimo do Legionnaire procurado de novo se o componente chega depois (`ascensao-notas.md`, `desempenho-notas.md`) | — |
| **1.6.0** | Volta à partida, animações dos objetos, cristais do Machinist, caído deitado | — |
| Reentrada | `AllowRejoin` ligado por padrão; Lutar no menu pede para entrar na run do host (`RunJoinRequest = 34`); reconexão automática de 45 s sem recarregar (`reentrada-notas.md`) | — |
| Passivas | Estados de `Animator` dos objetos de mecânica (`PassiveAnims = 149`) (`passivas-notas.md`) | — |
| Caído | Corpo deitado de costas em todas as telas (`caido-marcadores-notas.md`) | — |
| **1.5.0** | Espelhamento genérico das mecânicas de todos os heróis (`passivas-notas.md`, "1.5.0") (`a99e610`) | — |
| Passivas | Objetos de `Resources` `powers/` achados por varredura (raízes da cena + pool), tipo `255` com o nome do prefab; valores animados de material (`PassiveMaterials = 148`): barras, dissolves, fades | — |
| **1.4.1** | Correções do teste da 1.4.0 (`fa31e86`, `abaf7bf`) | — |
| Morte | Agente que anda mira ≥ 1 u à frente, sem `StopMovement`, `stoppingDistance` 0, sem freio e na velocidade medida do host; limiar de movimento 0,6 → 0,2 u/s (`inimigos-olhar-notas.md`) | — |
| Olhar | Inimigo em movimento sempre vira para onde anda, mesmo depois de um ataque replicado ter trocado o modo de rotação; modo esperado reaplicado a cada checagem (`inimigos-olhar-notas.md`) | — |
| Passivas | Estado das partes (ligado/escala) do objeto do dono aplicado na cópia, genérico para todos os heróis (`PassiveParts = 147`) (`passivas-notas.md`) | — |
| Passivas | Prefabs de mini-jogo de `Resources` `powers/` e árvore de habilidades do dono carregados sob demanda para achar o prefab de verdade; marcador no tamanho da área real (raio enviado no `PassiveSpawn`) (`passivas-notas.md`) | — |

## Agora

A **1.6.0** ainda não foi compilada nem testada (roteiros em `reentrada-notas.md`, `passivas-notas.md`, `sempre-ligadas-notas.md`, `caido-marcadores-notas.md`). A 1.5.0 (`a99e610`) foi testada em um PC com Demon Hunter, Machinist, Pirata e Xamã e numa partida com um amigo pela Steam. Antes de publicar com `tools\release.ps1`: mais uma partida pela Steam.

A **1.4.0** está com commit (`869a4fa`); a **1.4.1** (olhar dos inimigos em movimento e a área do Demon Hunter para os outros) ainda não, e nenhuma das duas foi compilada. Ascensão (modelo, acréscimo, legionários e efeitos), inimigos virados para o convidado e marcadores de caído já foram vistos funcionando no teste em um PC. A reentrada não foi testada. Próxima rodada: `tools\test-local.cmd` com os roteiros de `passivas-notas.md` e `inimigos-olhar-notas.md`; depois a partida pela Steam com um amigo (`teste-1.3.0.md`) e `tools\release.ps1`.

## Próximos itens

Em ordem de prioridade. Detalhes e endereços em `proximos-investigacao.md` (seção indicada).

| # | Item | Esforço | Origem |
|---|---|---|---|
| 1 | Reconexão quando o host trocou de estágio (portal) ou reiniciou durante a queda: hoje o convidado volta no estágio antigo | médio | `reentrada-notas.md` |
| 2 | Modo Crusade (começa num lobby e troca de mapa no meio; portas e baús agora têm base) | grande | `fase6-notas.md`, `portais-notas.md`, §6 |
| — | Esquiva por dash nos hits que o host calcula no proxy: o dash do convidado aparece, mas o host calcula o hit no proxy, que não dá dash (só afeta convidados; mexe no balanceamento) | pequeno–médio | `pendencias-notas.md` |
| — | XP ganho caído se perde se ninguém revive | pequeno | `fase4b-notas.md` |
| — | Cristais giratórios do Machinist no parceiro: NRE mais adiante no `DoActivate` da réplica, depois do pool da projectile skill (`sempre-ligadas-notas.md`) | médio | `sempre-ligadas-notas.md` |

## Limites por definição

- Skin de mod do parceiro só aparece se você tem o mesmo mod (não há de onde carregar). Efeitos de ascensão de outro personagem: nenhum caso de `isn't loaded here` nos testes da 1.5/1.6.
- Cristais giratórios do Machinist no parceiro: deixado como problema conhecido (item da tabela acima).

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
