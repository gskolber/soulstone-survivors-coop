using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SoulstoneCoop.Summons;

/// <summary>
/// 1.5.0 (docs/passivas-notas.md, "1.5.0: espelhamento genérico"): every hero's mini-game objects without a list of
/// types, and the values their scripts animate on their materials (progress bars, dissolves, fades).
///
/// Owner side, ScanPowers (2 Hz): our hero's mechanics create their objects from Resources "powers/" (the probe showed
/// most of them are plain Instantiates at the scene root - ghost ship, wheel, first mate, pyre, minerals - and a few
/// effects come from the game's pools). So: the scene roots whose name is a powers/ prefab, plus new pool instances of
/// a powers/ prefab (ResourcesUtil.PrefabIdByInstanceId), become Owned objects of type PowersType (255) with the prefab
/// name; the receiver builds the same prefab. Roots that aren't powers/ objects are remembered by pointer, so a scan
/// after the first costs little. Skipped: anything already tracked by a typed Setup patch, world fogs (a partner's
/// fog would darken our screen; AscensionFx already shows the ghost ship's), anything under one of our own objects
/// ("SoulstoneCoop_"), and an effect AscensionFx just replayed for a partner near there (those come out of the same
/// pools on our machine as ordinary instances).
///
/// Materials (PassiveMaterials = 148, both ways): for each renderer of an Owned object, the shader's numbers and colours
/// whose names say they're animated (fill, dissolve, fade, progress, alpha, ...) are read 5 times a second (property
/// block first, else the material) and sent when they change, in full with the 3 s refresh. The copy applies them with
/// a MaterialPropertyBlock on the renderer with the same path, keyed by the property name's hash and resolved against
/// its own shader. The probe saw _Fill (radial sprite bar, on the material), Dissolve_Death and _Fade (fade-ins, on a
/// property block and on a material instance).
/// </summary>
public static partial class PassiveObjectSync
{
    private const byte PowersType = 255;
    private const float ScanInterval = 0.5f;
    private const int MaxRenderers = 24, MaxPropsPerRenderer = 12;

    private static readonly Dictionary<string, GameObject> PowersByName = new();

    private static string TypeName(int type) => type == PowersType ? "powers" : type < Types.Length ? Types[type].Name : $"type{type}";

    private static GameObject? PowersPrefab(string name)
    {
        EnsurePowersLoaded();
        return PowersByName.TryGetValue(name, out var go) ? go : null;
    }

    // ---------------- owner: the scan ----------------

    private static readonly HashSet<IntPtr> NotPowers = new();         // scene roots that aren't powers/ objects
    private static readonly Dictionary<IntPtr, GameObject> Candidates = new(); // powers/ objects seen (any state)
    private static readonly HashSet<IntPtr> Foreign = new();           // a candidate that isn't ours, while it's active
    private static readonly Dictionary<int, string> PowersPrefabIds = new(); // prefab (or root component) instance id -> name
    private static readonly HashSet<int> SeenPoolKeys = new();
    private static readonly HashSet<IntPtr> OwnedGos = new();
    private static readonly List<IntPtr> DropKeys = new();
    private static int _lastPoolCount = -1;
    private static float _scanTimer, _scanRunStart = -1f;
    private static int _scanFound, _scanForeign, _scanSkippedFog;
    private static bool _powersIdsBuilt;

