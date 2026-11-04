using ECM2.Characters;
using HarmonyLib;
using SoulstoneCoop.Enemies;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Skills;

/// <summary>
/// Phase 4C, both sides: a stand-in that plays ANOTHER player's skills, visual only. It's a "Creeps/Dummy"
/// (same recipe as RemoteProxy: pool + Entity.Setup) in the Summons team - allied, so its projectiles fly at
/// Monsters, but outside LootUtil's Players-team walk (the phase 3b NRE) - invisible, without a collider and
/// ignored by target searches. It follows that player's ghost every frame. Each remote skill becomes a copy:
/// the "Skill-Prefabs/&lt;key&gt;" prefab instantiated under the caster and set up with Skill.Setup(caster)
/// (no AddSkill, so it never casts by itself). Every game effect of the caster is blocked (RemoteCasterDamageBlockPatch,
/// RemoteCasterStatusBlockPatch, RemoteCasterPushbackBlockPatch): damage stays on the phase 3 paths.
/// See docs/fase4c-investigacao.md.
/// Phase 9: one caster per other player in the run (the host has one per client; a client has the host's and one per
/// other client), each with its own skill copies and channels.
/// </summary>
public static class RemoteCaster
{
    private const string CasterPrefabPath = "Creeps/Dummy";
    private const string SkillPrefabFolder = "Skill-Prefabs/";

    private sealed class Copy
    {
        public string Key = "";
        public Skill Skill = null!;
        public IntPtr Ptr;
        public Il2CppSystem.Func<bool>? ChannelFunc;
        public Il2CppSystem.Func<Skill, Vector3>? FrontFunc, GroundFunc;
        public Vector3 Front, Ground;
        public Vector3 FrontTarget, GroundTarget; // latest SkillChannelAim; Front/Ground glide towards them
        public bool HasFrontTarget, HasGroundTarget;
        public bool AlwaysOn;              // runs every frame from Tick (AlwaysOnSync), not from casts
        public AlwaysOnSpec? AppliedSpec;
        public float AppliedScale = -1f;
        public string Kind = "";
    }

    /// <summary>The stand-in of one other player.</summary>
    private sealed class Caster
    {
        public byte Slot;
        public Entity? Entity;
        public IntPtr Ptr, StatsPtr;
        public ModifiedCharacter? Character;
        public float RetryTimer;
        public int CreateFailures;
        public readonly Dictionary<string, Copy> Copies = new();
        public readonly HashSet<string> FailedKeys = new();
        public readonly Dictionary<string, bool> Channeling = new();
        public readonly Dictionary<string, AlwaysOnSpec> AlwaysOnWanted = new(); // that player's always-on skills (SkillAlwaysOn)
        public IntPtr AnimGhostPtr;
        public Animator[]? GhostAnimators;
    }

    private static readonly Dictionary<byte, Caster> Casters = new();

    /// <summary>What shapes one always-on skill of a partner (AlwaysOnSync): level, extra projectiles, rotation speed,
    /// projectile cooldown and the adjusted scale (applied like a cast's, ApplyFidelity).</summary>
    internal sealed record AlwaysOnSpec(int Type, byte Level, int Extra, float Speed, float Cooldown, float Scale);
    private static int _alwaysOnErrors;
    private static readonly HashSet<IntPtr> CasterPtrs = new();
    private static readonly HashSet<IntPtr> StatsPtrs = new();
    private static Team _originalTeam;
    private static VfxReferences? _originalVfxOnSetup;
    private static bool _matchStarted;
    private static bool _hooked;

    /// <summary>At least one caster exists.</summary>
    public static bool Active => CasterPtrs.Count > 0;

    /// <summary>Phase 4D: the stand-in of that player (a target for enemy casts aimed at it: its ghost has no Entity).</summary>
    internal static Entity? CasterOf(byte slot) => Casters.TryGetValue(slot, out var c) ? c.Entity : null;

    public static int CopyCount { get { int n = 0; foreach (var c in Casters.Values) n += c.Copies.Count; return n; } }
    public static int OpenChannels { get { int n = 0; foreach (var c in Casters.Values) foreach (var on in c.Channeling.Values) if (on) n++; return n; } }

