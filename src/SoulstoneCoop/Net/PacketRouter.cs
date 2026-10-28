using LiteNetLib;
using LiteNetLib.Utils;

namespace SoulstoneCoop.Net;

public enum PacketType : byte
{
    PlayerInput = 1,   // client -> host: movement/aim/skill input for this frame
    PlayerState = 2,   // host -> client: authoritative transform/anim state for the remote player entity
    WorldSnapshot = 3, // host -> client: batched enemy/drop state
    LevelUpChoice = 4, // client -> host: chosen skill on level up
    Revive = 5,        // host -> client: target u8 (1 = client) is revived (phase 4A, ReliableOrdered)
    PauseState = 6,    // either way: sender's LOCAL pause reason (none / pause / level-up)
    EnemySpawn = 7,    // host -> client: enemy netId + prefab key + initial state (ReliableOrdered)
    EnemyDespawn = 8,  // host -> client: enemy recycled without dying (ReliableOrdered)
    EnemyDied = 9,     // host -> client: enemy killed, play the death (ReliableOrdered)
    EnemySnapshot = 10, // host -> client: positions/yaw/HP of live enemies, sliced (Unreliable)
    ClientDamage = 11, // client -> host: damage the client dealt to puppets, summed per netId per tick (ReliableUnordered)
    DamageToClient = 12, // host -> client: a hit on the client's proxy (healthChange f32, attacker netId u16) (ReliableOrdered)
    ClientState = 13,  // client -> host: local player dead flag + hp/maxHp/armor/damageReduction/block, every 0.5 s (ReliableOrdered)
    SkillCast = 14,    // either way: the sender's local player cast a skill, replayed visual-only on the RemoteCaster (ReliableOrdered)
    SkillChannelEnd = 15, // either way: a channel started by SkillCast stopped (ReliableOrdered, same channel as SkillCast)
    LootDrop = 16,     // host -> client: one LootUtil.ProcessLoot call's arguments, replayed locally (ReliableOrdered, before EnemyDied)
    XpShare = 17,      // either way: raw XP the sender's player collected (team XP: the receiver adds it too) (ReliableOrdered)
    CoopLife = 18,     // host -> client: hostDown u8 | clientDown u8 | revive progress f32, on change and 5 Hz while someone is down (ReliableOrdered)
    RunEnd = 19,       // host -> client: the run ends (Defeat / Victory / Surrender / PortalHome) for both (ReliableOrdered)
    RunEndRequest = 20, // client -> host: the client surrendered or took the Home portal (ReliableOrdered)
    EnemyCast = 21,    // host -> client: a boss/elite/special enemy cast, replayed visual-only on its puppet (phase 4D, ReliableUnordered)
    ObjectiveSync = 22, // host -> client: kills, tier, completed kill objectives, boss objective Current/Total, on change + every 2 s (phase 4D fix, ReliableOrdered)
    // 22-29 reserved for phase 4D
    Hello = 30,        // either way: name, character, weapon, skins, mod version (phase 6, on connect and when it changes) (ReliableOrdered)
    LobbyState = 31,   // either way: sender is in the menu / loading a run / in a run (phase 6, on change) (ReliableOrdered)
    RunStart = 32,     // host -> client: scene, mode, mastery tier/level, Survivors affixes: start the same run (phase 6) (ReliableOrdered)
    RunStartRefused = 33, // client -> host: could not follow RunStart, with the reason (phase 6) (ReliableOrdered)
    // 50-59: phase 5
    PartnerHealth = 50, // either way: sender's hp f32 | maxHp f32 | flags u8 (1 down, 2 dead), on change at <= 5 Hz + every 1 s (ReliableOrdered)
    SummonSpawn = 51,   // either way: one of the sender's summons/companions/panther, to show as a visual-only clone (ReliableOrdered, refreshed every 3 s)
    SummonDespawn = 52, // either way: that summon is gone (ReliableOrdered)
    SummonStates = 53,  // either way: positions/yaw of the sender's live summons, ~12 Hz (Sequenced)
    DashState = 54,     // either way: the sender's player started (1) / stopped (0) dashing, on change (ReliableOrdered)
    // 70-79: small pending items (docs/pendencias-notas.md)
    GodCast = 70,      // host -> client: the God-Entity cast a meteor, replayed visual-only on the RemoteCaster (ReliableUnordered)
    ProxyStatus = 71,  // host -> client: a status/stun applied to the client's proxy, replayed on the local player (ReliableUnordered)
    // 90-99: portals (docs/portais-notas.md)
    PortalTransition = 90, // host -> client: the host took the Endless/BossRush portal (with the map it drew) or restarted: follow it (ReliableOrdered)
    PortalRequest = 91,    // client -> host: the client walked into the Endless/BossRush portal; the host decides (ReliableOrdered)
    // 100-119: phase 9, up to 8 players (docs/fase9-investigacao.md)
    Relayed = 100,     // host -> client: origin slot u8 | a client's packet (type byte included), forwarded to the other clients
    PlayersState = 101, // host -> clients: every player's position, 20 Hz (Sequenced): count u8 | count * (slot u8, x, y, z, yaw f32, sentMs u32)
    Welcome = 102,     // host -> new client, first packet: yourSlot u8 | maxPlayers u8 | n u8 | n * slot u8 (the other clients)
    PlayerLeft = 103,  // host -> clients: slot u8 left the session (ReliableOrdered, before the slot is handed out again)
    PauseOverride = 104, // host -> clients: mask u8 of the players whose pause the host lifted (Esc closed on the host, or F4) (ReliableOrdered)
    // 125-129: issue #7, party panel (docs/issue7-notas.md)
    PartyStatus = 125, // either way: sender's level u16 | xp progress u8 (/255) | death guards u8, on change at <= 4 Hz + every 2 s (ReliableOrdered)
}

