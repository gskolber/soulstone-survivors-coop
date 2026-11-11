using ECM2.Characters;
using HarmonyLib;
using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Enemies;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Modes;

/// <summary>
/// 1.8.0: Mastery mode in co-op (docs/mastery-notas.md). Mastery runs MasteryGameManager (GameData.MasteryManager), not
/// SurvivorsGameManager, and nothing in the mod knew it. How the mode runs (GameAssembly 1.5.3.2):
///   - Start spawns the main boss (SpawnMainBoss -> SpawnMainBossRoutine -> BossSpawnProcessor.SpawnSpecificBoss), makes
///     it the MainObjective (EliminateSpecificEntitiesObjective) and raises the mastery pillars (SpawnMasteryPillar).
///   - A rune (MasteryBossSpawner) waits for a player to stand on it for TimeToEnter (OnTriggerStay). Then: its
///     OnPlayerTriggerSpawn event spawns the lords (SpawnBoss -> MasterySpawnBosses); on cycle 0 the local player
///     ascends, on later cycles CastMasterySkillAndResetPlayer resets it (level, upgrades); before the last cycle
///     CreateLordsObjectivesAfterTrigger makes the lords objective (EliminateEnemiesNonCumulativeObjective on
///     GameStats.BossesKilled).
///   - UpdateAll: lords objective done -> CurrentMasteryCycle+1, StopSpawning, apocalypse; a pillar whose
///     ObjectiveAmountNeededToDestroy is reached breaks (DestroyPillarRoutine -> AdvanceCycle -> GoToNextCycleRoutine,
///     which re-arms the rune); MainObjective done -> victory (OpenPortalAndObjective); the Home portal is
///     MasteryGameManager.OnPlayerEnterPortal.
///
/// What went wrong on a guest, and what this does:
///   - SpawnSpecificBoss returns null on the guest (ClientSpawnBlock), and the main boss coroutine threw right after it:
///     no MainObjective, no pillars, and UpdateAll threw every frame in the last phase. The guest now holds its
///     SpawnMainBoss until the host's main boss puppet exists (MasteryState carries its net id) and hands that puppet
///     out as the result of SpawnSpecificBoss. The puppet dying doesn't end the guest's run by itself (the main boss
///     objective isn't run on the guest); the victory comes from the host's RunEnd (CoopLife.ForceLocalVictory).
///   - The rune: each machine resets only its own player, so the trigger belongs to the host. On the guest the
///     game's OnTriggerStay fills the bar for its own player but stops just short of going off; the host counts its
///     triggers and the guest replays each one through the game's own OnTriggerStay with its own player's collider
///     (same lords objective, ascension / reset as solo). A guest standing on the rune tells the host
///     (MasteryRunePresence, from its own physics: the first test showed the host's position check missing a guest
///     standing on the edge), and the host fills its rune with its own collider while that guest is on it.
///   Packet MasteryRunePresence (151, guest -> host, ReliableOrdered): on u8, on change + every 1 s while on.
///   - The main boss's phase transitions (MasteryBossComponent: InitialPhase = its intro, IntermediatePhases[i] at each
///     health threshold = the portal to the void arena..., FinalPhase) are actions run by the boss's AI on the host only
///     (teleports of the Players team, camera, timeline, banner); a guest's puppet runs no AI, so the guest saw no intro
///     and wasn't pulled into the arena (5th test). The host sends each one (MasteryBossPhase, 152) and the guest
///     starts the same PhaseTransition on its puppet's MasteryBossComponent (set up on the puppet first if needed).
///   - Kills: the lords objective counts GameStats.BossesKilled, so the host's kill counters are copied to the guest
///     (ObjectiveSync does that for Survivors only). If the guest's cycle stays behind the host's for 3 s anyway, its
///     lords objective is completed (Starting lowered; created first if it isn't there, as after the host's F7).
///   - Home portal and victory: CoopLife / PortalSync know MasteryGameManager now.
/// Packet MasteryState (150, host -> clients, ReliableOrdered, on change + every 2 s):
///   cycle i32 | total cycles i32 | rune triggers i32 | main boss net id u16 (0 = none) | kills enemies/elites/bosses 3 x i32 |
///   flags u8 (1 = the host jumped to the main boss with F7).
/// </summary>
public static class MasterySync
{
    private const float RefreshInterval = 2f;
    private const float CheckInterval = 0.2f;
    private const float BehindGrace = 3f;
    private const float TelemetryInterval = 10f;

    private struct State
    {
        public int Cycle, Total, Triggers, Enemies, Elites, Bosses;
        public ushort MainBoss;
        public bool JumpedToFinal; // the host's F7 (test key)

        public bool SameAs(in State o) =>
            Cycle == o.Cycle && Total == o.Total && Triggers == o.Triggers && MainBoss == o.MainBoss &&
            Enemies == o.Enemies && Elites == o.Elites && Bosses == o.Bosses && JumpedToFinal == o.JumpedToFinal;
    }

    // Per match (reset when the MasteryGameManager changes).
    private static IntPtr _manager;
    private static bool _spawnerWasTriggered;
    private static int _triggers;            // host: rune triggers so far; guest: triggers replayed here
    private static State _host;              // guest: last MasteryState
    private static bool _hasHost;
    private static State _lastSent;
    private static bool _hasLastSent;
    private static float _refreshTimer, _checkTimer, _telemetryTimer;
    private static float _behindSince = -1f;
    private static int _forcedCycle = -1;
    private static int _createdForCycle = -1;

    // Guest: main boss hand-off.
    private static bool _mainBossPending;
    private static bool _allowMainBoss;
    private static Entity? _mainBossOverride;
    private static float _mainBossWaitSince = -1f;

    // Rune: the guest's own OnTriggerStay only runs when we replay a host trigger; the host remembers when the game's
    // own OnTriggerStay last ran (its player inside), so it doesn't fill the rune twice as fast.
    internal static bool ForcingTrigger;
    private static float _nativeStayAt = -10f;
    private const float PresenceResend = 1f;
    private const float PresenceTimeout = 2.5f;
    private static readonly float[] _guestOnRuneAt = new float[Players.MaxSlots]; // host: last "on" from that guest
    private static float _localOnRuneAt = -10f; // guest: our physics saw our player on the rune
    private static bool _presenceSent;
    private static float _presenceTimer;
    private static Collider? _localCollider;
    private static IntPtr _localColliderOwner;

    /// <summary>Guest: the host's RunEnd(Victory) arrived; the main boss objective may complete now.</summary>
    internal static bool HostWon;