    internal static bool IsCaster(Entity? e) => e != null && CasterPtrs.Count > 0 && CasterPtrs.Contains(e.Pointer);
    internal static bool IsCasterStats(StatsComponent? s) => s != null && StatsPtrs.Count > 0 && StatsPtrs.Contains(s.Pointer);

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        Players.Left += slot =>
        {
            RemoveCaster(slot, "player left");
            Casters.Remove(slot);
        };
    }

    public static void OnMatchStarted()
    {
        // A new match: the casters and their copies from the previous scene are gone with it.
        ForgetAll();
        _matchStarted = true;
    }

    private static Caster CasterFor(byte slot)
    {
        if (!Casters.TryGetValue(slot, out var c)) Casters[slot] = c = new Caster { Slot = slot };
        return c;
    }

    public static void Tick(float unscaledDeltaTime)
    {
        if (NetworkManager.Role == NetRole.None) return;
        try
        {
            if (!GameManagerUtil.HasGameManager) { _matchStarted = false; ForgetAll(); }
            foreach (var r in CoopSession.Remotes)
            {
                var c = CasterFor(r.Slot);
                if (c.Ptr != IntPtr.Zero && (c.Entity == null || !c.Entity.gameObject.activeInHierarchy))
                {
                    Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Remote caster {c.Slot} went inactive/destroyed - will recreate.");
                    ForgetCaster(c);
                }

                bool haveRemote = r.TryGetPosition(out var pos, out var rot);
                if (c.Entity == null)
                {
                    c.RetryTimer -= unscaledDeltaTime;
                    if (haveRemote && _matchStarted && GameState.LocalPlayerEntity != null && c.RetryTimer <= 0f && c.CreateFailures < 5)
                    {
                        c.RetryTimer = 2f;
                        CreateCaster(c, pos, rot);
                    }
                }
                else if (haveRemote)
                {
                    if (c.Character != null) c.Character.SetPosition(pos);
                    else c.Entity.transform.position = pos;
                    c.Entity.transform.rotation = rot;
                }
                else if (r.Ghost == null)
                {
                    RemoveCaster(r.Slot, "player out of the run"); // its ghost is gone: nothing to cast from
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] RemoteCaster.Tick: {ex}");
        }
        try { GlideAim(unscaledDeltaTime); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] GlideAim: {ex.Message}"); }
        try { UpdateAlwaysOn(); }
        catch (Exception ex) { if (_alwaysOnErrors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] UpdateAlwaysOn: {ex}"); }
        RunProbes();
    }

    // ---------------- 0.8.1 diagnostics: replica visuals and the ghost's cast animation ----------------

    private sealed class Probe
    {
        public float At;
        public Caster? Caster;
        public Copy? Copy;
        public Vector3 Source;
        public bool HasSource;
        public Animator? Anim;
        public string AnimName = "";
        public int Hash, Layer;
    }

    private const int ProbesPerKey = 3;
    private const float ProbeDelay = 0.25f;
    private static readonly List<Probe> Probes = new();
    private static readonly Dictionary<string, int> ProbeCounts = new();
    private static readonly HashSet<string> MissingStateLogged = new();

    /// <summary>The first casts of each key are checked 0.25 s later: what the replica put in the world.</summary>
    private static void QueueVisualProbe(Caster caster, Copy c, CastMessage m)
    {
        ProbeCounts.TryGetValue(c.Key, out int n);
        if (n >= ProbesPerKey) return;
        ProbeCounts[c.Key] = n + 1;
        Probes.Add(new Probe { At = Time.unscaledTime + ProbeDelay, Caster = caster, Copy = c, Source = m.Source, HasSource = m.Has(CastMessage.FSource) });
    }

    private static void QueueAnimProbe(Animator anim, string name, int hash, int layer)
    {
        string k = "anim:" + name;
        ProbeCounts.TryGetValue(k, out int n);
        if (n >= ProbesPerKey) return;
        ProbeCounts[k] = n + 1;
        Probes.Add(new Probe { At = Time.unscaledTime + ProbeDelay, Anim = anim, AnimName = name, Hash = hash, Layer = layer });
    }

    private static void RunProbes()
    {
        if (Probes.Count == 0) return;
        float now = Time.unscaledTime;
        for (int i = Probes.Count - 1; i >= 0; i--)
        {
            var p = Probes[i];
            if (p.At > now) continue;
            Probes.RemoveAt(i);
            try
            {
                if (p.Copy != null) ProbeVisuals(p);
                else if (p.Anim != null) ProbeAnim(p);
            }
            catch (Exception ex)
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Probe: {ex.Message}");
            }
        }
    }

    /// <summary>Projectiles and areas whose Skill (SkillProjectile+0x88, SkillAreaEffect+0x48) is the copy: how many,
    /// where, active, renderers on, and whether the camera draws their layer. Plus where the caster and the ghost are.</summary>
    private static void ProbeVisuals(Probe p)
    {
        var c = p.Copy!;
        var caster = p.Caster;
        var ghost = caster != null ? CoopSession.Get(caster.Slot)?.Ghost : null;
        Vector3 ghostPos = ghost != null ? ghost.transform.position : Vector3.zero;
        var cam = Camera.main;
        int projectiles = 0, areas = 0, rendOn = 0, rendTotal = 0;
        Vector3? first = null;
        int firstLayer = -1;

        void Count(GameObject go)
        {
            first ??= go.transform.position;
            if (firstLayer < 0) firstLayer = go.layer;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                rendTotal++;
                if (r.enabled && r.gameObject.activeInHierarchy) rendOn++;
            }
        }

        foreach (var sp in UnityEngine.Object.FindObjectsOfType<SkillProjectile>())
        {
            if (sp == null || sp.Skill == null || sp.Skill.Pointer != c.Ptr) continue;
            projectiles++;
            Count(sp.gameObject);
        }
        foreach (var ae in UnityEngine.Object.FindObjectsOfType<SkillAreaEffect>())
        {
            if (ae == null || ae.Skill == null || ae.Skill.Pointer != c.Ptr) continue;
            areas++;
            Count(ae.gameObject);
        }
        string layerInfo = firstLayer < 0 ? "-" :
            $"{LayerMask.LayerToName(firstLayer)}({firstLayer}) drawn={(cam != null && (cam.cullingMask & (1 << firstLayer)) != 0)}";
        string firstInfo = first.HasValue ? $"{first.Value} ({Vector3.Distance(first.Value, ghostPos):0.0} m from the ghost)" : "-";
        float casterGap = caster?.Entity != null ? Vector3.Distance(caster.Entity.transform.position, ghostPos) : -1f;
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][casts] Probe '{c.Key}' (slot {(caster != null ? caster.Slot : -1)}): projectiles={projectiles} areas={areas} first={firstInfo} renderers={rendOn}/{rendTotal} " +
            $"layer={layerInfo} copyActive={(c.Skill != null && c.Skill.gameObject.activeInHierarchy)} casterToGhost={casterGap:0.0} m " +
            $"source={(p.HasSource ? p.Source.ToString() : "none")}.");
    }

    /// <summary>Is the animator in (or going to) the state the CrossFade asked for, and does its layer have weight.</summary>
    private static void ProbeAnim(Probe p)
    {
        var a = p.Anim!;
        if (a == null || !a.isActiveAndEnabled) { Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Anim probe '{p.AnimName}': animator gone/inactive."); return; }
        var cur = a.GetCurrentAnimatorStateInfo(p.Layer);
        var next = a.GetNextAnimatorStateInfo(p.Layer);
        bool inState = cur.shortNameHash == p.Hash || cur.fullPathHash == p.Hash;
        bool going = next.shortNameHash == p.Hash || next.fullPathHash == p.Hash;
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][casts] Anim probe '{p.AnimName}' on '{a.gameObject.name}' layer {p.Layer} (weight {a.GetLayerWeight(p.Layer):0.##}): " +
            $"inState={inState} transitioning={going} normalizedTime={cur.normalizedTime:0.00} speed={a.speed:0.##} " +
            $"controller='{(a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "NONE")}'.");
    }

    // ---------------- the caster entity ----------------

    private static void CreateCaster(Caster c, Vector3 pos, Quaternion rot)
    {
        Entity? inst = null;
        try
        {
            var prefab = ResourcesUtil.Load<Entity>(CasterPrefabPath);
            if (prefab == null) throw new Exception($"ResourcesUtil.Load<Entity>(\"{CasterPrefabPath}\") returned null");
            ResourcesUtil.GetPrefabInstance<Entity>(prefab, pos, rot, out inst);
            if (inst == null) throw new Exception("GetPrefabInstance returned null");

            var team = inst._team ?? inst.GetComponent<TeamComponent>();
            if (team == null) throw new Exception("Dummy has no TeamComponent");
            _originalTeam = team.Team;
            team.Team = Team.Summons;
            inst.CacheReferences();
            var stats = inst._stats;
            if (stats != null && stats.Stats != null) stats.Stats.CopyStats(stats.Stats);
            if (stats != null && stats.MultiplicativeStats != null) stats.MultiplicativeStats.SetToOne();

            inst.CanBeRecycledByDistance = false;
            inst.IgnoredByUnitLimit = true;
            inst.IgnoredByEntitySearchQueries = true; // nobody targets it: it's only a launch point
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

            var ai = inst.GetComponent<BasicAIComponent>();
            if (ai != null) { ai.StopRefreshingTarget(); ai.enabled = false; }
            var staticAi = inst.GetComponent<StaticAIComponent>();
            if (staticAi != null) staticAi.enabled = false;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            var col = inst._physical != null ? inst._physical.Collider : null;
            if (col != null) col.enabled = false; // no body: enemies walk through, their hits can't find it

            c.Entity = inst;
            c.Ptr = inst.Pointer;
            c.StatsPtr = stats != null ? stats.Pointer : IntPtr.Zero;
            c.Character = inst._character;
            CasterPtrs.Add(c.Ptr);
            if (c.StatsPtr != IntPtr.Zero) StatsPtrs.Add(c.StatsPtr);
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][casts] Created remote caster {c.Slot} '{inst.gameObject.name}' at {pos}: team {_originalTeam}->{team.Team} " +
                $"isPlayer={inst.IsPlayer()} skillCaster={(inst._skillCaster != null)} animator={inst.Animator.HasReference} " +
                $"stats={(stats != null)} areaStat={(stats?.Stats != null ? stats.Stats.AreaModifier : -1f)}");
            PreloadWeaponSkills(c);
        }
        catch (Exception ex)
        {
            c.CreateFailures++;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] CreateCaster {c.Slot} failed (#{c.CreateFailures}): {ex}");
            if (inst != null && c.Entity == null)
            {
                try { RestoreForPool(inst); inst.Recycle(); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Phase 5: that player's weapon skills (WeaponDefinition.MainSkillId/OffhandSkillId of the weapon in its
    /// Hello) are loaded now, so the first cast doesn't hit the disk, and logged: a skill of another character that
    /// isn't under Skill-Prefabs shows up here instead of as a silent noSkill later. Copies are still made on the first
    /// cast, which carries the SkillType Skill.Setup needs.</summary>
    private static void PreloadWeaponSkills(Caster c)
    {
        try
        {
            var r = CoopSession.Get(c.Slot)?.Info;
            if (r == null || !r.Known || string.IsNullOrEmpty(r.WeaponId)) return;
            var weapons = Global.WeaponCollection;
            if (weapons == null || !weapons.TryGetWeaponDefinitionById(r.WeaponId, out var wd, out _) || wd == null)
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Slot {c.Slot} weapon '{r.WeaponId}' is not in this game's collection.");
                return;
            }
            var parts = new List<string>();
            foreach (var id in new[] { wd.MainSkillId, wd.OffhandSkillId, wd.ArtifactPowerSkillId })
            {
                if (string.IsNullOrEmpty(id)) continue;
                var prefab = ResourcesUtil.Load<Skill>(SkillPrefabFolder + id + "-Skill");
                parts.Add($"{id}={(prefab != null ? "ok" : "MISSING")}");
            }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Slot {c.Slot} plays {r.CharacterName} with '{r.WeaponId}': {string.Join(", ", parts)}.");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Preloading slot {c.Slot} weapon skills: {ex.Message}");
        }
    }

    /// <summary>Scene gone: just drop the references (the objects went with it).</summary>
    private static void ForgetCaster(Caster c)
    {
        if (c.Ptr != IntPtr.Zero) CasterPtrs.Remove(c.Ptr);
        if (c.StatsPtr != IntPtr.Zero) StatsPtrs.Remove(c.StatsPtr);
        c.Entity = null;
        c.StatsPtr = IntPtr.Zero;
        c.Ptr = IntPtr.Zero;
        c.Character = null;
        c.Copies.Clear();
        c.FailedKeys.Clear();
        c.Channeling.Clear();
        c.GhostAnimators = null;
        c.AnimGhostPtr = IntPtr.Zero;
    }

    private static void ForgetAll()
    {
        foreach (var c in Casters.Values)
        {
            ForgetCaster(c);
            c.CreateFailures = 0;
        }
        CasterPtrs.Clear();
        StatsPtrs.Clear();
        MissingStateLogged.Clear();
        Probes.Clear();
        ProbeCounts.Clear();
    }

    private static void RemoveCaster(byte slot, string reason)
    {
        if (!Casters.TryGetValue(slot, out var c)) return;
        var e = c.Entity;
        var copies = c.Copies.Values.ToArray();
        ForgetCaster(c);
        c.AlwaysOnWanted.Clear(); // out of the run: its next SkillAlwaysOn brings them back
        Probes.RemoveAll(p => p.Caster == c);
        if (e == null) return;
        try
        {
            // The Dummy goes back to the pool: our skill copies must not ride along.
            foreach (var copy in copies) DestroyCopy(copy);
            RestoreForPool(e);
            e.Recycle();
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Remote caster {slot} recycled ({reason}).");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] RemoveCaster {slot}: {ex.Message}");
        }
    }

    private static void DestroyCopy(Copy c)
    {
        try
        {
            if (c.Skill == null) return;
            c.Skill.CancelExecution();
            c.Skill.Recycle();
            UnityEngine.Object.Destroy(c.Skill.gameObject);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Destroying copy '{c.Key}': {ex.Message}");
        }
    }

    private static void RestoreForPool(Entity e)
    {
        var team = e._team ?? e.GetComponent<TeamComponent>();
        if (team != null) team.Team = _originalTeam;
        if (_originalVfxOnSetup != null) e.VfxOnSetup = _originalVfxOnSetup;
        e.IgnoredByEntitySearchQueries = false;
        foreach (var r in e.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
        var col = e._physical != null ? e._physical.Collider : null;
        if (col != null) col.enabled = true;
    }

    // ---------------- skill copies ----------------

    private static Copy? GetOrCreateCopy(Caster caster, string key, SkillType skillType, Skill? template = null)
    {
        if (caster.Copies.TryGetValue(key, out var c))
        {
            if (c.Skill != null) return c;
            caster.Copies.Remove(key);
        }
        if (caster.FailedKeys.Contains(key) || caster.Entity == null) return null;

        try
        {
            var inst = template != null ? CreateFromTemplate(caster, key, template, out string how) : CreateSkillInstance(caster, key, skillType, out how);
            inst.FillPools(3);

            c = new Copy { Key = key, Skill = inst, Ptr = inst.Pointer, Kind = inst.GetIl2CppType().Name };
            caster.Copies[key] = c;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Copy '{key}' ({c.Kind}, type={skillType}, via {how}) set up on remote caster {caster.Slot}.");
            return c;
        }
        catch (Exception ex)
        {
            caster.FailedKeys.Add(key);
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] Could not copy skill '{key}' on caster {caster.Slot} (won't retry this match): {ex}");
            return null;
        }
    }

    /// <summary>The skill the other player cast, straight from its prefab: "Skill-Prefabs/&lt;key&gt;" is the same asset on all
    /// machines whatever character anyone plays, so a skill the local character doesn't have resolves the same way.
    /// Phase 5 dropped the UnitUtil.SetupExtraSkillForEntity attempt that came first. It looks the id up in THIS match's
    /// MatchPowerUpData (the local character's power-ups), so it returned null for any other character's skill. For a
    /// shared skill it instantiated a copy and then threw on the Dummy (no PowerUpComponent at Entity+0x3C8), leaking
    /// that copy: those were the Buckshot/PowerBlast NREs of the 4C test.</summary>
    private static Skill CreateSkillInstance(Caster caster, string key, SkillType skillType, out string how)
    {
        var prefab = ResourcesUtil.Load<Skill>(SkillPrefabFolder + key);
        if (prefab == null) throw new Exception($"no skill prefab at Resources '{SkillPrefabFolder}{key}'");
        var inst = UnityEngine.Object.Instantiate(prefab, caster.Entity!.transform);
        if (inst == null) throw new Exception("Instantiate returned null");
        SetupOnCaster(caster.Entity, inst, skillType);
        how = "prefab + Skill.Setup";
        return inst;
    }

    /// <summary>Sets a fresh copy up on the caster; a half set-up copy is destroyed so it can't linger under it.</summary>
    private static void SetupOnCaster(Entity caster, Skill inst, SkillType skillType)
    {
        var phys = caster.Physical;
        Collider? col = phys.HasReference && phys.Reference != null ? phys.Reference.Collider : null;
        try
        {
            inst.Setup(caster, skillType, caster.Team, caster.Stats, caster.PassiveSkills, caster.Animator, col!,
                new Il2CppSystem.Nullable<float>(), new Il2CppSystem.Nullable<bool>());
        }
        catch
        {
            UnityEngine.Object.Destroy(inst.gameObject);
            throw;
        }
    }

    /// <summary>A skill that is not a player power-up (the God-Entity's meteor): the Resources prefab when there is one
    /// under Skill-Prefabs, else a clone of the local scene instance (the template), set up on the caster like the
    /// path above. The copy belongs to the caster, so every effect of it is blocked by the patches below.</summary>
    private static Skill CreateFromTemplate(Caster caster, string key, Skill template, out string how)
    {
        string prefabKey = key.StartsWith(GodKeyPrefix, StringComparison.Ordinal) ? key.Substring(GodKeyPrefix.Length) : key;
        Skill? source = null;
        try { source = ResourcesUtil.Load<Skill>(SkillPrefabFolder + prefabKey); }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Load '{SkillPrefabFolder}{prefabKey}': {ex.Message}"); }
        how = source != null ? "Resources prefab" : "clone of the local instance";
        source ??= template;
        var inst = UnityEngine.Object.Instantiate(source, caster.Entity!.transform);
        if (inst == null) throw new Exception("Instantiate returned null");
        SetupOnCaster(caster.Entity, inst, template.SkillType);
        return inst;
    }

    /// <summary>Copies made from a God-Entity skill live under their own key namespace.</summary>
    internal const string GodKeyPrefix = "god:";

    /// <summary>Il2Cpp delegates for the funcs some skills require (channeling, updated front position). Built
    /// lazily and guarded: if the conversion fails only those skills are affected.</summary>
    private static bool TryGetFuncs(Caster caster, Copy c, bool channel, bool front, bool ground)
    {
        try
        {
            var copy = c;
            var owner = caster;
            if (channel && c.ChannelFunc == null)
                c.ChannelFunc = (Func<bool>)(() => owner.Entity != null && owner.Channeling.TryGetValue(copy.Key, out bool on) && on);
            if (front && c.FrontFunc == null) c.FrontFunc = (Func<Skill, Vector3>)(_ => copy.Front);
            if (ground && c.GroundFunc == null) c.GroundFunc = (Func<Skill, Vector3>)(_ => copy.Ground);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] Delegates for '{c.Key}': {ex.Message}");
            return false;
        }
    }

    // ---------------- playing a cast ----------------

    public enum PlayResult { Played, NoCaster, NoSkill, Rejected, Error, Culled }

    /// <param name="slot">Whose cast it is: it plays on that player's caster.</param>
    /// <param name="template">Phase "pendências": a local skill instance to copy when the key isn't a player
    /// power-up (God meteors, see GodCasts, on the host's caster). Null for the other players' skills.</param>
    internal static PlayResult Play(byte slot, CastMessage m, Skill? template = null)
    {
        if (!Casters.TryGetValue(slot, out var caster) || caster.Entity == null || caster.Entity._skillCaster == null) return PlayResult.NoCaster;
        // Phase 9: a player far from us is off screen: its replica (visual only) would just cost effects. [Coop] ReplicaCastRadius.
        float radius = Plugin.ReplicaCastRadius?.Value ?? 0f;
        var me = GameState.LocalPlayerEntity;
        if (radius > 0f && me != null && RemoteProxy.TryGetPosition(slot, out var at, out _)
            && (at - me.transform.position).sqrMagnitude > radius * radius) return PlayResult.Culled;
        var c = GetOrCreateCopy(caster, m.Key, (SkillType)m.SkillType, template);
        if (c == null) return PlayResult.NoSkill;

        try
        {
            var tr = caster.Entity.transform;
            // 0.8.1: right on the ghost at cast time, transform included. Tick only moves it through
            // ModifiedCharacter.SetPosition, which may reach the transform a step later, and skills without a
            // source override start from the caster.
            if (RemoteProxy.TryGetPosition(slot, out var ghostPos, out _))
            {
                if (caster.Character != null) caster.Character.SetPosition(ghostPos);
                tr.position = ghostPos;
            }
            tr.rotation = Quaternion.Euler(0f, m.Yaw, 0f); // front-of-caster skills aim with the caster's forward
            ApplyFidelity(caster, c, m);

            var p = new SkillActivationParameters();
            // Raw writes: the interop setters for these Nullable<Vector3> fields throw (see ActivationParamsRaw).
            if (m.Has(CastMessage.FSource)) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.SourcePositionOverride, m.Source);
            if (m.Has(CastMessage.FGround)) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.TargetGroundPosition, m.Ground);
            if (m.Has(CastMessage.FFront)) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.TargetFrontPosition, m.Front);
            if (m.Has(CastMessage.FDirection)) ActivationParamsRaw.SetVector(p, ActivationParamsRaw.TargetDirection, m.Direction);

            var target = ResolveTarget(caster, m);
            if (target != null)
            {
                p.TargetEntity = target;
                p.TargetTransform = target.transform;
            }

            c.Front = m.Has(CastMessage.FFront) ? m.Front : tr.position + tr.forward * 5f;
            c.Ground = m.Has(CastMessage.FGround) ? m.Ground : c.Front;
            c.HasFrontTarget = c.HasGroundTarget = false; // a new cast starts from its own aim
            bool wantChannel = m.Has(CastMessage.FChannel), wantFront = m.Has(CastMessage.FFrontFunc), wantGround = m.Has(CastMessage.FGroundFunc);
            if ((wantChannel || wantFront || wantGround) && !TryGetFuncs(caster, c, wantChannel, wantFront, wantGround)) return PlayResult.Error;
            if (wantChannel)
            {
                caster.Channeling[m.Key] = true;
                p.IsBeingChanneled = c.ChannelFunc!;
            }
            if (wantFront) p.GetTargetFrontPosition = c.FrontFunc!;
            if (wantGround) p.GetTargetFrontPositionGround = c.GroundFunc!;

            // IgnoreAll: no cooldown, busy or combat checks, and no procs / chain casts / extra multicasts -
            // those arrive as their own cast messages from the other side.
            bool ok = caster.Entity._skillCaster.CastSkill(c.Skill, p, SkillActivationOptions.IgnoreAll);
            if (!ok) return PlayResult.Rejected;
            QueueVisualProbe(caster, c, m);
            if (template == null) PlayGhostAnimation(caster, c.Skill); // a God cast is not the ghost's own
            return PlayResult.Played;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] Playing '{m.Key}' for slot {slot}: {ex}");
            return PlayResult.Error;
        }
    }

    /// <summary>SkillChannelAim: where that player's open channel points now. Only for a channel we are playing.</summary>
    internal static void OnChannelAim(byte slot, string key, byte flags, Vector3 front, Vector3 ground)
    {
        if (!Casters.TryGetValue(slot, out var c) || !c.Copies.TryGetValue(key, out var copy)) return;
        if (!c.Channeling.TryGetValue(key, out bool on) || !on) return;
        if ((flags & 1) != 0) { copy.FrontTarget = front; copy.HasFrontTarget = true; }
        if ((flags & 2) != 0) { copy.GroundTarget = ground; copy.HasGroundTarget = true; }
    }

    /// <summary>SkillAlwaysOn: that player's whole list of always-on skills (empty = none).</summary>
    internal static void SetAlwaysOn(byte slot, List<(string Key, AlwaysOnSpec Spec)> list)
    {
        var caster = CasterFor(slot);
        bool same = caster.AlwaysOnWanted.Count == list.Count;
        if (same) foreach (var (key, spec) in list) if (!caster.AlwaysOnWanted.TryGetValue(key, out var have) || have != spec) { same = false; break; }
        if (same) return;
        caster.AlwaysOnWanted.Clear();
        foreach (var (key, spec) in list) caster.AlwaysOnWanted[key] = spec;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Slot {slot} always-on skills: {(list.Count > 0 ? string.Join(", ", list.Select(e => $"{e.Key}(+{e.Spec.Extra})")) : "none")}.");
    }

    /// <summary>Every frame: run each wanted always-on skill's OnUpdate on that player's caster, stop the others.
    /// While the player is down (or there's no caster) they all stop, and come back with it.</summary>
    private static void UpdateAlwaysOn()
    {
        List<string>? stop = null;
        foreach (var caster in Casters.Values)
        {
            bool active = caster.Entity != null && _matchStarted && !Life.CoopLife.IsRemoteDown(caster.Slot);
            stop?.Clear();
            foreach (var kv in caster.Copies)
                if (kv.Value.AlwaysOn && (!active || !caster.AlwaysOnWanted.ContainsKey(kv.Key))) (stop ??= new List<string>()).Add(kv.Key);
            if (stop != null) foreach (var key in stop) StopAlwaysOn(caster, key, active ? "no longer used" : "player down or no caster");
            if (!active || caster.AlwaysOnWanted.Count == 0) continue;

            foreach (var kv in caster.AlwaysOnWanted)
            {
                if (caster.FailedKeys.Contains(kv.Key)) continue;
                var c = GetOrCreateCopy(caster, kv.Key, (SkillType)kv.Value.Type);
                var rs = c != null ? c.Skill.TryCast<RotatingShieldSkill>() : null;
                if (c == null || rs == null) continue;
                try
                {
                    if (!c.AlwaysOn || c.AppliedSpec != kv.Value)
                    {
                        var spec = kv.Value;
                        if (spec.Level > 0) c.Skill.SkillLevel = spec.Level;
                        rs.ExtraProjectiles = spec.Extra;
                        rs.RotationSpeed = spec.Speed;
                        if (spec.Cooldown > 0f) rs.ProjectileCooldown = spec.Cooldown;
                        ApplyFidelity(caster, c, new CastMessage { Level = spec.Level, Scale = spec.Scale });
                        if (!c.AlwaysOn)
                            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Slot {caster.Slot}: always-on '{kv.Key}' running on its caster ({rs.BaseProjectiles}+{spec.Extra} projectiles, speed {spec.Speed:F0}).");
                        c.AlwaysOn = true;
                        c.AppliedSpec = spec;
                    }
                    rs.OnUpdate();
                }
                catch (Exception ex)
                {
                    caster.FailedKeys.Add(kv.Key); // stops trying this one for the match
                    if (_alwaysOnErrors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] Always-on '{kv.Key}' for slot {caster.Slot}: {ex}");
                    StopAlwaysOn(caster, kv.Key, "error");
                    break; // FailedKeys / Copies changed: next frame
                }
            }
        }
    }

    /// <summary>Recycle ends its projectiles (RotatingShieldSkill.Recycle 0x55E010), then the copy goes (DestroyCopy).</summary>
    private static void StopAlwaysOn(Caster caster, string key, string why)
    {
        if (!caster.Copies.TryGetValue(key, out var c)) return;
        caster.Copies.Remove(key);
        DestroyCopy(c);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Slot {caster.Slot}: always-on '{key}' stopped ({why}).");
    }

    /// <summary>Open channels' aim glides to the latest SkillChannelAim (packets come ~10 Hz; the skill reads it every frame).</summary>
    private static void GlideAim(float dt)
    {
        float k = 1f - Mathf.Exp(-15f * dt);
        foreach (var caster in Casters.Values)
            foreach (var kv in caster.Channeling)
            {
                if (!kv.Value || !caster.Copies.TryGetValue(kv.Key, out var copy)) continue;
                if (copy.HasFrontTarget) copy.Front = Vector3.Lerp(copy.Front, copy.FrontTarget, k);
                if (copy.HasGroundTarget) copy.Ground = Vector3.Lerp(copy.Ground, copy.GroundTarget, k);
            }
    }

    internal static void OnChannelEnd(byte slot, string key)
    {
        if (Casters.TryGetValue(slot, out var c) && c.Channeling.ContainsKey(key)) c.Channeling[key] = false;
    }

    private static Entity? ResolveTarget(Caster caster, CastMessage m)
    {
        switch (m.TargetKind)
        {
            case CastMessage.TargetSelf:
                return caster.Entity;
            case CastMessage.TargetEnemy:
                if (CoopRole.IsHostSide) return HostEnemySync.TryGet(m.TargetNetId, out var e) ? e : null;
                return ClientPuppets.TryGetLivePuppet(m.TargetNetId);
            case CastMessage.TargetPlayer:
                // Us, or another player: its stand-in follows its ghost (the ghost has no Entity).
                return m.TargetSlot == Players.MySlot ? GameState.LocalPlayerEntity : CasterOf(m.TargetSlot);
            default:
                return null;
        }
    }

    /// <summary>Level, projectile/area counts and the sender's area scale (UnitUtil.GetAdjustedScale), put
    /// into the copy's own modifiers so the receiver's math lands on the same size.</summary>
    private static void ApplyFidelity(Caster caster, Copy c, CastMessage m)
    {
        var s = c.Skill;
        if (m.Level > 0) s.SkillLevel = m.Level;
        if (m.CountA > 0)
        {
            var proj = s.TryCast<ProjectileSkill>();
            if (proj != null) proj.ProjectilesToFire = m.CountA;
            var area = s.TryCast<AreaSkill>();
            if (area != null) area.AreasToSpawn = m.CountA;
            var multi = s.TryCast<MultiProjectileSkill>();
            if (multi != null && m.CountB >= m.CountA)
            {
                multi.MinProjectilesQuantity = m.CountA;
                multi.MaxProjectilesQuantity = m.CountB;
            }
        }

        if (m.Scale <= 0f || Mathf.Abs(m.Scale - c.AppliedScale) < 0.001f) return;
        var trait = s.Modifiers;
        var mods = trait != null ? trait.Modifiers : null;
        var stats = caster.Entity!._stats;
        if (mods == null || stats == null) return;
        // scale = (statArea + modArea) * k  (k = multiplicative stat, runes, shrink, 1 + modAreaMult).
        // Measure the caster's s0 and k with modArea = 0 / 1 and AreaMult = 0, then solve for modArea.
        mods.AreaMultiplicativeModifier = 0f;
        mods.AreaModifier = 0f;
        float s0 = UnitUtil.GetAdjustedScale(trait, stats);
        mods.AreaModifier = 1f;
        float k = UnitUtil.GetAdjustedScale(trait, stats) - s0;
        mods.AreaModifier = k > 0.0001f ? (m.Scale - s0) / k : 0f;
        c.AppliedScale = m.Scale;
    }

    internal static int GhostAnimPlayed, GhostAnimMissing;

    /// <summary>Bonus: the cast animation on the ghost. SkillAnimationTrait.Activate does a CrossFade on the caster's
    /// AnimatorComponent, i.e. on every Animator of its list (the character's and a weapon's that syncs with it); the
    /// ghost only has plain Animators, so the same CrossFade goes to each one whose controller has the state. Phase 5:
    /// the ghost is that player's character, so the states are there for any character (logged once if not).</summary>
    private static void PlayGhostAnimation(Caster caster, Skill s)
    {
        try
        {
            SkillAnimationTrait? trait = s.TryCast<ProjectileSkill>()?.AnimationTrait
                ?? s.TryCast<AreaSkill>()?.AnimationTrait
                ?? s.TryCast<MultiProjectileSkill>()?.AnimationTrait;
            if (trait == null || string.IsNullOrEmpty(trait.AnimationName)) return;
            var who = CoopSession.Get(caster.Slot);
            var ghost = who?.Ghost;
            if (ghost == null) return;
            if (caster.GhostAnimators == null || ghost.Pointer != caster.AnimGhostPtr)
            {
                caster.AnimGhostPtr = ghost.Pointer;
                caster.GhostAnimators = ghost.GetComponentsInChildren<Animator>(true);
            }
            int hash = Animator.StringToHash(trait.AnimationName);
            bool played = false;
            foreach (var anim in caster.GhostAnimators)
            {
                if (anim == null || !anim.isActiveAndEnabled) continue;
                int layer = Mathf.Clamp(trait.AnimationLayer, 0, Mathf.Max(0, anim.layerCount - 1));
                if (!anim.HasState(layer, hash)) continue;
                anim.CrossFade(trait.AnimationName, trait.Fade, layer);
                played = true;
                QueueAnimProbe(anim, trait.AnimationName, hash, layer);
            }
            if (played) { GhostAnimPlayed++; return; }
            GhostAnimMissing++;
            if (MissingStateLogged.Add(trait.AnimationName))
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][casts] Ghost {caster.Slot} has no animator state '{trait.AnimationName}' (layer {trait.AnimationLayer}) for '{s.gameObject.name}' " +
                    $"(ghost built from that player={who!.GhostFromRemote}, {caster.GhostAnimators.Length} animators).");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Ghost animation: {ex.Message}");
        }
    }
}

