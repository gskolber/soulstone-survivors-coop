using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Summons;

/// <summary>
/// The partners' passive and "mini-game" objects, visual only (docs/passivas-notas.md): the fallen legionnaires the
/// Legionnaire rescues, the Demon Hunter's binding stone, rifts, shards and orbs, shrines and gates, altars, footprints,
/// ghost ship treasures, machine-spirit crystals, elemental orbs, mana / apocalypse-soul crystals.
///
/// They belong to one player's own mechanic (a SkillTree*SO applied to that player), so they exist only on that
/// player's machine, and only that player can use them. Each one goes through its own Setup when it's placed (patched by
/// hand, one postfix per type; every signature takes references, floats, ints and bools only). The owner tracks it and
/// tells the others where it is (PassiveSpawn, resent every 3 s), moves it (PassiveStates ~10 Hz, only what moved) and
/// when it's gone (PassiveDespawn: inactive or destroyed, checked every 0.25 s). The others show a clone of the same
/// prefab (a loaded prefab of that type and name: the skill tree assets of every character are in memory), stripped to
/// its renderers, particles, VFX graphs and lights (VisualGhostFactory.StripToVisualOnly with effects): no colliders, no
/// scripts, so nobody else can trigger, collect or block it.
/// CollectableSecretObject is also the map's own collectable: only the mana and apocalypse-soul crystals of the skill
/// trees are mirrored (their prefab names).
/// </summary>
public static partial class PassiveObjectSync
{
    private static readonly Type[] Types =
    {
        typeof(FallenLegionnaireComponent), typeof(DemonicBindingStone), typeof(ArcanePowerRift),
        typeof(DivineInspirationShard), typeof(DivineInspirationOrb), typeof(CursedShrineToriiGate),
        typeof(CursedShrineShrine), typeof(ChaosEnergyAltar), typeof(CallOfTheWildFootprint),
        typeof(GhostShipTreasure), typeof(MachineSpiritMineral), typeof(ElementalOverloadOrb),
        typeof(CollectableSecretObject),
    };
    private const int CollectableIndex = 12;

    private const float GoneCheckInterval = 0.25f;
    private const float StateInterval = 0.1f;
    private const float RefreshInterval = 3f;
    private const int MaxOwned = 64;

    private static readonly Dictionary<MethodBase, int> IndexByMethod = new();

    // ---------------- owner ----------------

    private sealed class Owned
    {
        public ushort Id;
        public byte Type;
        public string Name = "";
        public Component? Comp;
        public IntPtr Ptr;
        public Vector3 SentPos;
        public float SentYaw;
        public float Radius; // 1.4.1: the object's area on the ground (largest collider), for the others' marker
        // 1.4.1 part states: what the object's own script switched on / resized (the copy runs no scripts)
        public List<Part>? Parts;
        public float PartsAt = -99f;
        public readonly Dictionary<uint, PartState> SentParts = new();
        // 1.5.0 material values (PassiveObjectSync.Generic.cs)
        public List<MatWatch>? Mats;
        public float MatsAt = -99f;
        public readonly Dictionary<ulong, Vector4> SentMats = new();
    }

    private struct PartState
    {
        public bool On;
        public Vector3 Scale;
    }

    private struct Part
    {
        public uint Hash;
        public Transform T;
        public string Path;
    }

    private static readonly Dictionary<IntPtr, Owned> Own = new();
    private static readonly List<Owned> Gone = new();
    private static ushort _nextId = 1;
    private static float _goneTimer, _stateTimer, _refreshTimer, _partsTimer;
    private const float PartsInterval = 0.2f;
    private const int MaxParts = 400;
    private static int _sentParts, _recvParts, _appliedParts, _unmatchedParts;
    private static HashSet<string>? _crystalNames;
    private static int _sentSpawns, _sentStates, _recvSpawns, _built, _missing, _errors;
    private static float _telemetryTimer;

    internal static void OnSetup(Component comp, MethodBase? original)
    {
        if (comp == null || original == null || NetworkManager.Role == NetRole.None) return;
        if (!IndexByMethod.TryGetValue(original, out int type))
        {
            int byType = original.DeclaringType != null ? Array.IndexOf(Types, original.DeclaringType) : -1;
            if (byType < 0) return;
            type = byType;
        }
        if (!CoopSession.LocalInRun || GameState.LocalPlayerEntity == null) return;
        string name = Enemies.EnemyPrefabKeys.StripClone(comp.gameObject.name);
        if (type == CollectableIndex && !CrystalNames().Contains(name)) return; // the map's own collectables
        Track(comp, (byte)type, name);
    }