    private static int _sent, _recv, _replayed, _assists, _mainBossLinks, _forced, _errors, _phasesSent, _phasesPlayed;
    private static readonly HashSet<string> Once = new();

    internal static MasteryGameManager? Manager
    {
        get
        {
            try
            {
                if (!GameManagerUtil.HasGameManager) return null;
                var mm = GameManagerUtil.GameData?.MasteryManager;
                return mm != null ? mm : null;
            }
            catch { return null; }
        }
    }

    /// <summary>A co-op Mastery match is up on this machine.</summary>
    internal static bool Active => NetworkManager.Role != NetRole.None && Manager != null;

    private static void LogOnce(string key, string msg)
    {
        if (Once.Add(key)) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] {msg}");
    }

    private static void ResetFor(MasteryGameManager? mm)
    {
        _manager = mm != null ? mm.Pointer : IntPtr.Zero;
        _spawnerWasTriggered = false;
        _triggers = 0;
        _hasHost = false;
        _host = default;
        _hasLastSent = false;
        _behindSince = -1f;
        _forcedCycle = -1;
        _createdForCycle = -1;
        _mainBossPending = false;
        _allowMainBoss = false;
        _mainBossOverride = null;
        _mainBossWaitSince = -1f;
        _localCollider = null;
        _localColliderOwner = IntPtr.Zero;
        Array.Clear(_guestOnRuneAt);
        _localOnRuneAt = -10f;
        _presenceSent = false;
        HostWon = false;
        _pendingPhases.Clear();
        _phaseCheckAt = -1f;
        _phaseWatched = null;
        _holdFor = null;
        _holdUntil = -1f;
        _jumpedToFinal = false;
        _killAt = -1f;
        _cleanupUntil = -1f;
        _phaseWaitSince = -1f;
        _phaseReadyAt = -1f;
        Once.Clear();
        if (mm != null) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Mastery match ({NetworkManager.Role}): cycles={mm.TotalCyclesAmount}.");
    }

    public static void Tick(float dt)
    {
        try
        {
            var mm = Manager;
            IntPtr p = mm != null ? mm.Pointer : IntPtr.Zero;
            if (p != _manager) ResetFor(mm);
            if (mm == null || NetworkManager.Role == NetRole.None) return;
            if (CoopRole.IsHostSide) HostTick(mm, dt);
            else if (CoopRole.IsClientSide) ClientTick(mm, dt);
            Telemetry(mm, dt);
        }
        catch (Exception ex)
        {
            if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][mastery] Tick: {ex}");
        }
    }

    // ---------------- host ----------------

    private static void HostTick(MasteryGameManager mm, float dt)
    {
        var spawner = mm.MasteryBossSpawner;
        bool trig = spawner != null && spawner._triggered;
        if (trig && !_spawnerWasTriggered)
        {
            _triggers++;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Host: rune triggered (#{_triggers}, cycle {mm.CurrentMasteryCycle}).");
        }
        _spawnerWasTriggered = trig;
        if (spawner != null && !trig) AssistGuestsOnRune(spawner);
        if (_killAt > 0f || _cleanupUntil > 0f || _phaseWaitSince > 0f) DebugTick(mm, dt);

        if (!NetworkManager.HasPeer) { _hasLastSent = false; return; }
        _refreshTimer += dt;
        _checkTimer += dt;
        if (_checkTimer < CheckInterval) return;
        _checkTimer = 0f;
        var s = Read(mm);
        if (_hasLastSent && s.SameAs(_lastSent) && _refreshTimer < RefreshInterval) return;
        _refreshTimer = 0f;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.MasteryState);
        w.Put(s.Cycle); w.Put(s.Total); w.Put(s.Triggers);
        w.Put(s.MainBoss);
        w.Put(s.Enemies); w.Put(s.Elites); w.Put(s.Bosses);
        w.Put((byte)(s.JumpedToFinal ? 1 : 0));
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _lastSent = s;
        _hasLastSent = true;
        _sent++;
    }

    private static State Read(MasteryGameManager mm)
    {
        var s = new State { Cycle = mm.CurrentMasteryCycle, Total = mm.TotalCyclesAmount, Triggers = _triggers, JumpedToFinal = _jumpedToFinal };
        var boss = mm._mainBossEntity;
        if (boss != null && HostEnemySync.TryGetNetId(boss, out var id)) s.MainBoss = id;
        var stats = GameManagerUtil.GameStats;
        if (stats != null) { s.Enemies = stats.EnemiesKilled; s.Elites = stats.ElitesKilled; s.Bosses = stats.BossesKilled; }
        return s;
    }

    /// <summary>Host, prefix of the game's OnTriggerStay (any collider in the rune): remember when it was our own player.</summary>
    internal static void NoteNativeStay(Collider? other)
    {
        if (ForcingTrigger || other == null) return;
        var local = GameState.LocalPlayerEntity;
        var rb = other.attachedRigidbody;
        if (local == null || rb == null) return;
        var e = rb.GetComponent<Entity>();
        if (e != null && e.Pointer == local.Pointer) _nativeStayAt = Time.unscaledTime;
    }

    /// <summary>Host: a living guest inside the rune's trigger fills it like the host would (the game only sees
    /// the host's player). Not while the host's own player is on it (the game already counts that).</summary>
    private static void AssistGuestsOnRune(MasteryBossSpawner spawner)
    {
        if (Time.unscaledTime - _nativeStayAt < 0.2f) return;
        if (!spawner.isActiveAndEnabled) return;
        var col = spawner.TriggerCollider;
        if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) return;
        var b = col.bounds;
        b.Expand(new Vector3(1.2f, 0f, 1.2f)); // a player's capsule counts from its edge, not its centre
        bool inside = false;
        foreach (var slot in Players.Others)
        {
            if (!RemoteProxy.IsAlive(slot)) continue;
            if (slot < _guestOnRuneAt.Length && _guestOnRuneAt[slot] > 0f && Time.unscaledTime - _guestOnRuneAt[slot] < PresenceTimeout) { inside = true; break; }
            if (!RemoteProxy.TryGetPosition(slot, out var pos, out _)) continue;
            pos.y = b.center.y;
            if (b.Contains(pos)) { inside = true; break; }
        }
        if (!inside) return;
        var mine = LocalCollider();
        if (mine == null) { LogOnce("nocol-host", "Host: no collider on our player to fill the rune for a guest."); return; }
        try
        {
            ForcingTrigger = true;
            spawner.OnTriggerStay(mine);
            _assists++;
            LogOnce($"assist-{_triggers}", $"Host: a guest stands on the rune (trigger #{_triggers + 1}); filling it for them.");
        }
        finally { ForcingTrigger = false; }
    }

    /// <summary>A collider of our own player whose rigidbody the game maps to it (what the rune's OnTriggerStay
    /// gets when the player walks in).</summary>
    private static Collider? LocalCollider()
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null) return null;
        if (_localCollider != null && _localColliderOwner == local.Pointer) return _localCollider;
        _localCollider = null;
        _localColliderOwner = local.Pointer;
        var comps = local.GetComponentsInChildren(Il2CppType.Of<Collider>(), true);
        Collider? fallback = null;
        for (int i = 0; i < comps.Length; i++)
        {
            var c = comps[i]?.TryCast<Collider>();
            if (c == null || c.attachedRigidbody == null) continue;
            var e = c.attachedRigidbody.GetComponent<Entity>();
            if (e == null || e.Pointer != local.Pointer) continue;
            if (!c.isTrigger) { _localCollider = c; break; }
            fallback ??= c;
        }
        _localCollider ??= fallback;
        if (_localCollider != null) LogOnce("col", $"Our player's collider for the rune: '{_localCollider.name}' ({_localCollider.GetIl2CppType().Name}).");
        return _localCollider;
    }

    /// <summary>Host: a guest's own physics says its player is on (or left) the rune.</summary>
    public static void OnRunePresence(NetDataReader r)
    {
        bool on = r.GetByte() != 0;
        if (NetworkManager.Role != NetRole.Host) return;
        byte from = PacketRouter.From;
        if (from >= _guestOnRuneAt.Length) return;
        _guestOnRuneAt[from] = on ? Time.unscaledTime : -10f;
        if (on) LogOnce($"presence-{from}-{_triggers}", $"Host: slot {from} says it stands on the rune.");
    }

    // ---------------- guest ----------------

    /// <summary>Guest, prefix of the game's OnTriggerStay: true = let the game fill our bar this step (it never
    /// reaches TimeToEnter here; the host's trigger is replayed instead). Notes our player on the rune for the host.</summary>
    internal static bool GuestStay(MasteryBossSpawner spawner, Collider? other)
    {
        if (ForcingTrigger) return true;
        if (other == null || spawner == null) return false;
        var local = GameState.LocalPlayerEntity;
        var rb = other.attachedRigidbody;
        if (local == null || rb == null) return false;
        var e = rb.GetComponent<Entity>();
        if (e == null || e.Pointer != local.Pointer) return false;
        _localOnRuneAt = Time.unscaledTime;
        return spawner._currentTimer + 0.1f < spawner.TimeToEnter;
    }

    private static void SendPresence(float dt)
    {
        bool on = Time.unscaledTime - _localOnRuneAt < 0.3f && !Life.CoopLife.LocalDowned;
        _presenceTimer += dt;
        if (on == _presenceSent && !(on && _presenceTimer >= PresenceResend)) return;
        _presenceTimer = 0f;
        if (on != _presenceSent) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Guest: {(on ? "on" : "off")} the rune.");
        _presenceSent = on;
        if (!NetworkManager.HasPeer) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.MasteryRunePresence);
        w.Put((byte)(on ? 1 : 0));
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnState(NetDataReader r)
    {
        var s = new State
        {
            Cycle = r.GetInt(), Total = r.GetInt(), Triggers = r.GetInt(),
            MainBoss = r.GetUShort(),
            Enemies = r.GetInt(), Elites = r.GetInt(), Bosses = r.GetInt(),
            JumpedToFinal = r.GetByte() != 0,
        };
        if (!CoopRole.IsClientSide) return;
        if (!Lobby.CoopSession.HostInRun || Lobby.CoopSession.StageTransition) return;
        _recv++;
        _host = s;
        _hasHost = true;
        try
        {
            var stats = GameManagerUtil.GameStats;
            if (stats != null && Manager != null)
            {
                // Same as ObjectiveSync on the Survivors maps: the lords objective counts BossesKilled.
                stats.EnemiesKilled = s.Enemies;
                stats.ElitesKilled = s.Elites;
                stats.BossesKilled = s.Bosses;
            }
        }
        catch (Exception ex)
        {
            if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][mastery] Applying the host's kills: {ex}");
        }
    }

    private static void ClientTick(MasteryGameManager mm, float dt)
    {
        var spawner = mm.MasteryBossSpawner;
        bool trig = spawner != null && spawner._triggered;
        if (trig && !_spawnerWasTriggered) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Guest: rune triggered here (#{_triggers}, cycle {mm.CurrentMasteryCycle}).");
        _spawnerWasTriggered = trig;

        SendPresence(dt);
        MainBossTick(mm);
        if (_pendingPhases.Count > 0) PlayPendingPhases(mm);
        if (_phaseCheckAt > 0f && Time.unscaledTime >= _phaseCheckAt) CheckPhaseOutcome();
        if (!_hasHost) return;
        if (_host.JumpedToFinal && !_jumpedToFinal && mm.MainObjective != null)
        {
            _jumpedToFinal = true;
            JumpToFinal(mm, host: false);
        }
        if (_jumpedToFinal) return; // no rune replays nor cycle catch-up after the jump

        // The host's rune went off more times than ours: replay the next one through the game's own trigger.
        if (_host.Triggers > _triggers && spawner != null && !spawner._triggered)
        {
            if (ReplayTrigger(mm, spawner)) _triggers++;
        }

        // Cycle behind the host's for a while: finish our lords objective (its Current is BossesKilled, set by UpdateAll).
        int local = mm.CurrentMasteryCycle;
        if (_host.Cycle > local)
        {
            if (_behindSince < 0f) _behindSince = Time.unscaledTime;
            else if (Time.unscaledTime - _behindSince > BehindGrace && _forcedCycle != local) ForceLordsObjective(mm, local);
        }
        else _behindSince = -1f;
    }

    private static bool ReplayTrigger(MasteryGameManager mm, MasteryBossSpawner spawner)
    {
        if (Life.CoopLife.LocalDowned) { LogOnce($"down-{_triggers}", "Guest: the host's rune went off while we're down; replaying it when we're up."); return false; }
        var col = LocalCollider();
        if (col == null) { LogOnce("nocol", "Guest: no collider on our player; can't replay the host's rune."); return false; }
        try
        {
            spawner._currentTimer = spawner.TimeToEnter;
            ForcingTrigger = true;
            spawner.OnTriggerStay(col);
        }
        finally { ForcingTrigger = false; }
        bool ok = spawner._triggered;
        if (ok)
        {
            _replayed++;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Guest: replayed the host's rune trigger #{_triggers + 1} (cycle {mm.CurrentMasteryCycle}, host {_host.Cycle}).");
        }
        else LogOnce($"replayfail-{_triggers}", "Guest: replaying the host's rune didn't trigger it here (our player isn't registered?); retrying.");
        return ok;
    }

    private static void ForceLordsObjective(MasteryGameManager mm, int cycle)
    {
        var list = mm.ProgressObjectives;
        if (list == null || list.Count == 0)
        {
            // The host skipped the cycle with F7 before its rune: make the objective here too, then finish it.
            if (_createdForCycle != cycle)
            {
                _createdForCycle = cycle;
                mm.CreateLordsObjectivesAfterTrigger();
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][mastery] Guest: cycle {cycle} behind the host's {_host.Cycle}, no lords objective here: created it.");
            }
            return;
        }
        _forcedCycle = cycle;
        var o = list[0]?.TryCast<EliminateEnemiesNonCumulativeObjective>();
        if (o == null) { Plugin.StaticLog.LogWarning("[SoulstoneCoop][mastery] Guest: the lords objective isn't the expected type."); return; }
        o.Starting = o.Current - o.Total;
        _forced++;
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][mastery] Guest: cycle {cycle} behind the host's {_host.Cycle} for {BehindGrace:F0} s: lords objective completed here.");
    }

    // ---------------- guest: the main boss ----------------

    /// <summary>Guest, prefix of MasteryGameManager.SpawnMainBoss: false = wait for the host's main boss.</summary>
    internal static bool AllowSpawnMainBoss()
    {
        if (!CoopRole.IsClientSide || NetworkManager.Role == NetRole.None) return true;
        if (_allowMainBoss) return true;
        _mainBossPending = true;
        _mainBossWaitSince = Time.unscaledTime;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][mastery] Guest: main boss held until the host's arrives.");
        return false;
    }

    /// <summary>Guest, SpawnSpecificBoss (ClientSpawnBlock): the host's main boss puppet, once.</summary>
    internal static bool TakeMainBossOverride(out Entity? e)
    {
        e = _mainBossOverride;
        if (e == null) return false;
        _mainBossOverride = null;
        return true;
    }

    private static void MainBossTick(MasteryGameManager mm)
    {
        Entity? puppet = _hasHost && _host.MainBoss != 0 ? ClientPuppets.TryGetLivePuppet(_host.MainBoss) : null;
        if (_mainBossPending)
        {
            if (puppet == null)
            {
                if (_mainBossWaitSince > 0f && Time.unscaledTime - _mainBossWaitSince > 20f)
                    LogOnce("bosswait", $"Guest: still no main boss from the host after 20 s (net id {(_hasHost ? _host.MainBoss : 0)}).");
                return;
            }
            _mainBossPending = false;
            _mainBossOverride = puppet;
            try
            {
                _allowMainBoss = true;
                mm.SpawnMainBoss();
            }
            finally { _allowMainBoss = false; }
            _mainBossLinks++;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Guest: main boss = the host's (puppet {_host.MainBoss}); its objective and pillars are set up here.");
            return;
        }
        // After a reconnect the puppets are rebuilt: point the manager at the new one (UpdateAll reads its health).
        if (puppet != null && mm.MainObjective != null)
        {
            var cur = mm._mainBossEntity;
            if (cur == null || cur.Pointer != puppet.Pointer)
            {
                mm._mainBossEntity = puppet;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Guest: main boss re-linked to puppet {_host.MainBoss}.");
            }
        }
    }

    /// <summary>Guest, ClientPuppets.KillPuppet: the host's main boss died; its puppet stays (no network death, no
    /// recycle) so the host's FinalPhase can play on it here. The scene change after the Home portal takes it away.</summary>
    internal static bool KeepForCutscene(Entity e)
    {
        if (!CoopRole.IsClientSide) return false;
        var mm = Manager;
        var main = mm != null ? mm._mainBossEntity : null;
        return main != null && main.Pointer == e.Pointer;
    }

    /// <summary>Guest, prefix of ObjectivesPanelReferences.RunEliminateSpecificEntitiesObjective: the main boss
    /// objective isn't counted here (a puppet leaving with a reconnect would read as a kill); the host's victory ends it.</summary>
    internal static bool AllowEliminateObjective(IntPtr objective)
    {
        if (!CoopRole.IsClientSide || HostWon || objective == IntPtr.Zero) return true;
        var mm = Manager;
        if (mm == null) return true;
        var main = mm._eliminateMainBossObjective;
        return main == null || main.Pointer != objective;
    }

    /// <summary>Guest: RunEnd(Victory) from the host (CoopLife.ForceLocalVictory). True if this is a Mastery match.</summary>
    internal static bool ForceVictory()
    {
        var mm = Manager;
        if (mm == null) return false;
        HostWon = true;
        var o = mm.MainObjective?.TryCast<EliminateSpecificEntitiesObjective>();
        if (o == null)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][mastery] Victory: no main boss objective here (the main boss never arrived?).");
            return true;
        }
        o.Current = o.Total;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Victory: main boss objective {o.Current}/{o.Total}.");
        return true;
    }

    /// <summary>Victory reached in this Mastery match (main boss objective done). Null = not a Mastery match.</summary>
    internal static bool? VictoryReached()
    {
        var mm = Manager;
        if (mm == null) return null;
        var o = mm.MainObjective?.TryCast<EliminateSpecificEntitiesObjective>();
        return o != null && o.Total > 0 && o.Current >= o.Total;
    }

    /// <summary>F10 on the host in a Mastery match: complete the main boss objective. False = not Mastery.</summary>
    internal static bool DebugVictory()
    {
        var mm = Manager;
        if (mm == null) return false;
        var o = mm.MainObjective?.TryCast<EliminateSpecificEntitiesObjective>();
        if (o == null) { Plugin.StaticLog.LogWarning("[SoulstoneCoop][mastery] F10: no main boss objective yet."); return true; }
        o.Current = o.Total;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][mastery] F10: main boss objective completed.");
        return true;
    }

    // ---------------- test keys (host, [Debug] TestKeys; Enemies.SpecialsDebug) ----------------

    private static bool _jumpedToFinal;
    private static float _killAt = -1f;
    private static float _cleanupUntil = -1f, _cleanupTimer;
    private const float CleanupWindow = 10f;
    private static float _phaseWaitSince = -1f, _phaseReadyAt = -1f;
    private const float PhaseDelay = 3f;     // after the boss is in its fight (intro over), before the second part
    private const float PhaseWaitMax = 40f;

    /// <summary>F7 in a Mastery match (host): straight to the main boss fight. Every enemy but the main boss dies,
    /// spawning stops (lords still queued included), the lords objective goes away, the cycle is set to the last one
    /// and every pillar but one is put away; the game's own UpdateAll then breaks that pillar, and its
    /// DestroyPillarRoutine -> AdvanceCycle -> GoToNextCycleRoutine reaches ActivateSpecialBossRoutine (the main boss
    /// wakes up: BossSpawnProcessor.ActiveSpecialBoss gives it back its normal invulnerability time). Guests do the
    /// same on their side when MasteryState says so (flags bit 1). Versions before: ran or skipped cycle by cycle, and
    /// the next cycle's rune spawned lords again (3rd test: lords kept coming).
    /// Then, once the boss is in its fight (MasteryBossComponent.CurrentState = Intermediate, its intro done) and
    /// PhaseDelay later: the second part of the fight. The boss's phases hang on HealthComponent.HealthThresholds:
    /// UnitUtil.ChangeEntityHealth stops its health at the next threshold's Min and sets ThresholdChanged, and
    /// MasteryBossComponent.DoOnUpdate then starts that phase's PhaseTransition (the portal). One big hit does exactly
    /// that (4th test: F8's first hit stopped at 26.27 M of 43.78 M, a threshold).</summary>
    internal static void DebugSkipToFinal()
    {
        var mm = Manager;
        if (mm == null) return;
        if (_jumpedToFinal) { Plugin.StaticLog.LogInfo("[SoulstoneCoop][mastery] F7: already jumped to the main boss (F8 ends the run with the victory)."); return; }
        if (mm.MainObjective == null) { Plugin.StaticLog.LogWarning("[SoulstoneCoop][mastery] F7: the main boss isn't up yet."); return; }
        _jumpedToFinal = true;
        GameManagerUtil.StopSpawning();
        var bsp = mm.BossSpawnDetails;
        if (bsp != null)
        {
            // 4th test: lords already queued kept coming (MasterySpawnBosses fills HighPrioritySpawnQueue, spawned
            // later by ProcessBossSpawnQueue); zeroing _pendingBosses alone didn't stop them.
            bsp.HighPrioritySpawnQueue?.Clear();
            bsp._pendingBosses = 0;
            bsp._hasPendingBosses = false;
        }
        int killed = KillAllEnemies(mm);
        _cleanupUntil = Time.unscaledTime + CleanupWindow; // one already on its way (async spawn job) dies too
        _phaseWaitSince = Time.unscaledTime; // then: the second part of the fight (DebugTick)
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] F7: spawning stopped, {killed} enemies killed.");
        JumpToFinal(mm, host: true);
    }

    /// <summary>Both sides: the last cycle, one pillar left to break (the game breaks it next frame and wakes the main boss).</summary>
    private static void JumpToFinal(MasteryGameManager mm, bool host)
    {
        int last = mm.TotalCyclesAmount - 1;
        int from = mm.CurrentMasteryCycle;
        mm.ProgressObjectives?.Clear();
        var spawner = mm.MasteryBossSpawner;
        if (spawner != null && !spawner._triggered)
        {
            spawner._triggered = true; // an armed rune would spawn lords again
            var col = spawner.TriggerCollider;
            if (col != null) col.enabled = false;
        }
        MasteryPillar? keep = null;
        int hidden = 0;
        var pl = mm.CreatedMasteryPillarsReference;
        if (pl != null)
            for (int i = 0; i < pl.Count; i++)
            {
                var p = pl[i];
                if (p == null || p.Destroyed) continue;
                if (keep == null) { keep = p; continue; }
                p.Destroyed = true;
                p.gameObject.SetActive(false);
                hidden++;
            }
        if (keep != null) keep.ObjectiveAmountNeededToDestroy = 0f;
        mm._destroyingMasteryPillar = false;
        // 4th test: ActivateSpecialBossRoutine threw on a null DisruptThePillarsObjective (both sides): the game makes it
        // at the end of the first rune's SpawnBossRoutine (cycle 0), which the jump can cut short. Same objective as the
        // game's (SimpleObjectiveWithProgression("MasteryDisruptRitualObjective", 0, pillars), string literal 7315).
        // Without it the routine stopped before the boss intro, its tracker and IgnoredByEntitySearchQueries = false
        // (skills didn't aim at the boss).
        if (mm.DisruptThePillarsObjective == null)
        {
            mm.DisruptThePillarsObjective = new SimpleObjectiveWithProgression("MasteryDisruptRitualObjective", 0, pl != null ? pl.Count : 4);
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][mastery] Jump: the pillars objective wasn't made yet; made it.");
        }
        mm.CurrentMasteryCycle = last;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] {(host ? "F7" : "Guest: the host's F7")}: cycle {from} -> {last}/{mm.TotalCyclesAmount}, " +
                                 $"{hidden} pillar(s) put away, {(keep != null ? "the last one breaks now" : "NO pillar left to break")}; the main boss wakes up after it.");
    }

    private static int KillAllEnemies(MasteryGameManager mm)
    {
        var main = mm._mainBossEntity;
        int killed = 0;
        var all = UnityEngine.Object.FindObjectsOfType<Entity>();
        for (int i = 0; i < all.Length; i++)
        {
            var e = all[i];
            if (e == null || (main != null && e.Pointer == main.Pointer) || !HostEnemySync.IsTrackable(e)) continue;
            var h = e._health;
            if (h == null || h.HasBeenKilled) continue;
            for (int k = 0; k < 12 && !h.HasBeenKilled; k++) Kill(e);
            if (h.HasBeenKilled) killed++;
        }
        return killed;
    }

    /// <summary>F8 in a Mastery match (host): kill the main boss (its invulnerability off first); the game's own
    /// victory follows. If it's still standing 1 s later, its objective is completed instead (as F10).</summary>
    internal static void DebugKillMainBoss()
    {
        var mm = Manager;
        if (mm == null) return;
        var boss = mm._mainBossEntity;
        var h = boss != null ? boss._health : null;
        if (h == null || h.HasBeenKilled)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][mastery] F8: no living main boss; completing its objective.");
            DebugVictory();
            return;
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] F8: main boss hp={h.CurrentHealth:F0} invulnerable={h.InvulnerabilityTime} " +
                                 $"tutorialInvulnerable={h.TutorialInvulnerability} ignoreDeathState={h.IgnoreDeathState}.");
        h.TutorialInvulnerability = false;
        h.IgnoreDeathState = false;
        // One hit only took ~40% of 43.7 M (2nd test): the damage is scaled down. Hit until it drops (bounded).
        for (int i = 0; i < 12 && !h.HasBeenKilled; i++) Kill(boss!);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] F8: after the kill hp={h.CurrentHealth:F0} killed={h.HasBeenKilled}.");
        _killAt = Time.unscaledTime;
    }

    /// <summary>F7, after the jump: wait for the boss's fight to start, then hit it down to its next health threshold.</summary>
    private static void PhaseTick(MasteryGameManager mm)
    {
        var boss = mm._mainBossEntity;
        var h = boss != null ? boss._health : null;
        if (boss == null || h == null || h.HasBeenKilled) { _phaseWaitSince = -1f; return; }
        float now = Time.unscaledTime;
        if (_phaseReadyAt < 0f)
        {
            var ai = boss.GetComponentInChildren(Il2CppType.Of<MasteryBossComponent>(), true)?.TryCast<MasteryBossComponent>();
            int state = ai != null ? (int)ai.CurrentState : -1;
            bool fighting = state == 1; // Phases.Intermediate
            bool late = now - _phaseWaitSince > PhaseWaitMax;
            if (!fighting && !late) return;
            _phaseReadyAt = now + (fighting ? PhaseDelay : 0f);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] F7: main boss {(fighting ? "in its fight" : $"still in state {state} after {PhaseWaitMax:F0} s")}; the second part in {(fighting ? PhaseDelay : 0f):F0} s.");
            return;
        }
        if (now < _phaseReadyAt) return;
        _phaseWaitSince = -1f;
        _phaseReadyAt = -1f;
        var th = h.HealthThresholds;
        int idx = h.CurrentThresholdIndex;
        string next = th != null && idx >= 0 && idx < th.Count ? $"{th[idx].Min:F0}" : "none";
        float before = h.CurrentHealth;
        Kill(boss);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] F7: second part: boss hp {before:F0} -> {h.CurrentHealth:F0} (threshold #{idx} at {next}), thresholdChanged={h.ThresholdChanged}.");
    }

    /// <summary>F7 after the jump: lords (boss entities other than the main boss) that still show up.</summary>
    private static int KillLords(MasteryGameManager mm)
    {
        var main = mm._mainBossEntity;
        int killed = 0;
        var seen = new HashSet<IntPtr>();
        void From(Il2CppSystem.Collections.Generic.List<Entity>? list)
        {
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var e = list[i];
                if (e == null || !seen.Add(e.Pointer) || (main != null && e.Pointer == main.Pointer)) continue;
                if (Kill(e)) killed++;
            }
        }
        From(mm.BossSpawnDetails?.BossEntities);
        From(GameManagerUtil.GameData?.BossEntities);
        return killed;
    }

    private static bool Kill(Entity e)
    {
        var h = e._health;
        if (h == null || h.HasBeenKilled) return false;
        h.InvulnerabilityTime = 0f;
        UnitUtil.ChangeEntityHealth(null!, e, -(h.CurrentHealth + 1e9f), false, out _, out _, out _);
        return true;
    }

    private static void DebugTick(MasteryGameManager mm, float dt)
    {
        if (_cleanupUntil > 0f)
        {
            if (Time.unscaledTime > _cleanupUntil)
            {
                // After the window: only lords (queued ones still came in the 5th test), never the main boss's own adds.
                if ((_cleanupTimer += dt) >= 1f)
                {
                    _cleanupTimer = 0f;
                    mm.BossSpawnDetails?.HighPrioritySpawnQueue?.Clear();
                    int l = KillLords(mm);
                    if (l > 0) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] F7: {l} late lord(s) killed.");
                }
            }
            else if ((_cleanupTimer += dt) >= 0.5f)
            {
                _cleanupTimer = 0f;
                var bsp = mm.BossSpawnDetails;
                bsp?.HighPrioritySpawnQueue?.Clear();
                int n = KillAllEnemies(mm);
                if (n > 0) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] F7: {n} late enemy(ies) killed.");
            }
        }
        if (_phaseWaitSince > 0f) PhaseTick(mm);
        if (_killAt <= 0f || Time.unscaledTime - _killAt <= 1f) return;
        _killAt = -1f;
        var boss = mm._mainBossEntity;
        var h = boss != null ? boss._health : null;
        if (h != null && !h.HasBeenKilled)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][mastery] F8: the main boss survived the kill; completing its objective.");
            DebugVictory();
        }
    }

    // ---------------- the main boss's phase transitions ----------------

    private static readonly List<(byte Kind, byte Index)> _pendingPhases = new();
    private static float _phaseCheckAt = -1f;
    private static PhaseTransition? _phaseWatched;
    private static string _phaseWatchedName = "";
    private const float PhaseCheckDelay = 12f;
    private static PhaseTransition? _holdFor;   // guest: the boss phase running here; the boss puppet waits for it
    private static float _holdUntil = -1f;
    private const float HoldMax = 25f;

    private static MasteryBossComponent? BossAi(Entity? boss)
    {
        if (boss == null) return null;
        var c = boss.GetComponentInChildren(Il2CppType.Of<MasteryBossComponent>(), true);
        return c != null ? c.TryCast<MasteryBossComponent>() : null;
    }

    private static PhaseTransition? TransitionOf(MasteryBossComponent ai, int kind, int index)
    {
        if (kind == 0) return ai.InitialPhase;
        if (kind == 2) return ai.FinalPhase;
        var arr = ai.IntermediatePhases;
        return arr != null && index >= 0 && index < arr.Length ? arr[index].Transition : null;
    }

    private static string Describe(PhaseTransition tr)
    {
        var g = tr.TryCast<ActionGroupPhaseTransition>();
        var acts = g?.Actions;
        if (acts == null) return tr.GetIl2CppType().Name;
        var names = new List<string>();
        for (int i = 0; i < acts.Length; i++) names.Add(acts[i] != null ? acts[i].GetIl2CppType().Name : "null");
        return $"{tr.GetIl2CppType().Name} [{string.Join(", ", names)}]";
    }

    /// <summary>Host, postfix of PhaseTransition.StartAction: one of the main boss's phases started; tell the guests.</summary>
    internal static void OnTransitionStarted(PhaseTransition tr)
    {
        if (!CoopRole.IsHostSide || tr == null) return;
        var mm = Manager;
        var ai = mm != null ? BossAi(mm._mainBossEntity) : null;
        if (ai == null) return;
        int kind = -1, index = 0;
        if (ai.InitialPhase != null && ai.InitialPhase.Pointer == tr.Pointer) kind = 0;
        else if (ai.FinalPhase != null && ai.FinalPhase.Pointer == tr.Pointer) kind = 2;
        else
        {
            var arr = ai.IntermediatePhases;
            if (arr != null)
                for (int i = 0; i < arr.Length; i++)
                {
                    var x = arr[i].Transition;
                    if (x != null && x.Pointer == tr.Pointer) { kind = 1; index = i; break; }
                }
        }
        if (kind < 0) return;
        WatchPhase(tr, kind == 0 ? "intro" : kind == 2 ? "final" : $"#{index}");
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Host: main boss phase {(kind == 0 ? "intro" : kind == 2 ? "final" : $"#{index}")} started: {Describe(tr)}.");
        if (!NetworkManager.HasPeer) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.MasteryBossPhase);
        w.Put((byte)kind);
        w.Put((byte)index);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _phasesSent++;
    }

    public static void OnBossPhase(NetDataReader r)
    {
        byte kind = r.GetByte(), index = r.GetByte();
        if (!CoopRole.IsClientSide || Manager == null) return;
        _pendingPhases.Add((kind, index));
    }

    /// <summary>A boss phase (intro, arena, death) is playing on this machine: casting stops on purpose.</summary>
    internal static bool BossCutsceneRunning
    {
        get
        {
            var tr = _phaseWatched;
            if (tr == null || Manager == null) return false;
            try { var s = tr.State.ToString(); return s == "Ongoing" || s == "Waiting"; }
            catch { return false; }
        }
    }

    /// <summary>Guest, ClientPuppets.DriveMovement: true = leave the main boss puppet where it is (no host position)
    /// while one of its phase transitions runs here. The transition's ChangeSceneAction teleports every player to
    /// (player - boss + arena offset): the boss must be where this transition put it (TeleportToSpawnPositionAction,
    /// its spawn point), as on the host. 8th test: the host's boss had already gone on to the arena by the time the
    /// guest's slower additive scene load finished, the puppet followed it, and the guest's teleport landed back in
    /// the cathedral (6th test: loads finished closer together, and it worked).</summary>
    internal static bool HoldBossPuppet(Entity e)
    {
        if (_holdFor == null || e == null) return false;
        var mm = Manager;
        var main = mm != null ? mm._mainBossEntity : null;
        if (main == null || main.Pointer != e.Pointer) return false;
        string state = "";
        try { state = _holdFor.State.ToString(); } catch { }
        if ((state == "Ongoing" || state == "Waiting") && Time.unscaledTime < _holdUntil) return true;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Guest: boss phase over here (state {state}); the boss puppet follows the host again.");
        _holdFor = null;
        return false;
    }

    private static void WatchPhase(PhaseTransition tr, string name)
    {
        _phaseWatched = tr;
        _phaseWatchedName = name;
        _phaseCheckAt = Time.unscaledTime + PhaseCheckDelay;
    }

    /// <summary>PhaseCheckDelay after a boss phase started: log how it went (both sides). Guest, after an intermediate
    /// phase (the arena): if we're still far from the host, the phase didn't take us there (7th test: the guest stayed
    /// in the cathedral, frozen, though the phase was played): go to the host and get moving again.</summary>
    private static void CheckPhaseOutcome()
    {
        _phaseCheckAt = -1f;
        var tr = _phaseWatched;
        string state = "?";
        try { state = tr != null ? tr.State.ToString() : "gone"; } catch { }
        var local = GameState.LocalPlayerEntity;
        Vector3? hostPos = null;
        foreach (var r in Lobby.CoopSession.Remotes)
            if (r.IsHost && r.Ghost != null) hostPos = r.Ghost.transform.position;
        float dist = local != null && hostPos.HasValue ? Vector3.Distance(local.transform.position, hostPos.Value) : -1f;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Boss phase {_phaseWatchedName} {PhaseCheckDelay:F0} s later: state={state}, " +
                                 $"me at {(local != null ? local.transform.position.ToString() : "-")}{(dist >= 0f ? $", {dist:F0} u from the host" : "")}.");
        if (!CoopRole.IsClientSide || !_phaseWatchedName.StartsWith("#") || local == null || !hostPos.HasValue || dist < 60f) return;
        if (Life.CoopLife.LocalDowned) return;
        try
        {
            var ch = local._character;
            if (ch == null) return;
            var to = hostPos.Value + new Vector3(1.5f, 0f, 1.5f);
            ch.Teleport(to);
            ch.SetMovementMode(MovementMode.Walking);
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][mastery] Guest: the boss phase {_phaseWatchedName} didn't bring us to the host's arena ({dist:F0} u away): moved next to the host.");
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][mastery] Guest: moving to the host: {ex.Message}"); }
    }

    private static void PlayPendingPhases(MasteryGameManager mm)
    {
        var boss = mm._mainBossEntity;
        var ai = BossAi(boss);
        if (ai == null) { LogOnce("phase-noai", "Guest: a boss phase came, but our main boss puppet has no MasteryBossComponent yet; waiting."); return; }
        foreach (var (kind, index) in _pendingPhases)
        {
            var tr = TransitionOf(ai, kind, index);
            string name = kind == 0 ? "intro" : kind == 2 ? "final" : $"#{index}";
            if (tr == null) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][mastery] Guest: main boss phase {name}: no such transition on our puppet."); continue; }
            try
            {
                // Set up only if the puppet's own MasteryBossComponent.Setup didn't (Entity.Setup at its spawn):
                // a new Setup would take the boss's current position as its spawn point (TeleportToSpawnPositionAction).
                var ce = tr.TryCast<CustomEntityActionGroupPhaseTransition>();
                var g = tr.TryCast<ActionGroupPhaseTransition>();
                bool setUp = ce != null ? ce._hasBeenSetup && ce.EntityContext != null : g != null && g._dataHolder != null;
                if (!setUp) { tr.Setup(boss!); Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Guest: boss phase {name} wasn't set up on the puppet; set it up now."); }
                tr.StartAction();
                _phasesPlayed++;
                WatchPhase(tr, name);
                if (kind != 2) { _holdFor = tr; _holdUntil = Time.unscaledTime + HoldMax; }
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] Guest: played the host's main boss phase {name}: {Describe(tr)}.");
            }
            catch (Exception ex)
            {
                Plugin.StaticLog.LogError($"[SoulstoneCoop][mastery] Guest: main boss phase {name}: {ex.Message}");
            }
        }
        _pendingPhases.Clear();
    }

    // ---------------- telemetry ----------------

    private static void Telemetry(MasteryGameManager mm, float dt)
    {
        _telemetryTimer += dt;
        if (_telemetryTimer < TelemetryInterval) return;
        _telemetryTimer = 0f;
        var spawner = mm.MasteryBossSpawner;
        int pillars = 0, broken = 0;
        var pl = mm.CreatedMasteryPillarsReference;
        if (pl != null)
            for (int i = 0; i < pl.Count; i++) { var x = pl[i]; if (x == null) continue; pillars++; if (x.Destroyed) broken++; }
        var main = mm.MainObjective?.TryCast<EliminateSpecificEntitiesObjective>();
        var lords = mm.ProgressObjectives;
        var lo = lords != null && lords.Count > 0 ? lords[0]?.TryCast<EliminateEnemiesNonCumulativeObjective>() : null;
        var stats = GameManagerUtil.GameStats;
        var boss = mm._mainBossEntity;
        string bossInfo = boss == null ? "none" : (ClientPuppets.IsPuppet(boss) ? "puppet" : "real");
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][mastery] {NetworkManager.Role}: cycle={mm.CurrentMasteryCycle}/{mm.TotalCyclesAmount} " +
            $"rune={(spawner == null ? "-" : spawner._triggered ? "triggered" : $"{spawner._currentTimer:F1}/{spawner.TimeToEnter:F1}")} triggers={_triggers} " +
            $"lords={(lo != null ? $"{lo.Current - lo.Starting}/{lo.Total}" : "-")} lordsKilled={mm._lordsKilled} bossesKilled={(stats != null ? stats.BossesKilled : -1)} " +
            $"main={(main != null ? $"{main.Current}/{main.Total}" : "-")} mainBoss={bossInfo} pillars={broken}/{pillars} " +
            $"host={(_hasHost ? $"c{_host.Cycle} t{_host.Triggers} boss{_host.MainBoss}" : "-")} pending={_mainBossPending} " +
            $"sent={_sent} recv={_recv} replayed={_replayed} assists={_assists} bossLinks={_mainBossLinks} forced={_forced} phases={_phasesSent}/{_phasesPlayed} errors={_errors}");
        _sent = _recv = 0;
    }
}

