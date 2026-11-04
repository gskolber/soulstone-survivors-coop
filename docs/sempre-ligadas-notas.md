# Skills sempre ligadas do parceiro (escudos giratórios)

Item 4 de `proximos-investigacao.md`. **Sem teste em jogo.** Código em `src/SoulstoneCoop/Skills/AlwaysOnSync.cs` e `RemoteCaster` (`SetAlwaysOn`, `UpdateAlwaysOn`, `StopAlwaysOn`).

## O problema

O `RotatingShieldSkill` não tem `DoActivate` e nunca passa pelo `CastSkill`. É o `OnUpdate` dele (0x55D830), chamado a cada frame pelo `SkillCasterComponent.UpdateAll` do dono, que cria os projéteis (`GetPrefabInstance`, `StaticProjectile.Setup`) e os gira. O `CastSync`, que repete casts, nunca via essa skill, então os escudos do parceiro não apareciam.

## O que mudou

- **Emissor** (`AlwaysOnSync`):
  - a cada 0,5 s, lista os `RotatingShieldSkill` em `SkillCasterComponent.Skills` do jogador local, com nível, `ExtraProjectiles`, `RotationSpeed`, `ProjectileCooldown` e escala ajustada;
  - manda `SkillAlwaysOn = 141` quando muda e a cada 3 s (ReliableOrdered; o host repassa aos outros);
  - lista vazia quando não há mais nenhum, ou com o jogador caído.
- **Receptor** (`RemoteCaster`):
  - guarda a lista por jogador;
  - a cada frame, cria (se preciso) a cópia da skill no lançador remoto daquele jogador e chama o `OnUpdate` dela. As cópias não passam por `AddSkill`, então nada mais as roda;
  - aplica os números recebidos e a escala (`ApplyFidelity`);
  - quando a skill sai da lista, ou o jogador cai, a cópia é destruída. O `RotatingShieldSkill.Recycle` (0x55E010) encerra os projéteis.
  - Os acertos do lançador remoto continuam bloqueados (`RemoteCasterDamageBlockPatch`): só visual.
- Um erro numa cópia a coloca em `FailedKeys` pelo resto da partida (para de tentar) e fica no log.
- **Logs:** `Own always-on skills: ...` em quem tem a skill. `Slot N always-on skills: ...` e `always-on '...' running on its caster` em quem vê.

## Riscos

- Rodar o `OnUpdate` de uma cópia fora do `SkillCasterComponent.UpdateAll` é caminho novo. Se der erro, o log mostra `Always-on '...' for slot N:` e só aquela skill para.
- A contagem de projéteis vem de `BaseProjectiles` + `ExtraProjectiles` do emissor. Se o jogo tirar a contagem de outro modificador, o número pode diferir.

## Teste

`tools\test-local.cmd`. Num dos dois, pegue um escudo giratório nos upgrades (ou um personagem que comece com um).
- O outro vê os escudos girando em volta do fantasma, sem dar dano.
- Somem quando aquele jogador cai e voltam quando revive.
