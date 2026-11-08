# Ataque animado dos inimigos comuns no convidado

Item 3 de `proximos-investigacao.md`. **Sem teste em jogo.** Código em `src/SoulstoneCoop/Enemies/EnemyAttackAnims.cs`.

## O problema

Boss, elite e especial já repetem o cast inteiro no convidado (`EnemyCasts`, `ReplayCasts`). Os comuns não: no convidado eles chegavam e batiam sem animar o golpe. Repetir o cast de todo mob seria caro.

## Como o jogo anima

- Todo `DoActivate` (`MeleeSkill` 0x5371A0, `ProjectileSkill`, `AreaSkill`, ...) chama `SkillAnimationTrait.Activate(AnimatorComponent)` (0x55E710).
- Esse método só faz `AnimatorComponent.CrossFade(AnimationName, Fade, AnimationLayer)` (0x5DCBF0): `Animator.StringToHash(nome)` e `Animator.CrossFade(hash, fade, camada)` em cada animator da lista.

## O que mudou

- **Host:**
  - postfix em `SkillAnimationTrait.Activate`, patch feito à mão (uma falha só desliga isto);
  - o `AnimatorComponent` é ligado à sua `Entity` (cache por ponteiro);
  - se for um inimigo rastreado sem `ReplayCasts`, entra na fila (netId, hash do estado, fade, camada);
  - no máximo 1 a cada 0,15 s por inimigo, e só para inimigos a até `[Coop] ReplicaCastRadius` de algum convidado;
  - a fila sai uma vez por frame como `EnemyAttackAnim = 23` (Unreliable: perder um golpe não vale reenviar).
- **Convidado:** cada animator da marionete faz `CrossFade(hash, fade, camada)`, só se o controller tiver o estado (`Animator.HasState`). As transições do próprio controller voltam para a locomoção, como no host.
- **Telemetria:** `[enemyanims]` a cada 10 s. Host: `captured`, `sent`, `throttled`, `culled`, `skippedReplay`. Convidado: `recv`, `played`, `noPuppet`, `noState`.

## Teste

`tools\test-local.cmd`. Na janela B, deixe inimigos comuns chegarem perto: eles devem animar o golpe (antes só encostavam).
- No log do A: `[enemyanims] Host: captured>0 sent>0` e a linha `SkillAnimationTrait.Activate patched`.
- No log do B: `played>0` e `noState` baixo.
