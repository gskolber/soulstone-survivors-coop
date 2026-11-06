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

## 1.6.0: cristais do Machinist (`TemporaryRotatingSkill`)

`ResonatingCrystals-Skill` dava NRE no `DoActivate` (0x56A5B0) da réplica: ele faz `CrossFade` do `AnimationTrait` (+0x268) no `CasterAnimator` (+0x130) da skill e lança se há trait e não há animator; o dublê não tem `AnimatorComponent`. Na criação da cópia (`GetOrCreateCopy`), um `TemporaryRotatingSkill` sem `CasterAnimator` perde o trait (a animação do fantasma vem do `PlayGhostAnimation`). Depois de um cast aceito, o `OnUpdate` da cópia roda todo frame por `ProjectileSkillDuration + 1 s` (2 a 60 s) no mesmo laço das sempre-ligadas, porque as cópias não são `AddSkill`'adas. Endereços achados com `MethodAddressToToken.db` do interop + objdump.

### Primeiro teste da 1.6.0

Ainda NRE no `DoActivate`, e a linha "animation trait dropped" não apareceu: o trait/animator não era o nulo. O `DoActivate` testa +0x90, +0xE0, +0x130, +0x258, +0x260, +0x268 e +0x288 antes da primeira chamada. `Skills/FieldDump.cs` loga, pelo offset real de cada campo (`il2cpp_field_get_offset` dos `NativeFieldInfoPtr_` do interop), quais desses estão nulos: na cópia (`Fields of copy 'ResonatingCrystals-Skill' (slot N)`) e na instância do próprio Machinist no primeiro cast (`Fields of local 'ResonatingCrystals-Skill'`). O campo que está `set` no dono e `NULL` na cópia é o que falta preencher.

### Segundo teste da 1.6.0

`Fields of copy` e `Fields of local`: os seis campos testados no começo do `DoActivate` estão `set` nos dois lados (o +0x130 não tem campo com nome no interop). O NRE é mais adiante: depois do `GetPrefabInstance` do placeholder e da projectile skill (testes em `[rbp+0x70]`, `[rbp+0x88]`, `[instância+0x90]` antes do `Skill.Setup`). Próximo passo: comparar o estado das instâncias do pool da `ResonatingCrystals-Projectile` na réplica e no dono (o `FillPools(3)` da cópia pode não preparar como o do jogo), ou espelhar os cristais pelo objeto (placeholders girando) em vez de rodar a skill. Item próprio no roadmap.
