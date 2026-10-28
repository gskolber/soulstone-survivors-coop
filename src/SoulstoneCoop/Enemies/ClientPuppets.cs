using ECM2.Characters;
using HarmonyLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Phase 2, client side: the host's enemies as "puppets" - real enemy Entities created through the
/// game's own pipeline (pool + SetEntityBasedOnPrefabData + Entity.Setup, like SpawnEnemyEntity), so hit
/// detection, auto-aim and health bars work, but with the AI off and transform/HP driven by the host.
/// The client never kills a puppet itself: ChangeEntityHealth is clamped to keep it at >= 1 HP, and
/// death is only played when the host says Died. See docs/fase2-notas.md.
/// </summary>
public static class ClientPuppets
{
    private const int MaxCreatesPerFrame = 8;
    private const float TeleportError = 3f;     // u, horizontal
    private const float ArriveDistance = 0.15f;  // u: closer than this = stand still, face the host's yaw
    private const float LeadSeconds = 0.1f;      // MoveToLocation aims slightly ahead; the agent smooths it
    private const float MaxExtrapolation = 0.25f;
    private const float TelemetryInterval = 5f;

    public sealed class Puppet
    {
        public ushort NetId;
        public Entity Entity = null!;
        public IntPtr Ptr;
        public ModifiedCharacter? Character;
        public bool UsePathFollowing;
        public string Key = "";
        public float HostMaxHealth;

        public ushort LastSeq;
        public bool HasSample;
        public Vector3 PrevPos, LatestPos;
        public float PrevTime, LatestTime;     // our clock: arrival, or the host's send time mapped (SenderClock)
        public double PrevHostT, LatestHostT;  // phase 8: the host's own clock, seconds
        public float Yaw;
        public float HealthPercent = 1f;
        public bool HealthDirty = true;

        public bool Dead;        // host said Died; playing the local death, local dissolve recycles it
        public bool Despawning;  // we called Recycle ourselves

        // Phase 4D
        public bool IsBoss;
        public bool BossRegistered; // in GameManagerUtil.BossEntities (the boss health bar): must be deregistered by us
        public IntPtr StatsPtr;
    }

    private static readonly Dictionary<IntPtr, Puppet> ByStatsPtr = new();
    private static readonly Dictionary<ushort, float> RetryUntil = new();
    private const float SummonResolveWindow = 3f; // s: a sum: key may arrive before its owner is a puppet

    internal static void ResetMatchContext() => RetryUntil.Clear();

    /// <summary>Phase 4D effect blocks: is this the stats component of a puppet (pushback from its replayed casts)?</summary>
    internal static bool IsPuppetStats(StatsComponent? s) => s != null && ByStatsPtr.ContainsKey(s.Pointer);

    /// <summary>Boss puppets still alive (ObjectiveSync waits for them before catching up the next objective).</summary>
    internal static int LiveBossCount
    {
        get { int n = 0; foreach (var p in ByNetId.Values) if (p.IsBoss && !p.Dead && p.Entity != null) n++; return n; }
    }

    internal static Puppet? TryGetLive(ushort netId) =>
        ByNetId.TryGetValue(netId, out var p) && !p.Dead && p.Entity != null ? p : null;

    private static readonly Dictionary<ushort, Puppet> ByNetId = new();
    private static readonly Dictionary<IntPtr, Puppet> ByPtr = new();
    private static readonly List<EnemySpawnMsg> Pending = new();
    private static readonly Dictionary<string, int> Unresolved = new();
    private static readonly HashSet<string> LoggedKeys = new();
    private static bool _hooked;

    /// <summary>Set while we apply the host's death ourselves, so the HP clamp lets it through.</summary>
    internal static bool ApplyingNetworkDeath;

    // Telemetry window
    private static float _telemetryTimer;
    private static int _snapshotsRecv, _slicesRecv, _snapshotBytes, _unknownNetIds, _teleports, _localDeaths, _clampedHits;
    private static double _errSum;
    private static int _errCount;
    private static float _errMax;
    private static ushort _lastSnapshotSeqSeen;

    // Phase 3a: damage dealt to puppets, summed per netId and flushed to the host at 15 Hz.
    private const float DamageFlushInterval = 1f / 15f;
    private const int MaxDamageEntriesPerPacket = 200; // 1 + 1 + 200 * 6 B
    private static readonly Dictionary<ushort, float> DamageOut = new();
    private static float _damageFlushTimer;
    private static int _dmgHits, _dmgPackets, _dmgSkippedEnemyAttacker;
    private static double _dmgSum;

