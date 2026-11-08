# Fase 8: robustez em rede real

**Commitada sem teste em jogo** (o jogo do usuário estava em uso). Só compilada; o relógio do remetente e a fila de simulação do Steam foram testados num console à parte (números mais abaixo). Até a fase 7 tudo rodou com 0 ms de latência, num PC só.

> **Formato de rede mudou:** `PlayerState` (2) e `EnemySnapshot` (10) ganharam o relógio de quem envia (4 bytes). Um 0.7.0 não conversa com esta versão: suba o `PluginVersion` no merge.

Nenhum pacote novo (60–69 continuam livres).

## O que entrou

### 1. Simulação de rede (desligada por padrão)

- **Onde liga:** `[Debug]` no `BepInEx/config/com.kolber.soulstonecoop.cfg`:
  - `SimulateLatencyMs`
  - `SimulateJitterMs`
  - `SimulatePacketLossPercent` (inteiro, de 0 a 50)
- **Por instância:** `--coop-sim-latency 75 --coop-sim-jitter 15 --coop-sim-loss 3` na linha de comando. Têm prioridade sobre o `.cfg`, que é o mesmo pros dois jogos no mesmo PC.
- **Cada jogo degrada só um sentido.** Ligue nos dois: 75 ms em cada dá uns 150 ms de ping.
- **IP (LiteNetLib):** usa a simulação do próprio LiteNetLib. Ela vem compilada no 1.3.1 do NuGet (conferido por reflexão: `_pingSimulationList`).
  - Atrasa e perde os pacotes UDP que **este** jogo recebe, inclusive acks e pings. Então reliable é reenviado de verdade.
  - A latência sorteada fica entre `latência − jitter` e `latência + jitter`.
- **Steam:** uma fila de envio dentro do `SteamTransport` (`Net/NetSim.cs`, `SimSendQueue`). Vale para o que **este** jogo envia: jogo e pings.
  - Unreliable (snapshot, posição) é descartado com a chance de perda. Com o jitter, também pode chegar fora de ordem, e as checagens de sequência do receptor cuidam disso.
  - Reliable nunca some nem troca de ordem. Uma "perda" vira atraso de reenvio (cerca de uma ida e volta, no mínimo 200 ms), e o que vem atrás espera, como num canal ordenado de verdade.
  - O handshake (`Hello`/`Accept`/`Reject`/`Bye`) não passa pela fila.
  - Ao desconectar, o reliable que ainda estava na fila sai na hora.
- **Bem visível:**
  - No startup, um bloco `### SIMULACAO DE REDE LIGADA: +75 ms ±15 ms, 3% de perda` (warning, tag `[netsim]`), mais uma linha por transporte quando ele sobe.
  - No painel Co-op, uma linha laranja "Simulação de rede ligada".
  - `SIM` na linha `[sync]` do log.

### 2. Auditoria de latência e correções

Tabela completa mais abaixo. As correções que entraram:

- **Fatia de snapshot maior que o MTU do LiteNetLib.** O 1.3.1 deixa o MTU fixo em 1024 (`MtuDiscovery = false` por padrão, conferido) e lança `TooBigPacketException` em Unreliable/Sequenced acima disso. A fatia ia até 1200 B: no IP, com mais de uns 90 inimigos, todo snapshot dava exceção, e a exceção pulava o resto do `Update` do mod naquele frame.
  - `EnemySnapshotFormat.MaxPacketBytes` agora é 1000 (89 inimigos por fatia). Isso também afasta a fatia do limite do relay da Steam, um risco da fase 7.
  - `LiteNetTransport.Send` manda como `ReliableUnordered` qualquer Unreliable/Sequenced acima do limite, em vez de lançar, com um aviso no log (até 5).
  - `NetworkManager.Send` nunca lança: erro vira uma linha no log (até 10).
