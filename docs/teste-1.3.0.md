# Teste da 1.3.0 com um amigo (Steam)

A 1.3.0 junta a 1.3.0 (nunca publicada sozinha) e os itens novos. Antes desta partida, rode `tools\test-local.cmd` e confira os roteiros das notas novas (`tarefas-mapa-notas.md`, `host-caido-notas.md`, `invocacoes-ascensao-notas.md`, `inimigos-olhar-notas.md`, `caido-marcadores-notas.md`, `ascensao-notas.md`, `passivas-notas.md`, `perigos-notas.md`). Esta partida pela Steam confere o que só aparece com latência de verdade e dois PCs. Depois dela, publique com `tools\release.ps1`.

## Antes

- [ ] **Sem `steam_appid.txt`** na pasta do jogo (o `test-local` cria e apaga; se uma rodada foi interrompida, ele fica). Com o arquivo, o convite pela Steam não funciona.
- [ ] **Gerar o zip da 1.3.0 e mandar ao amigo:**
  - `powershell -ExecutionPolicy Bypass -File tools\release.ps1 -DryRun`: compila em Release, empacota em `dist\SoulstoneCoop-1.3.0.zip` e para antes de criar a tag. Ele exige master limpa e igual ao `origin/master`, então faça o commit e o push antes.
  - Ou, sem commit: `dotnet build src\SoulstoneCoop -c Release` e depois `powershell -ExecutionPolicy Bypass -File tools\package.ps1`.
- [ ] O amigo instala por cima da versão anterior. Os dois com o jogo na **1.5.3.2** e o mod na **1.3.0** (versões diferentes são recusadas na conexão).
- [ ] Na sua máquina, a DLL instalada é a do último build (o `test-local` instala direto no jogo). Confira se é a 1.3.0.

## Durante

1. Você hospeda, convida pela Steam, e o amigo aceita. Os dois escolhem personagem, e você aperta Lutar.
2. **Baú natural:** espere aparecer um (ou aperte F3 na sua janela).
   - O amigo vê no mesmo lugar e com o ícone no minimapa.
   - Ele abre sozinho; depois vocês dois juntos (mais rápido).
   - O baú some nas duas telas e o loot chega aos dois.
3. **Tarefas do mapa:** o amigo vê o marcador de tarefa (e o ícone no minimapa) e começa uma sozinho. Depois, complete uma que dê minério: o amigo vê e consegue minerar.
   - O timer ou o contador da tarefa aparece no painel de objetivos do amigo e some quando ela termina.
   - **Ascensões:** se der, um de vocês joga de Legionnaire (resgatar um centurião) ou Demon Hunter (capturar um demônio): o outro continua atacando, vê os legionários caídos ou a pedra (sem conseguir usar) e vê o modelo ascendido quando a ascensão ativa.
   - **Meteoros:** com meteoros no mapa, eles caem perto de um e do outro.
   - **Caído:** o anel vermelho aparece em volta de quem caiu, com uma linha até o outro; o arco verde enche ao reviver. Os inimigos perto do amigo ficam virados para ele na tela dele.
   - **Host caído:** com o amigo longe, caia (ou aperte F9): os inimigos em volta do seu corpo somem e as ondas continuam indo até ele. Ele volta e te revive.
4. **Reviver:**
   - um cai, o outro chega perto e sai no meio;
   - a barra cai devagar e, ao voltar, termina;
   - a XP de enquanto estava caído chega ao levantar.
5. **Magias:** um raio/canal segue a mira na tela do outro; um escudo giratório aparece em volta de quem tem.
6. **Inimigos comuns:** na tela do amigo, eles animam o golpe.
7. Jogar uns 10 minutos, voltar ao menu e jogar uma segunda partida sem fechar o jogo.
8. Anotar o horário de qualquer coisa estranha.

## Depois

- Os dois fecham o jogo e mandam `<pasta do jogo>\BepInEx\LogOutput.log` antes de abrir o jogo de novo (ele é reescrito a cada abertura).
- No log do host, procurar:
  - `[chests] ... errors=0` e `Patches: 10 applied`; `[passives] Patches: 13 applied`; `[hazards] God meteor patches: 2/2`; `[ascension] InstantiateAscensionModelRoutine patched`;
  - `Host: task marker #N ... started` (tarefa começada pelo amigo);
  - `[proxy] Host down: N enemies ... recycled` (se o host caiu longe do amigo);
  - `Downed markers ready (material ...)`;
  - nenhum `Local player has not cast for` (nos dois logs; se aparecer, guardar os dois);
  - `[enemyanims] ... SkillAnimationTrait.Activate patched`;
  - `Host: slot 1 down ...` (as linhas do reviver).
- No do amigo: `[casts] ... aim=` e `always-on`.
- Se estiver tudo bem: `tools\release.ps1` (sem `-DryRun`) publica a tag `v1.3.0` com o zip.
