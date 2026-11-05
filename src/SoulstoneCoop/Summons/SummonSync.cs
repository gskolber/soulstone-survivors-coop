using ECM2.Examples.Animation.UnityCharacterAnimatorExample;
using HarmonyLib;
using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Summons;

/// <summary>
/// Phase 5, item 5: the partner's summons, companions and the Sentinel's panther, as VISUAL-ONLY clones, the same way
/// the partner is a ghost. Nothing here creates an Entity: the clone is the summon's prefab instantiated under an
/// inactive holder and stripped (VisualGhostFactory.StripToVisualOnly) before it's ever switched on.
///
/// Damage is untouched: each side's own summons keep hitting their own world. On the client their hits on the puppets go
/// through ClientPuppetHealthClampPatch -> ClientPuppets.RecordDamage, which counts any attacker that isn't an enemy
/// (and isn't the remote caster) into ClientDamage. On the host they hit the real enemies.
///
/// What each side tracks of its own:
/// - Entity summons: UnitUtil.SetupSummonStats(caster, summoned, originalPrefab, ...) with the local player (or one of
///   its tracked summons) as caster. The prefab is a direct reference in the summoning skill's SummonEntity*Effect, so
///   the key is that skill ("Skill-Prefabs/&lt;key&gt;", the same asset on both machines) plus the prefab name.
/// - Companions: the local player's CompanionComponent instances (CompanionInfo.CompanionPrefab, "Companions/&lt;name&gt;").
/// - The panther: NaturesGuardianComponent.PantherInstance, a NavMeshAgent, not an Entity. Its prefab is
///   SkillTreeNaturesGuardianSO.PantherPrefab. It's hidden while the Sentinel rides it.
///
/// Packets (ReliableOrdered except the states):
///   SummonSpawn   = 51: id u16 | kind u8 | anim u8 | skillKey str | prefabName str | resPath str | scale f32 | pos 3f | yaw f
///   SummonDespawn = 52: id u16
///   SummonStates  = 53: count u8 | count * (id u16 | pos 3f | yaw f), ~12 Hz, Sequenced
/// The spawn of every live one is sent again every 3 s, so the other side recovers by itself from its own resets (scene
/// change, the partner down and back, a late Hello).
/// </summary>
public static class SummonSync
{
    public enum Kind : byte { EntitySummon = 0, Companion = 1, Panther = 2 }

    /// <summary>Which locomotion parameters the source's own animator script writes (so the clone can get the same):
    /// CharacterAnimator writes Forward/Turn/OnGround (+ ForwardMovement/SidewaysMovement when strafing), the panther's
    /// NaturesGuardianComponent writes Forward (constructor 0x7D11F0). Never read from the controller: the interop
    /// can't list Animator.parameters (the 0.8.0 locomotion regression).</summary>
    public enum AnimKind : byte { None = 0, Character = 1, CharacterStrafing = 2, ForwardOnly = 3 }

    private const float StateInterval = 1f / 12f;
    private const float RefreshInterval = 3f;
    private const float PollInterval = 0.5f;
    private const int MaxTracked = 32;
    private const float InterpDelay = 0.12f;

    private static readonly int ForwardHash = Animator.StringToHash("Forward");
    private static readonly int TurnHash = Animator.StringToHash("Turn");
    private static readonly int OnGroundHash = Animator.StringToHash("OnGround");
    private static readonly int ForwardMovementHash = Animator.StringToHash("ForwardMovement");
    private static readonly int SidewaysMovementHash = Animator.StringToHash("SidewaysMovement");