// ---------------- patches ----------------

/// <summary>Guest: the rune only goes off when the host's does (MasterySync replays it); our bar still fills and the host
/// hears that we're on it. Host: note our own player on it.</summary>
[HarmonyPatch(typeof(MasteryBossSpawner), nameof(MasteryBossSpawner.OnTriggerStay))]
public static class MasteryRuneStayPatch
{
    [HarmonyPrefix]
    public static bool Prefix(MasteryBossSpawner __instance, Collider __0)
    {
        try
        {
            if (CoopRole.IsHostSide) { MasterySync.NoteNativeStay(__0); return true; }
            if (CoopRole.IsClientSide) return MasterySync.GuestStay(__instance, __0);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] MasteryRuneStayPatch: {ex}");
            return true;
        }
    }
}

/// <summary>Guest: the main boss waits for the host's (MasterySync.MainBossTick runs it then).</summary>
[HarmonyPatch(typeof(MasteryGameManager), nameof(MasteryGameManager.SpawnMainBoss))]
public static class MasteryMainBossPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return MasterySync.AllowSpawnMainBoss(); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] MasteryMainBossPatch: {ex}");
            return true;
        }
    }
}

/// <summary>Guest: the main boss objective isn't counted from the puppet (MasterySync.AllowEliminateObjective).</summary>
[HarmonyPatch(typeof(ObjectivesPanelReferences), nameof(ObjectivesPanelReferences.RunEliminateSpecificEntitiesObjective))]
public static class MasteryMainObjectivePatch
{
    [HarmonyPrefix]
    public static bool Prefix(object __0)
    {
        try
        {
            if (!CoopRole.IsClientSide) return true;
            return __0 is not Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase o || MasterySync.AllowEliminateObjective(o.Pointer);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] MasteryMainObjectivePatch: {ex}");
            return true;
        }
    }
}

