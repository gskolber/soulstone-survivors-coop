using ECM2.Characters;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Phase 3b, host side: an invisible allied stand-in for a client's player, so the host's enemies chase and
/// attack the client. It's a "Creeps/Dummy" (the CheatManager.SpawnDummy recipe) with Team = Players - NOT a
/// player: no Inventory, so IsPlayer() stays false. It goes through the game's pipeline (pool + Entity.Setup),
/// is moved to the client's ghost every frame, has its renderers off (the ghost is the visual) and
/// never takes damage: hits on it are forwarded to that client as DamageToClient. See docs/fase3-notas.md.
/// Phase 9: one proxy per client in the run (slot 1-7). Spawns rotate around the living players, the recycler spares
/// what is near any living client, and seekers chase the nearest living player.
/// Host down with a client still up (docs/host-caido-notas.md): spawns all go around the living clients, seekers go to
/// them, and the enemies left around the host's body with no living player within the recycler's radius are recycled,
/// as the game's own recycler would do around the living players (HostDownSweep).
/// </summary>
public static class RemoteProxy
{
    private const string ProxyPrefabPath = "Creeps/Dummy";
    private const float RecycleRadiusSqr = 35f * 35f; // EntityRegistry.DefaultContinuousRecycler
    private const float TelemetryInterval = 5f;

    // ---------------- client state (ClientState packet) ----------------

    public struct RemotePlayerState
    {
        public bool Dead;   // down or dead
        public bool Killed; // phase 9: dead for real (surrendered, final death): never revived
        public float Health, MaxHealth, Armor, DamageReduction, BlockChance;
        // pendências: status resistance (StatsComponent.AbnormalStatusDampening / Factor). The game has no dodge stat
        // (only the dash dodge, a timing thing) and no elemental resistances: these are the remaining defensive numbers.
        public bool HasDampening;
        public float StatusDampening, StatusDampeningFactor;
    }

    /// <summary>One client's stand-in and what it last said about itself.</summary>
    private sealed class Proxy
    {
        public byte Slot;
        public Entity? Entity;
        public IntPtr Ptr;
        public ModifiedCharacter? Character;
        public bool DashApplied; // 1.6.1: our _isDashing is on this stand-in's character
        public RemotePlayerState? State;
        public bool AppliedDead;
        public float RetryTimer;
        public int CreateFailures;
        // Telemetry window
        public int Hits;
        public double Damage;

        /// <summary>Proxy exists and the client's player is alive: only then is it a target / spawn / recycle reference.</summary>
        public bool Targetable => Entity != null && State.HasValue && !State.Value.Dead;
    }

    private static readonly Dictionary<byte, Proxy> Proxies = new();
    private static readonly Dictionary<IntPtr, Proxy> ByPtr = new();
    private static Team _originalTeam;
    private static VfxReferences? _originalVfxOnSetup;
    private static bool _matchStarted;
    private static bool _hooked;

    // Telemetry window
    private static float _telemetryTimer;
    private static int _hostHits, _spawnRedirects, _spawnCalls, _recyclerProtected, _seekerRetargets;
    private static int _sweepRecycled, _spawnSkippedFar;
    private static double _hostDamage;
    private static float _maxHit;
    private static string _maxHitBy = "-";

    /// <summary>At least one proxy exists.</summary>
    public static bool Active => ByPtr.Count > 0;

    /// <summary>Phase 4B: that client's player exists on our side (proxy up) and isn't dead or down.</summary>
    internal static bool IsAlive(byte slot) => Proxies.TryGetValue(slot, out var p) && p.Targetable;

    /// <summary>Phase 4A: that client said it's down (or dead). ClientState.dead covers both.</summary>
    internal static bool IsDownOrDead(byte slot) => Proxies.TryGetValue(slot, out var p) && p.State.HasValue && p.State.Value.Dead;

    /// <summary>Phase 9: that client is dead for real (surrendered or a final death): out, and never revived.</summary>
    internal static bool IsKilled(byte slot) => Proxies.TryGetValue(slot, out var p) && p.State.HasValue && p.State.Value.Killed;

    /// <summary>Some client is alive on our side.</summary>
    internal static bool AnyAlive
    {
        get
        {
            foreach (var p in Proxies.Values) if (p.Targetable) return true;
            return false;
        }
    }

    /// <summary>The slots whose ClientState arrived (the clients in this run, as far as the host knows).</summary>
    internal static IEnumerable<byte> KnownSlots
    {
        get
        {
            foreach (var p in Proxies.Values) if (p.State.HasValue) yield return p.Slot;
        }
    }

    /// <summary>Phase 4A: the client sent at least one ClientState this run.</summary>
    internal static bool HasState(byte slot) => Proxies.TryGetValue(slot, out var p) && p.State.HasValue;

    internal static bool IsProxy(Entity? e) => e != null && ByPtr.ContainsKey(e.Pointer);

    internal static bool TryGetProxySlot(Entity? e, out byte slot)
    {
        if (e != null && ByPtr.TryGetValue(e.Pointer, out var p)) { slot = p.Slot; return true; }
        slot = Players.NoSlot;
        return false;
    }

    internal static Entity? ProxyOf(byte slot) => Proxies.TryGetValue(slot, out var p) ? p.Entity : null; // pendências: ProxyStatusForward

