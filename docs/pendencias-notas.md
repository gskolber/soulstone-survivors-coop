# Pendências pequenas (branch `pendencias-pequenas`)

Nada disto foi testado em jogo: tudo só compilou. Cada item tem um commit próprio, então dá pra reverter um sozinho com `git revert <hash>` se der problema. Os pacotes novos ficam na faixa 70–79 (`GodCast = 70`, `ProxyStatus = 71`). A versão do mod e o `roadmap.md` continuam iguais; a sessão principal mexe neles no merge.

## O que foi feito

### 1. Código morto: `2c7204f`

`GhostFactory.cs` e `FieldDiff.cs` foram removidos. Antes, o grep confirmou que só um referenciava o outro.

### 2. Números de dano das réplicas: `e225e80`

**Causa.** O `ChangeHealthEffect.DoApplyEffects` (0x521460) chama `ChangeEntityHealth` e logo em seguida `OverheadCombatTextPanelManager.RequestCombatText(..., GetTargetTransform(alvo, Overhead), ...)`. Quando o atacante é aliado, o valor desenhado é o dano **pedido**, não o efetivo. Por isso o bloqueio da 4C/4D devolver 0 não escondia o número.

**Correção.**
- Os bloqueios (`RemoteCasterDamageBlockPatch` e `ClientPuppetEffectDamageBlockPatch`) agora registram o transform overhead do alvo do hit barrado (`Skills/ReplicaCombatText.cs`).
- Um prefix no `RequestCombatText` descarta o próximo número `Damage`/`DamageOnPlayer` desse transform no mesmo frame.
- Só entram os hits que desenham número: o lançador remoto como atacante, ou uma marionete acertando um jogador.
- Os hits reais não são afetados, porque o pedido da réplica vem logo depois do bloqueio, na mesma chamada, e consome o registro.

**Escolha de segurança.** O patch é no `RequestCombatText`, que só recebe enum, ponteiro, floats e bool. O `DoApplyEffects` roda em todo hit do jogo e ficou de fora de propósito.

**Continua aparecendo (de propósito).** O número no fantasma do cliente, na tela do host, é dano real: o patch 9 repassa o hit ao cliente.

**Efeito colateral.** O host deixa de ver número nos ataques do cliente. O dano real do cliente chega pelo `ClientDamage` sem número; antes, o número que aparecia vinha da réplica, com os stats do Dummy.

**Telemetria.** `numbersHidden=` na linha `[casts]`.

### 3. God Meteors: `9b8f910`

- A `GameManagerGodMeteorsComponent` faz a God-Entity lançar a god skill `"Meteor"` (`GetGodSkillById(god, "Meteor")` no `Setup` 0x80DE00) no chão a ±16 u do host. O disparo vem de kills e do affix, e isso só anda no host.
- **Host:** um postfix em `SkillCasterComponent.CastSkill`, filtrado pelo ponteiro da skill de meteoro, manda `GodCast = 70` (chave e vetores, ReliableUnordered).
- **Cliente:** toca uma cópia da skill de meteoro da própria God-Entity no `RemoteCaster` da 4C, com dano, status, empurrão e números bloqueados. A cópia vem do prefab `Skill-Prefabs/<chave>`, se existir; senão, de um clone da instância local.
- O dano no cliente continua vindo do host pelo proxy.
- Não lancei na God-Entity do cliente de propósito: os hits dela seriam dano local real, sem como distinguir da God-Entity "de verdade".
- **Telemetria:** `[godcasts]`. Logs de uma vez só: `God meteor skill: '<chave>'` (nos dois lados), `Host: first meteor sent`, `Client: first meteor ... -> Played`, e `Copy 'god:<chave>' (... via Resources prefab | clone of the local instance)`.

### 4. Status negativos no cliente: `0989994`