    private static void ScanPowers(float dt)
    {
        if (!CoopSession.LocalInRun || GameState.LocalPlayerEntity == null || !NetworkManager.HasPeer) return;
        _scanTimer += dt;
        if (_scanTimer < ScanInterval) return;
        _scanTimer = 0f;

        float runStart = CoopSession.LocalRunStartTime;
        if (!Mathf.Approximately(runStart, _scanRunStart)) // a new run scene
        {
            _scanRunStart = runStart;
            NotPowers.Clear(); Candidates.Clear(); Foreign.Clear(); SeenPoolKeys.Clear();
            _lastPoolCount = -1;
        }
        EnsurePowersLoaded();
        if (PowersByName.Count == 0) return;
        if (!_powersIdsBuilt) BuildPowersIds();

        // 1. scene roots
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            var scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                var go = roots[i];
                if (go == null) continue;
                var ptr = go.Pointer;
                if (NotPowers.Contains(ptr) || Candidates.ContainsKey(ptr)) continue;
                string name = Enemies.EnemyPrefabKeys.StripClone(go.name);
                if (PowersByName.ContainsKey(name)) Candidates[ptr] = go;
                else NotPowers.Add(ptr);
            }
        }

        // 2. new pool instances of a powers/ prefab (effects; sometimes parented under another object)
        var dict = ResourcesUtil.PrefabIdByInstanceId;
        if (dict != null && dict.Count != _lastPoolCount)
        {
            _lastPoolCount = dict.Count;
            foreach (var kv in dict)
            {
                if (!SeenPoolKeys.Add(kv.Key) || !PowersPrefabIds.ContainsKey(kv.Value)) continue;
                var obj = Resources.InstanceIDToObject(kv.Key);
                var go = obj?.TryCast<GameObject>() ?? obj?.TryCast<Component>()?.gameObject;
                if (go != null) Candidates[go.Pointer] = go;
            }
        }

        // 3. the candidates: ours to mirror while active?
        OwnedGos.Clear();
        foreach (var o in Own.Values) if (o.Comp != null) OwnedGos.Add(o.Comp.gameObject.Pointer);
        DropKeys.Clear();
        foreach (var kv in Candidates)
        {
            var go = kv.Value;
            if (go == null) { DropKeys.Add(kv.Key); continue; }
            if (!go.activeInHierarchy) { Foreign.Remove(kv.Key); continue; }
            if (OwnedGos.Contains(kv.Key) || Foreign.Contains(kv.Key)) continue;
            string name = Enemies.EnemyPrefabKeys.StripClone(go.name);
            if (name.IndexOf("WorldFog", StringComparison.OrdinalIgnoreCase) >= 0) { Foreign.Add(kv.Key); _scanSkippedFog++; continue; }
            if (IsOurs(go.transform) || AscensionFx.WasReplayed(name, go.transform.position))
            {
                Foreign.Add(kv.Key);
                _scanForeign++;
                continue;
            }
            Track(go.transform, PowersType, name);
            _scanFound++;
        }
        foreach (var k in DropKeys) { Candidates.Remove(k); Foreign.Remove(k); }
    }

    /// <summary>Under one of the mod's own objects (a partner copy, a ghost, a replica caster).</summary>
    private static bool IsOurs(Transform t)
    {
        int guard = 0;
        for (var p = t; p != null && guard++ < 32; p = p.parent)
            if (p.name.StartsWith("SoulstoneCoop_")) return true;
        return false;
    }

    private static void BuildPowersIds()
    {
        _powersIdsBuilt = true;
        foreach (var kv in PowersByName)
        {
            var go = kv.Value;
            if (go == null) continue;
            PowersPrefabIds[go.GetInstanceID()] = kv.Key;
            // a pool of T is keyed by the prefab's T component: index the root's components too
            var comps = go.GetComponents(Il2CppType.Of<Component>());
            for (int c = 0; c < comps.Length; c++) if (comps[c] != null) PowersPrefabIds[comps[c].GetInstanceID()] = kv.Key;
        }
    }

    // ---------------- owner: materials ----------------

    private sealed class MatWatch
    {
        public Renderer R = null!;
        public uint PathHash;
        public readonly List<(uint NameHash, int Id, bool Color)> Props = new();
    }

    private struct MatValue
    {
        public bool Color;
        public Vector4 V;
    }

    private static readonly string[] AnimatedWords =
        { "fill", "dissolve", "fade", "progress", "alpha", "opacity", "amount", "cutoff", "intensity", "emission", "tint", "visib", "reveal", "color" };

    private static bool LooksAnimated(string prop)
    {
        string p = prop.ToLowerInvariant();
        foreach (var w in AnimatedWords) if (p.Contains(w)) return true;
        return false;
    }

    private static MaterialPropertyBlock? _mpb;

    private static List<MatWatch> CollectMats(Transform root)
    {
        var list = new List<MatWatch>();
        var rs = root.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
        for (int i = 0; i < rs.Length && list.Count < MaxRenderers; i++)
        {
            var r = rs[i]?.TryCast<Renderer>();
            var sh = r != null && r.sharedMaterial != null ? r.sharedMaterial.shader : null;
            if (r == null || sh == null) continue;
            var w = new MatWatch { R = r, PathHash = PathHash(RelPath(r.transform, root)) };
            int n = sh.GetPropertyCount();
            for (int k = 0; k < n && w.Props.Count < MaxPropsPerRenderer; k++)
            {
                var type = sh.GetPropertyType(k);
                bool color = type == ShaderPropertyType.Color;
                if (!color && type != ShaderPropertyType.Float && type != ShaderPropertyType.Range) continue;
                string name = sh.GetPropertyName(k);
                if (!LooksAnimated(name)) continue;
                w.Props.Add((PathHash(name), sh.GetPropertyNameId(k), color));
            }
            if (w.Props.Count > 0) list.Add(w);
        }
        return list;
    }

    /// <summary>The path under the object the same way CollectParts builds it ("" for the root itself).</summary>
    private static string RelPath(Transform t, Transform root)
    {
        if (t.Pointer == root.Pointer) return "";
        string path = Enemies.EnemyPrefabKeys.StripClone(t.name);
        var up = t.parent;
        int guard = 0;
        while (up != null && up.Pointer != root.Pointer && guard++ < 32)
        {
            path = Enemies.EnemyPrefabKeys.StripClone(up.name) + "/" + path;
            up = up.parent;
        }
        return path;
    }

    private static Vector4 ReadValue(Renderer r, int id, bool color)
    {
        _mpb ??= new MaterialPropertyBlock();
        if (r.HasPropertyBlock())
        {
            r.GetPropertyBlock(_mpb);
            if (color ? _mpb.HasColor(id) : _mpb.HasFloat(id))
                return color ? (Vector4)_mpb.GetColor(id) : new Vector4(_mpb.GetFloat(id), 0, 0, 0);
        }
        var m = r.sharedMaterial;
        if (m == null) return Vector4.zero;
        return color ? (Vector4)m.GetColor(id) : new Vector4(m.GetFloat(id), 0, 0, 0);
    }

    private static int _sentMats, _recvMats, _appliedMats, _unmatchedMats;

    private static void SendMaterials(Owned o, bool full)
    {
        var c = o.Comp;
        if (c == null || !NetworkManager.HasPeer) return;
        try
        {
            if (o.Mats == null || Time.unscaledTime - o.MatsAt > RefreshInterval)
            {
                o.Mats = CollectMats(c.transform);
                o.MatsAt = Time.unscaledTime;
            }
            if (o.Mats.Count == 0) return;
            NetDataWriter? w = null;
            int countPos = 0, n = 0;
            foreach (var mw in o.Mats)
            {
                if (mw.R == null) continue;
                foreach (var (nameHash, id, color) in mw.Props)
                {
                    if (n >= 255) break;
                    var v = ReadValue(mw.R, id, color);
                    ulong key = ((ulong)mw.PathHash << 32) | nameHash;
                    if (!full && o.SentMats.TryGetValue(key, out var prev) && (prev - v).sqrMagnitude < 1e-6f) continue;
                    if (w == null)
                    {
                        w = new NetDataWriter();
                        w.Put((byte)PacketType.PassiveMaterials);
                        w.Put(o.Id);
                        countPos = w.Length;
                        w.Put((byte)0);
                    }
                    w.Put(mw.PathHash);
                    w.Put(nameHash);
                    w.Put(color);
                    if (color) { w.Put(v.x); w.Put(v.y); w.Put(v.z); w.Put(v.w); }
                    else w.Put(v.x);
                    o.SentMats[key] = v;
                    n++;
                }
            }
            if (w == null) return;
            w.Data[countPos] = (byte)n;
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            _sentMats += n;
        }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] Materials of '{o.Name}': {ex.Message}"); }
    }

    // ---------------- the others': materials ----------------

    public static void OnMaterials(NetDataReader r)
    {
        ushort id = r.GetUShort();
        int n = r.GetByte();
        Remote.TryGetValue(KeyOf(PacketRouter.From, id), out var m);
        for (int i = 0; i < n; i++)
        {
            uint pathHash = r.GetUInt(), nameHash = r.GetUInt();
            bool color = r.GetBool();
            var v = color ? new Vector4(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()) : new Vector4(r.GetFloat(), 0, 0, 0);
            _recvMats++;
            if (m == null) continue;
            ulong key = ((ulong)pathHash << 32) | nameHash;
            var mv = new MatValue { Color = color, V = v };
            m.MatStates[key] = mv;
            if (m.Go != null && !m.IsMarker) ApplyMat(m, key, mv);
        }
    }

    /// <summary>Per copy renderer: its shader's property ids by name hash (resolved once).</summary>
    private static readonly Dictionary<IntPtr, Dictionary<uint, int>> ShaderIds = new();

    private static void ApplyMat(Mirror m, ulong key, MatValue mv)
    {
        if (m.RendererMap == null) return;
        uint pathHash = (uint)(key >> 32), nameHash = (uint)key;
        if (!m.RendererMap.TryGetValue(pathHash, out var r) || r == null) { _unmatchedMats++; return; }
        var mat = r.sharedMaterial;
        var sh = mat != null ? mat.shader : null;
        if (sh == null) { _unmatchedMats++; return; }
        if (!ShaderIds.TryGetValue(sh.Pointer, out var ids))
        {
            ids = new Dictionary<uint, int>();
            int count = sh.GetPropertyCount();
            for (int k = 0; k < count; k++) ids[PathHash(sh.GetPropertyName(k))] = sh.GetPropertyNameId(k);
            ShaderIds[sh.Pointer] = ids;
        }
        if (!ids.TryGetValue(nameHash, out int propId)) { _unmatchedMats++; return; }
        _mpb ??= new MaterialPropertyBlock();
        r.GetPropertyBlock(_mpb);
        if (mv.Color) _mpb.SetColor(propId, new Color(mv.V.x, mv.V.y, mv.V.z, mv.V.w));
        else _mpb.SetFloat(propId, mv.V.x);
        r.SetPropertyBlock(_mpb);
        _appliedMats++;
    }

    /// <summary>Right after a copy is built (IndexParts): its renderers by path, and what the owner already sent.</summary>
    private static void IndexRenderers(Mirror m)
    {
        if (m.Go == null) return;
        try
        {
            m.RendererMap = new Dictionary<uint, Renderer>();
            var root = m.Go.transform;
            var rs = root.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i]?.TryCast<Renderer>();
                if (r == null) continue;
                m.RendererMap[PathHash(RelPath(r.transform, root))] = r;
            }
            foreach (var kv in m.MatStates) ApplyMat(m, kv.Key, kv.Value);
        }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] Renderers of '{m.Name}': {ex.Message}"); }
    }

    /// <summary>Telemetry line of the generic part (every 10 s from Tick).</summary>
    private static string GenericTelemetry()
    {
        string s = $"scan found={_scanFound} foreign={_scanForeign} fogsSkipped={_scanSkippedFog} roots={NotPowers.Count + Candidates.Count} | mats sent={_sentMats} recv={_recvMats} applied={_appliedMats} unmatched={_unmatchedMats}";
        _scanFound = _scanForeign = _scanSkippedFog = 0;
        _sentMats = _recvMats = _appliedMats = _unmatchedMats = 0;
        return s;
    }
}
