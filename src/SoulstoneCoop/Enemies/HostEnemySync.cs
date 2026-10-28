using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Phase 2, host side: gives every enemy-team Entity a netId when the game sets it up, tells the
/// client about spawns / deaths / despawns, and streams a position+HP snapshot at 15 Hz.
/// Entity.Id is NOT used: it's per pooled instance and differs between machines.
/// See docs/fase0-mapa-mundo.md (patches #5 and #6) and docs/fase2-notas.md.
/// </summary>
public static class HostEnemySync
{
    private const float SnapshotInterval = 1f / 15f;
    private const float TelemetryInterval = 5f;

    public sealed class Tracked
    {
        public ushort NetId;
        public Entity Entity = null!;
        public IntPtr Ptr;
        public string Key = "";
        public sbyte Tier = -1;
        public bool IsElite;
        public bool DiedSent;
        // Phase 4D
        public bool IsBoss;
        public bool ReplayCasts; // bosses, elites and specials: their casts go to the client (EnemyCasts)
        public sbyte BossTier = -1, BossSkillsTier = -1, BossLootTier = -1;
        public float BossMultMaxHealth;
        public sbyte HostPendingBosses;
        public bool HostHasPendingBosses;
        // Issue #6: the tier list's EnemyPrefabData (SpawnEnemyEntity), so the client can rebuild it.
        public bool HasPrefabType;
        public int PrefabType;
        public bool DataIsElite, DataIsEndless;
    }

    // Phase 4D: captured right before Entity.Setup by the patches at the end of this file.
    internal static readonly Dictionary<IntPtr, (Entity Caster, Entity? Prefab)> SummonCtx = new();
    internal static readonly Dictionary<IntPtr, (int Tier, int Skills, int Loot)> BossTierCtx = new();

    internal static void ResetMatchContext()
    {
        SummonCtx.Clear();
        BossTierCtx.Clear();
    }

    internal static bool TryGetTracked(IntPtr entityPtr, out Tracked t) => ByPtr.TryGetValue(entityPtr, out t!);

    private static readonly Dictionary<IntPtr, Tracked> ByPtr = new();
    private static readonly List<Tracked> All = new();
    private static readonly Dictionary<ushort, Tracked> ByNetId = new();
    private static ushort _nextNetId = 1;
    private static ushort _snapshotSeq;
    private static float _snapshotTimer;
    private static float _telemetryTimer;
    private static bool _hooked;

    // Spawn context captured from EnemyPrefabsUtil.SpawnEnemyEntity (Entity.Setup runs inside it).
    internal static bool CtxActive;
    internal static Entity? CtxPrefab;
    internal static sbyte CtxTier = -1;
    internal static bool CtxElite;
    internal static bool CtxHasType;
    internal static int CtxType;
    internal static bool CtxDataElite, CtxDataEndless;

    // Telemetry window
    private static int _snapshotsSent, _slicesSent, _snapshotBytes, _spawnsSent, _diedSent, _despawnsSent;
    private static int _maxTracked;

