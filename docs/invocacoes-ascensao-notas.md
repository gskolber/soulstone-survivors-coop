# Ascensão do parceiro travando as magias dos outros

## O relato

- Com um Legionnaire no grupo (testado com 2 e 3 jogadores), quando ele resgata um centurião caído, os outros param de lançar magias pelo resto do mapa.
- Com o Demon Hunter acontece o mesmo quando ele termina de capturar um demônio.
- XP e level-up continuam funcionando.
- Acontece com quem hospeda e com quem conecta.

## Causa provável

Nos dois casos, o momento é o da invocação da ascensão:

- `SiegeExpertComponent.OnRescue` (0x7EE5B0) lança `_summonLegionnaireSkill` com `SkillCasterComponent.CastSkillMultipleTimesAtTarget`;
- `DemonicBindingComponent.ReceiveSoulOnCollect` (0x6C9AD0) lança `DemonicBindingSkill`, uma `SummonEntityPositionalEffect`;
- o `SoulHarvestComponent` (`SummonApocalypseKnightSkill`) segue o mesmo modelo.

Essas habilidades são montadas por `UnitUtil.SetupExtraSkillForEntity`, com o `SkillType` dos dados de power-up. O `CastSync` só deixava de enviar uma invocação quando ela tinha a etiqueta `SkillType.Summon`, então essas iam para os outros jogadores.

Em cada máquina, o `RemoteCaster` (o Dummy invisível que repete as magias do parceiro) lançava a cópia. A cópia criava uma unidade **real** naquele mundo, com o Dummy como dono, quando o certo é ter só o clone visual do `SummonSync`. Uma unidade de ascensão sem o componente de ascensão no dono quebra o laço de atualização do jogo (um NRE dentro de um `UpdateAll` interrompe o laço inteiro naquele frame), e o laço que roda as magias do jogador local fica parado de vez.

O `EnemyCasts` já procurava os efeitos de invocação nos componentes. O `CastSync` dos jogadores não procurava.

## Correção

1. **Quem envia (`CastSync.Summons`):** uma habilidade que tem a etiqueta Summon **ou** qualquer `SummonEntityEffect`, `SummonEntityPositionalEffect` ou `SummonEntityAndForgetPositionalEffect` embaixo dela não é enviada. As unidades continuam aparecendo para os outros pelo `SummonSync`. O log mostra, uma vez por habilidade, `'<chave>' summons units ...: not sent`.
2. **Quem recebe (`RemoteCaster`):** a mesma checagem vale no prefab antes de copiar. Se a etiqueta ou o prefab indicam invocação, a cópia é recusada pelo resto da partida (`... is not replayed on caster N`).
3. **Rede de segurança (`SummonOwnerCapturePatch`):** se `UnitUtil.SetupSummonStats` rodar com um `RemoteCaster` ou um proxy como invocador, a unidade volta ao pool no frame seguinte, e o log mostra `A replayed skill summoned '...' here: removed`.
4. **Vigia (`CastSync.Watchdog`):** em co-op, vivo, sem pausa, com auto-cast ligado, inimigos a menos de 15 u e nenhuma magia nossa por 12 s, o log registra:
   - os `PlayerSkillAutoCastComponent` existentes;
   - as cópias de cada lançador remoto;
   - quantas unidades de réplica foram removidas.

   Um auto-cast de outro objeto que nunca foi configurado (`_casterEntity` nulo) é desligado, porque o `UpdateAll` dele lançaria exceção a cada frame. A mensagem pede para mandar o log, e se repete no máximo a cada 60 s.

## Como testar

Com `tools\test-local.cmd`:

1. A joga de Legionnaire e B de qualquer outro.
2. A resgata um centurião caído. B continua lançando magias. No log de A sai `'...' summons units ...: not sent`; no de B não sai `Copy '...'` dessa habilidade.
3. Repita com A de Demon Hunter, capturando um demônio.
4. Troque os papéis (B de Legionnaire): A continua lançando.
5. Em nenhum dos logs deve aparecer `Local player has not cast for`. Se aparecer, mande os dois logs.
