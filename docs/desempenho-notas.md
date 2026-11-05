# Passada de desempenho (1.3)

O que rodava a cada frame e foi aliviado:

| Onde | Antes | Agora |
|---|---|---|
| `RemoteProxy.BeforeRecycler` (host, a cada chamada do reciclador do jogo, ou seja, a cada frame) | percorria todas as entidades × todos os convidados vivos | a lista dos inimigos perto de um convidado é refeita 5 vezes por segundo; cada chamada só mexe nessa lista curta |
| `HostEnemySync.SendSnapshot` (a cada snapshot, para cada inimigo) | `StatsComponent.GetMaxHealth()` (recalcula os stats) por inimigo | vida máxima em cache, relida no máximo uma vez por segundo por inimigo |
| `ClientPuppets.Tick` (convidado, todo frame) | `ByNetId.Values.ToArray()`, um array novo por frame | uma lista reaproveitada |
| `CoopLife.HostTick` (host, todo frame na partida) | `new bool[8]` por frame | um array só, zerado a cada frame |
| `CastSync.Watchdog` | podia varrer todas as entidades a cada frame, enquanto ficava parado sem inimigos | roda uma vez por segundo |

Os módulos novos já nasceram com intervalo:

- ascensão a 4 Hz;
- passivas a 4 Hz (sumiço) e a 10 Hz (posição);
- objetivos de tarefa a 2 Hz;
- facing a cada 0,2 s por marionete.