    public static int TrackedCount => All.Count;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerConnected += OnPeerConnected;
    }

    internal static bool IsTrackable(Entity e)
    {
        if (e == null || e.IsPlayer() || !ClientSpawnBlock.IsEnemyTeam(e)) return false;
        // The map's God-Entity (Creeps team, at the origin) casts environment skills: scene object, not an enemy.
        if (e.EntityName == "EntityNameGod" || e == GameManagerUtil.GameData?.GodEntity) return false;
        return true;
    }

    private static readonly HashSet<string> LoggedUntracked = new();

    /// <summary>Phase 4D: minerals / healing crystals (and maybe pillars) are not on the enemy team, so IsTrackable
    /// skips them (first test: the F7 mineral never reached the client). Track a non-allied, non-player entity when its
    /// prefab lives under Resources/Minerals or Resources/Pillars; log every other untracked name once.</summary>
    private static bool IsNeutralSpecial(Entity e)
    {
        if (e == null || e.IsPlayer()) return false;
        var team = e._team;
        if (team == null || ((int)team.Team & 3) != 0) return false; // Players | Summons
        if (e.EntityName == "EntityNameGod" || e == GameManagerUtil.GameData?.GodEntity) return false;
        string key = EnemyPrefabKeys.HostKeyFor(e, null);
        bool ok = key.StartsWith(EnemyPrefabKeys.ResPrefix + "Minerals/", StringComparison.OrdinalIgnoreCase)
               || key.StartsWith(EnemyPrefabKeys.ResPrefix + "Pillars/", StringComparison.OrdinalIgnoreCase);
        string name = EnemyPrefabKeys.StripClone(e.gameObject.name);
        if (LoggedUntracked.Add(name))
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][enemies] Host: non-enemy-team entity '{name}' team={team.Team} key={key} -> {(ok ? "tracked" : "not tracked")}");
        return ok;
    }

    /// <param name="realSetup">Entity.Setup's body only runs when HasBeenSetUp was false (Entity.Recycle clears
    /// it). A real Setup on an instance we still track means it came back from the pool without our Recycle
    /// prefix seeing it: treat it as a despawn + new spawn, so the client never keeps the old prefab.</param>
    internal static void OnEntitySetup(Entity e, bool realSetup = true)
    {
        if (ByPtr.TryGetValue(e.Pointer, out var existing))
        {
            if (!realSetup) { CtxActive = false; return; } // second Setup call on a live instance: no-op in the game too
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][enemies] Host: #{existing.NetId} ({existing.Key}) set up again without Recycle - respawning.");
            Untrack(existing, sendDespawn: true);
        }
        if (!IsTrackable(e) && !IsNeutralSpecial(e)) return;

        Entity? prefab = null;
        sbyte tier = -1;
        bool elite = false, hasType = false;
        if (CtxActive)
        {
            prefab = CtxPrefab;
            tier = CtxTier;
            elite = CtxElite;
            hasType = CtxHasType;
            CtxActive = false; // one Setup per SpawnEnemyEntity
        }

        // Phase 4D: a summon (UnitUtil.SetupSummonStats ran just before this Setup) knows its prefab and owner.
        ushort ownerNetId = 0;
        if (SummonCtx.TryGetValue(e.Pointer, out var sum))
        {
            SummonCtx.Remove(e.Pointer);
            if (prefab == null) prefab = sum.Prefab;
            if (sum.Caster != null && ByPtr.TryGetValue(sum.Caster.Pointer, out var owner)) ownerNetId = owner.NetId;
        }

        var t = new Tracked
        {
            NetId = AllocNetId(),
            Entity = e,
            Ptr = e.Pointer,
            Key = EnemyPrefabKeys.HostKeyFor(e, prefab, ownerNetId),
            Tier = tier,
            IsElite = elite,
            HasPrefabType = hasType,
            PrefabType = hasType ? CtxType : 0,
            DataIsElite = hasType && CtxDataElite,
            DataIsEndless = hasType && CtxDataEndless,
        };
        FillPhase4D(t);
        ByPtr[t.Ptr] = t;
        ByNetId[t.NetId] = t;
        All.Add(t);
        if (All.Count > _maxTracked) _maxTracked = All.Count;

        if (NetworkManager.HasPeer) SendSpawn(t);
    }

    private static readonly HashSet<string> LoggedSpecialKeys = new();

    /// <summary>Boss block (what SpawnBossRoutine did before Setup) and which enemies get their casts replayed.</summary>
    private static void FillPhase4D(Tracked t)
    {
        try
        {
            var e = t.Entity;
            var hType = e._health != null ? e._health.GameStatsHealthType : GameStatsHealthType.None;
            t.IsBoss = hType == GameStatsHealthType.Boss;
            bool special = !t.Key.Contains('/') || t.Key.StartsWith(EnemyPrefabKeys.ResPrefix, StringComparison.Ordinal)
                || t.Key.StartsWith(EnemyPrefabKeys.SumPrefix, StringComparison.Ordinal);
            t.ReplayCasts = t.IsBoss || t.IsElite || hType == GameStatsHealthType.Elite || special;
            if (t.IsBoss)
            {
                if (BossTierCtx.TryGetValue(e.Pointer, out var tiers))
                {
                    BossTierCtx.Remove(e.Pointer);
                    t.BossTier = (sbyte)Mathf.Clamp(tiers.Tier, -1, 127);
                    t.BossSkillsTier = (sbyte)Mathf.Clamp(tiers.Skills, -1, 127);
                    t.BossLootTier = (sbyte)Mathf.Clamp(tiers.Loot, -1, 127);
                }
                var mult = e._stats != null ? e._stats.MultiplicativeStats : null;
                t.BossMultMaxHealth = mult != null ? mult.MaxHealth : 0f;
                var bsp = GameManagerUtil.GameData?.BossSpawnDetails;
                if (bsp != null)
                {
                    t.HostPendingBosses = (sbyte)Mathf.Clamp(bsp._pendingBosses, -100, 100);
                    t.HostHasPendingBosses = bsp._hasPendingBosses;
                }
            }
            if ((t.IsBoss || special) && LoggedSpecialKeys.Add(t.Key))
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][enemies] Host: special '{t.Key}' ({e.gameObject.name}) boss={t.IsBoss} type={hType} " +
                    $"tiers={t.BossTier}/{t.BossSkillsTier}/{t.BossLootTier} multHP={t.BossMultMaxHealth:F2} pending={t.HostPendingBosses}/{t.HostHasPendingBosses}");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][enemies] Host: phase 4D info for #{t.NetId}: {ex.Message}");
        }
    }

    internal static void OnEntityRecycle(Entity e)
    {
        if (!ByPtr.TryGetValue(e.Pointer, out var t)) return;
        Untrack(t, sendDespawn: true);
    }

    private static void Untrack(Tracked t, bool sendDespawn)
    {
        ByPtr.Remove(t.Ptr);
        if (ByNetId.TryGetValue(t.NetId, out var cur) && cur == t) ByNetId.Remove(t.NetId);
        All.Remove(t);
        if (!sendDespawn || !NetworkManager.HasPeer) return;

        var w = new NetDataWriter();
        w.Put((byte)PacketType.EnemyDespawn);
        w.Put(t.NetId);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _despawnsSent++;
    }

    private static ushort AllocNetId()
    {
        // netIds wrap after 65535 spawns; skip any still in use.
        for (int guard = 0; guard < 65536; guard++)
        {
            ushort id = _nextNetId++;
            if (_nextNetId == 0) _nextNetId = 1;
            if (id == 0) continue;
            if (!ByNetId.ContainsKey(id)) return id;
        }
        return 1;
    }

    /// <param name="to">Phase 9: one client only (a resync for the player who just entered the run), NoSlot = everyone.</param>
    private static void SendSpawn(Tracked t, byte to = Net.Players.NoSlot)
    {
        var e = t.Entity;
        var health = e._health;
        float max = e._stats != null ? e._stats.GetMaxHealth() : 0f;
        float pct = health != null && max > 0f ? health.CurrentHealth / max : 1f;
        var tr = e.transform;
        var msg = new EnemySpawnMsg
        {
            NetId = t.NetId,
            Key = t.Key,
            Tier = t.Tier,
            IsElite = t.IsElite,
            Position = tr.position,
            Yaw = tr.eulerAngles.y,
            MaxHealth = max,
            HealthPercent = pct,
            HasBoss = t.IsBoss,
            BossTier = t.BossTier,
            BossSkillsTier = t.BossSkillsTier,
            BossLootTier = t.BossLootTier,
            BossMultMaxHealth = t.BossMultMaxHealth,
            HostPendingBosses = t.HostPendingBosses,
            HostHasPendingBosses = t.HostHasPendingBosses,
            HasPrefabType = t.HasPrefabType,
            PrefabType = t.PrefabType,
            DataIsElite = t.DataIsElite,
            DataIsEndless = t.DataIsEndless,
        };
        var w = new NetDataWriter();
        msg.Write(w);
        if (to != Net.Players.NoSlot) { NetworkManager.SendTo(to, w, DeliveryMethod.ReliableOrdered); _spawnsSent++; return; }
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _spawnsSent++;
    }

    /// <summary>Phase 6: the connection is made in the menu, so "the client entered my run" is now CoopSession's
    /// LobbyState, not PeerConnected. Same full resend.</summary>
    internal static void ResyncToPeer(byte slot, string why)
    {
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][enemies] Host: resync to slot {slot} ({why}).");
        OnPeerConnected(slot);
    }

    private static void OnPeerConnected(byte slot)
    {
        try
        {
            if (!CoopRole.IsHostSide) return;

            int sent = 0;
            foreach (var t in All)
            {
                if (t.DiedSent) continue;
                SendSpawn(t, slot);
                sent++;
            }

            // Enemies set up before we were hosting (or that the patch missed): track them now.
            // OnEntitySetup sends their Spawn itself, since the peer is connected.
            var reg = EntityRegistry.AllEntities;
            int adopted = 0;
            if (reg != null)
            {
                for (int i = 0; i < reg.Count; i++)
                {
                    var e = reg[i];
                    if (e == null || ByPtr.ContainsKey(e.Pointer) || !IsTrackable(e)) continue;
                    if (e._health != null && e._health.HasBeenKilled) continue;
                    CtxActive = false;
                    OnEntitySetup(e);
                    adopted++;
                }
            }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][enemies] Host: peer connected - resync sent {sent} spawns, adopted {adopted} untracked.");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostEnemySync.OnPeerConnected: {ex}");
        }
    }

    public static bool TryGet(ushort netId, out Entity entity)
    {
        entity = null!;
        if (!ByNetId.TryGetValue(netId, out var t) || t.Entity == null) return false;
        entity = t.Entity;
        return true;
    }

    internal static bool TryGetNetId(Entity e, out ushort netId)
    {
        netId = 0;
        if (e == null || !ByPtr.TryGetValue(e.Pointer, out var t)) return false;
        netId = t.NetId;
        return true;
    }

    /// <summary>Phase 4B: the tracked enemy that was just killed at this position (ProcessLoot gets the dying
    /// entity's transform position), or 0. Only used as a fallback key for the puppet's own item list.</summary>
    internal static ushort FindKilledAt(Vector3 pos)
    {
        for (int i = 0; i < All.Count; i++)
        {
            var t = All[i];
            var e = t.Entity;
            if (e == null || t.DiedSent || e._health == null || !e._health.HasBeenKilled) continue;
            if ((e.transform.position - pos).sqrMagnitude < 0.0001f) return t.NetId;
        }
        return 0;
    }

    private static int _clientDmgPackets, _clientDmgEntries, _clientDmgApplied, _clientKills, _clientDmgDropped, _clientDmgImmune;
    private static double _clientDmgSum;

    /// <summary>Phase 3a: damage the client's skills dealt to our enemies' puppets. Applied through the game's
    /// own funnel with attacker = null (no on-kill procs for the host) and yieldLoot = true, so the kill, the
    /// stats and the loot/XP happen here as usual; the death goes back to the client as EnemyDied.</summary>
    public static void OnClientDamage(NetDataReader reader)
    {
        int count = reader.GetByte();
        if (!CoopRole.IsHostSide) return;
        _clientDmgPackets++;
        for (int i = 0; i < count; i++)
        {
            ushort netId = reader.GetUShort();
            float damage = reader.GetFloat();
            _clientDmgEntries++;
            _clientDmgSum += damage;
            if (damage <= 0f || float.IsNaN(damage)) continue;
            try
            {
                if (!ByNetId.TryGetValue(netId, out var t) || t.DiedSent || t.Entity == null) { _clientDmgDropped++; continue; }
                var e = t.Entity;
                var health = e._health;
                if (health == null || health.HasBeenKilled || !e.gameObject.activeInHierarchy) { _clientDmgDropped++; continue; }

                UnitUtil.ChangeEntityHealth(null!, e, -damage, true, out _, out _, out bool immune);
                if (immune) _clientDmgImmune++;
                else _clientDmgApplied++;
                if (health.HasBeenKilled) _clientKills++;
            }
            catch (Exception ex)
            {
                Plugin.StaticLog.LogError($"[SoulstoneCoop][damage] Host: applying {damage:F1} to #{netId}: {ex.Message}");
            }
        }
    }

    /// <summary>Per frame (unscaled): death detection, stale-entry cleanup, snapshots, telemetry.</summary>
    public static void Tick(float unscaledDeltaTime)
    {
        if (!CoopRole.IsHostSide) return;

        // Death + stale detection every frame: cheap (tens of entities) and keeps Died timely.
        for (int i = All.Count - 1; i >= 0; i--)
        {
            var t = All[i];
            var e = t.Entity;
            if (e == null || !e.gameObject.activeInHierarchy)
            {
                // Destroyed/pooled without passing through Entity.Recycle.
                Untrack(t, sendDespawn: true);
                continue;
            }
            if (!t.DiedSent && e._health != null && e._health.HasBeenKilled)
            {
                t.DiedSent = true;
                if (NetworkManager.HasPeer)
                {
                    var w = new NetDataWriter();
                    w.Put((byte)PacketType.EnemyDied);
                    w.Put(t.NetId);
                    NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
                    _diedSent++;
                }
            }
        }

        _snapshotTimer += unscaledDeltaTime;
        if (_snapshotTimer >= SnapshotInterval)
        {
            _snapshotTimer = 0f;
            if (NetworkManager.HasPeer) SendSnapshot();
        }

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer >= TelemetryInterval)
        {
            float secs = _telemetryTimer;
            _telemetryTimer = 0f;
            if (NetworkManager.HasPeer || All.Count > 0)
            {
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][enemies] Host: tracked={All.Count} peak={_maxTracked} snapshots={_snapshotsSent / secs:F1}/s " +
                    $"bytesPerSnapshot={(_snapshotsSent > 0 ? _snapshotBytes / _snapshotsSent : 0)} slicesPerSnapshot={(_snapshotsSent > 0 ? (float)_slicesSent / _snapshotsSent : 0):F2} " +
                    $"kbps={_snapshotBytes / secs / 1024f:F1} spawns={_spawnsSent} died={_diedSent} despawns={_despawnsSent}");
                if (_clientDmgPackets > 0)
                    Plugin.StaticLog.LogInfo(
                        $"[SoulstoneCoop][damage] Host: from client packets={_clientDmgPackets} entries={_clientDmgEntries} damage={_clientDmgSum:F0} " +
                        $"applied={_clientDmgApplied} kills={_clientKills} unknownOrDead={_clientDmgDropped} immune={_clientDmgImmune}");
            }
            _snapshotsSent = _slicesSent = _snapshotBytes = _spawnsSent = _diedSent = _despawnsSent = 0;
            _clientDmgPackets = _clientDmgEntries = _clientDmgApplied = _clientKills = _clientDmgDropped = _clientDmgImmune = 0;
            _clientDmgSum = 0;
        }
    }

    private static readonly List<Tracked> SnapshotScratch = new();

    private static void SendSnapshot()
    {
        SnapshotScratch.Clear();
        for (int i = 0; i < All.Count; i++)
            if (!All[i].DiedSent) SnapshotScratch.Add(All[i]);
        if (SnapshotScratch.Count == 0) return;

        var local = GameState.LocalPlayerEntity;
        Vector3 basePos = local != null ? local.transform.position : Vector3.zero;
        ushort seq = ++_snapshotSeq;
        uint hostMs = RemoteClock.NowMs();
        int totalBytes = 0;

        for (int start = 0; start < SnapshotScratch.Count; start += EnemySnapshotFormat.MaxEntriesPerSlice)
        {
            int count = Math.Min(EnemySnapshotFormat.MaxEntriesPerSlice, SnapshotScratch.Count - start);
            var w = new NetDataWriter();
            EnemySnapshotFormat.WriteHeader(w, seq, hostMs, basePos, (byte)count);
            for (int k = 0; k < count; k++)
            {
                var t = SnapshotScratch[start + k];
                var e = t.Entity;
                var tr = e.transform;
                float max = e._stats != null ? e._stats.GetMaxHealth() : 0f;
                float pct = e._health != null && max > 0f ? e._health.CurrentHealth / max : 1f;
                EnemySnapshotFormat.WriteEntry(w, t.NetId, tr.position, basePos, tr.eulerAngles.y, pct);
            }
            NetworkManager.Send(w, DeliveryMethod.Unreliable);
            totalBytes += w.Length;
            _slicesSent++;
        }
        _snapshotsSent++;
        _snapshotBytes += totalBytes;
    }
}