    /// <summary>One of our own objects to mirror: by its typed Setup patch, or (1.5.0) found by the powers/ scan.</summary>
    private static void Track(Component comp, byte type, string name)
    {
        if (Own.TryGetValue(comp.Pointer, out var old)) Forget(old);
        if (Own.Count >= MaxOwned) return;
        ushort id = _nextId++;
        if (_nextId == 0) _nextId = 1;
        var o = new Owned { Id = id, Type = type, Name = name, Comp = comp, Ptr = comp.Pointer };
        Own[o.Ptr] = o;
        if (NetworkManager.HasPeer) { SendSpawn(o); SendParts(o, full: true); SendMaterials(o, full: true); }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][passives] Own {TypeName(type)} #{id} '{name}' at {comp.transform.position}.");
    }

    /// <summary>The prefab names of the skill trees' crystals (CollectableSecretObject used by a player mechanic).</summary>
    private static HashSet<string> CrystalNames()
    {
        if (_crystalNames != null) return _crystalNames;
        var set = new HashSet<string>();
        try
        {
            var mana = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SkillTreeManaTalentSO>());
            for (int i = 0; i < mana.Length; i++)
            {
                var p = mana[i]?.TryCast<SkillTreeManaTalentSO>()?.ManaCrystalPrefab;
                if (p != null) set.Add(p.gameObject.name);
            }
            var souls = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SkillTreeSoulHarvestTalentSO>());
            for (int i = 0; i < souls.Length; i++)
            {
                var p = souls[i]?.TryCast<SkillTreeSoulHarvestTalentSO>()?.ApocalypseSoulCrystalPrefab;
                if (p != null) set.Add(p.gameObject.name);
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] Crystal names: {ex.Message}"); }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][passives] Mirrored crystals: {(set.Count > 0 ? string.Join(", ", set) : "none found")}.");
        _crystalNames = set;
        return set;
    }

    private static void Forget(Owned o)
    {
        Own.Remove(o.Ptr);
        if (!NetworkManager.HasPeer) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.PassiveDespawn);
        w.Put(o.Id);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static void SendSpawn(Owned o)
    {
        var c = o.Comp;
        if (c == null) return;
        var t = c.transform;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.PassiveSpawn);
        w.Put(o.Id);
        w.Put(o.Type);
        w.Put(o.Name);
        var p = t.position;
        w.Put(p.x); w.Put(p.y); w.Put(p.z);
        w.Put(t.eulerAngles.y);
        w.Put(t.lossyScale.x);
        if (o.Radius <= 0f) o.Radius = AreaRadius(c);
        w.Put(o.Radius); // 1.4.1, appended: older readers stop before it
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        o.SentPos = p;
        o.SentYaw = t.eulerAngles.y;
        _sentSpawns++;
    }

    private static void TickOwn(float dt)
    {
        if (Own.Count == 0) return;
        if (!CoopSession.LocalInRun) { Own.Clear(); return; }

        _goneTimer += dt;
        if (_goneTimer >= GoneCheckInterval)
        {
            _goneTimer = 0f;
            Gone.Clear();
            foreach (var o in Own.Values)
                if (o.Comp == null || !o.Comp.gameObject.activeInHierarchy) Gone.Add(o);
            foreach (var o in Gone) Forget(o);
        }
        if (!NetworkManager.HasPeer) return;

        _refreshTimer += dt;
        if (_refreshTimer >= RefreshInterval)
        {
            _refreshTimer = 0f;
            foreach (var o in Own.Values) SendSpawn(o);
            foreach (var o in Own.Values) SendParts(o, full: true);
            foreach (var o in Own.Values) SendMaterials(o, full: true);
            return;
        }

        _partsTimer += dt;
        if (_partsTimer >= PartsInterval)
        {
            _partsTimer = 0f;
            foreach (var o in Own.Values) SendParts(o, full: false);
            foreach (var o in Own.Values) SendMaterials(o, full: false);
        }

        _stateTimer += dt;
        if (_stateTimer < StateInterval) return;
        _stateTimer = 0f;
        NetDataWriter? w = null;
        int countPos = 0, n = 0;
        foreach (var o in Own.Values)
        {
            if (o.Comp == null || n >= 255) continue;
            var t = o.Comp.transform;
            var p = t.position;
            float yaw = t.eulerAngles.y;
            if ((p - o.SentPos).sqrMagnitude < 0.0025f && Mathf.Abs(Mathf.DeltaAngle(yaw, o.SentYaw)) < 2f) continue;
            if (w == null)
            {
                w = new NetDataWriter();
                w.Put((byte)PacketType.PassiveStates);
                countPos = w.Length;
                w.Put((byte)0);
            }
            w.Put(o.Id);
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
            w.Put(yaw);
            o.SentPos = p;
            o.SentYaw = yaw;
            n++;
        }
        if (w == null) return;
        w.Data[countPos] = (byte)n;
        NetworkManager.Send(w, DeliveryMethod.Sequenced);
        _sentStates++;
    }

    /// <summary>1.4.1: how far the object reaches on the ground, in world units: its largest collider (the area the
    /// owner stands in, e.g. the Demon Hunter's binding stone). 0 if it has none enabled yet (measured again on the
    /// next 3 s refresh).</summary>
    private static float AreaRadius(Component c)
    {
        float best = 0f;
        try
        {
            var cols = c.GetComponentsInChildren(Il2CppType.Of<Collider>(), true); // non-generic: see SummonSync's 1.3.1 note
            for (int i = 0; i < cols.Length; i++)
            {
                var col = cols[i]?.TryCast<Collider>();
                if (col == null) continue;
                var s = col.transform.lossyScale;
                float sc = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
                float r;
                var sph = col.TryCast<SphereCollider>();
                var cap = sph == null ? col.TryCast<CapsuleCollider>() : null;
                var box = sph == null && cap == null ? col.TryCast<BoxCollider>() : null;
                if (sph != null) r = sph.radius * sc;
                else if (cap != null) r = cap.radius * sc;
                else if (box != null) r = 0.5f * Mathf.Max(Mathf.Abs(box.size.x * s.x), Mathf.Abs(box.size.z * s.z));
                else { var e = col.bounds.extents; r = Mathf.Max(e.x, e.z); }
                if (r > best) best = r;
            }
        }
        catch { /* no colliders readable: the default marker size */ }
        return best > 30f ? 0f : best; // a map-sized trigger isn't this object's area
    }

    // ---------------- 1.4.1: part states (generic, every hero) ----------------
    // The others' copy has no scripts, so whatever the owner's script does to the object after it's placed (the Demon
    // Hunter's stone sizes and shows its rune circle, CircleVfxTransform; shrines light up; ...) never happened there.
    // The owner sends each part's active flag and local scale, by the hash of its path under the object, on change
    // (5 Hz) and in full with the 3 s refresh; the copy applies them to its parts with the same path. Same prefab, same
    // paths: no per-hero code. Children the script instantiates at runtime have no counterpart (logged as unmatched).

    private static uint PathHash(string path)
    {
        uint h = 2166136261;
        for (int i = 0; i < path.Length; i++) { h ^= path[i]; h *= 16777619; }
        return h;
    }

    private static List<Part> CollectParts(Transform root)
    {
        var list = new List<Part>();
        var all = root.GetComponentsInChildren(Il2CppType.Of<Transform>(), true);
        for (int i = 0; i < all.Length && list.Count < MaxParts; i++)
        {
            var t = all[i]?.TryCast<Transform>();
            if (t == null || t.Pointer == root.Pointer) continue;
            string path = Enemies.EnemyPrefabKeys.StripClone(t.name);
            var up = t.parent;
            int guard = 0;
            while (up != null && up.Pointer != root.Pointer && guard++ < 32)
            {
                path = Enemies.EnemyPrefabKeys.StripClone(up.name) + "/" + path;
                up = up.parent;
            }
            list.Add(new Part { Hash = PathHash(path), T = t, Path = path });
        }
        return list;
    }

    private static readonly Dictionary<string, int> LoggedLayout = new();

    /// <summary>The object's parts once or twice per name and side, for comparing the owner's live object with the copy.</summary>
    private static void LogLayout(string side, string name, List<Part> parts)
    {
        string key = side + "|" + name;
        LoggedLayout.TryGetValue(key, out int n);
        if (n >= 2) return;
        LoggedLayout[key] = n + 1;
        var sb = new System.Text.StringBuilder();
        int shown = 0;
        foreach (var p in parts)
        {
            if (p.T == null) continue;
            if (shown++ >= 40) { sb.Append(" ..."); break; }
            var sc = p.T.localScale;
            sb.Append($" {p.Path}[{(p.T.gameObject.activeSelf ? "on" : "off")} {sc.x:F2},{sc.y:F2},{sc.z:F2}]");
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][passives] Layout ({side}) '{name}': {parts.Count} parts:{sb}");
    }

    private static void SendParts(Owned o, bool full)
    {
        var c = o.Comp;
        if (c == null || !NetworkManager.HasPeer) return;
        try
        {
            if (o.Parts == null || Time.unscaledTime - o.PartsAt > RefreshInterval)
            {
                o.Parts = CollectParts(c.transform);
                o.PartsAt = Time.unscaledTime;
                LogLayout("owner", o.Name, o.Parts);
            }
            NetDataWriter? w = null;
            int countPos = 0, n = 0;
            foreach (var p in o.Parts)
            {
                if (p.T == null || n >= 255) continue;
                var st = new PartState { On = p.T.gameObject.activeSelf, Scale = p.T.localScale };
                if (!full && o.SentParts.TryGetValue(p.Hash, out var prev) && prev.On == st.On && (prev.Scale - st.Scale).sqrMagnitude < 1e-5f) continue;
                if (w == null)
                {
                    w = new NetDataWriter();
                    w.Put((byte)PacketType.PassiveParts);
                    w.Put(o.Id);
                    countPos = w.Length;
                    w.Put((byte)0);
                }
                w.Put(p.Hash);
                w.Put(st.On);
                w.Put(st.Scale.x); w.Put(st.Scale.y); w.Put(st.Scale.z);
                o.SentParts[p.Hash] = st;
                n++;
            }
            if (w == null) return;
            w.Data[countPos] = (byte)n;
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            _sentParts += n;
        }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] Parts of '{o.Name}': {ex.Message}"); }
    }

    public static void OnParts(NetDataReader r)
    {
        ushort id = r.GetUShort();
        int n = r.GetByte();
        Remote.TryGetValue(KeyOf(PacketRouter.From, id), out var m);
        for (int i = 0; i < n; i++)
        {
            uint h = r.GetUInt();
            var st = new PartState { On = r.GetBool(), Scale = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()) };
            _recvParts++;
            if (m == null) continue;
            m.PartStates[h] = st;
            if (m.Go != null && !m.IsMarker) ApplyPart(m, h, st);
        }
    }

    private static void ApplyPart(Mirror m, uint h, PartState st)
    {
        if (m.PartMap == null) return;
        if (!m.PartMap.TryGetValue(h, out var t) || t == null) { _unmatchedParts++; return; }
        if (t.gameObject.activeSelf != st.On) t.gameObject.SetActive(st.On);
        t.localScale = st.Scale;
        _appliedParts++;
    }

    /// <summary>Right after a copy is built: index its parts and apply what the owner already sent.</summary>
    private static void IndexParts(Mirror m)
    {
        if (m.Go == null) return;
        try
        {
            var parts = CollectParts(m.Go.transform);
            m.PartMap = new Dictionary<uint, Transform>();
            foreach (var p in parts) m.PartMap[p.Hash] = p.T;
            LogLayout("copy", m.Name, parts);
            foreach (var kv in m.PartStates) ApplyPart(m, kv.Key, kv.Value);
            IndexRenderers(m); // 1.5.0
        }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] Indexing '{m.Name}': {ex.Message}"); }
    }

    // ---------------- the others' ----------------

    private sealed class Mirror
    {
        public byte Slot;
        public ushort Id;
        public byte Type;
        public string Name = "";
        public Vector3 Target;
        public float Yaw, Scale = 1f;
        public float Radius; // 1.4.1: the owner's measured area (0: unknown)
        public GameObject? Go;
        public bool Failed;
        public bool IsMarker;
        public readonly Dictionary<uint, PartState> PartStates = new(); // 1.4.1: latest per part, kept across rebuilds
        public Dictionary<uint, Transform>? PartMap;
        // 1.5.0 material values: latest per (renderer path hash, property name hash); the copy's renderers by path hash
        public readonly Dictionary<ulong, MatValue> MatStates = new();
        public Dictionary<uint, Renderer>? RendererMap;
    }

    private static readonly Dictionary<int, Mirror> Remote = new();
    private static readonly List<int> RemoveKeys = new();
    private static readonly Dictionary<string, GameObject?> Templates = new();
    private static readonly HashSet<string> LoggedMissing = new();
    private static float _remoteRunStart = -1f;

    private static int KeyOf(byte slot, ushort id) => (slot << 16) | id;

    public static void OnSpawn(NetDataReader r)
    {
        ushort id = r.GetUShort();
        byte type = r.GetByte();
        string name = r.GetString();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        float yaw = r.GetFloat(), scale = r.GetFloat();
        float radius = r.AvailableBytes >= 4 ? r.GetFloat() : 0f;
        _recvSpawns++;
        byte from = PacketRouter.From;
        if ((type >= Types.Length && type != PowersType) || !Players.IsPresent(from) || !CoopSession.LocalInRun) return;
        int key = KeyOf(from, id);
        if (!Remote.TryGetValue(key, out var m))
        {
            m = new Mirror { Slot = from, Id = id, Type = type, Name = name, Target = pos, Yaw = yaw, Scale = scale };
            Remote[key] = m;
        }
        else if (m.Name != name || m.Type != type)
        {
            DestroyMirror(m); // a pooled id reused for another object
            m.Name = name; m.Type = type; m.Failed = false;
        }
        m.Target = pos; m.Yaw = yaw; m.Scale = scale > 0.01f ? scale : 1f;
        if (radius > 0.05f && Mathf.Abs(radius - m.Radius) > 0.05f)
        {
            bool resize = m.IsMarker && m.Go != null;
            m.Radius = radius;
            if (resize) { DestroyMirror(m); m.Failed = false; } // rebuilt at the real size next tick
        }
    }

    public static void OnDespawn(NetDataReader r)
    {
        ushort id = r.GetUShort();
        int key = KeyOf(PacketRouter.From, id);
        if (!Remote.TryGetValue(key, out var m)) return;
        DestroyMirror(m);
        Remote.Remove(key);
    }

    public static void OnStates(NetDataReader r)
    {
        int n = r.GetByte();
        for (int i = 0; i < n; i++)
        {
            ushort id = r.GetUShort();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            float yaw = r.GetFloat();
            if (Remote.TryGetValue(KeyOf(PacketRouter.From, id), out var m)) { m.Target = pos; m.Yaw = yaw; }
        }
    }

    private static void DestroyMirror(Mirror m)
    {
        if (m.Go != null) UnityEngine.Object.Destroy(m.Go);
        m.Go = null;
    }

    private static void ClearRemote()
    {
        foreach (var m in Remote.Values) DestroyMirror(m);
        Remote.Clear();
    }

    private static void TickRemote(float dt)
    {
        if (Remote.Count == 0) return;
        // A new run scene: the old scene's clones are gone with it (Unity-null) and their owners resend theirs.
        float runStart = CoopSession.LocalRunStartTime;
        if (_remoteRunStart < 0f) _remoteRunStart = runStart;
        if (!CoopSession.LocalInRun || !GameManagerUtil.HasGameManager || !Mathf.Approximately(runStart, _remoteRunStart))
        {
            _remoteRunStart = runStart;
            ClearRemote();
            return;
        }
        float k = 1f - Mathf.Exp(-12f * dt);
        RemoveKeys.Clear();
        foreach (var kv in Remote)
        {
            var m = kv.Value;
            if (!CoopSession.InRun(m.Slot)) { DestroyMirror(m); RemoveKeys.Add(kv.Key); continue; }
            if (m.Go == null && !m.Failed) Build(m);
            if (m.Go == null) continue;
            var t = m.Go.transform;
            t.position = Vector3.Lerp(t.position, m.Target, k);
            t.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(t.eulerAngles.y, m.Yaw, k), 0f);
        }
        foreach (var key in RemoveKeys) Remote.Remove(key);
    }

    private static void Build(Mirror m)
    {
        var template = ResolveTemplate(m.Type, m.Name);
        // 1.4.1: another character's objects come with that character's skill tree, which this game never loaded.
        // Load the owner's tree from Resources once (the objects are referenced from its talents) and look again.
        // 1.4.1 (second test): the mini-game objects themselves live under Resources "powers/" (the binding stone is
        // powers/demonicbinding/demonicbindingstone, loaded by path when the mechanic starts): load that folder once.
        if (template == null && EnsurePowersLoaded())
            template = ResolveTemplate(m.Type, m.Name, force: true);
        if (template == null && EnsureOwnerTreeLoaded(m.Slot))
            template = ResolveTemplate(m.Type, m.Name, force: true);
        m.IsMarker = template == null;
        if (template == null)
        {
            _missing++;
            if (LoggedMissing.Add(m.Name))
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] No prefab for slot {m.Slot}'s {TypeName(m.Type)} '{m.Name}' here (another character's asset): a marker stands in.");
            // 1.3.1: that character's own assets aren't loaded in this game: a ring and a beam in its player's color.
            m.Go = BuildMarker(m);
            if (m.Go == null) m.Failed = true;
            return;
        }
        GameObject? holder = null;
        try
        {
            holder = new GameObject("SoulstoneCoop_PassiveBuild");
            holder.SetActive(false); // nothing wakes up before it's stripped
            var go = UnityEngine.Object.Instantiate(template, holder.transform);
            go.name = $"SoulstoneCoop_Partner{m.Slot}_{m.Name}";
            VisualGhostFactory.StripToVisualOnly(go, null, keepEffects: true);
            go.transform.SetParent(null, false);
            go.transform.position = m.Target;
            go.transform.rotation = Quaternion.Euler(0f, m.Yaw, 0f);
            go.transform.localScale = Vector3.one * m.Scale;
            if (!go.activeSelf) go.SetActive(true);
            m.Go = go;
            _built++;
            IndexParts(m);
        }
        catch (Exception ex)
        {
            m.Failed = true;
            if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][passives] Building '{m.Name}': {ex.Message}");
        }
        finally
        {
            if (holder != null) UnityEngine.Object.Destroy(holder);
        }
    }

    private const int MarkerSegments = 40;
    private const float MarkerRadius = 1.1f, MarkerHeight = 2.6f;

    /// <summary>The stand-in for an object whose prefab isn't loaded here: a ring on the ground and a short beam, in the
    /// owner's color (GhostOverhead.ColorFor), with the downed markers' line material. Visual only, like the clones.</summary>
    private static GameObject? BuildMarker(Mirror m)
    {
        try
        {
            var mat = Life.DownedMarkers.LineMaterial(out bool vertexColors);
            if (mat == null) return null;
            var color = GhostOverhead.ColorFor(m.Slot);
            color.a = 0.85f;
            var root = new GameObject($"SoulstoneCoop_Partner{m.Slot}_{m.Name}_Marker");
            float radius = m.Radius > 0.3f ? m.Radius : MarkerRadius;
            root.transform.position = m.Target;

            var ringGo = new GameObject("Ring");
            ringGo.transform.SetParent(root.transform, false);
            ringGo.transform.localPosition = new Vector3(0f, 0.12f, 0f);
            ringGo.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // local XY lies on the ground
            var ring = ringGo.AddComponent<LineRenderer>();
            ring.useWorldSpace = false;
            ring.loop = true;
            ring.positionCount = MarkerSegments;
            for (int i = 0; i < MarkerSegments; i++)
            {
                float a = i * Mathf.PI * 2f / MarkerSegments;
                ring.SetPosition(i, new Vector3(Mathf.Sin(a) * radius, Mathf.Cos(a) * radius, 0f));
            }
            ring.alignment = LineAlignment.TransformZ;
            ring.widthMultiplier = 0.14f;
            Paint(ring, mat, vertexColors, color, color);

            // 1.4.1: a second, fainter ring just inside, so the area reads as a zone and not a line.
            if (radius > 0.6f)
            {
                var innerGo = new GameObject("Inner");
                innerGo.transform.SetParent(root.transform, false);
                innerGo.transform.localPosition = new Vector3(0f, 0.11f, 0f);
                innerGo.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                var inner = innerGo.AddComponent<LineRenderer>();
                inner.useWorldSpace = false;
                inner.loop = true;
                inner.positionCount = MarkerSegments;
                float ir = radius - 0.35f;
                for (int i = 0; i < MarkerSegments; i++)
                {
                    float a = i * Mathf.PI * 2f / MarkerSegments;
                    inner.SetPosition(i, new Vector3(Mathf.Sin(a) * ir, Mathf.Cos(a) * ir, 0f));
                }
                inner.alignment = LineAlignment.TransformZ;
                inner.widthMultiplier = 0.5f;
                var faint = color;
                faint.a = 0.3f;
                Paint(inner, mat, vertexColors, faint, faint);
            }

            var beamGo = new GameObject("Beam");
            beamGo.transform.SetParent(root.transform, false);
            var beam = beamGo.AddComponent<LineRenderer>();
            beam.useWorldSpace = false;
            beam.positionCount = 2;
            beam.SetPosition(0, Vector3.zero);
            beam.SetPosition(1, Vector3.up * MarkerHeight);
            beam.widthMultiplier = 0.08f;
            var top = color;
            top.a = 0f;
            Paint(beam, mat, vertexColors, color, top);
            return root;
        }
        catch (Exception ex)
        {
            if (_errors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] Marker for '{m.Name}': {ex.Message}");
            return null;
        }
    }

    private static void Paint(LineRenderer lr, Material mat, bool vertexColors, Color start, Color end)
    {
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.startColor = start;
        lr.endColor = end;
        if (vertexColors) { lr.sharedMaterial = mat; return; }
        // One tinted copy per color, kept (a new Material per marker outlived its marker: materials aren't destroyed
        // with their GameObject).
        if (!TintedMaterials.TryGetValue(start, out var own) || own == null)
        {
            own = new Material(mat);
            if (own.HasProperty("_BaseColor")) own.SetColor("_BaseColor", start);
            if (own.HasProperty("_Color")) own.SetColor("_Color", start);
            TintedMaterials[start] = own;
        }
        lr.sharedMaterial = own;
    }

    private static readonly Dictionary<Color, Material> TintedMaterials = new();

    private static readonly Dictionary<string, float> MissAt = new();

    private static GameObject? ResolveTemplate(byte type, string name, bool force = false)
    {
        string key = type + "|" + name;
        if (Templates.TryGetValue(key, out var cached))
        {
            if (cached != null) return cached;
            if (force) MissAt.Remove(key);
            // 1.3.2: a miss is remembered for 20 s. Each try scans every loaded object of that type, and objects that
            // aren't loaded here (another character's) spawn over and over: a hitch per spawn.
            if (MissAt.TryGetValue(key, out float at) && Time.unscaledTime - at < 20f) return null;
        }
        if (type == PowersType) return PowersPrefab(name); // 1.5.0: generic, by powers/ prefab name
        GameObject? found = null;
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.From(Types[type]));
            for (int i = 0; i < all.Length && found == null; i++)
            {
                var c = all[i]?.TryCast<Component>();
                if (c == null || c.gameObject.scene.IsValid()) continue; // prefabs only
                if (c.gameObject.name == name) found = c.gameObject;
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] Resolving '{name}': {ex.Message}"); }
        Templates[key] = found;
        if (found == null) MissAt[key] = Time.unscaledTime;
        return found;
    }

    /// <summary>1.4.1: each character's skill tree is a Resources asset
    /// (skill-tree-survivors-character/&lt;char&gt;-skill-tree/&lt;char&gt;-skill-tree-collection), and its talents reference
    /// that character's passive objects (binding stone, rifts, shrines...). Loading it brings their prefabs into memory.
    /// Kept referenced for the session (a few MB at most) so Unity doesn't unload it. True if it was loaded just now.</summary>
    private static bool EnsureOwnerTreeLoaded(byte slot)
    {
        string ch = CoopSession.Get(slot)?.Info.CharacterName ?? "";
        if (ch.Length == 0) return false;
        string c = ch.ToLowerInvariant();
        if (!TreeTried.Add(c)) return false;
        // 1.4.1 (second test): the collection alone didn't bring the binding stone: the talents are separate assets
        // (t1/<char>_t01n01...), so the whole folder is loaded (LoadAll includes subfolders), with the collection.
        string path = $"skill-tree-survivors-character/{c}-skill-tree";
        try
        {
            var all = Resources.LoadAll(path, Il2CppType.Of<UnityEngine.Object>()); // no untyped LoadAll in this interop
            int n = all?.Length ?? 0;
            for (int i = 0; i < n; i++) { var o = all![i]; if (o != null) LoadedTrees.Add(o); }
            if (n == 0)
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] {ch}'s skill tree isn't at '{path}': its objects stay markers.");
                return false;
            }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][passives] Loaded {ch}'s skill tree ({n} assets) for slot {slot}'s objects ('{path}').");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] Loading {ch}'s skill tree: {ex.Message}");
            return false;
        }
    }

    private static bool _powersTried;

    /// <summary>1.4.1: every character's mini-game prefabs (Resources "powers/": binding stone, shrines, torii gates,
    /// footprints, ghost ship, machine-spirit minerals...; about 26 assets). Loaded once, on the first object whose
    /// prefab isn't in memory, and kept referenced. True if it was loaded just now.</summary>
    private static bool EnsurePowersLoaded()
    {
        if (_powersTried) return false;
        _powersTried = true;
        try
        {
            var all = Resources.LoadAll("powers", Il2CppType.Of<UnityEngine.Object>()); // no untyped LoadAll in this interop
            int n = all?.Length ?? 0;
            for (int i = 0; i < n; i++)
            {
                var o = all![i];
                if (o == null) continue;
                LoadedTrees.Add(o);
                var go = o.TryCast<GameObject>();
                if (go != null && !PowersByName.ContainsKey(go.name)) PowersByName[go.name] = go; // 1.5.0
            }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][passives] Loaded the mini-game prefabs ('powers/', {n} assets, {PowersByName.Count} prefabs).");
            return n > 0;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] Loading 'powers/': {ex.Message}");
            return false;
        }
    }

    private static readonly HashSet<string> TreeTried = new();
    private static readonly List<UnityEngine.Object> LoadedTrees = new();

    // ---------------- tick, lifecycle, patches ----------------

    public static void Tick(float dt)
    {
        if (NetworkManager.Role == NetRole.None) return;
        try { ScanPowers(dt); } // 1.5.0: generic owner detection (PassiveObjectSync.Generic.cs)
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][passives] Scan: {ex}"); }
        try { TickOwn(dt); }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][passives] Own: {ex}"); }
        try { TickRemote(dt); }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][passives] Remote: {ex}"); }

        _telemetryTimer += dt;
        if (_telemetryTimer < 10f) return;
        _telemetryTimer = 0f;
        if (Own.Count + Remote.Count + _sentSpawns + _recvSpawns + _missing + _errors + _sentParts + _recvParts == 0) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][passives] own={Own.Count} sentSpawns={_sentSpawns} sentStates={_sentStates} | others={Remote.Count} recvSpawns={_recvSpawns} built={_built} missing={_missing} errors={_errors} | parts sent={_sentParts} recv={_recvParts} applied={_appliedParts} unmatched={_unmatchedParts} | {GenericTelemetry()}");
        _sentSpawns = _sentStates = _recvSpawns = _built = 0;
        _sentParts = _recvParts = _appliedParts = _unmatchedParts = 0;
    }

    internal static void ApplyPatches(Harmony harmony)
    {
        Players.Left += slot =>
        {
            RemoveKeys.Clear();
            foreach (var kv in Remote) if (kv.Value.Slot == slot) { DestroyMirror(kv.Value); RemoveKeys.Add(kv.Key); }
            foreach (var k in RemoveKeys) Remote.Remove(k);
        };
        NetworkManager.PeerDisconnected += _ => { if (!NetworkManager.HasPeer) ClearRemote(); };

        int ok = 0, failed = 0;
        for (int i = 0; i < Types.Length; i++)
        {
            var t = Types[i];
            try
            {
                MethodInfo? target = null;
                foreach (var mi in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (mi.Name == "Setup") { target = mi; break; }
                if (target == null) throw new MissingMethodException(t.Name, "Setup");
                IndexByMethod[target] = i;
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(PassiveObjectPatch), nameof(PassiveObjectPatch.Postfix)));
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                Plugin.StaticLog.LogError($"[SoulstoneCoop][passives] Could not patch {t.Name}.Setup: {ex.Message}");
            }
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][passives] Patches: {ok} applied, {failed} failed.");
    }
}

public static class PassiveObjectPatch
{
    public static void Postfix(Component __instance, MethodBase __originalMethod)
    {
        try { PassiveObjectSync.OnSetup(__instance, __originalMethod); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] PassiveObjectPatch: {ex}"); }
    }
}
