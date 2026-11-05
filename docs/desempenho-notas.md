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

## 1.4: buscas que falham não se repetem a cada spawn

Os logs da 1.3.0 mostraram a mesma invocação do parceiro sem prefab 16 vezes seguidas. Cada tentativa de achar um prefab que não existe nesta máquina terminava em `Resources.FindObjectsOfTypeAll` (todas as `Entity`, ou todos os objetos daquele tipo, carregados no jogo): um engasgo por spawn.

| Onde | Antes | Agora |
|---|---|---|
| `SummonSync.ResolveTemplate` (invocações do parceiro) | sem cache de falha: varredura a cada invocação | a falha vale por 20 s; o aviso no log sai uma vez por nome |
| `PassiveObjectSync.ResolveTemplate` (passivas do parceiro) | idem, a cada objeto (as pedras do Demon Hunter nascem o tempo todo) | falha vale por 20 s |
| `EnemyPrefabKeys.ClientResolve` (inimigos no convidado) | uma chave sem prefab varria tudo a cada spawn da onda | falha vale por 5 s (as chaves de invocação de boss continuam sem cache de falha: o dono pode virar marionete a qualquer momento) |
| `AscensionSync.ResolvePrefab` | idem, a cada fantasma | falha vale por 20 s |

Outros:

- os marcadores de passiva criavam um `Material` novo por marcador e nunca o destruíam; agora há uma cópia por cor;
- o `SiegeExpertComponent` do jogador local é procurado de novo a cada 5 s enquanto não aparece (antes, se a primeira busca viesse cedo demais, nunca mais era procurado e o acréscimo da ascensão do Legionnaire não era enviado);
- os tipos Il2Cpp usados pelo `SummonSync` são buscados no primeiro uso, não no construtor estático (uma falha ali derrubaria a classe inteira).