// ---------------- Harmony patches (host side; every one wrapped in try/catch, see fase0 risk 6) ----------------

[HarmonyPatch(typeof(Entity), nameof(Entity.Setup))]
public static class HostEntitySetupPatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity __instance, out bool __state)
    {
        __state = true;
        try { if (__instance != null) __state = !__instance.HasBeenSetUp; }
        catch { /* keep default */ }
    }

    [HarmonyPostfix]
    public static void Postfix(Entity __instance, bool __state)
    {
        try
        {
            if (!CoopRole.IsHostSide || __instance == null) return;
            HostEnemySync.OnEntitySetup(__instance, __state);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostEntitySetupPatch: {ex}");
        }
    }
}

/// <summary>
/// Client order observed in-game: GameplayUIManager.Setup (we connect) -> Spawn messages -> StartMatch.
/// So StartMatch must NOT drop tracking (that orphaned the first puppets, frozen in place); stale entries
/// from a previous scene are dropped by the null/inactive checks in the ticks. It only clears caches and
/// lets the client start creating puppets.
/// </summary>
[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.StartMatch))]
public static class EnemySyncStartMatchPatch
{
    [HarmonyPostfix]
    public static void Postfix(string mapId)
    {
        try
        {
            EnemyPrefabKeys.Reset();
            HostEnemySync.CtxActive = false;
            HostEnemySync.ResetMatchContext();
            ClientPuppets.ResetMatchContext();
            ClientPuppets.OnMatchStarted(mapId);
            RemoteProxy.OnMatchStarted();
            Skills.RemoteCaster.OnMatchStarted();
            Life.CoopLife.OnMatchStarted();
            EnemyScaling.OnMatchStarted();
            Lobby.CoopSession.OnLocalMatchStarted();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] EnemySyncStartMatchPatch: {ex}");
        }
    }
}