- **Timeout do IP:** o `DisconnectTimeout` do LiteNetLib passou de 5 s (padrão) para 10 s. Uma oscilação de Wi-Fi passava de 5 s. Os pings do LiteNetLib rodam numa thread própria, então o jogo travado num carregamento não conta como silêncio.
- **Timeout do Steam durante carregamento:** os nossos pings saem da thread do jogo, que trava num carregamento de cena. Enquanto um dos lados está `Loading`, o limite de silêncio vai de 15 s para 45 s.
- **"Conexão instável":** com 2 s sem nada do parceiro, a faixa de avisos mostra "Conexão instável com X (N s sem resposta)..." e o painel mostra o mesmo em vermelho. Isso acontece bem antes da queda (10 s no IP, 15 s no Steam).
  - A faixa não aparece enquanto um lado carrega.
  - Log: `[net] Link unstable` e `[net] Link back after Ns`.
  - `ITransport.SilenceSeconds`: no IP é o `TimeSinceLastPacket` do LiteNetLib; no Steam, o tempo desde a última mensagem.
- **Ping no painel do IP:** mostrava `NetPeer.Ping`, que no LiteNetLib é a **metade** da ida e volta. Agora mostra a ida e volta inteira, como o Steam e como a interface já dizia.
- **Relógio do remetente (`[Coop] SenderClock`, desligado):** `PlayerState` e `EnemySnapshot` levam o relógio de quem envia (sempre, porque é formato). Ligado:
  - o fantasma é interpolado pela hora em que a posição foi **tirada**, não pela hora em que chegou;
  - a velocidade das marionetes usa o espaçamento do host.
  - Com jitter, a chegada amontoa amostras (duas no mesmo frame = velocidade zero; uma atrasada = metade). Isso fazia o fantasma acelerar e frear e as marionetes errarem a previsão.
  - Teste no console (20 Hz, 75 ms ±15 ms): o erro de espaçamento das amostras cai de 29 ms (pela chegada) para 5,5 ms. Depois de um degrau de +100 ms de latência, o relógio alcança em uns 6 s, e nesse meio tempo extrapola um pouco mais.
  - Fica desligado até ser comparado em jogo (passo 11 do roteiro).

### 3. Host cai no meio da run (visto pelo cliente)

Antes, o cliente ficava num mapa vazio: as marionetes somem, não nasce mais nada (`ClientRunLatched`) e só restava desistir pelo menu.

Agora (`CoopLife.BeginHostLost` / `HostLostTick`):

- **Aviso:** "O host desconectou. Encerrando a partida...". Antes dele, já tinha aparecido "Conexão instável" enquanto o transporte esperava o timeout.
- **Fim da run:** depois de 4 s, a run termina pela **desistência do próprio jogo** (`GameManagerUtil.Surrender`): tela de fim normal, estatísticas salvas no perfil e volta ao menu pelo botão de sempre. Pelo decompilado (`0x6F7D90`), o `Surrender` não depende do menu de pausa: liga `LossBySurrender`, mata os jogadores e o `UpdateAll` faz o resto.
- **Espera** enquanto a tela de level-up ou o menu de pausa está aberto, com o aviso "Escolha o upgrade pra encerrar" ou "Feche a pausa (ou desista)".
- **Caído** quando o host cai: morte real (regra solo da 4A, já existia) e tela de derrota.
- **Run já vencida:** se a vitória já chegou (portal Home aberto ou objetivo principal completo), não desiste: "Entre no portal pra terminar a partida". O portal funciona sozinho, porque o patch do portal só espelha com parceiro conectado.
- `[Coop] EndRunWhenHostLeaves = false` desliga o fim automático; fica só o aviso pra desistir pela pausa.
- **Já limpos no disconnect (conferido):**
  - marionetes (`DespawnAll`, que agora também limpa o cache de skills do `EnemyCasts`);
  - fantasma;
  - lançador remoto (`RemoveCaster`);
  - pausa remota (`RemoteReason`);
  - barreira de carregamento (`Connected` falso);
  - câmera de espectador.
- **Host cai enquanto o cliente ainda carrega:** o `StartMatch` do cliente vê `Role = None`, então a run não fica marcada como de cliente e vira uma run solo normal, com spawners. Não é ideal, mas não trava.

### 4. Cliente cai no meio da run (visto pelo host)

Conferido item a item:

- **Fantasma:** some (`DestroyGhost`).
- **Proxy:** reciclado. **Novo:** antes de reciclar, os inimigos que perseguiam o proxy são soltos (`RemoteProxy.ReleaseTargets`). Os seekers voltam pro host, como no `RetargetSeeker`, e os demais fazem `RemoveTarget`, como quando o cliente morre. Antes, eles ficavam com uma entidade inativa do pool como alvo; os seekers, para sempre, porque o `RetargetSeeker` sai cedo sem proxy.
- **Lançador remoto:** reciclado com as cópias de skill.
- **XP retido:** com o host de pé, o XP compartilhado guardado (`_shareIn`) é aplicado normalmente. Com o host caído, ele morre de verdade (regra solo) e o XP guardado deixa de importar. O `ClientOwnsXp` volta a dar todo XP pro host. O XP que já estava a caminho do cliente se perde.
- **Pausa remota:** zerada.
- **Caído:** `_remoteState = null` tira o cliente caído da conta. Se o host estava caído, morte real (já existia).
- **Spawn e reciclador:** voltam a ser só do host (`Targetable` falso).
- **Aviso:** "X desconectou. Você continua sozinho.".
- **Lobby:** IP continua hospedando; Steam continua com o lobby (fechado durante a run, a não ser com `AllowRejoin`).

### 5. Reconexão no meio da run (`[Coop] AllowRejoin = false`)

Entrou a versão mínima e isolada, só no host:

- **Com a opção ligada:**
  - o host lembra os parâmetros da run (os mesmos do `RunStart`);
  - quando um parceiro conecta com o host numa run e o `Hello` dele chega dizendo que está no menu, o host reenvia o `RunStart` (uma vez por conexão);
  - o cliente carrega a mesma partida como **personagem novo (nível 1, sem itens)**. Os inimigos vêm pelo resync normal de quando ele entra InRun, e o fantasma, o proxy e o lançador nascem como numa run comum;
  - no Steam, o lobby fica aberto durante a run.
- **Com a opção desligada:** nada muda (o host nem guarda a run sem parceiro).
- **Não vem junto:** o nível, as skills e os itens que o cliente tinha. Eles só existem no jogo do cliente, e a run dele foi encerrada na queda (item 3).
- **1.4:** agora vem, se o jogo do cliente não fechou: ele guarda a build a cada 5 s e a devolve ao jogo na volta (`reentrada-notas.md`).

**Desenho da reconexão "de verdade" (fase futura):**

1. **Cliente:** na queda do host, em vez de desistir, pausa a run ("Aguardando o host voltar... 30 s") e tenta reconectar sozinho. No IP, o mesmo endereço; no Steam, o mesmo lobby, que precisa ficar joinable.
2. **Host:** na queda do cliente, mantém por 30 s o estado do parceiro:
   - o proxy parado e fora da mira;
   - o estado de caído;
   - o XP que seria dele.
   Só limpa se o tempo acabar.
3. **Na volta:** handshake normal e um "resync de run" no lugar do `RunStart`:
   - spawns de todos os inimigos (já existe);
   - `CoopLife` forçado;
   - `PauseState`;
   - `XpShare` acumulado.
   O cliente segue na **mesma** cena, sem recarregar.
4. **Pacotes:** 60–62 bastam (`RejoinRequest`, `RunResync`, `RejoinDone`).
5. **Riscos:** o cliente parado 30 s num mapa sem marionetes. O `ClientRunLatched` precisa continuar valendo durante a espera, e já continua.

### 6. Keepalive e timeout (valores finais)

| | IP (LiteNetLib) | Steam |
|---|---|---|
| Ping | 1 s, thread do LiteNetLib | 1 s (`Ping`/`Pong` 44/45), thread do jogo |
| Queda por silêncio | 10 s (`DisconnectTimeout`, era 5 s) | 15 s; 45 s com um lado carregando |
| Outras detecções | desconexão explícita do LiteNetLib | `Bye`, saída do lobby, estado da sessão 1×/s, `SessionFailed` |
| Conectar | tenta para sempre (reconecta a cada ~5 s) | `Hello` a cada 1 s, desiste em 30 s |
| "Conexão instável" | 2 s sem pacote | 2 s sem mensagem |

## Resultado da auditoria

"1 sentido" = a latência de um lado pro outro (metade do ping).