    private static bool _hooked;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerDisconnected += _ => { if (!NetworkManager.HasPeer) { ClearOwn(); ClearRemote("everyone disconnected"); } };
        Players.Left += ClearRemoteSlot;
    }

    /// <summary>PortalSync.OnLocalSceneGone: both lists belong to the scene that just went.</summary>
    public static void OnLocalSceneGone()
    {
        ClearOwn();
        ClearRemote("local scene gone");
    }

    public static void Tick(float dt)
    {
        try { TickOwn(dt); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][summons] Own: {ex.Message}"); }
        try { TickRemote(dt); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][summons] Remote: {ex.Message}"); }

        _telemetryTimer += dt;
        if (_telemetryTimer < 5f) return;
        _telemetryTimer = 0f;
        if (Own.Count + Remote.Count + _sentSpawns + _recvSpawns + _prefabFailures == 0) return;
        int visible = 0;
        foreach (var r in Remote.Values) if (r.Go != null) visible++;
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][summons] own={Own.Count} sentSpawns={_sentSpawns} sentStates={_sentStates} sentDespawns={_sentDespawns} | " +
            $"partner known={Remote.Count} visible={visible} recvSpawns={_recvSpawns} recvStates={_recvStates} recvDespawns={_recvDespawns} " +
            $"prefabFailures={_prefabFailures}{(FailedNames.Count > 0 ? " (" + string.Join(", ", FailedNames) + ")" : "")} buildErrors={_buildErrors}");
        _sentSpawns = _sentStates = _sentDespawns = _recvSpawns = _recvStates = _recvDespawns = 0;
    }

    private static float _telemetryTimer;
    private static int _sentSpawns, _sentStates, _sentDespawns, _recvSpawns, _recvStates, _recvDespawns, _prefabFailures, _buildErrors;

    // =====================================================================================
    // Own side
    // =====================================================================================

    private sealed class Owned
    {
        public ushort Id;
        public Kind Kind;
        public AnimKind Anim;
        public Entity? Entity;
        public NaturesGuardianComponent? Guardian;
        public IntPtr Ptr;
        public string SkillKey = "", PrefabName = "", ResPath = "";
        public bool Visible;   // currently announced to the partner (spawned, not despawned)
    }

    private static readonly Dictionary<IntPtr, Owned> Own = new();
    private static ushort _nextId = 1;
    private static float _stateTimer, _refreshTimer, _pollTimer;
    private static readonly Dictionary<IntPtr, string> SkillKeyByPrefab = new();

    private static ushort NextId()
    {
        ushort id = _nextId++;
        if (_nextId == 0) _nextId = 1;
        return id;
    }

    private static void ClearOwn()
    {
        Own.Clear();
        SkillKeyByPrefab.Clear();
    }

    /// <summary>SetupSummonStats prefix (SummonOwnerCapturePatch): a summon of the local player or of one of its summons.</summary>
    internal static void OnSummonSetup(Entity? caster, Entity summoned, Entity? prefab)
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null || caster == null || !CoopSession.Connected) return;
        bool ours = caster.Pointer == local.Pointer || (Own.TryGetValue(caster.Pointer, out var parent) && parent.Kind != Kind.Panther);
        if (!ours) return;
        if (Own.TryGetValue(summoned.Pointer, out var old)) Forget(old, "pooled instance set up again");
        if (Own.Count >= MaxTracked) return;
        var name = Enemies.EnemyPrefabKeys.StripClone(prefab != null ? prefab.name : summoned.gameObject.name);
        var o = new Owned
        {
            Id = NextId(), Kind = Kind.EntitySummon, Entity = summoned, Ptr = summoned.Pointer, PrefabName = name,
            SkillKey = prefab != null ? FindSkillKey(local, prefab) : "",
            Anim = AnimOf(summoned),
        };
        Own[o.Ptr] = o;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][summons] Own summon #{o.Id} '{name}' (skill '{o.SkillKey}', anim {o.Anim}).");
    }

    /// <summary>The skill of the local player whose summon effects list this prefab (cached per prefab).</summary>
    private static string FindSkillKey(Entity local, Entity prefab)
    {
        if (SkillKeyByPrefab.TryGetValue(prefab.Pointer, out var k)) return k;
        string found = "";
        try
        {
            foreach (var c in local.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity>? list =
                    c.TryCast<SummonEntityEffect>()?.PossiblePrefabs
                    ?? c.TryCast<SummonEntityPositionalEffect>()?.PossiblePrefabs
                    ?? c.TryCast<SummonEntityAndForgetPositionalEffect>()?.PossiblePrefabs;
                if (list == null) continue;
                bool hit = false;
                for (int i = 0; i < list.Length && !hit; i++) hit = list[i] != null && list[i].Pointer == prefab.Pointer;
                if (!hit) continue;
                var skill = c.GetComponentInParent<Skill>(true);
                if (skill != null) { found = Enemies.EnemyPrefabKeys.StripClone(skill.gameObject.name); break; }
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][summons] Finding the skill of '{prefab.name}': {ex.Message}"); }
        SkillKeyByPrefab[prefab.Pointer] = found;
        return found;
    }

    private static AnimKind AnimOf(Component root)
    {
        try
        {
            var ca = root.GetComponentInChildren<CharacterAnimator>(true);
            if (ca != null) return ca.HasStrafingMovement ? AnimKind.CharacterStrafing : AnimKind.Character;
        }
        catch { /* none */ }
        return AnimKind.None;
    }

    private static void Forget(Owned o, string why)
    {
        Own.Remove(o.Ptr);
        if (o.Visible) SendDespawn(o.Id);
    }

    private static void TickOwn(float dt)
    {
        var local = GameState.LocalPlayerEntity;
        if (!CoopSession.Connected || !CoopSession.LocalInRun || local == null)
        {
            if (Own.Count > 0) ClearOwn();
            return;
        }

        _pollTimer -= dt;
        if (_pollTimer <= 0f)
        {
            _pollTimer = PollInterval;
            PollCompanions(local);
            PollPanther(local);
        }

        bool partnerHere = CoopSession.AnyRemoteInRun;
        _refreshTimer -= dt;
        bool refresh = _refreshTimer <= 0f;
        if (refresh) _refreshTimer = RefreshInterval;

        List<Owned>? gone = null;
        foreach (var o in Own.Values)
        {
            bool alive = IsAlive(o);
            if (!alive)
            {
                if (o.Kind == Kind.Panther) { if (o.Visible) { o.Visible = false; if (partnerHere) SendDespawn(o.Id); } continue; }
                (gone ??= new List<Owned>()).Add(o);
                continue;
            }
            if (partnerHere && (!o.Visible || refresh)) { SendSpawn(o); o.Visible = true; }
        }
        if (gone != null) foreach (var o in gone) Forget(o, "died / recycled");

        if (!partnerHere) { foreach (var o in Own.Values) o.Visible = false; return; }
        _stateTimer += dt;
        if (_stateTimer < StateInterval) return;
        _stateTimer = 0f;
        SendStates();
    }

    private static bool IsAlive(Owned o)
    {
        if (o.Kind == Kind.Panther)
        {
            var g = o.Guardian;
            if (g == null) return false;
            var p = g.PantherInstance;
            return p != null && g.IsPantherInGame && !g.IsRiding && p.gameObject.activeInHierarchy;
        }
        var e = o.Entity;
        if (e == null || !e.gameObject.activeInHierarchy) return false;
        var h = e._health;
        return h == null || !h.HasBeenKilled;
    }

    private static Transform? TransformOf(Owned o)
    {
        if (o.Kind == Kind.Panther) { var p = o.Guardian != null ? o.Guardian.PantherInstance : null; return p != null ? p.transform : null; }
        return o.Entity != null ? o.Entity.transform : null;
    }

    private static readonly HashSet<IntPtr> SeenCompanions = new();

    private static void PollCompanions(Entity local)
    {
        CompanionComponent? cc = null;
        try { cc = local.GetComponent<CompanionComponent>(); } catch { }
        if (cc == null || cc.CompanionInfo == null) return;
        for (int i = 0; i < cc.CompanionInfo.Count; i++)
        {
            var info = cc.CompanionInfo[i];
            if (info == null || info.CompanionInstances == null) continue;
            for (int j = 0; j < info.CompanionInstances.Count; j++)
            {
                Entity? e = null;
                try { e = info.CompanionInstances[j].Entity; } catch { }
                if (e == null || Own.ContainsKey(e.Pointer) || !e.gameObject.activeInHierarchy || Own.Count >= MaxTracked) continue;
                var prefab = info.CompanionPrefab;
                string name = Enemies.EnemyPrefabKeys.StripClone(prefab != null ? prefab.name : e.gameObject.name);
                var o = new Owned
                {
                    Id = NextId(), Kind = Kind.Companion, Entity = e, Ptr = e.Pointer, PrefabName = name,
                    ResPath = CompanionPath(prefab, name), Anim = AnimOf(e),
                };
                Own[o.Ptr] = o;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][summons] Own companion #{o.Id} '{name}' (res '{o.ResPath}', anim {o.Anim}).");
            }
        }
    }

    /// <summary>CompanionComponent.AddCompanionType loads them from Resources "Companions/..." (globalgamemanagers lists
    /// companions/dog, hydra, parrot...): accepted only if that path gives back this very prefab.</summary>
    private static string CompanionPath(Entity? prefab, string name)
    {
        if (prefab == null) return "";
        foreach (var path in new[] { "Companions/" + name, "Companions/SkillTree/" + name })
        {
            try
            {
                var loaded = ResourcesUtil.Load<Entity>(path);
                if (loaded != null && loaded.Pointer == prefab.Pointer) return path;
            }
            catch { /* not there */ }
        }
        return "";
    }

    private static void PollPanther(Entity local)
    {
        foreach (var o in Own.Values) if (o.Kind == Kind.Panther) return; // one per player
        NaturesGuardianComponent? g = null;
        try { g = local.GetComponentInChildren<NaturesGuardianComponent>(true); } catch { }
        if (g == null || g.PantherInstance == null) return;
        var o2 = new Owned
        {
            Id = NextId(), Kind = Kind.Panther, Guardian = g, Ptr = g.Pointer, Anim = AnimKind.ForwardOnly,
            PrefabName = Enemies.EnemyPrefabKeys.StripClone(g.PantherInstance.gameObject.name),
        };
        Own[o2.Ptr] = o2;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][summons] Own panther #{o2.Id} '{o2.PrefabName}'.");
    }

    private static void SendSpawn(Owned o)
    {
        var t = TransformOf(o);
        if (t == null) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.SummonSpawn);
        w.Put(o.Id);
        w.Put((byte)o.Kind);
        w.Put((byte)o.Anim);
        w.Put(o.SkillKey);
        w.Put(o.PrefabName);
        w.Put(o.ResPath);
        w.Put(t.localScale.x);
        var p = t.position;
        w.Put(p.x); w.Put(p.y); w.Put(p.z);
        w.Put(t.eulerAngles.y);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _sentSpawns++;
    }

    private static void SendDespawn(ushort id)
    {
        if (!CoopSession.Connected) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.SummonDespawn);
        w.Put(id);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _sentDespawns++;
    }

    private static void SendStates()
    {
        int n = 0;
        foreach (var o in Own.Values) if (o.Visible) n++;
        if (n == 0) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.SummonStates);
        w.Put((byte)Math.Min(n, 255));
        int written = 0;
        foreach (var o in Own.Values)
        {
            if (!o.Visible || written >= 255) continue;
            var t = TransformOf(o);
            var p = t != null ? t.position : Vector3.zero;
            w.Put(o.Id);
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
            w.Put(t != null ? t.eulerAngles.y : 0f);
            written++;
        }
        NetworkManager.Send(w, DeliveryMethod.Sequenced);
        _sentStates++;
    }

    // =====================================================================================
    // Partner side
    // =====================================================================================

    private struct Sample { public float T; public Vector3 Pos; public float Yaw; }

    private sealed class Mirror
    {
        public byte Slot; // phase 9: whose summon it is (PacketRouter.From)
        public ushort Id;
        public Kind Kind;
        public AnimKind Anim;
        public string SkillKey = "", PrefabName = "", ResPath = "";
        public float Scale = 1f;
        public GameObject? Go;
        public Animator[]? Animators;
        public bool Failed;
        public readonly List<Sample> Buffer = new();
        public Vector3 LastPos;
        public bool HasLast;
        public float MaxSpeedSeen = 4f;
    }

    /// <summary>Phase 9: keyed by (slot << 16 | id): each player numbers its own summons.</summary>
    private static readonly Dictionary<int, Mirror> Remote = new();
    private static int KeyOf(byte slot, ushort id) => (slot << 16) | id;
    private static readonly List<int> GoneMirrors = new();
    private static readonly Dictionary<string, GameObject?> TemplateCache = new();
    private static readonly HashSet<string> FailedNames = new();

    private static void ClearRemote(string why)
    {
        if (Remote.Count == 0) return;
        foreach (var m in Remote.Values) DestroyVisual(m);
        Remote.Clear();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][summons] Partner's summons cleared ({why}).");
    }

    private static void DestroyVisual(Mirror m)
    {
        if (m.Go != null) UnityEngine.Object.Destroy(m.Go);
        m.Go = null;
        m.Animators = null;
        m.Buffer.Clear();
        m.HasLast = false;
    }

    public static void OnSpawn(NetDataReader r)
    {
        ushort id = r.GetUShort();
        var kind = (Kind)r.GetByte();
        var anim = (AnimKind)r.GetByte();
        string skillKey = r.GetString(), name = r.GetString(), res = r.GetString();
        float scale = r.GetFloat();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        float yaw = r.GetFloat();
        _recvSpawns++;
        byte from = PacketRouter.From;
        if (!Players.IsPresent(from)) return;
        if (!Remote.TryGetValue(KeyOf(from, id), out var m))
        {
            m = new Mirror { Slot = from, Id = id };
            Remote[KeyOf(from, id)] = m;
        }
        m.Kind = kind; m.Anim = anim; m.SkillKey = skillKey; m.PrefabName = name; m.ResPath = res;
        m.Scale = scale > 0.01f ? scale : 1f;
        AddSample(m, pos, yaw);
    }

    public static void OnDespawn(NetDataReader r)
    {
        ushort id = r.GetUShort();
        _recvDespawns++;
        if (!Remote.TryGetValue(KeyOf(PacketRouter.From, id), out var m)) return;
        DestroyVisual(m);
        Remote.Remove(KeyOf(PacketRouter.From, id));
    }

    public static void OnStates(NetDataReader r)
    {
        int n = r.GetByte();
        _recvStates++;
        for (int i = 0; i < n; i++)
        {
            ushort id = r.GetUShort();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            float yaw = r.GetFloat();
            if (Remote.TryGetValue(KeyOf(PacketRouter.From, id), out var m)) AddSample(m, pos, yaw);
        }
    }

    private static void AddSample(Mirror m, Vector3 pos, float yaw)
    {
        float now = Time.unscaledTime;
        if (m.Buffer.Count > 0 && (m.Buffer[m.Buffer.Count - 1].Pos - pos).sqrMagnitude > 64f) { m.Buffer.Clear(); m.HasLast = false; }
        m.Buffer.Add(new Sample { T = now, Pos = pos, Yaw = yaw });
        while (m.Buffer.Count > 2 && m.Buffer[0].T < now - 1f) m.Buffer.RemoveAt(0);
    }

    private static void TickRemote(float dt)
    {
        if (!CoopSession.LocalInRun)
        {
            ClearRemote("we are out of the run");
            return;
        }
        GoneMirrors.Clear();
        foreach (var kv in Remote)
        {
            var m = kv.Value;
            // That player left the run: its summons go with it (they come back with its next spawn refresh).
            if (!CoopSession.InRun(m.Slot)) { DestroyVisual(m); GoneMirrors.Add(kv.Key); continue; }
            if (Life.CoopLife.IsRemoteDown(m.Slot)) { if (m.Go != null) DestroyVisual(m); continue; } // back with the next spawn refresh
            if (m.Go == null && !m.Failed) Build(m);
            if (m.Go == null) continue;
            Place(m, dt);
        }
        foreach (var k in GoneMirrors) Remote.Remove(k);
    }

    /// <summary>Phase 9: that player left the session.</summary>
    private static void ClearRemoteSlot(byte slot)
    {
        GoneMirrors.Clear();
        foreach (var kv in Remote) if (kv.Value.Slot == slot) { DestroyVisual(kv.Value); GoneMirrors.Add(kv.Key); }
        foreach (var k in GoneMirrors) Remote.Remove(k);
    }

    private static void Place(Mirror m, float dt)
    {
        if (m.Buffer.Count == 0 || m.Go == null) return;
        float renderT = Time.unscaledTime - InterpDelay;
        Vector3 pos; float yaw;
        var first = m.Buffer[0];
        var last = m.Buffer[m.Buffer.Count - 1];
        if (m.Buffer.Count == 1 || renderT <= first.T) { pos = (m.Buffer.Count == 1 ? last : first).Pos; yaw = (m.Buffer.Count == 1 ? last : first).Yaw; }
        else if (renderT >= last.T) { pos = last.Pos; yaw = last.Yaw; }
        else
        {
            int i = m.Buffer.Count - 2;
            while (i > 0 && m.Buffer[i].T > renderT) i--;
            var a = m.Buffer[i]; var b = m.Buffer[i + 1];
            float k = Mathf.Clamp01((renderT - a.T) / Mathf.Max(0.001f, b.T - a.T));
            pos = Vector3.Lerp(a.Pos, b.Pos, k);
            yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, k);
        }
        var t = m.Go.transform;
        t.position = pos;
        t.eulerAngles = new Vector3(0f, yaw, 0f);
        Animate(m, pos, dt);
    }

    private static void Animate(Mirror m, Vector3 pos, float dt)
    {
        if (m.Anim == AnimKind.None || m.Animators == null || dt <= 0f) { m.LastPos = pos; m.HasLast = true; return; }
        Vector3 vel = m.HasLast && Time.timeScale > 0f ? (pos - m.LastPos) / dt : Vector3.zero;
        m.LastPos = pos;
        m.HasLast = true;
        vel.y = 0f;
        float speed = vel.magnitude;
        m.MaxSpeedSeen = Mathf.Max(m.MaxSpeedSeen * 0.999f, speed, 4f);
        Vector3 lv = m.Go!.transform.InverseTransformDirection(vel) / m.MaxSpeedSeen;
        float fwd01 = Mathf.Clamp01(speed / m.MaxSpeedSeen);
        float animDt = Time.deltaTime > 0f ? Time.deltaTime : dt;
        foreach (var a in m.Animators)
        {
            try
            {
                if (a == null || !a.isActiveAndEnabled) continue;
                a.SetFloat(ForwardHash, fwd01, 0.1f, animDt);
                if (m.Anim == AnimKind.ForwardOnly) continue;
                a.SetFloat(TurnHash, 0f, 0.1f, animDt);
                a.SetBool(OnGroundHash, true);
                if (m.Anim == AnimKind.CharacterStrafing)
                {
                    a.SetFloat(ForwardMovementHash, Mathf.Clamp(lv.z, -1f, 1f), 0.1f, animDt);
                    a.SetFloat(SidewaysMovementHash, Mathf.Clamp(lv.x, -1f, 1f), 0.1f, animDt);
                }
            }
            catch { /* this animator only, this frame only */ }
        }
    }

    // ---------------- building a clone ----------------

    private static void Build(Mirror m)
    {
        var template = ResolveTemplate(m);
        if (template == null)
        {
            m.Failed = true;
            _prefabFailures++;
            if (FailedNames.Count < 8) FailedNames.Add(m.PrefabName);
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][summons] No prefab for the partner's {m.Kind} '{m.PrefabName}' (skill '{m.SkillKey}', res '{m.ResPath}') - it stays invisible.");
            return;
        }
        GameObject? holder = null;
        try
        {
            holder = new GameObject("SoulstoneCoop_SummonBuild");
            holder.SetActive(false); // no Awake/OnEnable on anything until it's stripped: no Entity ever registers
            var go = UnityEngine.Object.Instantiate(template, holder.transform);
            go.name = $"SoulstoneCoop_PartnerSummon_{m.Id}_{m.PrefabName}";
            VisualGhostFactory.StripToVisualOnly(go);
            go.transform.SetParent(null, false);
            go.transform.localScale = Vector3.one * m.Scale;
            if (m.Buffer.Count > 0) go.transform.position = m.Buffer[m.Buffer.Count - 1].Pos;
            go.SetActive(true);
            var anims = go.GetComponentsInChildren<Animator>(true);
            foreach (var a in anims)
            {
                try { a.applyRootMotion = false; a.cullingMode = AnimatorCullingMode.AlwaysAnimate; } catch { }
            }
            m.Go = go;
            m.Animators = anims;
            m.HasLast = false;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][summons] Partner's {m.Kind} #{m.Id} '{m.PrefabName}' shown (visual only, {anims.Length} animator(s), anim {m.Anim}).");
        }
        catch (Exception ex)
        {
            m.Failed = true;
            _buildErrors++;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][summons] Building '{m.PrefabName}': {ex.Message}");
        }
        finally
        {
            if (holder != null) UnityEngine.Object.Destroy(holder);
        }
    }

    private static GameObject? ResolveTemplate(Mirror m)
    {
        string cacheKey = $"{(byte)m.Kind}|{m.SkillKey}|{m.ResPath}|{m.PrefabName}";
        if (TemplateCache.TryGetValue(cacheKey, out var cached) && cached != null) return cached;
        GameObject? found = null;
        try
        {
            if (m.Kind == Kind.Panther) found = FindPantherPrefab();
            else
            {
                if (m.ResPath.Length > 0) found = ResourcesUtil.Load<Entity>(m.ResPath)?.gameObject;
                if (found == null && m.SkillKey.Length > 0) found = FindInSkillPrefab(m.SkillKey, m.PrefabName);
                found ??= FindLoadedEntityAsset(m.PrefabName);
                if (found == null && m.Kind == Kind.Companion) found = ResourcesUtil.Load<Entity>("Companions/" + m.PrefabName)?.gameObject;
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][summons] Resolving '{m.PrefabName}': {ex.Message}"); }
        TemplateCache[cacheKey] = found;
        return found;
    }

    /// <summary>The summoning skill's own prefab (same on both machines) and its SummonEntity*Effect.PossiblePrefabs.</summary>
    private static GameObject? FindInSkillPrefab(string skillKey, string name)
    {
        var skill = ResourcesUtil.Load<Skill>("Skill-Prefabs/" + skillKey);
        if (skill == null) return null;
        foreach (var c in skill.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            var list = c.TryCast<SummonEntityEffect>()?.PossiblePrefabs
                ?? c.TryCast<SummonEntityPositionalEffect>()?.PossiblePrefabs
                ?? c.TryCast<SummonEntityAndForgetPositionalEffect>()?.PossiblePrefabs;
            if (list == null) continue;
            for (int i = 0; i < list.Length; i++)
                if (list[i] != null && Enemies.EnemyPrefabKeys.StripClone(list[i].name) == name) return list[i].gameObject;
        }
        return null;
    }

    private static GameObject? FindLoadedEntityAsset(string name)
    {
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Entity>());
        for (int i = 0; i < all.Length; i++)
        {
            var e = all[i]?.TryCast<Entity>();
            if (e == null || e.gameObject.scene.IsValid()) continue; // prefabs have no valid scene
            if (e.name == name) return e.gameObject;
        }
        return null;
    }

    /// <summary>The Sentinel skill tree item that holds the panther prefab (SkillTreeNaturesGuardianSO.PantherPrefab).</summary>
    private static GameObject? FindPantherPrefab()
    {
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SkillTreeNaturesGuardianSO>());
        for (int i = 0; i < all.Length; i++)
        {
            var so = all[i]?.TryCast<SkillTreeNaturesGuardianSO>();
            if (so != null && so.PantherPrefab != null) return so.PantherPrefab.gameObject;
        }
        return null;
    }
}

/// <summary>Both sides: remember the summons of the local player (or of its summons). Next to the host's
/// HostSummonCapturePatch on the same method. Signature: references and bools only, trampoline-safe.</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.SetupSummonStats))]
public static class SummonOwnerCapturePatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity casterEntity, Entity summonedEntity, Entity originalSummonPrefab)
    {
        try
        {
            if (summonedEntity == null || NetworkManager.Role == NetRole.None) return;
            // A replayed (visual-only) skill must never put a real unit in this world: the partner's own units are
            // shown here as SummonSync clones. Covers the stand-ins of both sides.
            if (Skills.RemoteCaster.IsCaster(casterEntity) || Enemies.RemoteProxy.IsProxy(casterEntity))
            {
                Skills.RemoteCaster.OnReplicaSummon(summonedEntity);
                return;
            }
            SummonSync.OnSummonSetup(casterEntity, summonedEntity, originalSummonPrefab);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] SummonOwnerCapturePatch: {ex}");
        }
    }
}