/// <summary>Captures the prefab/tier/elite flag of an enemy spawned through the regular pipeline.
/// Applied manually (Plugin.Load) so a failure to patch this signature can't take the others down.</summary>
public static class HostSpawnEnemyEntityPatch
{
    public static void Prefix(EntityByEnemyPrefabData prefabData, bool isElite)
    {
        try
        {
            if (!CoopRole.IsHostSide) return;
            HostEnemySync.CtxActive = true;
            HostEnemySync.CtxPrefab = prefabData?.Prefab;
            HostEnemySync.CtxTier = prefabData != null ? (sbyte)Mathf.Clamp(prefabData.Tier, -1, 127) : (sbyte)-1;
            HostEnemySync.CtxElite = isElite;
            var data = prefabData?.PrefabData;
            HostEnemySync.CtxHasType = data != null;
            if (data != null)
            {
                HostEnemySync.CtxType = (int)data.Type;
                HostEnemySync.CtxDataElite = data.IsElite;
                HostEnemySync.CtxDataEndless = data.IsEndless;
            }
        }
        catch (Exception ex)
        {
            HostEnemySync.CtxActive = false;
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostSpawnEnemyEntityPatch.Prefix: {ex}");
        }
    }

    public static void Postfix()
    {
        HostEnemySync.CtxActive = false;
        HostEnemySync.CtxPrefab = null;
        HostEnemySync.CtxHasType = false;
    }
}