| Sistema | Com atraso (100–200 ms) ou perda (2–5%) | Corrigido | Ficou |
|---|---|---|---|
| Fantasma (`PlayerSync`, Sequenced 20 Hz, interpolação de 100 ms) | Fica 1 sentido + 100 ms atrás (inerente). Perda: 1 amostra perdida é coberta pelos 100 ms. Jitter: amostras amontoadas fazem o fantasma acelerar e frear | Relógio do remetente, atrás de `SenderClock` | Desligado por padrão até comparar em jogo |
| Marionetes (`EnemySnapshot`, Unreliable 15 Hz, extrapolação ≤ 0,25 s) | Posições 1 sentido atrás (inerente; o dano vai por netId e não depende disso). Perda: cada fatia é independente e tem sequência por marionete. Jitter: velocidade errada e mais teleportes. **Bug:** no IP, fatia > 1023 B lançava exceção com mais de ~90 inimigos | Fatia de 1000 B, fallback reliable, `Send` sem exceção; velocidade pelo relógio do host (`SenderClock`) | `SenderClock` desligado |
| Spawn / Died / Despawn (ReliableOrdered) contra snapshot (Unreliable) | Um snapshot pode chegar antes do spawn: as entradas são ignoradas (`unknownNetIds`) e o próximo cobre | — | Ok |
| `ClientDamage` (ReliableUnordered, 15 Hz) | A morte aparece no cliente 1 ping depois do golpe. Dano num inimigo que o host já matou é descartado (normal) | — | Ok |
| `DamageToClient` (ReliableOrdered) | O host bate no proxy, que está onde o cliente estava 1 sentido + 100 ms atrás. Com ping alto, o cliente "apanha de longe" ou depois de desviar. Inerente ao host autoritativo | — | Documentado. Só predição resolveria (fora do escopo) |
| Pausa (`PauseState` reliable na mudança) | O outro congela 1 sentido depois. Sem trava: o estado remoto zera ao desconectar, no `RunStart` e fora da run | — | Ok |
| Caído e reviver (`CoopLife`) | O host decide com a posição atrasada do fantasma (raio de 2,5 m, 3 s: sobra margem). Depois do `Revive`, o host espera 3 s pelo `ClientState` "vivo", que cobre ida e volta de até ~2 s. `DefeatGrace` 0,5 s está ok, porque o `ClientState` sai na hora da mudança | — | Ok. Se a confirmação demorar mais de 3 s, o host conta o cliente caído de novo e reenvia o `Revive` (inofensivo) |
| `LootDrop` antes de `EnemyDied` | Mesmo canal ordenado nos dois transportes e na fila simulada: o loot sempre chega antes da morte | — | Ok |
| XP de equipe (`XpShare` e dono do orbe) | O dono do orbe é quem está mais perto, pela posição atrasada do proxy. XP mandado ao cliente no instante em que ele cai se perde | — | Documentado |
| Magias (`SkillCast` / `SkillChannelEnd`) | Réplica 1 sentido depois, na posição atrasada do fantasma; ordem garantida | — | Ok |
| Ataques de inimigo (`EnemyCast`, ReliableUnordered) | No IP pode chegar antes do spawn (ignorado). O aviso de área chega 1 sentido depois, junto com o dano, que também vem do host | — | Ok |
| `RunStart` e barreira de carregamento | O `Loading` do cliente chega ao host ~1 ping depois do Lutar. O carregamento do host leva segundos, então a barreira pega. Limite de 45 s | — | Ok |
| `RunEnd` / `RunEndRequest` | O Desistir do cliente espera 1 ping. Se o host travar **sem cair**, o cliente fica no menu de pausa sem resposta | — | Não corrigido (raro). A queda do host já é tratada |
| Handshake do Steam | `Hello` a cada 1 s até o `Accept`; `Accept` reenviado se o `Hello` se repete; 30 s de limite | — | Ok |
| Keepalive | IP: 5 s era curto para Wi-Fi. Steam: 15 s com a thread do jogo travada no carregamento | 10 s no IP, 45 s carregando no Steam, "conexão instável" com 2 s | — |
| Queda do host (cliente) | Indefinido: mapa vazio | Aviso e desistência controlada | Reconexão transparente: desenho no item 5 |
| Queda do cliente (host) | Inimigos presos ao proxy reciclado | `ReleaseTargets` | XP em trânsito perdido |
| Pacote unreliable que deveria ser reliable | Nenhum: todo estado vai reliable; só posição, snapshot e ping são unreliable, e são repetidos | — | — |