    public static int AliveCount
    {
        get { int n = 0; foreach (var p in ByNetId.Values) if (!p.Dead) n++; return n; }
    }

    internal static bool IsPuppet(Entity e) => ByPtr.ContainsKey(e.Pointer);

    internal static bool TryGetNetId(Entity e, out ushort netId)
    {
        netId = 0;
        if (e == null || !ByPtr.TryGetValue(e.Pointer, out var p) || p.Dead) return false;
        netId = p.NetId;
        return true;
    }

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerDisconnected += _ =>
        {
            if (CoopRole.IsClientSide) DespawnAll("peer disconnected");
        };
    }

    /// <summary>The client connects (GameplayUIManager.Setup) before its own StartMatch runs, so spawns can
    /// arrive first. They wait in Pending until the local match has started, so puppets get the match's
    /// scaling. Cleared whenever there's no game manager (menu between runs).</summary>
    private static bool _matchStarted;
    internal static bool MatchStarted => _matchStarted;

    /// <summary>Live or dying (still not recycled) puppet entity.</summary>
    internal static Entity? TryGetPuppetEntity(ushort netId) =>
        netId != 0 && ByNetId.TryGetValue(netId, out var p) && p.Entity != null ? p.Entity : null;

    internal static Entity? TryGetLivePuppet(ushort netId) =>
        ByNetId.TryGetValue(netId, out var p) && !p.Dead && p.Entity != null ? p.Entity : null;

    public static void OnMatchStarted(string mapId)
    {
        if (!CoopRole.IsClientSide) return;
        _matchStarted = true;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][puppets] StartMatch({mapId}): creating puppets now ({Pending.Count} pending, {ByNetId.Count} live).");
    }

    /// <summary>Phase 6, client: the host started a new run. Drop everything from the previous one (the scene and
    /// its puppets are already gone), so stale spawns waiting in Pending never reach the new match.</summary>
    internal static void ResetForNewRun()
    {
        DespawnAll("new run");
        _matchStarted = false;
        Unresolved.Clear();
    }

    /// <summary>The local scene's game manager was just destroyed (GameManagerUtil.UnsetGameManager). A direct
    /// scene-to-scene change (Endless/BossRush portal, Restart) may never give the tick a frame without one, so
    /// spawns arriving from now on wait for the next StartMatch here too.</summary>
    internal static void OnLocalSceneGone() => _matchStarted = false;

    /// <summary>Client: the host's old scene is gone (Lobby.PortalSync). Everything it sent from that scene is
    /// stale. If our next match is already up, puppets from the host's new scene can be created right away.</summary>
    internal static void DropStaleFromHostScene(bool newMatchUp)
    {
        DespawnAll("host left its scene");
        Unresolved.Clear();
        if (!newMatchUp) _matchStarted = false;
    }

    /// <summary>Phase 8: the host's snapshot clock on ours ([Coop] SenderClock).</summary>
    private static readonly RemoteClock SnapshotClock = new();

    private static void DespawnAll(string reason)
    {
        SnapshotClock.Reset();
        EnemyCasts.ForgetAll();
        int n = 0;
        foreach (var p in ByNetId.Values.ToArray())
        {
            if (p.Dead) continue;
            DespawnPuppet(p);
            n++;
        }
        ByNetId.Clear();
        ByPtr.Clear();
        ByStatsPtr.Clear();
        Pending.Clear();
        RetryUntil.Clear();
        DamageOut.Clear();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][puppets] Despawned {n} puppets ({reason}).");
    }

    // ---------------- messages ----------------

    public static void OnSpawn(NetDataReader reader)
    {
        var msg = EnemySpawnMsg.Read(reader);
        if (!CoopRole.IsClientSide) return;
        if (ByNetId.TryGetValue(msg.NetId, out var existing))
        {
            // Resync after a reconnect, or a netId reused after wrap: replace.
            if (existing.Key == msg.Key && !existing.Dead) return;
            RemoveTracking(existing);
            if (!existing.Dead) DespawnPuppet(existing);
        }
        Pending.RemoveAll(m => m.NetId == msg.NetId);
        Pending.Add(msg);
    }

    public static void OnDied(NetDataReader reader)
    {
        ushort netId = reader.GetUShort();
        if (!CoopRole.IsClientSide) return;
        if (Pending.RemoveAll(m => m.NetId == netId) > 0) return;
        if (!ByNetId.TryGetValue(netId, out var p) || p.Dead) return;
        KillPuppet(p);
    }

    public static void OnDespawn(NetDataReader reader)
    {
        ushort netId = reader.GetUShort();
        if (!CoopRole.IsClientSide) return;
        if (Pending.RemoveAll(m => m.NetId == netId) > 0) return;
        if (!ByNetId.TryGetValue(netId, out var p)) return;
        RemoveTracking(p);
        // A dying puppet is recycled by its own dissolve; recycling it now would cut the death short.
        if (!p.Dead) DespawnPuppet(p);
    }

    public static void OnSnapshot(NetDataReader reader, int packetBytes)
    {
        ushort seq = reader.GetUShort();
        uint hostMs = reader.GetUInt();
        var basePos = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        int count = reader.GetByte();
        if (!CoopRole.IsClientSide) return;

        _slicesRecv++;
        _snapshotBytes += packetBytes;
        if (seq != _lastSnapshotSeqSeen) { _snapshotsRecv++; _lastSnapshotSeqSeen = seq; }

        float arrival = Time.unscaledTime;
        // Phase 8: with SenderClock the sample is placed when the host took it, not when it got here (jitter).
        float now = Plugin.SenderClock.Value ? (float)SnapshotClock.ToLocal(hostMs, arrival) : arrival;
        double hostT = hostMs / 1000.0;
        for (int i = 0; i < count; i++)
        {
            EnemySnapshotFormat.ReadEntry(reader, basePos, out ushort netId, out var pos, out float yaw, out float hp);
            if (!ByNetId.TryGetValue(netId, out var p))
            {
                bool pending = false;
                for (int k = 0; k < Pending.Count; k++) if (Pending[k].NetId == netId) { pending = true; break; }
                if (!pending) _unknownNetIds++;
                continue;
            }
            if (p.HasSample && !SeqNewer(seq, p.LastSeq)) continue;
            p.LastSeq = seq;

            if (p.HasSample) { p.PrevPos = p.LatestPos; p.PrevTime = p.LatestTime; p.PrevHostT = p.LatestHostT; }
            else { p.PrevPos = pos; p.PrevTime = now; p.PrevHostT = hostT; p.HasSample = true; }
            p.LatestPos = pos;
            p.LatestTime = now;
            p.LatestHostT = hostT;
            p.Yaw = yaw;
            if (Mathf.Abs(hp - p.HealthPercent) > 0.0001f) { p.HealthPercent = hp; p.HealthDirty = true; }
        }
    }

    private static bool SeqNewer(ushort a, ushort b) => (short)(a - b) > 0;

    // ---------------- per frame ----------------

    public static void Tick(float unscaledDeltaTime)
    {
        if (!CoopRole.IsClientSide) return;

        ProcessPending();

        _damageFlushTimer += unscaledDeltaTime;
        if (_damageFlushTimer >= DamageFlushInterval)
        {
            _damageFlushTimer = 0f;
            FlushDamage();
        }

        bool paused = Time.timeScale < 0.0001f;
        float now = Time.unscaledTime;

        foreach (var p in ByNetId.Values.ToArray())
        {
            try
            {
                var e = p.Entity;
                if (e == null || !e.gameObject.activeInHierarchy)
                {
                    ReleaseBoss(p);
                    RemoveTracking(p);
                    continue;
                }
                if (p.Dead) continue;

                ApplyHealth(p);
                if (!p.HasSample || paused) continue;
                DriveMovement(p, now);
            }
            catch (Exception ex)
            {
                Plugin.StaticLog.LogError($"[SoulstoneCoop][puppets] Tick puppet #{p.NetId} ({p.Key}): {ex.Message}");
                RemoveTracking(p);
            }
        }

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer >= TelemetryInterval)
        {
            float secs = _telemetryTimer;
            _telemetryTimer = 0f;
            if (ByNetId.Count > 0 || Pending.Count > 0 || _slicesRecv > 0)
            {
                string unresolved = Unresolved.Count == 0 ? "-" : string.Join(",", Unresolved.Select(kv => $"{kv.Key}x{kv.Value}"));
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][puppets] alive={AliveCount} dying={ByNetId.Count - AliveCount} pending={Pending.Count} " +
                    $"snapshots={_snapshotsRecv / secs:F1}/s bytesPerSnapshot={(_snapshotsRecv > 0 ? _snapshotBytes / _snapshotsRecv : 0)} " +
                    $"avgErr={(_errCount > 0 ? _errSum / _errCount : 0):F2}u maxErr={_errMax:F2}u teleports={_teleports} " +
                    $"unknownNetIds={_unknownNetIds} clampedHits={_clampedHits} localDeaths={_localDeaths} killCredits={_killCredits} unresolved={unresolved} outsideLists={EnemyPrefabKeys.OutsideListsResolved}");
                if (_bossesCreated + _bossesReleased + _bossErrors + _maxHealthMatched > 0 || RetryUntil.Count > 0)
                    Plugin.StaticLog.LogInfo(
                        $"[SoulstoneCoop][puppets] bosses created={_bossesCreated} released={_bossesReleased} errors={_bossErrors} " +
                        $"bossBars={GameManagerUtil.BossEntities?.Count ?? -1} summonRetries={RetryUntil.Count} maxHealthMatched={_maxHealthMatched}");
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][damage] Client: hits={_dmgHits} damage={_dmgSum:F0} packets={_dmgPackets} skippedEnemyAttacker={_dmgSkippedEnemyAttacker}");
            }
            _snapshotsRecv = _slicesRecv = _snapshotBytes = _unknownNetIds = _teleports = _clampedHits = _killCredits = 0;
            _dmgHits = _dmgPackets = _dmgSkippedEnemyAttacker = 0;
            _dmgSum = 0;
            _errSum = 0; _errCount = 0; _errMax = 0f;
        }
    }

    private static void ApplyHealth(Puppet p)
    {
        if (!p.HealthDirty) return;
        var health = p.Entity._health;
        var stats = p.Entity._stats;
        if (health == null || stats == null || health.HasBeenKilled) return;
        float max = stats.GetMaxHealth();
        // Never write 0: only the host's Died message kills a puppet.
        health.CurrentHealth = Mathf.Max(1f, p.HealthPercent * max);
        p.HealthDirty = false;
    }

    private static void DriveMovement(Puppet p, float now)
    {
        var tr = p.Entity.transform;
        Vector3 actual = tr.position;

        Vector3 vel = Vector3.zero;
        // Phase 8: with SenderClock the velocity uses the host's own spacing between the two samples; arrival
        // times bunch up with jitter (two samples in one frame = no velocity, a late one = half of it).
        float dt = Plugin.SenderClock.Value ? (float)(p.LatestHostT - p.PrevHostT) : p.LatestTime - p.PrevTime;
        if (dt > 0.001f && dt < 0.5f) vel = (p.LatestPos - p.PrevPos) / dt;

        float age = Mathf.Clamp(now - p.LatestTime, 0f, MaxExtrapolation);
        Vector3 hostNow = p.LatestPos + vel * age;
        Vector3 target = p.LatestPos + vel * Mathf.Clamp(age + LeadSeconds, 0f, MaxExtrapolation + LeadSeconds);

        float err = Horizontal(actual - hostNow);
        _errSum += err; _errCount++;
        if (err > _errMax) _errMax = err;

        var ch = p.Character;
        if (ch == null) { tr.position = hostNow; return; }

        if (err > TeleportError)
        {
            ch.Teleport(hostNow);
            _teleports++;
            return;
        }

        if (Horizontal(target - actual) > ArriveDistance)
        {
            if (p.UsePathFollowing) ch.MoveToLocation(target);
            else ch.SetPosition(Vector3.MoveTowards(actual, target, Mathf.Max(vel.magnitude, 1f) * Time.deltaTime * 1.5f));
        }
        else
        {
            if (p.UsePathFollowing) ch.StopMovement();
            float rad = p.Yaw * Mathf.Deg2Rad;
            ch.RotateTowards(new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)), true, true);
        }
    }

    private static float Horizontal(Vector3 v) => Mathf.Sqrt(v.x * v.x + v.z * v.z);

    // ---------------- create / kill / despawn ----------------

    private static void ProcessPending()
    {
        if (!GameManagerUtil.HasGameManager) { _matchStarted = false; return; }
        if (Pending.Count == 0) return;
        if (!_matchStarted || GameState.LocalPlayerEntity == null || !EnemyPrefabKeys.ListsReady()) return;

        int created = 0;
        int budget = Pending.Count; // retried summons go back to the end: visit each entry at most once per frame
        while (Pending.Count > 0 && created < MaxCreatesPerFrame && budget-- > 0)
        {
            var msg = Pending[0];
            Pending.RemoveAt(0);
            if (TryCreatePuppet(msg)) created++;
        }
    }

    private static bool TryCreatePuppet(EnemySpawnMsg msg)
    {
        EnemyPrefabKeys.Resolved? res;
        try { res = EnemyPrefabKeys.ClientResolve(msg.Key, msg); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][puppets] Resolving '{msg.Key}': {ex.Message}");
            res = null;
        }
        if (res == null && msg.Key.StartsWith(EnemyPrefabKeys.SumPrefix, StringComparison.Ordinal))
        {
            // The owner (boss) may still be in the queue, or its prefab not loaded yet: try again for a while.
            float now = Time.unscaledTime;
            if (!RetryUntil.TryGetValue(msg.NetId, out float until)) RetryUntil[msg.NetId] = until = now + SummonResolveWindow;
            if (now < until) { Pending.Add(msg); return false; }
        }
        RetryUntil.Remove(msg.NetId);
        if (res == null)
        {
            Unresolved.TryGetValue(msg.Key, out int n);
            Unresolved[msg.Key] = n + 1;
            if (n == 0) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] Unresolved prefab key '{msg.Key}' (netId {msg.NetId}) - skipped.");
            return false;
        }

        Entity? inst = null;
        ClientSpawnBlock.NetworkSpawnInProgress = true;
        try
        {
            var rot = Quaternion.Euler(0f, msg.Yaw, 0f);
            ResourcesUtil.GetPrefabInstance<Entity>(res.Prefab, msg.Position, rot, out inst);
            if (inst == null) throw new Exception("GetPrefabInstance returned null");

            bool fromTierLists = res.PrefabData != null && msg.Tier >= 0;
            if (fromTierLists)
            {
                var dpt = res.PrefabData!.DataPerTier;
                if (dpt != null && msg.Tier < dpt.Length)
                {
                    var data = new EntityByEnemyPrefabData { Tier = msg.Tier, Prefab = res.Prefab, PrefabData = res.PrefabData };
                    UnitUtil.SetEntityBasedOnPrefabData(inst, data);
                }
                UnitUtil.ApplyGenericEnemyScaling(inst, msg.IsElite, true, true, true);
                UnitUtil.ApplyGenericGameModeEnemyScaling(inst);
            }
            else
            {
                // Phase 4D: the recipe of the special spawners (SpawnAffixSpecialEntities, SpawnVoidHunter,
                // SpawnBossRoutine): stats straight from the prefab, multiplicative stats reset.
                inst.CacheReferences();
                var stats = inst._stats;
                var prefabStats = res.Prefab._stats;
                if (stats?.Stats != null && prefabStats?.Stats != null) stats.Stats.CopyStats(prefabStats.Stats);
                if (stats?.MultiplicativeStats != null) stats.MultiplicativeStats.SetToOne();
                if (msg.HasBoss) SetupBossBeforeSetup(inst, msg);
            }

            // Before Setup: RegisterEntity reads these.
            inst.CanBeRecycledByDistance = false;
            inst.IgnoredByUnitLimit = true;

            inst.Setup();
            DisableAI(inst);
            if (!fromTierLists) MatchHostMaxHealth(inst, msg);

            var p = new Puppet
            {
                NetId = msg.NetId,
                Entity = inst,
                Ptr = inst.Pointer,
                Key = msg.Key,
                HostMaxHealth = msg.MaxHealth,
                Character = inst._character,
                HealthPercent = msg.HealthPercent,
                Yaw = msg.Yaw,
            };
            p.UsePathFollowing = p.Character != null && p.Character._hasPathFollowingBehaviour;
            p.StatsPtr = inst._stats != null ? inst._stats.Pointer : IntPtr.Zero;
            ByNetId[p.NetId] = p;
            ByPtr[p.Ptr] = p;
            if (p.StatsPtr != IntPtr.Zero) ByStatsPtr[p.StatsPtr] = p;
            ApplyHealth(p);

            var hType = inst._health != null ? inst._health.GameStatsHealthType : GameStatsHealthType.None;
            p.IsBoss = hType == GameStatsHealthType.Boss;
            if (p.IsBoss) SetupBossAfterSetup(p, msg);
            else RequestHealthBar(inst, msg.IsElite || hType == GameStatsHealthType.Elite); // bosses only get the big bar, like SpawnBossRoutine

            if (LoggedKeys.Add(msg.Key))
            {
                float localMax = inst._stats != null ? inst._stats.GetMaxHealth() : 0f;
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][puppets] First '{msg.Key}' -> {inst.gameObject.name} tier={msg.Tier} elite={msg.IsElite} type={hType} " +
                    $"boss={p.IsBoss} bossBar={p.BossRegistered} prefabData={(res.PrefabData != null)} pathFollowing={p.UsePathFollowing} " +
                    $"hostMaxHP={msg.MaxHealth:F0} localMaxHP={localMax:F0}");
            }
            return true;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][puppets] Creating puppet #{msg.NetId} '{msg.Key}': {ex}");
            if (inst != null)
            {
                try { inst.Recycle(); } catch { /* best effort */ }
            }
            return false;
        }
        finally
        {
            ClientSpawnBlock.NetworkSpawnInProgress = false;
        }
    }

    /// <summary>Specials skip the tier-list scaling and their spawners differ (the affix applies the generic enemy
    /// scaling, void hunters don't...): instead of copying each recipe, scale the multiplicative max health so the
    /// local max equals the host's. HP% already comes from the snapshot; this keeps the numbers equal too
    /// (first test: VoidPresence 4.25M on the host, 1M here).</summary>
    private static void MatchHostMaxHealth(Entity inst, EnemySpawnMsg msg)
    {
        try
        {
            var stats = inst._stats;
            var mult = stats != null ? stats.MultiplicativeStats : null;
            if (mult == null || msg.MaxHealth <= 0f) return;
            float local = stats!.GetMaxHealth();
            if (local <= 0f || Mathf.Abs(local - msg.MaxHealth) <= msg.MaxHealth * 0.01f) return;
            mult.MaxHealth *= msg.MaxHealth / local;
            _maxHealthMatched++;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] Matching max health of #{msg.NetId}: {ex.Message}");
        }
    }

    private static int _maxHealthMatched;

    // ---------------- phase 4D: bosses ----------------

    private static int _bossesCreated, _bossesReleased, _bossErrors;

    /// <summary>What SpawnBossRoutine (0x813BC0) does between CopyStats and Entity.Setup: the AI's SetupWithTier
    /// (stats, loot and the skill set of that tier) and the HealthMultiplier on MultiplicativeStats.MaxHealth.</summary>
    private static void SetupBossBeforeSetup(Entity inst, EnemySpawnMsg msg)
    {
        try
        {
            if (msg.BossTier >= 0)
            {
                var ai = inst._basicAI;
                var plague = ai != null ? ai.TryCast<PlaguebringerComponent>() : null;
                var mastery = ai != null ? ai.TryCast<MasteryBossComponent>() : null;
                if (plague != null) plague.SetupWithTier(inst, msg.BossTier, msg.BossSkillsTier, msg.BossLootTier);
                else if (mastery != null) mastery.SetupWithTier(inst, msg.BossTier, msg.BossSkillsTier, msg.BossLootTier);
            }
            var mult = inst._stats != null ? inst._stats.MultiplicativeStats : null;
            if (mult != null && msg.BossMultMaxHealth > 0f) mult.MaxHealth = msg.BossMultMaxHealth;
        }
        catch (Exception ex)
        {
            _bossErrors++;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] Boss setup before Setup (#{msg.NetId} '{msg.Key}'): {ex.Message}");
        }
    }

    /// <summary>The boss health bar: PlaguebringerComponent.Setup (inside Entity.Setup) already registered it; the
    /// Mastery boss doesn't, so register if missing. The game deregisters it from the AI's DoOnUpdate, which a puppet
    /// never runs: ReleaseBoss does it. The host's pending-boss counter is mirrored so the client's own
    /// BossSpawnProcessor.OnUpdate creates the next progress objective and plays the boss music when they die.
    /// Also the "Eliminate &lt;boss&gt;" objective, as SpawnBossRoutine adds it (not to MainObjective: the run end
    /// stays with the host, phase 4A RunEnd).</summary>
    private static void SetupBossAfterSetup(Puppet p, EnemySpawnMsg msg)
    {
        var inst = p.Entity;
        try
        {
            var list = GameManagerUtil.BossEntities;
            if (list != null && !list.Contains(inst)) GameManagerUtil.RegisterBossEntity(inst);
            list = GameManagerUtil.BossEntities;
            p.BossRegistered = list != null && list.Contains(inst);
            _bossesCreated++;

            var bsp = GameManagerUtil.GameData?.BossSpawnDetails;
            if (bsp != null && msg.HasBoss)
            {
                bsp._pendingBosses = Math.Max(bsp._pendingBosses, (int)msg.HostPendingBosses);
                if (msg.HostHasPendingBosses) bsp._hasPendingBosses = true;
            }

            var sgm = bsp?.SurvivorsGameManager;
            var objectives = sgm != null ? sgm.EliminationObjectives : null;
            if (objectives != null)
            {
                var obj = new EliminateSpecificEntitiesObjective("Eliminate" + inst.EntityName + "Objective", 1);
                obj.EntitiesToEliminate?.Add(inst);
                objectives.Add(obj);
            }
        }
        catch (Exception ex)
        {
            _bossErrors++;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] Boss setup after Setup (#{p.NetId} '{p.Key}'): {ex.Message}");
        }
    }

    /// <summary>Take the boss bar down (GameManagerUtil.DeregisterBossEntity), once, on death, despawn or recycle.</summary>
    private static void ReleaseBoss(Puppet p)
    {
        if (!p.IsBoss || !p.BossRegistered) return;
        p.BossRegistered = false;
        try
        {
            var e = p.Entity;
            if (e == null) return;
            var ai = e._basicAI;
            var plague = ai != null ? ai.TryCast<PlaguebringerComponent>() : null;
            if (plague != null) plague.HealthBarRegistered = false;
            var mastery = ai != null ? ai.TryCast<MasteryBossComponent>() : null;
            if (mastery != null) mastery.BossRegistered = false;
            var list = GameManagerUtil.BossEntities;
            if (list != null && list.Contains(e)) GameManagerUtil.DeregisterBossEntity(e);
            _bossesReleased++;
        }
        catch (Exception ex)
        {
            _bossErrors++;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] Releasing boss #{p.NetId}: {ex.Message}");
        }
    }

    private static void DisableAI(Entity e)
    {
        // OnDisable removes them from ComponentList.Instances, so UpdateAll skips them. Disabling doesn't stop
        // coroutines, hence StopRefreshingTarget (fase0 patch #12). PlayerSeekerAIComponent derives from BasicAI.
        var ai = e.GetComponent<BasicAIComponent>();
        if (ai != null)
        {
            ai.StopRefreshingTarget();
            ai.enabled = false;
        }
        var staticAi = e.GetComponent<StaticAIComponent>();
        if (staticAi != null) staticAi.enabled = false;
    }

    private static void RequestHealthBar(Entity e, bool isElite)
    {
        try
        {
            var team = e._team != null ? e._team.Team : default;
            OverheadHealthBarPanelManager.RequestHealthBar(
                UnitUtil.GetTargetTransform(e, PlaceholderType.Overhead), Vector3.zero,
                e.Health, e.AbnormalStatus, e.BuffStatus, e.Ragdoll, e.Stunnable, team, isElite);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] RequestHealthBar failed: {ex.Message}");
        }
    }

    /// <summary>Plays the death through the game's own funnel (sounds, ragdoll, death anim, dissolve,
    /// then the game recycles it). Phase 4B: the AttackerList is kept - our local hits registered the local
    /// player (and, through Entity.Summoner, its summons) - and yieldLoot=true, so the game itself runs the
    /// on-kill procs (SkillTree/Runes/PassiveSkills) and the class resources (Rage, SoulHarvest...) for whoever
    /// hit it here. The puppet's own ProcessLoot is blocked (LootSyncProcessLootPatch): the loot comes from the
    /// host's LootDrop.</summary>
    private static void KillPuppet(Puppet p)
    {
        p.Dead = true;
        var e = p.Entity;
        var health = e != null ? e._health : null;
        if (health == null) { RemoveTracking(p); return; }
        try
        {
            if (HasLocalAttacker(health)) _killCredits++;
            health.InvulnerabilityTime = 0f;
            ApplyingNetworkDeath = true;
            UnitUtil.ChangeEntityHealth(null!, e, -(health.CurrentHealth + 1f), true, out _, out _, out _);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][puppets] KillPuppet #{p.NetId}: {ex.Message}");
        }
        finally
        {
            ApplyingNetworkDeath = false;
        }
        ReleaseBoss(p);

        if (!health.HasBeenKilled)
        {
            // Shield/immunity ate it: just remove the puppet.
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] #{p.NetId} survived the network death - recycling instead.");
            RemoveTracking(p);
            DespawnPuppet(p);
        }
    }

    private static int _killCredits, _killCreditErrors;

    /// <summary>Telemetry only: did the local player (or something it summoned) register on this puppet?</summary>
    private static bool HasLocalAttacker(HealthComponent health)
    {
        try
        {
            var list = health.AttackerList;
            var local = GameState.LocalPlayerEntity;
            if (list == null || local == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i].Entity;
                if (a != null && a.Pointer == local.Pointer) return true;
            }
        }
        catch (Exception ex)
        {
            if (_killCreditErrors++ == 0) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] Reading AttackerList: {ex.Message}");
        }
        return false;
    }

    private static void DespawnPuppet(Puppet p)
    {
        var e = p.Entity;
        if (e == null) return;
        ReleaseBoss(p);
        try
        {
            p.Despawning = true;
            // Same as the game's distance recycler: Entity.Recycle marks it killed+instant-dissolve, and
            // HealthComponent.UpdateAll returns it to the pool.
            e.Recycle();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][puppets] Despawn #{p.NetId}: {ex.Message}");
        }
    }

    private static void RemoveTracking(Puppet p)
    {
        if (ByNetId.TryGetValue(p.NetId, out var cur) && cur == p) ByNetId.Remove(p.NetId);
        ByPtr.Remove(p.Ptr);
        if (p.StatsPtr != IntPtr.Zero && ByStatsPtr.TryGetValue(p.StatsPtr, out var sp) && sp == p) ByStatsPtr.Remove(p.StatsPtr);
        EnemyCasts.ForgetPuppet(p.Ptr);
    }

    /// <summary>Entity.Recycle prefix: the pooled instance may come back as a different puppet, so drop it now.</summary>
    internal static void OnEntityRecycle(Entity e)
    {
        if (!ByPtr.TryGetValue(e.Pointer, out var p)) return;
        if (!p.Dead && !p.Despawning)
        {
            _localDeaths++;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][puppets] #{p.NetId} ({p.Key}) recycled locally without the host saying so.");
        }
        ReleaseBoss(p);
        RemoveTracking(p);
    }

    internal static void CountClampedHit() => _clampedHits++;

    /// <summary>ChangeEntityHealth prefix, BEFORE the clamp: the full (already calculated) damage goes to the host.
    /// Hits from enemy-team attackers (puppets hitting each other, the map's God-Entity) are skipped: the host
    /// simulates those on its own enemies already.</summary>
    internal static void RecordDamage(Entity? attacker, Entity target, float healthChange)
    {
        if (healthChange >= 0f || !ByPtr.TryGetValue(target.Pointer, out var p) || p.Dead) return;
        if (attacker != null && ClientSpawnBlock.IsEnemyTeam(attacker)) { _dmgSkippedEnemyAttacker++; return; }
        DamageOut.TryGetValue(p.NetId, out float sum);
        DamageOut[p.NetId] = sum - healthChange;
        _dmgHits++;
        _dmgSum -= healthChange;
    }

    /// <summary>Layout: type u8 | count u8 | count * (netId u16 | damage f32). Damage is positive.</summary>
    private static void FlushDamage()
    {
        if (DamageOut.Count == 0) return;
        if (!NetworkManager.HasPeer) { DamageOut.Clear(); return; }

        var entries = DamageOut.ToArray();
        DamageOut.Clear();
        for (int start = 0; start < entries.Length; start += MaxDamageEntriesPerPacket)
        {
            int count = Math.Min(MaxDamageEntriesPerPacket, entries.Length - start);
            var w = new NetDataWriter();
            w.Put((byte)PacketType.ClientDamage);
            w.Put((byte)count);
            for (int k = 0; k < count; k++)
            {
                w.Put(entries[start + k].Key);
                w.Put(entries[start + k].Value);
            }
            NetworkManager.Send(w, LiteNetLib.DeliveryMethod.ReliableUnordered);
            _dmgPackets++;
        }
    }
}

