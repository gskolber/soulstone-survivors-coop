using Il2CppInterop.Runtime;
using SoulstoneCoop.Lobby;
using UnityEngine;
using UnityEngine.Rendering;

namespace SoulstoneCoop.Summons;

/// <summary>
/// 1.4.1 diagnostic, LOG ONLY (docs/passivas-notas.md, "Sonda powers/"): what our own hero spawns from Resources
/// "powers/" (every hero's mini-game prefabs) and how, to design the generic mirroring (plan #2) and the progress bars
/// (plan #1) from real data. Sends nothing, changes nothing in the game. On with [Debug] PowersProbe = true (off by default since 1.5.0).
///
/// - Pooled: ResourcesUtil.PrefabIdByInstanceId maps every pooled instance to its prefab. New entries whose prefab is
///   one of the powers/ prefabs are fetched (Resources.InstanceIDToObject) and watched: each activation logs the
///   object, where it is, its parts; each deactivation how long it lived.
/// - Not pooled: every 3 s, GameObject.Find("&lt;prefab&gt;(Clone)") for each powers/ prefab; an active one that isn't
///   a watched pooled instance (and isn't one of our partner copies) is logged as NOT pooled, and watched the same way.
/// - Materials: on activation every renderer's shader numbers and colours are recorded (material and property block);
///   while it lives, the first change of each one is logged once per prefab, renderer and property.
/// </summary>
public static class PowersProbe
{
    private const float PoolScanInterval = 1f;
    private const float FindInterval = 3f;
    private const float WatchInterval = 0.25f;
    private const float MaterialInterval = 0.5f;
    private const float MaterialWatchSeconds = 90f;
    private const int MaxRenderers = 24, MaxProps = 64, MaxPartsLogged = 30;

    private sealed class Watched
    {
        public GameObject Go = null!;
        public string Name = "";
        public bool Pooled;
        public bool WasActive;
        public float ActiveSince;
        public int Activations;
        public List<RendererWatch>? Renderers;
    }

    private sealed class RendererWatch
    {
        public Renderer R = null!;
        public string Path = "";
        public readonly List<(string Prop, bool Color, Vector4 Value, bool FromBlock)> Baseline = new();
    }

    private static bool _loaded, _loadFailed;
    private static readonly Dictionary<int, string> PowersPrefabs = new(); // prefab instance id -> name
    private static readonly List<string> PowersNames = new();
    private static readonly HashSet<int> SeenPoolKeys = new();
    private static readonly Dictionary<IntPtr, Watched> Watch = new();
    private static readonly List<IntPtr> DeadKeys = new();
    private static readonly HashSet<string> LoggedMaterial = new();
    private static readonly HashSet<string> LoggedNotPooledName = new();
    private static int _lastPoolCount = -1;
    private static float _poolTimer, _findTimer, _watchTimer, _matTimer;
    private static float _runStart = -1f;
    private static int _errors;
    private static MaterialPropertyBlock? _mpb;

    public static void Tick(float dt)
    {
        if (!Plugin.PowersProbe.Value) return;
        try
        {
            if (!CoopSession.LocalInRun || GameState.LocalPlayerEntity == null)
            {
                if (Watch.Count > 0) { Watch.Clear(); SeenPoolKeys.Clear(); _lastPoolCount = -1; }
                return;
            }
            float runStart = CoopSession.LocalRunStartTime;
            if (!Mathf.Approximately(runStart, _runStart)) // a new run scene: the old instances are gone
            {
                _runStart = runStart;
                Watch.Clear();
                SeenPoolKeys.Clear();
                _lastPoolCount = -1;
            }
            if (!EnsureLoaded()) return;

            _poolTimer += dt;
            if (_poolTimer >= PoolScanInterval) { _poolTimer = 0f; ScanPool(); }
            _findTimer += dt;
            if (_findTimer >= FindInterval) { _findTimer = 0f; FindUnpooled(); }
            _watchTimer += dt;
            if (_watchTimer >= WatchInterval) { _watchTimer = 0f; WatchActivity(); }
            _matTimer += dt;
            if (_matTimer >= MaterialInterval) { _matTimer = 0f; WatchMaterials(); }
        }
        catch (Exception ex)
        {
            if (_errors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][probe] {ex.Message}");
        }
    }