## Roteiro de teste

Simulação: **75 ms + 15 ms de jitter + 3% de perda em cada jogo**, o que dá uns 150 ms de ping. No mesmo PC os dois jogos leem o mesmo `.cfg`, então basta editar uma vez.

1. Com os jogos fechados, edite `BepInEx/config/com.kolber.soulstonecoop.cfg`. Em `[Debug]`: `SimulateLatencyMs = 75`, `SimulateJitterMs = 15`, `SimulatePacketLossPercent = 3`. As chaves aparecem depois da primeira abertura desta versão; se não estiverem lá, abra e feche o jogo uma vez.
2. Abra os dois jogos pelo exe. No log: `### SIMULACAO DE REDE LIGADA: +75 ms ±15 ms, 3% de perda`. Na aba Co-op, a linha laranja.
3. A: **Hospedar por IP**. B: **Entrar** (`127.0.0.1`). O status mostra uns 150–180 ms.
4. **Run normal**, uns 5 min, com A escolhendo o mapa:
   - fantasma andando liso;
   - inimigos sem pular;
   - dano nos dois sentidos;
   - magias, XP, loot, pausa e level-up nos dois lados.
   Não deve aparecer "Conexão instável" com 3%.
5. **Muitos inimigos:** deixe juntar mais de 100. As marionetes do B continuam se mexendo, e não há `Exception in Update` nem `TooBigPacket` no log.
6. **Revive com atraso:**
   - F9 no A, B fica perto 3 s: A levanta.
   - F9 no B, A perto: B levanta.
   - F9 nos dois: derrota nas duas telas.
7. **Cliente cai:** numa run nova, feche a janela do B. No A aparece "Conexão instável..." por até 10 s, depois "B desconectou. Você continua sozinho.". Os inimigos que iam no B voltam pro A, e a run segue.
8. **Host cai:** reabra o B, conecte e comece uma run pelo A. Feche a janela do A. No B: "Conexão instável..." por até 10 s, depois "O host desconectou. Encerrando a partida...", e 4 s depois a tela de desistência. Volte ao menu: o B joga solo normalmente.
9. **Host cai com o cliente no level-up:** repita o 8 com o B na tela de upgrade. O aviso pede pra escolher, e a run encerra depois da escolha.
10. **Reentrada (opcional):**
    - `[Coop] AllowRejoin = true` no `.cfg`.
    - A numa run, feche e reabra o B, Entrar.
    - O A mostra "B chegou: entrando na sua partida", e o B carrega a mesma partida como personagem novo, com inimigos, fantasma e dano funcionando.
11. **Relógio do remetente:** `[Coop] SenderClock = true` nos dois e repita o 4 por 2 min. Compare a suavidade do fantasma e dos inimigos com o passo 4.
12. Com o amigo (Steam), simulação em 0:
    - repita o 7 e o 8;
    - veja se "Conexão instável" aparece em rede real (anote quando).
13. Volte a simulação pra 0 e mande `LogOutput.log` (A) e `LogOutput.1.log` (B).

Logs úteis: `[netsim]`, `[net] Link`, `[life] Host lost`, `[proxy] Proxy going away`, `[run] Host: partner connected mid-run`, `ping=`/`silence=` na linha `[sync]`.

## Riscos sem teste

- **Maior risco:** a desistência automática quando o host cai. O `Surrender` fora do menu de pausa nunca rodou. O decompilado diz que funciona, e o espelho da 4A chama o mesmo método, mas também não foi confirmado nos logs. Se der problema: `EndRunWhenHostLeaves = false` e desistir na mão.
- **Simulação no IP:** depende do código de simulação do LiteNetLib, que nunca usamos.
- **Fila do Steam:** só foi testada fora do jogo.
- **`AllowRejoin` e `SenderClock`:** nunca rodaram; ficam desligados.
- **Mudança de formato:** os dois lados precisam do mesmo build (por isso a versão sobe no merge).