public static class PacketRouter
{
    /// <summary>Phase 9: whose packet is being handled right now. On the host, the slot of the client it came from (the
    /// transport knows the connection); on a client, 0 (the host) or the origin written in a Relayed envelope.</summary>
    public static byte From { get; private set; }

    /// <summary>Host: which client packets the other clients must see too, and how they are forwarded (null = host only).</summary>
    private static DeliveryMethod? RelayMethod(PacketType type) => type switch
    {
        // Who it is and where: every client builds the ghosts of the others from these.
        PacketType.Hello or PacketType.LobbyState => DeliveryMethod.ReliableOrdered,
        // What the others see of that player: its pause, health bar, spells (visual) and summons; and its XP (team XP).
        PacketType.PauseState or PacketType.PartnerHealth or PacketType.XpShare or PacketType.DashState => DeliveryMethod.ReliableOrdered,
        PacketType.PartyStatus => DeliveryMethod.ReliableOrdered, // issue #7: level, XP progress and revives (party panel)
        PacketType.SkillCast or PacketType.SkillChannelEnd => DeliveryMethod.ReliableOrdered, // an end never overtakes its start
        PacketType.SummonSpawn or PacketType.SummonDespawn => DeliveryMethod.ReliableOrdered,
        PacketType.SummonStates => DeliveryMethod.Sequenced,
        // Positions go in the host's PlayersState (one packet per tick); damage, ClientState, requests: host only.
        _ => null,
    };

    public static void Handle(NetDataReader reader, byte from)
    {
        if (reader.AvailableBytes < 1) return;
        int start = reader.Position, length = reader.AvailableBytes;
        NetworkManager.CountReceived(from, length);
        var type = (PacketType)reader.RawData[start];

        if (NetworkManager.Role == NetRole.Host)
        {
            if (type == PacketType.Relayed) return; // only the host wraps packets
            var relay = RelayMethod(type);
            // Forwarded before it's handled here, so a throwing handler can't keep the others from seeing it.
            if (relay.HasValue && Players.OtherCount > 1) NetworkManager.Relay(from, reader.RawData, start, length, relay.Value);
        }
        else if (type == PacketType.Relayed)
        {
            if (length < 3) return;
            reader.GetByte();
            byte origin = reader.GetByte();
            if (origin == Players.MySlot || origin == Players.HostSlot || !Players.IsValid(origin)) return;
            var inner = (PacketType)reader.RawData[reader.Position];
            // A client the Welcome didn't list (it joined after us) shows up with its Hello / LobbyState.
            if (!Players.IsPresent(origin))
            {
                if (inner != PacketType.Hello && inner != PacketType.LobbyState) return; // a late packet of a player who left
                Players.MarkJoined(origin);
            }
            Dispatch(reader, origin);
            return;
        }
        Dispatch(reader, from);
    }

    private static void Dispatch(NetDataReader reader, byte from)
    {
        var prev = From;
        From = from;
        try { DispatchCore(reader); }
        finally { From = prev; }
    }