/// <summary>Phase 4D, host: every summon (SummonEntityEffect / SummonEntityPositionalEffect /
/// SummonEntityAndForgetPositionalEffect) goes through UnitUtil.SetupSummonStats(caster, summoned, originalPrefab, ...)
/// right before its Entity.Setup. Remember who summoned what, so the key can be res: or sum:&lt;owner&gt;:&lt;name&gt;.
/// Signature is trampoline-safe: only references and bools.</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.SetupSummonStats))]
public static class HostSummonCapturePatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity casterEntity, Entity summonedEntity, Entity originalSummonPrefab)
    {
        try
        {
            if (!CoopRole.IsHostSide || summonedEntity == null) return;
            HostEnemySync.SummonCtx[summonedEntity.Pointer] = (casterEntity, originalSummonPrefab);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostSummonCapturePatch: {ex}");
        }
    }
}

/// <summary>Phase 4D, host: the tiers SpawnBossRoutine (and the boss MapTasks) pass to the boss AI before Setup.</summary>
[HarmonyPatch(typeof(PlaguebringerComponent), nameof(PlaguebringerComponent.SetupWithTier))]
public static class HostPlaguebringerTierPatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity entity, int currentTier, int skillsTier, int lootTier)
    {
        try
        {
            if (CoopRole.IsHostSide && entity != null) HostEnemySync.BossTierCtx[entity.Pointer] = (currentTier, skillsTier, lootTier);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostPlaguebringerTierPatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(MasteryBossComponent), nameof(MasteryBossComponent.SetupWithTier))]
public static class HostMasteryBossTierPatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity entity, int currentTier, int skillsTier, int lootTier)
    {
        try
        {
            if (CoopRole.IsHostSide && entity != null) HostEnemySync.BossTierCtx[entity.Pointer] = (currentTier, skillsTier, lootTier);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostMasteryBossTierPatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(BossAIBasedOnCurseLevelComponent), nameof(BossAIBasedOnCurseLevelComponent.SetupWithTier))]
public static class HostCurseBossTierPatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity entity, int currentTier, int skillsTier, int lootTier)
    {
        try
        {
            if (CoopRole.IsHostSide && entity != null) HostEnemySync.BossTierCtx[entity.Pointer] = (currentTier, skillsTier, lootTier);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostCurseBossTierPatch: {ex}");
        }
    }
}