- **Host:** dois postfixes, `AbnormalStatusComponent.ApplyEffects` e `StunnableComponent.SetToRagdoll` (quem chama é o `SetToRagdollEffect`). Os dois são filtrados pelos ponteiros dos componentes do proxy e mandam `ProxyStatus = 71` com a **entrada** da chamada.
- **Só vão os status de contagem:** slow, weakness, fragility, dazed, disoriented, exposed, distracted, brittle, debilitated, crippled, disarray, shattered, wound e condemned. Burn, bleed, poison e doom ficam de fora, porque o dano deles já chega como `DamageToClient`. Por isso não há DoT em dobro.
- **Cliente:**
  - Reaplica o `ApplyEffects` no jogador local com a marionete do atacante como aplicador. O bloqueio de status de marionete da 4D é liberado só nessa chamada (`ProxyStatusForward.ApplyingNetworkStatus`).
  - As imunidades, resistências e sorteios do próprio cliente valem, como no single player.
  - Se algum dano local escapasse, o aplicador é marionete, e o `ClientPuppetEffectDamageBlockPatch` bloqueia.
  - O stun vai para o `StunnableComponent` do jogador, e as flags de imunidade dele decidem. Se o jogador não tiver `StunnableComponent`, nada acontece.
- **Descartado:**
  - status cujo aplicador não tem marionete (God-Entity, desconhecido), porque o `ApplyEffects` desreferencia o aplicador;
  - acima de 40 envios por segundo.
- **Telemetria:** `[status]` (`sent`, `stunsSent`, `onlyDot`, `noApplier`, `applied`, `stuns`, `noPuppet`, `errors`). Log de uma vez: `Proxy components: abnormal=True stunnable=True`.

### 5. Stats do proxy: `51843b2`

- O jogo **não tem stat de esquiva**: só o "dash dodge", que depende do tempo do dash. Também **não tem resistências elementais**: a `Stats` não tem nenhum campo assim.
- A resistência que existe é a de status: `StatsComponent.AbnormalStatusDampening` e `AbnormalStatusDampeningFactor`. As duas entraram no fim do `ClientState` (lidas só se vierem) e são copiadas para o proxy.
- Com isso, os status acumulados no proxy, e o dano de DoT repassado a partir deles, resistem como o cliente.

### 6. Ações de time de boss/cutscene: `b678f34`

Revisei todos os chamadores de `AIUtil.GetAllEntitiesInSpecificTeam` fora de cheat e teste (decompilado):

| Chamador | O que faz com o proxy |
|---|---|
| `MoveEntityTeamToCenterAction` | **Travaria.** Move uma entidade por vez com `MoveTowards` até ficar a 0,001 u do alvo e só então passa pra próxima. O proxy (e o `RemoteCaster`, se o time incluir Summons) volta pro fantasma a cada frame, então a corrotina nunca passaria dele. |
| `KillAllEntitiesAction` | `ChangeEntityHealth(-MaxValue)`: pelo patch 9, mataria o cliente com um hit absurdo. |
| `LootUtil.SearchForPlayerEntities` | Lê o Inventory, como o `ProcessLoot`. Pode dar o mesmo NRE fora do `ProcessLoot`. |
| `StopEntityTeamAction` / `ResumeEntityTeamAction` | Invulnerabilidade, cancelar cast, modo de movimento. Inofensivo. |
| `PlayLineVisualEffectAction`, `ApplyForceFromEntityAction` | Cosmético / irrelevante (a posição do proxy é forçada). |
| `HealthProjectile.FireProjectileToAllEnemies` | Um orbe de cura pra cada entidade Players. O proxy ignora cura, então é cosmético. |

**Correção.** Um postfix em `GetAllEntitiesInSpecificTeam` tira o proxy e o `RemoteCaster` da lista sempre que o time pedido inclui Players ou Summons. A troca de time do `ProcessLoot` continua lá (redundante agora, mas inofensiva). Fica um log no máximo a cada 10 s: `[proxy] Kept the stand-ins out of a team query`.

## O que não foi feito

- **Esquiva por dash:** os hits que o host calcula no proxy não sabem que o cliente estava dando dash. Resolver isso pede mandar o estado do dash com precisão de frame, ou descartar o `DamageToClient` no cliente durante o dash. As duas coisas mudam o balanceamento e precisam de teste. **Feito na 1.6.1** (seção no fim), menos as power-ups "ao esquivar".
- **Afixos do jogador no proxy:** não mexi.
- **Stun:** se o jogo nunca atordoa jogadores, o encaminhamento simplesmente não faz nada (sem `StunnableComponent` ou com imunidade).

## Achados para depois (fora do escopo)

- **`ProxyGodSkill` nos replays da 4D:** algumas skills de boss chamam `GameManagerUtil.TryCastGodSkill`, que faz a God-Entity lançar a god skill. Quando o cliente reproduz uma skill dessas na marionete, quem lança é a God-Entity **do cliente**, com efeito real. O dano não é bloqueado, porque o atacante é a God-Entity e não uma marionete, e o cliente pode tomar o hit duas vezes: local e pelo host. Vale olhar nos logs `[enemycasts]` se aparece alguma skill `ProxyGodSkill`.
- **Orbes de cura (`HealthProjectile`):** o cliente nunca ganha a cura que os kills do host gerariam pra ele. Já era assim antes.

