# Mira contínua do channeling do parceiro

Item 2 de `proximos-investigacao.md`. **Compilado só depois, sem teste em jogo.**

## O problema

- O `SkillCast` leva o `TargetFrontPosition` e o `TargetGroundPosition` do início do cast.
- No receptor, o `RemoteCaster` dá à cópia da skill os funcs `GetTargetFrontPosition` / `GetTargetFrontPositionGround` que devolvem `copy.Front` / `copy.Ground`.
- O `ChannelingSkill.OnUpdate` (0x516B00) relê esses funcs a cada frame, mas os valores só mudavam no próximo cast. Por isso o raio do parceiro ficava parado onde o cast começou.
- A rotação já acompanhava: o `RemoteCaster.Tick` copia a rotação do fantasma a cada frame. Só o ponto mirado faltava.

## O que mudou

- **Emissor** (`CastSync`):
  - ao abrir um canal, guarda também os funcs de mira do próprio cast e a skill;
  - enquanto o canal está aberto, chama esses funcs a ~10 Hz (o mesmo ponto que a nossa skill usa) e manda `SkillChannelAim = 140` (Sequenced);
  - um func que der erro para de ser chamado naquele canal (fica a mira do início).
- **Receptor** (`RemoteCaster.OnChannelAim`): só vale para um canal que estamos tocando. Guarda o alvo, e o `Front` / `Ground` deslizam até ele (`1 − e^(−15·dt)` por frame), sem degraus a cada pacote.
- **Host:** repassa o `SkillChannelAim` aos outros convidados (Sequenced), como o resto das magias.
- **Telemetria:** `aim=enviados/recebidos` na linha `[casts]`, mais `aimErrors` se houver.

## Teste

`tools\test-local.cmd`. Na janela B, um personagem com skill de channeling (raio contínuo). Segure o canal e mova o mouse em volta.
- Na janela A, o raio do B segue a mira em vez de ficar parado.
- No log do B, `[casts] ... aim=N/0` com N > 0; no do A, `aim=0/N`.
- Repita trocando: A canaliza, B vê.