// ---------------- Harmony patches: the remote caster is visual only (every one wrapped in try/catch) ----------------

// NOT patched on purpose: Skill.ApplyEffects / DoApplyEffects and SkillEffect.ApplyEffect. ApplyEffects and
// DoApplyEffects end in a by-value Nullable<int>: an 8-byte struct, which the x64 ABI passes in a register, but the
// Il2CppInterop native->managed trampoline reads it as a pointer (NRE in Buffer.Memmove), so the original never
// ran and NO skill applied any effect anymore (fase 4C, first test). Effects are blocked downstream instead,
// at entry points whose structs are all > 8 bytes (passed by pointer, like the patches that already work).

/// <summary>Safety net and main damage block: nothing the remote caster does changes anyone's health, and
/// nothing changes its own. The phase 3 ChangeEntityHealth patches also skip it (no ClientDamage from it).
/// DoT ticks keep the applying entity as attacker, but statuses are blocked at the source below anyway.</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.ChangeEntityHealth))]
public static class RemoteCasterDamageBlockPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(Entity attackerEntity, Entity targetEntity, float healthChange,
        ref float effectiveHealthChange, ref float absorbedDamage, ref bool isImmune)
    {
        try
        {
            bool casterAttacks = RemoteCaster.IsCaster(attackerEntity);
            if (!casterAttacks && !RemoteCaster.IsCaster(targetEntity)) return true;
            CastSync.CountBlockedDamage();
            if (casterAttacks) ReplicaCombatText.OnBlockedHit(targetEntity, healthChange); // the effect still draws the number
            effectiveHealthChange = 0f;
            absorbedDamage = 0f;
            isImmune = false;
            return false;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] RemoteCasterDamageBlockPatch: {ex}");
            return true;
        }
    }
}