    private static bool EnsureLoaded()
    {
        if (_loaded) return true;
        if (_loadFailed) return false;
        try
        {
            var all = Resources.LoadAll("powers", Il2CppType.Of<UnityEngine.Object>());
            int n = all?.Length ?? 0;
            for (int i = 0; i < n; i++)
            {
                var go = all![i]?.TryCast<GameObject>();
                if (go == null) continue;
                PowersPrefabs[go.GetInstanceID()] = go.name;
                if (!PowersNames.Contains(go.name)) PowersNames.Add(go.name);
                // a pooled T may be keyed by its component's prefab id: index every component of the prefab root too
                var comps = go.GetComponents(Il2CppType.Of<Component>());
                for (int c = 0; c < comps.Length; c++)
                    if (comps[c] != null) PowersPrefabs[comps[c].GetInstanceID()] = go.name;
            }
            _loaded = true;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][probe] powers/ prefabs ({PowersNames.Count}): {string.Join(", ", PowersNames)}.");
            return true;
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][probe] Loading powers/: {ex.Message}");
            return false;
        }
    }

    private static void ScanPool()
    {
        var dict = ResourcesUtil.PrefabIdByInstanceId;
        if (dict == null) return;
        int count = dict.Count;
        if (count == _lastPoolCount) return;
        _lastPoolCount = count;
        foreach (var kv in dict)
        {
            int inst = kv.Key;
            if (!SeenPoolKeys.Add(inst)) continue;
            if (!PowersPrefabs.TryGetValue(kv.Value, out var name)) continue;
            var obj = Resources.InstanceIDToObject(inst);
            var go = obj?.TryCast<GameObject>() ?? obj?.TryCast<Component>()?.gameObject;
            if (go == null)
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][probe] Pooled '{name}' instance {inst}: not resolvable ({obj?.GetIl2CppType().Name ?? "null"}).");
                continue;
            }
            if (Watch.ContainsKey(go.Pointer)) continue;
            Watch[go.Pointer] = new Watched { Go = go, Name = name, Pooled = true };
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][probe] Pooled instance of '{name}' (via {obj!.GetIl2CppType().Name}) registered; active={go.activeInHierarchy}.");
        }
    }

    private static void FindUnpooled()
    {
        foreach (var name in PowersNames)
        {
            var go = GameObject.Find(name + "(Clone)");
            if (go == null || Watch.ContainsKey(go.Pointer)) continue;
            if (go.name.StartsWith("SoulstoneCoop_")) continue; // our partner copies
            Watch[go.Pointer] = new Watched { Go = go, Name = name, Pooled = false };
            if (LoggedNotPooledName.Add(name))
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][probe] '{name}' is live and NOT from the pool (plain Instantiate or another path).");
        }
    }

    private static void WatchActivity()
    {
        DeadKeys.Clear();
        float now = Time.unscaledTime;
        foreach (var kv in Watch)
        {
            var w = kv.Value;
            if (w.Go == null) { DeadKeys.Add(kv.Key); continue; }
            bool active = w.Go.activeInHierarchy;
            if (active == w.WasActive) continue;
            w.WasActive = active;
            if (active)
            {
                w.Activations++;
                w.ActiveSince = now;
                LogSpawn(w);
                w.Renderers = RecordRenderers(w);
            }
            else
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][probe] '{w.Name}' off after {now - w.ActiveSince:F1}s (activation #{w.Activations}).");
                w.Renderers = null;
            }
        }
        foreach (var k in DeadKeys)
        {
            if (Watch.TryGetValue(k, out var w)) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][probe] '{w.Name}' destroyed.");
            Watch.Remove(k);
        }
    }

    private static string PathUnder(Transform t, Transform root)
    {
        if (t.Pointer == root.Pointer) return ".";
        string path = t.name;
        var up = t.parent;
        int guard = 0;
        while (up != null && up.Pointer != root.Pointer && guard++ < 32) { path = up.name + "/" + path; up = up.parent; }
        return path;
    }

    private static void LogSpawn(Watched w)
    {
        var t = w.Go.transform;
        var me = GameState.LocalPlayerEntity;
        float dist = me != null ? Vector3.Distance(me.transform.position, t.position) : -1f;
        var sb = new System.Text.StringBuilder();
        var all = w.Go.GetComponentsInChildren(Il2CppType.Of<Transform>(), true);
        int shown = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var p = all[i]?.TryCast<Transform>();
            if (p == null || p.Pointer == t.Pointer) continue;
            if (shown++ >= MaxPartsLogged) { sb.Append(" ..."); break; }
            var sc = p.localScale;
            sb.Append($" {PathUnder(p, t)}[{(p.gameObject.activeSelf ? "on" : "off")} {sc.x:F2}]");
        }
        var rootComps = w.Go.GetComponents(Il2CppType.Of<Component>());
        var names = new List<string>();
        for (int i = 0; i < rootComps.Length; i++) if (rootComps[i] != null) names.Add(rootComps[i].GetIl2CppType().Name);
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][probe] '{w.Name}' ON #{w.Activations} ({(w.Pooled ? "pooled" : "NOT pooled")}) at {t.position} {dist:F1}u from me, " +
            $"parent '{(t.parent != null ? t.parent.name : "-")}', scale {t.lossyScale.x:F2}, root components [{string.Join(", ", names)}], " +
            $"{all.Length - 1} parts:{sb}");
    }

    private static List<RendererWatch> RecordRenderers(Watched w)
    {
        var list = new List<RendererWatch>();
        try
        {
            var rs = w.Go.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; i < rs.Length && list.Count < MaxRenderers; i++)
            {
                var r = rs[i]?.TryCast<Renderer>();
                var mat = r != null ? r.sharedMaterial : null;
                var sh = mat != null ? mat.shader : null;
                if (r == null || sh == null) continue;
                var rw = new RendererWatch { R = r, Path = PathUnder(r.transform, w.Go.transform) };
                Snapshot(rw, mat!, sh, rw.Baseline);
                list.Add(rw);
            }
        }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][probe] Renderers of '{w.Name}': {ex.Message}"); }
        return list;
    }

    private static void Snapshot(RendererWatch rw, Material mat, Shader sh, List<(string, bool, Vector4, bool)> into)
    {
        _mpb ??= new MaterialPropertyBlock();
        bool hasBlock = rw.R.HasPropertyBlock();
        if (hasBlock) rw.R.GetPropertyBlock(_mpb);
        int n = Mathf.Min(sh.GetPropertyCount(), MaxProps);
        for (int i = 0; i < n; i++)
        {
            var type = sh.GetPropertyType(i);
            if (type != ShaderPropertyType.Float && type != ShaderPropertyType.Range && type != ShaderPropertyType.Color) continue;
            string prop = sh.GetPropertyName(i);
            int id = sh.GetPropertyNameId(i);
            bool color = type == ShaderPropertyType.Color;
            bool fromBlock = false;
            Vector4 v;
            if (hasBlock && !_mpb.isEmpty && (color ? _mpb.HasColor(id) : _mpb.HasFloat(id)))
            {
                fromBlock = true;
                v = color ? (Vector4)_mpb.GetColor(id) : new Vector4(_mpb.GetFloat(id), 0, 0, 0);
            }
            else v = color ? (Vector4)mat.GetColor(id) : new Vector4(mat.GetFloat(id), 0, 0, 0);
            into.Add((prop, color, v, fromBlock));
        }
    }

    private static readonly List<(string, bool, Vector4, bool)> Scratch = new();

    private static void WatchMaterials()
    {
        float now = Time.unscaledTime;
        foreach (var w in Watch.Values)
        {
            if (w.Renderers == null || !w.WasActive || now - w.ActiveSince > MaterialWatchSeconds) continue;
            foreach (var rw in w.Renderers)
            {
                try
                {
                    if (rw.R == null) continue;
                    var mat = rw.R.sharedMaterial;
                    var sh = mat != null ? mat.shader : null;
                    if (mat == null || sh == null) continue;
                    Scratch.Clear();
                    Snapshot(rw, mat, sh, Scratch);
                    int n = Mathf.Min(Scratch.Count, rw.Baseline.Count);
                    for (int i = 0; i < n; i++)
                    {
                        var (prop, color, v, fromBlock) = Scratch[i];
                        var b = rw.Baseline[i];
                        if (b.Prop != prop || (v - b.Value).sqrMagnitude < 1e-6f) continue;
                        string key = w.Name + "|" + rw.Path + "|" + prop;
                        if (!LoggedMaterial.Add(key)) continue;
                        string fmt(Vector4 x) => color ? $"({x.x:F2},{x.y:F2},{x.z:F2},{x.w:F2})" : $"{x.x:F3}";
                        Plugin.StaticLog.LogInfo(
                            $"[SoulstoneCoop][probe] Material '{w.Name}' {rw.Path} ({rw.R.GetIl2CppType().Name}, shader '{sh.name}', material '{mat.name}'): " +
                            $"{prop} {fmt(b.Value)} -> {fmt(v)} via {(fromBlock ? "property block" : "material")} after {now - w.ActiveSince:F1}s.");
                    }
                }
                catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][probe] Materials of '{w.Name}': {ex.Message}"); }
            }
        }
    }
}