## Riscos sem teste (do maior pro menor)

1. **Item 4:** chamar `ApplyEffects` no jogador local a partir do mod é o ponto mais novo. Se o slow ou outro status vier forte ou fraco demais, reverta o `0989994`. Se `[status] errors` ficar acima de 0, o log diz o motivo.
2. **Item 3:** o caminho "clone of the local instance" clona uma skill já montada na cena. Se o meteoro do cliente aparecer estranho (ou der erro no `Copy 'god:...'`), reverta o `9b8f910`. Fora do visual, nada muda.
3. **Item 6:** se uma cutscene de boss se comportar diferente, procure o log `Kept the stand-ins out of a team query`.
4. **Itens 2 e 5:** baixo risco. Pior caso: um número de dano real some, ou a resistência de status do proxy não muda.

## Roteiro de teste

Host e cliente na mesma versão deste branch, Desert, perto um do outro.

1. Abrir uma run em co-op e jogar 1 min. No `LogOutput.log` dos dois, procurar exceções novas e erros de trampoline. No host deve aparecer `[status] Proxy components: abnormal=True stunnable=...`; nos dois lados, `[godcasts] God meteor skill: '...'`.
2. **Números (item 2):** cada um lança magias perto do outro. Na tela do host, os inimigos acertados pelas magias do **cliente** não mostram número. Os números das magias do próprio host continuam aparecendo. Na linha `[casts]`, `numbersHidden` deve ficar acima de 0.
3. **Meteoros (item 3):** matar bastante até cair God Meteor em volta do host (ou num mapa com o affix de meteoros). O cliente deve ver os meteoros caindo nos mesmos pontos, perto do host. Conferir `[godcasts] ... played>0 failed=0` no cliente e `sent>0` no host. Se o cliente estiver embaixo de um meteoro, toma dano uma vez só (o `[damage]` do cliente bate com o host).
4. **Status (item 4):** o cliente fica no meio de inimigos que aplicam slow (ou qualquer status visível, como o ícone sobre o personagem) e deve ficar lento na própria tela. No host, `[status] sent>0`; no cliente, `applied>0 errors=0`. Burn e poison não devem dobrar o dano (`onlyDot` sobe no host e o dano do cliente segue batendo com o do host).
5. **Resistência (item 5):** sem efeito visível. Só conferir que o `ClientState` continua chegando: o proxy acompanha a vida do cliente e não há erro `[proxy] ApplyRemoteState`.
6. **Ações de time (item 6):** matar um boss com cutscene ou intro e confirmar que a sequência termina normalmente nas duas telas. Procurar `Kept the stand-ins out of a team query` no host e verificar que o loot continua caindo sem NRE.
7. **Se der problema num item:** `git revert <hash do item>` e testar de novo.

## 1.6.1: esquiva por dash para convidados

O jogo decide a esquiva em `Skill.GetSkillEffectModifierForTarget` (0x5650B0): se a entidade alvo tem personagem (+0x260/+0x268), o `_isDashing` dele (+0x12C) vira o `IsDashDodged` do resultado. As skills dos inimigos acertam o proxy do convidado no host, e o proxy nunca dava dash.

- Host: o `DashState` do cliente (que já vinha para o fantasma) agora também liga, na chegada, o `_isDashing` do `ModifiedCharacter` do proxy (`RemoteProxy.ApplyDash`, todo frame enquanto dura, com `_dashRemainingDuration` mantido em 0,1 s para o update do jogo não desligar; limite de 0,6 s se o fim se perder; nunca com o proxy caído).
- Mesmas regras do solo: só skills; dano contínuo de status não é esquivado. O atraso da rede desloca a janela (o host vê o dash meia latência depois).
- Fora: as power-ups "ao esquivar" (`SkillOnDashDodge`) do convidado não disparam (rodariam no proxy, que não as tem).
- Telemetria: `dashWindows=N` na linha `[proxy] Host:`.

### Como testar

Em B, dê dash atravessando ataques corpo a corpo e projéteis: B não toma dano durante o dash (compare com A fazendo o mesmo). Em A, `dashWindows` > 0 no log.