/// <summary>Home portal of the Mastery map: whoever enters takes the others along (as on the other maps).</summary>
[HarmonyPatch(typeof(MasteryGameManager), nameof(MasteryGameManager.OnPlayerEnterPortal))]
public static class MasteryHomePortalPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try { if (!Life.CoopLife.FromNetwork) Life.CoopLife.OnLocalPortalEntered(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] MasteryHomePortalPatch: {ex}"); }
    }
}

/// <summary>Log only: the Mastery steps as they happen on each machine (docs/mastery-notas.md).</summary>
[HarmonyPatch]
public static class MasteryStepLogPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        foreach (var n in new[] { "CastMasterySkillAndResetPlayer", "CreateLordsObjectivesAfterTrigger", "AdvanceCycle", "SpawnBoss", "SpawnMasteryPillar", "ActivateSpecialBossRoutine" })
        {
            var m = AccessTools.Method(typeof(MasteryGameManager), n);
            if (m != null) yield return m;
        }
    }

    [HarmonyPostfix]
    public static void Postfix(System.Reflection.MethodBase __originalMethod, MasteryGameManager __instance)
    {
        try
        {
            if (NetworkManager.Role == NetRole.None || __instance == null) return;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][mastery] {__originalMethod.Name} ({NetworkManager.Role}, cycle {__instance.CurrentMasteryCycle}/{__instance.TotalCyclesAmount}).");
        }
        catch { /* log only */ }
    }
}

/// <summary>Host: the main boss's phase transitions go to the guests (MasterySync.OnTransitionStarted).</summary>
[HarmonyPatch(typeof(PhaseTransition), nameof(PhaseTransition.StartAction))]
public static class MasteryBossPhasePatch
{
    [HarmonyPostfix]
    public static void Postfix(PhaseTransition __instance)
    {
        try { MasterySync.OnTransitionStarted(__instance); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] MasteryBossPhasePatch: {ex.Message}"); }
    }
}