    private static Proxy ProxyFor(byte slot)
    {
        if (!Proxies.TryGetValue(slot, out var p)) Proxies[slot] = p = new Proxy { Slot = slot };
        return p;
    }

    /// <summary>Client -> host, every 0.5 s: alive/dead plus the defensive numbers the host's damage math reads
    /// from the TARGET (the proxy). Layout: type u8 | life u8 (0 alive, 1 down, 2 dead) | hp, maxHp, armor, damageReduction, block f32
    /// | [statusDampening, statusDampeningFactor f32] (optional tail, pendências).</summary>
    public static void OnClientState(NetDataReader r)
    {
        byte life = r.GetByte(); // 0 alive, 1 down, 2 dead for real (phase 9; was a bool)
        var s = new RemotePlayerState
        {
            Dead = life != 0,
            Killed = life == 2,
            Health = r.GetFloat(),
            MaxHealth = r.GetFloat(),
            Armor = r.GetFloat(),
            DamageReduction = r.GetFloat(),
            BlockChance = r.GetFloat(),
        };
        if (r.AvailableBytes >= 8)
        {
            s.HasDampening = true;
            s.StatusDampening = r.GetFloat();
            s.StatusDampeningFactor = r.GetFloat();
        }
        if (!CoopRole.IsHostSide || !Players.IsPresent(PacketRouter.From)) return;
        var p = ProxyFor(PacketRouter.From);
        bool changed = !p.State.HasValue || p.State.Value.Dead != s.Dead;
        p.State = s;
        if (changed) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][proxy] Client {p.Slot} player is now {(s.Dead ? "DEAD" : "alive")} (hp={s.Health:F0}/{s.MaxHealth:F0}).");
        try { if (p.Entity != null) ApplyRemoteState(p); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][proxy] ApplyRemoteState({p.Slot}): {ex}"); }
    }

    private static void ApplyRemoteState(Proxy p)
    {
        var e = p.Entity;
        if (e == null || !p.State.HasValue) return;
        var s = p.State.Value;

        var st = e._stats;
        if (st != null && st.Stats != null && s.MaxHealth > 0f)
        {
            st.Stats.ArmorModifier = s.Armor;
            st.Stats.DamageReduction = s.DamageReduction;
            st.Stats.BlockChance = s.BlockChance;
            if (s.HasDampening && !float.IsNaN(s.StatusDampening) && !float.IsNaN(s.StatusDampeningFactor))
            {
                // The proxy's statuses scale the DoT damage the host forwards (DamageToClient): resist like the client.
                st.AbnormalStatusDampening = s.StatusDampening;
                st.AbnormalStatusDampeningFactor = s.StatusDampeningFactor;
            }
            // Percent-of-max-HP skills read the target's max health (the Dummy has 100M -> 1.67M hits).
            float cur = st.GetMaxHealth();
            if (cur > 0f && Mathf.Abs(cur - s.MaxHealth) > 0.5f) st.Stats.MaxHealth *= s.MaxHealth / cur;
        }
        var h = e._health;
        if (h != null && !h.HasBeenKilled) h.CurrentHealth = Mathf.Max(1f, s.Health); // never 0: the proxy can't die

        if (s.Dead == p.AppliedDead) return;
        p.AppliedDead = s.Dead;
        // Dead client: out of every target search, no body to bump into, and enemies drop it as target.
        e.IgnoredByEntitySearchQueries = s.Dead;
        var col = e._physical != null ? e._physical.Collider : null;
        if (col != null) col.enabled = !s.Dead;
        if (s.Dead) DropAsTarget(p);
    }

    private static void DropAsTarget(Proxy p)
    {
        var all = EntityRegistry.AllEntities;
        if (all == null || p.Entity == null) return;
        int dropped = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var ai = all[i] != null ? all[i]._basicAI : null;
            if (ai == null) continue;
            var t = ai._entityToFollow;
            if (t == null || t.Pointer != p.Ptr) continue;
            ai.RemoveTarget(); // the next RefreshTarget picks the closest valid target again
            dropped++;
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][proxy] Client {p.Slot} dead: {dropped} enemies dropped its proxy as target.");
    }

    // ---------------- loot: the proxies must not look like players there ----------------

    private static bool _lootTeamSwapped;

    /// <summary>LootUtil.ProcessLoot walks AIUtil.GetAllEntitiesInSpecificTeam(Players) and reads each one's
    /// Inventory without a null check: the Inventory-less proxy made every kill throw there. So while loot is
    /// processed the proxies are in the Summons team (enemies target Allies = Players|Summons anyway).</summary>
    internal static void BeforeLoot()
    {
        if (_lootTeamSwapped || ByPtr.Count == 0) return;
        foreach (var p in Proxies.Values)
        {
            var team = p.Entity != null ? p.Entity._team : null;
            if (team != null) team.Team = Team.Summons;
        }
        _lootTeamSwapped = true;
    }

    internal static void AfterLoot()
    {
        if (!_lootTeamSwapped) return;
        _lootTeamSwapped = false;
        foreach (var p in Proxies.Values)
        {
            var team = p.Entity != null ? p.Entity._team : null;
            if (team != null) team.Team = Team.Players;
        }
    }

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        Players.Left += slot =>
        {
            if (!CoopRole.IsHostSide) return;
            RemoveProxy(slot, "player left");
            Proxies.Remove(slot);
        };
    }

    public static void OnMatchStarted()
    {
        if (!CoopRole.IsHostSide) return;
        // A new match: any proxy from the previous scene is gone with it. The clients resend their state within 0.5 s.
        Proxies.Clear();
        ByPtr.Clear();
        _matchStarted = true;
        _lootTeamSwapped = false;
    }

    /// <summary>A player's position: its ghost follows its PlayerState packets (RemotePlayer.TryGetPosition).</summary>
    internal static bool TryGetPosition(byte slot, out Vector3 pos, out Quaternion rot)
    {
        var r = CoopSession.Get(slot);
        if (r == null || !NetworkManager.HasPeer) { pos = default; rot = default; return false; }
        return r.TryGetPosition(out pos, out rot);
    }

    public static void Tick(float unscaledDeltaTime)
    {
        if (!CoopRole.IsHostSide) return;
        try
        {
            if (!GameManagerUtil.HasGameManager)
            {
                _matchStarted = false;
                foreach (var p in Proxies.Values) Forget(p);
                ByPtr.Clear();
            }

            foreach (var r in CoopSession.Remotes)
            {
                var p = ProxyFor(r.Slot);
                if (p.Ptr != IntPtr.Zero && (p.Entity == null || !p.Entity.gameObject.activeInHierarchy))
                {
                    Plugin.StaticLog.LogWarning($"[SoulstoneCoop][proxy] Proxy {p.Slot} went inactive/destroyed - will recreate.");
                    ByPtr.Remove(p.Ptr);
                    Forget(p);
                }

                bool haveRemote = r.TryGetPosition(out var pos, out var rot);
                if (p.Entity == null)
                {
                    p.RetryTimer -= unscaledDeltaTime;
                    if (haveRemote && _matchStarted && GameState.LocalPlayerEntity != null && p.RetryTimer <= 0f && p.CreateFailures < 5)
                    {
                        p.RetryTimer = 2f;
                        CreateProxy(p, pos, rot);
                    }
                }
                else if (haveRemote)
                {
                    MoveProxy(p, pos, rot);
                    ApplyDash(p);
                }
                else if (r.Ghost == null)
                {
                    // That player left the run (end screen, menu, surrender): no stand-in without a ghost to follow.
                    RemoveProxy(r.Slot, "player out of the run");
                }
            }
            AfterLoot(); // safety net if ProcessLoot threw past its postfix
            HostDownSweep(unscaledDeltaTime);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][proxy] Tick: {ex}");
        }

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer >= TelemetryInterval)
        {
            _telemetryTimer = 0f;
            if (NetworkManager.HasPeer)
            {
                var parts = new List<string>();
                foreach (var p in Proxies.Values)
                {
                    string where = p.Entity != null ? p.Entity.transform.position.ToString() : "-";
                    parts.Add($"{p.Slot}: active={p.Entity != null} pos={where} hits={p.Hits} dmg={p.Damage:F0} dead={(p.State.HasValue ? p.State.Value.Dead.ToString() : "?")}");
                    p.Hits = 0;
                    p.Damage = 0;
                }
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][proxy] Host: [{string.Join(" | ", parts)}] " +
                    $"hostHits={_hostHits} hostDmg={_hostDamage:F0} spawnRedirects={_spawnRedirects}/{_spawnCalls} " +
                    $"recyclerProtected={_recyclerProtected} seekerRetargets={_seekerRetargets} dashWindows={TakeDashWindows()} hostDown={Life.CoopLife.LocalDowned} " +
                    $"sweepRecycled={_sweepRecycled} spawnNotHostRef={_spawnSkippedFar} maxHit={_maxHit:F0} by {_maxHitBy}");
            }
            _hostHits = _spawnRedirects = _spawnCalls = _recyclerProtected = _seekerRetargets = 0;
            _sweepRecycled = _spawnSkippedFar = 0;
            _hostDamage = 0;
            _maxHit = 0f; _maxHitBy = "-";
        }
    }

    private static void Forget(Proxy p)
    {
        p.Entity = null;
        p.Ptr = IntPtr.Zero;
        p.Character = null;
        p.AppliedDead = false;
    }

    private static void CreateProxy(Proxy p, Vector3 pos, Quaternion rot)
    {
        Entity? inst = null;
        try
        {
            var prefab = ResourcesUtil.Load<Entity>(ProxyPrefabPath);
            if (prefab == null) throw new Exception($"ResourcesUtil.Load<Entity>(\"{ProxyPrefabPath}\") returned null");
            ResourcesUtil.GetPrefabInstance<Entity>(prefab, pos, rot, out inst);
            if (inst == null) throw new Exception("GetPrefabInstance returned null");

            // Same order as CheatManager.SpawnDummy (team, CacheReferences, stats, Setup), with Team = Players.
            var team = inst._team ?? inst.GetComponent<TeamComponent>();
            if (team == null) throw new Exception("Dummy has no TeamComponent");
            _originalTeam = team.Team;
            team.Team = Team.Players;
            inst.CacheReferences();
            var stats = inst._stats;
            if (stats != null && stats.Stats != null) stats.Stats.CopyStats(stats.Stats);
            if (stats != null && stats.MultiplicativeStats != null) stats.MultiplicativeStats.SetToOne();

            // Before Setup: RegisterEntity / HealthComponent.Setup read these.
            inst.CanBeRecycledByDistance = false;
            inst.IgnoredByUnitLimit = true;
            inst.IgnoredByEntitySearchQueries = false; // the enemies' target search must find it
            inst.HasSpawnSound = false;
            _originalVfxOnSetup = inst.VfxOnSetup;
            inst.VfxOnSetup = null!;
            var health = inst._health;
            if (health != null)
            {
                health.AvoidCreatingHealthBar = true;
                health.IgnoreMinimapTracking = true;
            }

            inst.Setup();

            // It's a stand-in, not an actor: no AI, no visuals (the ghost is the visual).
            var ai = inst.GetComponent<BasicAIComponent>();
            if (ai != null) { ai.StopRefreshingTarget(); ai.enabled = false; }
            var staticAi = inst.GetComponent<StaticAIComponent>();
            if (staticAi != null) staticAi.enabled = false;
            int renderers = 0;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true)) { r.enabled = false; renderers++; }

            p.Entity = inst;
            p.Ptr = inst.Pointer;
            p.Character = inst._character;
            ByPtr[p.Ptr] = p;
            var dst = stats != null ? stats.Stats : null;
            string dummyStats = dst != null
                ? $"maxHP={stats!.GetMaxHealth():F0} armor={dst.ArmorModifier} dr={dst.DamageReduction} block={dst.BlockChance}"
                : "no stats";
            p.AppliedDead = false;
            ApplyRemoteState(p);

            var comps = inst.GetComponents<Component>();
            var names = new List<string>();
            foreach (var c in comps) if (c != null) names.Add(c.GetIl2CppType().Name);
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][proxy] Created proxy {p.Slot} '{inst.gameObject.name}' at {pos}: team {_originalTeam}->{team.Team} isPlayer={inst.IsPlayer()} " +
                $"character={(p.Character != null)} collider={(inst._physical != null && inst._physical.Collider != null)} " +
                $"ai={(ai != null)} staticAi={(staticAi != null)} renderersOff={renderers} dummy[{dummyStats}] " +
                $"now[maxHP={(stats != null ? stats.GetMaxHealth() : -1):F0} hp={(health != null ? health.CurrentHealth : -1):F0} clientState={p.State.HasValue}] " +
                $"components=[{string.Join(",", names)}]");
        }
        catch (Exception ex)
        {
            p.CreateFailures++;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][proxy] CreateProxy {p.Slot} failed (#{p.CreateFailures}): {ex}");
            if (inst != null && p.Entity == null)
            {
                try { RestoreForPool(inst); inst.Recycle(); } catch { /* best effort */ }
            }
        }
    }

    // ---------------- 1.6.1: dash dodge (docs/pendencias-notas.md) ----------------
    // The game's dodge: Skill.GetSkillEffectModifierForTarget (0x5650B0) copies the target's ModifiedCharacter
    // _isDashing (+0x12C) into the result's IsDashDodged when the target Entity has a character (+0x260/+0x268), so a
    // dashing player isn't hit by skills (damage over time from statuses doesn't go through it). Enemy skills hit the
    // guest's stand-in here on the host, which never dashed: guests couldn't dodge. Now, while that guest's DashState
    // says it's dashing (applied on arrival, capped at MaxDashSeconds in case the stop is lost), the stand-in's
    // character reports dashing, with its remaining duration kept up so the game's own dash update doesn't stop it.

    private const float MaxDashSeconds = 0.6f;
    private static readonly Dictionary<byte, (bool On, float Since)> ClientDash = new();
    private static int _dashWindows, _dashErrors;

    /// <summary>Host: a client's DashState (PlayerSync), on arrival.</summary>
    internal static void OnClientDash(byte slot, bool on)
    {
        ClientDash[slot] = (on, Time.unscaledTime);
        if (on) _dashWindows++;
    }

    private static void ApplyDash(Proxy p)
    {
        var ch = p.Character;
        if (ch == null) return;
        bool want = ClientDash.TryGetValue(p.Slot, out var d) && d.On && Time.unscaledTime - d.Since < MaxDashSeconds
                    && !(p.State.HasValue && p.State.Value.Dead);
        try
        {
            if (want)
            {
                if (!ch._isDashing) ch._isDashing = true;
                if (ch._dashRemainingDuration < 0.1f) ch._dashRemainingDuration = 0.1f;
                p.DashApplied = true;
            }
            else if (p.DashApplied)
            {
                ch._isDashing = false;
                ch._dashRemainingDuration = 0f;
                p.DashApplied = false;
            }
        }
        catch (Exception ex)
        {
            if (_dashErrors++ < 3) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][proxy] Dash on stand-in {p.Slot}: {ex.Message}");
        }
    }

    /// <summary>Telemetry: dash windows received since the last call.</summary>
    internal static int TakeDashWindows() { int n = _dashWindows; _dashWindows = 0; return n; }

    private static void MoveProxy(Proxy p, Vector3 pos, Quaternion rot)
    {
        var tr = p.Entity!.transform;
        if (p.Character != null) p.Character.SetPosition(pos);
        else tr.position = pos;
        tr.rotation = rot;
    }

    private static void RemoveProxy(byte slot, string reason)
    {
        if (!Proxies.TryGetValue(slot, out var p)) return;
        var e = p.Entity;
        if (e != null)
        {
            ByPtr.Remove(p.Ptr);
            // Phase 8: enemies chasing the proxy would keep a pooled, inactive entity as target until their next
            // refresh (seekers forever: RetargetSeeker stops once the proxy is gone). Point them back at a player.
            try { ReleaseTargets(e); }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][proxy] Releasing proxy {slot}'s chasers: {ex.Message}"); }
        }
        Forget(p);
        p.State = null;
        if (e == null) return;
        try
        {
            RestoreForPool(e);
            e.Recycle();
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][proxy] Proxy {slot} recycled ({reason}).");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][proxy] RemoveProxy {slot}: {ex.Message}");
        }
    }

    /// <summary>Seekers (always after a player) go to the nearest living player like RetargetSeeker does; every other AI
    /// drops the target, as when the client dies (DropAsTarget), and its next RefreshTarget picks the closest one.</summary>
    private static void ReleaseTargets(Entity proxy)
    {
        var all = EntityRegistry.AllEntities;
        if (all == null) return;
        int seekers = 0, dropped = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var ai = all[i] != null ? all[i]._basicAI : null;
            if (ai == null) continue;
            var t = ai._entityToFollow;
            if (t == null || t.Pointer != proxy.Pointer) continue;
            var seeker = ai.TryCast<PlayerSeekerAIComponent>();
            var to = seeker != null && seeker._transform != null ? NearestLivingPlayer(seeker._transform.position, proxy.Pointer) : null;
            if (seeker != null && to != null)
            {
                Follow(seeker, to);
                seekers++;
            }
            else
            {
                ai.RemoveTarget();
                dropped++;
            }
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][proxy] Proxy going away: {seekers} seekers moved to another player, {dropped} enemies dropped it as target.");
    }

    /// <summary>The host's own player, if it's up (not downed, not dead).</summary>
    private static Entity? LivingHost()
    {
        var host = GameState.LocalPlayerEntity;
        if (host == null || Life.CoopLife.LocalDowned) return null;
        var h = host._health;
        return h != null && h.HasBeenKilled ? null : host;
    }

    /// <summary>Every living player entity: the host's player while it's up, then each living client's proxy
    /// (HostHazards: who the God's meteors go for).</summary>
    internal static void CollectLivingPlayers(List<Entity> into)
    {
        into.Clear();
        var host = LivingHost();
        if (host != null) into.Add(host);
        foreach (var p in Proxies.Values)
            if (p.Targetable && p.Entity != null) into.Add(p.Entity);
    }

    /// <summary>The nearest living player entity (host or a living client's proxy), skipping <paramref name="except"/>.
    /// Falls back to the host itself (even down) so a seeker never ends up without a target.</summary>
    private static Entity? NearestLivingPlayer(Vector3 from, IntPtr except)
    {
        Entity? best = null;
        float bestD = float.MaxValue;
        var host = LivingHost();
        if (host != null)
        {
            best = host;
            bestD = (host.transform.position - from).sqrMagnitude;
        }
        foreach (var p in Proxies.Values)
        {
            if (!p.Targetable || p.Ptr == except) continue;
            float d = (p.Entity!.transform.position - from).sqrMagnitude;
            if (d < bestD) { bestD = d; best = p.Entity; }
        }
        return best ?? GameState.LocalPlayerEntity;
    }

    private static void Follow(PlayerSeekerAIComponent ai, Entity want)
    {
        ai._entityToFollow = want;
        ai._hasEntityToFollow = true;
        ai._transformToFollow = want.transform;
        ai._hasTransformToFollow = true;
        var phys = want._physical;
        ai._entityToFollowRadius = phys != null ? phys.ColliderRadius : 0f;
    }

    /// <summary>The instance goes back to the Dummy pool: undo what would leak into a cheat-spawned dummy.</summary>
    private static void RestoreForPool(Entity e)
    {
        var team = e._team ?? e.GetComponent<TeamComponent>();
        if (team != null) team.Team = _originalTeam;
        if (_originalVfxOnSetup != null) e.VfxOnSetup = _originalVfxOnSetup;
        foreach (var r in e.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
    }

    // ---------------- patch bodies ----------------

    /// <summary>Patch #9: damage aimed at a proxy goes to its client; the proxy itself never changes.</summary>
    internal static void ForwardDamage(byte slot, Entity? attacker, float healthChange)
    {
        if (healthChange >= 0f || !NetworkManager.HasPeer) return;
        ushort attackerNetId = 0;
        if (attacker != null) HostEnemySync.TryGetNetId(attacker, out attackerNetId);
        var w = new NetDataWriter();
        w.Put((byte)PacketType.DamageToClient);
        w.Put(healthChange);
        w.Put(attackerNetId);
        NetworkManager.SendTo(slot, w, DeliveryMethod.ReliableOrdered);
        if (Proxies.TryGetValue(slot, out var p))
        {
            p.Hits++;
            p.Damage -= healthChange;
        }
        if (-healthChange > _maxHit) { _maxHit = -healthChange; _maxHitBy = attacker != null ? attacker.gameObject.name : "null"; }
    }

    internal static void CountHostHit(float healthChange)
    {
        if (healthChange >= 0f) return;
        _hostHits++;
        _hostDamage -= healthChange;
    }

    private static int _spawnRotation;
    internal static bool InSpawnRedirect;
    private static readonly List<Vector3> SpawnCandidates = new();

    /// <summary>Patch #7: the spawn points taken around the host player rotate among the living players: with the host
    /// and one client up, every other one is re-rolled around the client (as before phase 9); with the host down, all
    /// go around the living clients.</summary>
    internal static void MaybeRedirectSpawn(Vector3 referencePoint, float extraDistanceMultiplier, ref Vector3 result)
    {
        if (InSpawnRedirect || ByPtr.Count == 0) return;
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        // Only reference points that are the host player (other callers pass their own areas). Flat distance: the
        // callers pass the player's root or one of its placeholders (UnitUtil.GetTargetTransform), at another height,
        // and the downed host's ragdoll shouldn't make its spawns slip through.
        var d = referencePoint - local.transform.position;
        d.y = 0f;
        if (d.sqrMagnitude > HostReferenceRadius * HostReferenceRadius)
        {
            if (Life.CoopLife.LocalDowned) _spawnSkippedFar++;
            return;
        }

        SpawnCandidates.Clear();
        bool hostUp = LivingHost() != null;
        foreach (var p in Proxies.Values)
            if (p.Targetable && TryGetPosition(p.Slot, out var pos, out _)) SpawnCandidates.Add(pos);
        if (SpawnCandidates.Count == 0) return;
        _spawnCalls++;
        int n = SpawnCandidates.Count + (hostUp ? 1 : 0);
        int pick = (++_spawnRotation) % n;
        if (hostUp && pick == 0) return; // this one stays around the host
        var around = SpawnCandidates[hostUp ? pick - 1 : pick];
        InSpawnRedirect = true;
        try { result = GameManagerUtil.GetRandomSpawnPointOutsideOfView(around, extraDistanceMultiplier); }
        finally { InSpawnRedirect = false; }
        _spawnRedirects++;
    }

    private const float HostReferenceRadius = 2.5f;
    private const float SweepInterval = 1f;
    private const float SweepGrace = 3f;      // the host just went down: a reviver may be on its way
    private const int SweepMaxPerPass = 25;   // spread a big cleanup over a few seconds
    private static float _sweepTimer, _downSince = -1f;
    private static readonly List<Entity> SweepScratch = new();

    /// <summary>Host down, a client up. The game's recycler (EntityRegistry.DefaultContinuousRecycler) measures from the
    /// host's player, now a body on the ground: what is around it is never recycled, even with nobody alive nearby. Those
    /// enemies would sit there (their target search has a range) and fill the alive-unit limit that the spawner checks
    /// (GetCurrentActiveEntitiesCount), so fewer enemies come to the players still fighting. Every second, recycle the
    /// recyclable enemies that are within the recycler's radius of the body and outside it for every living client,
    /// the same rule the recycler applies around a living player. Bosses and the dead are left alone.</summary>
    private static void HostDownSweep(float dt)
    {
        if (!Life.CoopLife.LocalDowned || ByPtr.Count == 0 || !_matchStarted) { _downSince = -1f; _sweepTimer = 0f; return; }
        float now = Time.unscaledTime;
        if (_downSince < 0f) _downSince = now;
        if (now - _downSince < SweepGrace) return;
        _sweepTimer += dt;
        if (_sweepTimer < SweepInterval) return;
        _sweepTimer = 0f;
        if (Time.timeScale <= 0f) return;

        var local = GameState.LocalPlayerEntity;
        var all = EntityRegistry.AllEntities;
        if (local == null || all == null) return;
        LivingClients.Clear();
        foreach (var p in Proxies.Values)
            if (p.Targetable && TryGetPosition(p.Slot, out var pos, out _)) LivingClients.Add(pos);
        if (LivingClients.Count == 0) return; // everyone down: the defeat is on its way

        var body = local.transform.position;
        SweepScratch.Clear();
        for (int i = 0; i < all.Count && SweepScratch.Count < SweepMaxPerPass; i++)
        {
            var e = all[i];
            if (e == null || !e.CanBeRecycledByDistance || ByPtr.ContainsKey(e.Pointer)) continue;
            if (!HostEnemySync.TryGetTracked(e.Pointer, out var t) || t.DiedSent || t.IsBoss) continue;
            var h = e._health;
            if (h != null && h.HasBeenKilled) continue;
            var ep = e.transform.position;
            if ((ep - body).sqrMagnitude >= RecycleRadiusSqr) continue; // the game's recycler handles those
            bool near = false;
            foreach (var c in LivingClients)
                if ((ep - c).sqrMagnitude < RecycleRadiusSqr) { near = true; break; }
            if (!near) SweepScratch.Add(e);
        }
        foreach (var e in SweepScratch)
        {
            try { e.Recycle(); _sweepRecycled++; }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][proxy] Host down: recycling '{e.gameObject.name}': {ex.Message}"); }
        }
        if (SweepScratch.Count > 0)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][proxy] Host down: {SweepScratch.Count} enemies left around the host's body with no living player near recycled.");
    }

    private static readonly List<Entity> RecyclerProtected = new();
    private static readonly List<Vector3> LivingClients = new();

    /// <summary>Patch #8, before the recycler: enemies within 35 u of any living client are temporarily not recyclable.</summary>
    private const float NearScanInterval = 0.2f;
    private static readonly List<Entity> NearClients = new();
    private static float _nearScanAt;

    /// <summary>Perf: which enemies are near a living client is worked out 5 times a second (all entities x all clients),
    /// not on every recycler call (every frame); each call only flips the flag of that short list.</summary>
    internal static void BeforeRecycler()
    {
        AfterRecycler(); // in case the last call threw before its postfix
        if (ByPtr.Count == 0) { NearClients.Clear(); return; }
        float now = Time.unscaledTime;
        if (now >= _nearScanAt)
        {
            _nearScanAt = now + NearScanInterval;
            ScanNearClients();
        }
        if (NearClients.Count == 0) return;
        foreach (var e in NearClients)
        {
            if (e == null || !e.CanBeRecycledByDistance) continue;
            e.CanBeRecycledByDistance = false;
            RecyclerProtected.Add(e);
        }
        _recyclerProtected += RecyclerProtected.Count > 0 ? 1 : 0;
    }

    private static void ScanNearClients()
    {
        NearClients.Clear();
        LivingClients.Clear();
        foreach (var p in Proxies.Values)
            if (p.Targetable && TryGetPosition(p.Slot, out var pos, out _)) LivingClients.Add(pos);
        if (LivingClients.Count == 0) return;
        var all = EntityRegistry.AllEntities;
        if (all == null) return;
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (e == null || !e.CanBeRecycledByDistance) continue;
            var ep = e.transform.position;
            foreach (var c in LivingClients)
                if ((ep - c).sqrMagnitude < RecycleRadiusSqr) { NearClients.Add(e); break; }
        }
    }

    internal static void AfterRecycler()
    {
        if (RecyclerProtected.Count == 0) return;
        foreach (var e in RecyclerProtected)
            if (e != null) e.CanBeRecycledByDistance = true;
        RecyclerProtected.Clear();
    }

    /// <summary>Patch #13: PlayerSeekerAI always targets PlayerEntities[0]; make it chase the nearest living player
    /// (the host or a client's proxy).</summary>
    internal static void RetargetSeeker(PlayerSeekerAIComponent ai)
    {
        if (ByPtr.Count == 0) return;
        var self = ai._transform;
        if (self == null) return;
        var cur = ai._entityToFollow;
        var want = NearestLivingPlayer(self.position, IntPtr.Zero);
        if (want == null) return;
        if (cur != null && cur.Pointer == want.Pointer) return;
        Follow(ai, want);
        _seekerRetargets++;
    }
}