/// <summary>Every status a skill effect applies (burn, poison, bleed, slow, doom... and the DoT ticks that follow)
/// enters through here with the applying entity. Signature is safe for the trampoline: AbnormalStatusApplyData
/// is 0x6C bytes (passed by pointer), the rest are pointers, floats, ushorts, an enum and bools.</summary>
[HarmonyPatch(typeof(AbnormalStatusComponent), nameof(AbnormalStatusComponent.ApplyEffects))]
public static class RemoteCasterStatusBlockPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Entity applyingEntity)
    {
        try
        {
            if (!RemoteCaster.IsCaster(applyingEntity)) return true;
            CastSync.CountBlockedEffects();
            return false;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] RemoteCasterStatusBlockPatch: {ex}");
            return true;
        }
    }
}

/// <summary>Pushback scales with this multiplier, computed from the caster's stats: zero for the remote caster,
/// so replayed hits don't shove the host's enemies (or the client's puppets against their snapshots).</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.CalculatePushbackForceMultiplier))]
public static class RemoteCasterPushbackBlockPatch
{
    [HarmonyPostfix]
    public static void Postfix(StatsComponent casterStats, ref float __result)
    {
        try
        {
            if (!RemoteCaster.IsCasterStats(casterStats)) return;
            CastSync.CountBlockedEffects();
            __result = 0f;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] RemoteCasterPushbackBlockPatch: {ex}");
        }
    }
}
