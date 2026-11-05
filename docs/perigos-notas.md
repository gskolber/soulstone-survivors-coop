# Perigos que só iam atrás do host

## Meteoros do deus

- `GameManagerGodMeteorsComponent` só roda no host e joga os meteoros em volta de `_playerEntity`, que o `Setup` define uma vez com o jogador do host. São dois caminhos:
  - `OnUpdate` 0x80DA10 (por abates);
  - `LaunchGodMeteorAroundPlayer` 0x80D820 (maldição do mapa).
- O `GodCasts` já repete cada meteoro no convidado (só visual), e o acerto no proxy chega como `DamageToClient`. Os meteoros só caçavam o host e, com ele caído, caíam no corpo dele.
- **Agora (`Enemies/HostHazards.cs`):**
  - um prefix e um finalizer nesses dois métodos trocam `_playerEntity` por um dos jogadores vivos: o do host ou o proxy de um convidado, que fica onde o convidado está;
  - a vez passa de um para o outro a cada 5 s; com o host caído, só os convidados;
  - o valor original volta logo depois, mesmo se o jogo lançar exceção.
- No convidado, o `RemoteCaster.Play` não corta mais um meteoro do deus pela distância até o host (`ReplicaCastRadius`), porque ele pode cair em cima de nós.

## Zonas que perseguem o jogador

Não precisou mudar nada. A `EnvironmentalDamageOnTriggerEnterFollowPlayerSkill` é lançada pelo `EnvironmentalEntityCastSkillsOnCooldownManager`, cujo `UpdateAll` roda no `GameLoop.Update` de toda máquina, conforme as maldições (que já vêm do host). Cada jogo já tem a sua, atrás do próprio jogador: o convidado tem a dele. A do host fica parada no corpo enquanto ele está caído, o que não faz mal (ele está invulnerável).

## Como testar

- Num mapa com meteoros (maldição ou depois de muitos abates), com A e B afastados: os meteoros caem perto de um, depois do outro.
- Na tela de B, os que caem perto dele aparecem e acertam.
- Com A caído (F9), todos caem perto de B.
- Log do host: `[hazards] Host: god meteor updates aimed at a guest: N`.