/// <summary>
/// Phase 3b, client side: damage the host's enemies dealt to our proxy, applied to the local player through the
/// game's own funnel, so death guard, UI and the local game over follow the normal path.
/// </summary>
public static class ClientIncomingDamage
{
    internal static bool ApplyingNetworkDamage;
    private static float _telemetryTimer;
    private static int _hits, _applied, _immune, _ignored;
    private static double _damage, _effective;

    public static void OnDamageToClient(NetDataReader reader)
    {
        float healthChange = reader.GetFloat();
        ushort attackerNetId = reader.GetUShort();
        if (!CoopRole.IsClientSide) return;
        _hits++;
        _damage -= healthChange;

        var local = GameState.LocalPlayerEntity;
        var health = local != null ? local._health : null;
        if (local == null || health == null || health.HasBeenKilled || !ClientPuppets.MatchStarted || healthChange >= 0f || float.IsNaN(healthChange))
        {
            _ignored++;
            return;
        }

        Entity? attacker = attackerNetId != 0 ? ClientPuppets.TryGetLivePuppet(attackerNetId) : null;
        try
        {
            ApplyingNetworkDamage = true;
            UnitUtil.ChangeEntityHealth(attacker!, local, healthChange, false, out float effective, out _, out bool immune);
            if (immune) _immune++;
            else { _applied++; _effective -= effective; }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][damage] Client: applying {healthChange:F1} from #{attackerNetId}: {ex.Message}");
        }
        finally
        {
            ApplyingNetworkDamage = false;
        }
    }

    private static float _stateTimer;
    private static byte _lastSentLife;

    /// <summary>Sends ClientState (see RemoteProxy.OnClientState) every 0.5 s, and right away when we die or revive.</summary>
    private static void SendState(float unscaledDeltaTime)
    {
        var local = GameState.LocalPlayerEntity;
        var h = local != null ? local._health : null;
        var st = local != null ? local._stats : null;
        if (!NetworkManager.HasPeer || h == null || st == null || st.Stats == null || !ClientPuppets.MatchStarted) return;
        // Phase 4A: downed counts as dead for the host (proxy out of the fight, XP ownership, revive/defeat logic).
        byte life = (byte)(h.HasBeenKilled ? 2 : Life.CoopLife.LocalDowned ? 1 : 0);
        _stateTimer += unscaledDeltaTime;
        if (_stateTimer < 0.5f && life == _lastSentLife) return;
        _stateTimer = 0f;
        _lastSentLife = life;

        var w = new NetDataWriter();
        w.Put((byte)PacketType.ClientState);
        w.Put(life);
        w.Put(h.CurrentHealth);
        w.Put(st.GetMaxHealth());
        w.Put(st.Stats.ArmorModifier);
        w.Put(st.Stats.DamageReduction);
        w.Put(st.Stats.BlockChance);
        w.Put(st.AbnormalStatusDampening);
        w.Put(st.AbnormalStatusDampeningFactor);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    public static void Tick(float unscaledDeltaTime)
    {
        if (!CoopRole.IsClientSide) return;
        try { SendState(unscaledDeltaTime); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][damage] Client SendState: {ex.Message}"); }
        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer < 5f) return;
        _telemetryTimer = 0f;
        if (_hits > 0)
        {
            var h = GameState.LocalPlayerEntity?._health;
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][damage] Client: incoming hits={_hits} damage={_damage:F0} applied={_applied} effective={_effective:F0} " +
                $"immune={_immune} ignored={_ignored} localHP={(h != null ? h.CurrentHealth : -1):F0} dead={(h != null && h.HasBeenKilled)}");
        }
        _hits = _applied = _immune = _ignored = 0;
        _damage = _effective = 0;
    }
}