// ---------------- Harmony patches ----------------

[HarmonyPatch(typeof(Entity), nameof(Entity.Recycle))]
public static class EnemySyncEntityRecyclePatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity __instance)
    {
        try
        {
            if (__instance == null) return;
            if (CoopRole.IsHostSide) HostEnemySync.OnEntityRecycle(__instance);
            else if (CoopRole.IsClientSide) ClientPuppets.OnEntityRecycle(__instance);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] EnemySyncEntityRecyclePatch: {ex}");
        }
    }
}

/// <summary>Client: a puppet can't die from local damage. The full damage is forwarded to the host (phase 3a),
/// which kills its real enemy and sends Died back. Locally the hit still runs - damage number, flash, sound -
/// but HP stays >= 1.</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.ChangeEntityHealth))]
public static class ClientPuppetHealthClampPatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity attackerEntity, Entity targetEntity, ref float healthChange)
    {
        try
        {
            if (healthChange >= 0f || ClientPuppets.ApplyingNetworkDeath || !CoopRole.IsClientSide) return;
            if (targetEntity == null || !ClientPuppets.IsPuppet(targetEntity)) return;
            if (Skills.RemoteCaster.IsCaster(attackerEntity)) return; // visual-only replay, never sent as damage
            ClientPuppets.RecordDamage(attackerEntity, targetEntity, healthChange);
            var health = targetEntity._health;
            if (health == null) return;
            float floor = 1f - health.CurrentHealth; // most negative change that still leaves 1 HP
            if (healthChange < floor)
            {
                healthChange = Mathf.Min(0f, floor);
                ClientPuppets.CountClampedHit();
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientPuppetHealthClampPatch: {ex}");
        }
    }
}
