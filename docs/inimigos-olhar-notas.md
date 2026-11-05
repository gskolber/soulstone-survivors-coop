# Inimigos olhando para o convidado

## O problema

No convidado, os inimigos não ficavam virados para o jogador que atacavam, nem com o host caído.

- No host, a IA usa `OrientToMovement` (`BasicAIComponent.BehaviourWhenBestSkillSelected` / `CheckIfCanMove` chamam `ModifiedCharacter.SetRotationMode(1, null)`): o inimigo vira para onde anda.
- O snapshot só leva o yaw do host, que aponta para onde o proxy estava (atrasado).
- A marionete do convidado anda com `MoveToLocation` e vira conforme os próprios ajustes pequenos (`OrientToMovement`), quase sempre de lado. Ela só usava o yaw do host quando parava.

## Correção (`ClientPuppets.UpdateFacing`)

A cada 0,2 s, para cada marionete do time inimigo, o mod procura o jogador vivo mais perto: nós mesmos, se de pé, ou o fantasma de um parceiro de pé. Ele conta:

- a até 6 u, sempre;
- a até 14 u, quando a marionete está quase parada (< 1,5 u/s), como um inimigo de longe atirando.

Com alvo, a marionete passa para `RotationMode.LookTowardsTarget` com o transform desse jogador. O próprio `ModifiedCharacter` gira suave, e confere se o alvo ainda existe (`UpdateRotationAndNavMeshAgent` 0x7A63B0). Sem alvo, ela volta ao modo dela, e o yaw do host volta a valer quando ela para.

Só muda o visual no convidado; no host nada muda.

## Como testar

No convidado (B):

- ande em volta de um grupo de inimigos parado: eles giram acompanhando B;
- os de longe, parados atirando, ficam virados para B;
- com o host caído (F9 em A), os que atacam B ficam virados para ele;
- um inimigo passando longe continua virado para onde anda.