// ---------------- Harmony patches (host side; every one wrapped in try/catch, see fase0 risk 6) ----------------

/// <summary>Patch #9: the proxy never takes damage or healing; damage on it is sent to the client.</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.ChangeEntityHealth))]
public static class HostProxyDamagePatch
{
    [HarmonyPrefix]
    public static bool Prefix(Entity attackerEntity, Entity targetEntity, float healthChange,
        ref float effectiveHealthChange, ref float absorbedDamage, ref bool isImmune)
    {
        try
        {
            if (!CoopRole.IsHostSide || targetEntity == null) return true;
            // The remote caster's replays are visual only (RemoteCasterDamageBlockPatch skips the original too).
            if (Skills.RemoteCaster.IsCaster(attackerEntity) || Skills.RemoteCaster.IsCaster(targetEntity)) return false;
            if (RemoteProxy.TryGetProxySlot(targetEntity, out byte proxySlot))
            {
                RemoteProxy.ForwardDamage(proxySlot, attackerEntity, healthChange);
                // Report it as dealt, so the caller shows the damage number (at the ghost) and runs on-hit logic.
                effectiveHealthChange = healthChange < 0f ? healthChange : 0f;
                absorbedDamage = 0f;
                isImmune = false;
                return false;
            }
            if (RemoteProxy.Active && targetEntity == GameState.LocalPlayerEntity) RemoteProxy.CountHostHit(healthChange);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostProxyDamagePatch: {ex}");
        }
        return true;
    }
}