    private static void DispatchCore(NetDataReader reader)
    {
        var type = (PacketType)reader.GetByte();

        switch (type)
        {
            case PacketType.Welcome:
                NetworkManager.OnWelcome(reader);
                break;
            case PacketType.PlayerLeft:
                NetworkManager.OnPlayerLeft(reader);
                break;
            case PacketType.PlayerInput:
                // Task #6/#7: real input-driven simulation, not wired yet (current milestone mirrors raw transforms).
                break;
            case PacketType.PlayersState:
                PlayerSync.OnPlayersStateReceived(reader);
                break;
            case PacketType.PauseOverride:
                PauseSync.OnPauseOverride(reader);
                break;
            case PacketType.PlayerState:
                PlayerSync.OnPlayerStateReceived(reader);
                break;
            case PacketType.WorldSnapshot:
                // Task #6: host-authoritative world sync, not wired yet.
                break;
            case PacketType.LevelUpChoice:
                // Task #7: co-op level-up patches, not wired yet.
                break;
            case PacketType.Revive:
                Life.CoopLife.OnRevive(reader);
                break;
            case PacketType.CoopLife:
                Life.CoopLife.OnLife(reader);
                break;
            case PacketType.RunEnd:
                Life.CoopLife.OnRunEnd(reader);
                break;
            case PacketType.RunEndRequest:
                Life.CoopLife.OnRunEndRequest(reader);
                break;
            case PacketType.EnemySpawn:
                Enemies.ClientPuppets.OnSpawn(reader);
                break;
            case PacketType.EnemyDespawn:
                Enemies.ClientPuppets.OnDespawn(reader);
                break;
            case PacketType.EnemyDied:
                Enemies.ClientPuppets.OnDied(reader);
                break;
            case PacketType.EnemyCast:
                Enemies.EnemyCasts.OnEnemyCast(reader);
                break;
            case PacketType.ObjectiveSync:
                Enemies.ObjectiveSync.OnObjectiveSync(reader);
                break;
            case PacketType.GodCast:
                Enemies.GodCasts.OnGodCast(reader);
                break;
            case PacketType.ProxyStatus:
                Enemies.ProxyStatusForward.OnProxyStatus(reader);
                break;
            case PacketType.EnemySnapshot:
                Enemies.ClientPuppets.OnSnapshot(reader, reader.RawDataSize);
                break;
            case PacketType.ClientDamage:
                Enemies.HostEnemySync.OnClientDamage(reader);
                break;
            case PacketType.DamageToClient:
                Enemies.ClientIncomingDamage.OnDamageToClient(reader);
                break;
            case PacketType.ClientState:
                Enemies.RemoteProxy.OnClientState(reader);
                break;
            case PacketType.SkillCast:
                Skills.CastSync.OnSkillCast(reader);
                break;
            case PacketType.SkillChannelEnd:
                Skills.CastSync.OnSkillChannelEnd(reader);
                break;
            case PacketType.LootDrop:
                Loot.LootSync.OnLootDrop(reader);
                break;
            case PacketType.XpShare:
                Loot.LootSync.OnXpShare(reader);
                break;
            case PacketType.Hello:
                Lobby.CoopSession.OnHello(reader);
                break;
            case PacketType.LobbyState:
                Lobby.CoopSession.OnLobbyState(reader);
                break;
            case PacketType.RunStart:
                Lobby.RunSync.OnRunStart(reader);
                break;
            case PacketType.RunStartRefused:
                Lobby.RunSync.OnRunStartRefused(reader);
                break;
            case PacketType.PortalTransition:
                Lobby.PortalSync.OnPortalTransition(reader);
                break;
            case PacketType.PortalRequest:
                Lobby.PortalSync.OnPortalRequest(reader);
                break;
            case PacketType.DashState:
                PlayerSync.OnDashStateReceived(reader);
                break;
            case PacketType.SummonSpawn:
                Summons.SummonSync.OnSpawn(reader);
                break;
            case PacketType.SummonDespawn:
                Summons.SummonSync.OnDespawn(reader);
                break;
            case PacketType.SummonStates:
                Summons.SummonSync.OnStates(reader);
                break;
            case PacketType.PartnerHealth:
                Life.PartnerHealth.OnPacket(reader);
                break;
            case PacketType.PartyStatus:
                Life.PartyStatus.OnPacket(reader);
                break;
            case PacketType.PauseState:
                PauseSync.OnPauseStateReceived(reader);
                break;
            default:
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Unknown packet type {type}");
                break;
        }
    }
}