/// <summary>See RemoteProxy.BeforeLoot: ProcessLoot NREs on a Players-team entity without Inventory.</summary>
[HarmonyPatch(typeof(LootUtil), nameof(LootUtil.ProcessLoot))]
public static class HostLootProxyTeamPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try { if (CoopRole.IsHostSide) RemoteProxy.BeforeLoot(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] HostLootProxyTeamPatch.Prefix: {ex}"); }
    }

    [HarmonyFinalizer]
    public static void Finalizer()
    {
        try { RemoteProxy.AfterLoot(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] HostLootProxyTeamPatch.Finalizer: {ex}"); }
    }
}

/// <summary>Patch #7.</summary>
[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.GetRandomSpawnPointOutsideOfView))]
public static class HostSpawnPointPatch
{
    [HarmonyPostfix]
    public static void Postfix(Vector3 referencePoint, float extraDistanceMultiplier, ref Vector3 __result)
    {
        try
        {
            if (!CoopRole.IsHostSide) return;
            RemoteProxy.MaybeRedirectSpawn(referencePoint, extraDistanceMultiplier, ref __result);
        }
        catch (Exception ex)
        {
            RemoteProxy.InSpawnRedirect = false;
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostSpawnPointPatch: {ex}");
        }
    }
}

/// <summary>Patch #8.</summary>
[HarmonyPatch(typeof(EntityRegistry), nameof(EntityRegistry.DefaultContinuousRecycler))]
public static class HostRecyclerPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try { if (CoopRole.IsHostSide) RemoteProxy.BeforeRecycler(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] HostRecyclerPatch.Prefix: {ex}"); }
    }

    [HarmonyPostfix]
    public static void Postfix()
    {
        try { RemoteProxy.AfterRecycler(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] HostRecyclerPatch.Postfix: {ex}"); }
    }
}

/// <summary>Patch #13: runs every frame before the seeker moves (DoOnUpdate reads _transformToFollow).</summary>
[HarmonyPatch(typeof(PlayerSeekerAIComponent), nameof(PlayerSeekerAIComponent.DoOnUpdate))]
public static class HostPlayerSeekerPatch
{
    [HarmonyPrefix]
    public static void Prefix(PlayerSeekerAIComponent __instance)
    {
        try
        {
            if (!CoopRole.IsHostSide || __instance == null) return;
            RemoteProxy.RetargetSeeker(__instance);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] HostPlayerSeekerPatch: {ex}");
        }
    }
}
