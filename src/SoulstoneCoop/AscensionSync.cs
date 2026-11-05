using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop;

/// <summary>
/// The partner's ascension and other model changes on its ghost (docs/ascensao-notas.md).
///
/// Every character's ascension (SiegeExpertComponent for the Legionnaire, DemonicBindingComponent, DivineInspiration,
/// ArcanePower, Concentration...) and the other transformations (NaturesGuardian riding, Mechatron robot, MyrmidonFavor)
/// swap or add a PlayerPrefabReferences model under the player entity. The ascension ones come from
/// PlayerPrefabReferences.InstantiateAscensionModelRoutine(entity, originalModel, ascensionModelPath, out) (0x8137D0
/// MoveNext: ResourcesUtil.Load of that path), so the path is captured there.
///
/// Sender (both sides, every 0.25 s): the local player's ACTIVE PlayerPrefabReferences models (name, Resources path when
/// known, local scale) and the entity's own scale (ascensions grow the player). Sent as ModelState when it changes and
/// every 3 s (ReliableOrdered, relayed).
/// Receiver: the ghost keeps its own model (built from the skin) and gets each other model once, built like the main
/// one (VisualGhostFactory.BuildExtraModel) from the path, else "Characters/&lt;name&gt;", else a loaded prefab of that
/// name. The models the partner has on are switched on, the others off; ModelAnimator follows the shown one, so
/// locomotion and the cast animations keep working. A model that can't be found leaves the main one on (logged once).
/// Layout (after the type byte): count u8 | count * (name str | path str | scale f32) | rootScale f32.
/// </summary>
public static class AscensionSync
{
    private const float ScanInterval = 0.25f;
    private const float RefreshInterval = 3f;

    private readonly struct ModelInfo
    {
        public readonly string Name, Path;
        public readonly float Scale;
        public ModelInfo(string name, string path, float scale) { Name = name; Path = path; Scale = scale; }
    }

    // ---- sender ----
    private static float _scanTimer, _refreshTimer;
    private static string _sentSignature = "";
    private static readonly Dictionary<string, string> PathByName = new();
    private static readonly List<ModelInfo> Scratch = new();
    private static int _sent, _recv, _applied, _buildFailures, _errors;

    // ---- receiver ----
    private sealed class Wanted
    {
        public readonly List<ModelInfo> Models = new();
        public float RootScale = 1f;
        public string Signature = "";
    }

    private sealed class GhostModels
    {
        public IntPtr GhostPtr;
        public GameObject? BaseModel;
        public Animator? BaseAnimator;
        public string BaseName = "";
        public readonly Dictionary<string, (GameObject? Go, Animator? Anim)> Extra = new();
        public string AppliedSignature = "";
        public string ShownName = "";
    }

    private static readonly Dictionary<byte, Wanted> WantedBySlot = new();
    private static readonly Dictionary<byte, GhostModels> Applied = new();
    private static readonly HashSet<string> LoggedMissing = new();
    private static float _telemetryTimer;

    public static void Tick(float dt)
    {
        if (NetworkManager.Role == NetRole.None) return;
        try { SenderTick(dt); }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][ascension] Sender: {ex}"); }
        try { ReceiverTick(); }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][ascension] Receiver: {ex}"); }

        _telemetryTimer += dt;
        if (_telemetryTimer < 10f) return;
        _telemetryTimer = 0f;
        if (_sent + _recv + _applied + _buildFailures + _errors == 0) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] sent={_sent} recv={_recv} applied={_applied} buildFailures={_buildFailures} errors={_errors} own='{_sentSignature}'");
        _sent = _recv = _applied = 0;
    }

    // =====================================================================================
    // Sender
    // =====================================================================================

    /// <summary>InstantiateAscensionModelRoutine prefix: where our own ascension model comes from.</summary>
    internal static void OnAscensionModelRequested(Entity entity, string path)
    {
        var local = GameState.LocalPlayerEntity;
        if (entity == null || local == null || entity.Pointer != local.Pointer || string.IsNullOrEmpty(path)) return;
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 ? path.Substring(slash + 1) : path;
        if (PathByName.TryGetValue(name, out var old) && old == path) return;
        PathByName[name] = path;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] Own ascension model '{name}' comes from '{path}'.");
    }

    private static void SenderTick(float dt)
    {
        _scanTimer += dt;
        _refreshTimer += dt;
        if (_scanTimer < ScanInterval) return;
        _scanTimer = 0f;
        if (!NetworkManager.HasPeer || !CoopSession.LocalInRun) { _sentSignature = ""; return; }
        var local = GameState.LocalPlayerEntity;
        if (local == null || !GameManagerUtil.HasGameManager) return;

        Scratch.Clear();
        foreach (var refs in local.GetComponentsInChildren<PlayerPrefabReferences>(false))
        {
            if (refs == null || !refs.gameObject.activeInHierarchy || Scratch.Count >= 4) continue;
            string name = Enemies.EnemyPrefabKeys.StripClone(refs.gameObject.name);
            PathByName.TryGetValue(name, out var path);
            Scratch.Add(new ModelInfo(name, path ?? "", refs.transform.localScale.x));
        }
        float rootScale = local.transform.localScale.x;
        var sb = new System.Text.StringBuilder();
        foreach (var m in Scratch) sb.Append(m.Name).Append('@').Append(m.Scale.ToString("F2")).Append(';');
        sb.Append("root@").Append(rootScale.ToString("F2"));
        string sig = sb.ToString();
        bool changed = sig != _sentSignature;
        if (!changed && _refreshTimer < RefreshInterval) return;

        var w = new NetDataWriter();
        w.Put((byte)PacketType.ModelState);
        w.Put((byte)Scratch.Count);
        foreach (var m in Scratch) { w.Put(m.Name); w.Put(m.Path); w.Put(m.Scale); }
        w.Put(rootScale);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _sent++;
        _refreshTimer = 0f;
        if (changed) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] Own models now: {sig}.");
        _sentSignature = sig;
    }

    // =====================================================================================
    // Receiver
    // =====================================================================================

    public static void OnPacket(NetDataReader r)
    {
        int n = r.GetByte();
        var w = new Wanted();
        for (int i = 0; i < n; i++)
        {
            string name = r.GetString(), path = r.GetString();
            float scale = r.GetFloat();
            w.Models.Add(new ModelInfo(name, path, scale));
        }
        w.RootScale = r.GetFloat();
        var sb = new System.Text.StringBuilder();
        foreach (var m in w.Models) sb.Append(m.Name).Append('@').Append(m.Scale.ToString("F2")).Append(';');
        sb.Append("root@").Append(w.RootScale.ToString("F2"));
        w.Signature = sb.ToString();
        _recv++;
        WantedBySlot[PacketRouter.From] = w;
    }

    private static void ReceiverTick()
    {
        if (WantedBySlot.Count == 0) return;
        foreach (var r in CoopSession.Remotes)
        {
            var ghost = r.Ghost;
            if (ghost == null || !WantedBySlot.TryGetValue(r.Slot, out var want)) continue;
            if (!Applied.TryGetValue(r.Slot, out var g) || g.GhostPtr != ghost.Pointer)
            {
                // A new ghost (run start, rebuild): its extra models went with the old one.
                g = new GhostModels { GhostPtr = ghost.Pointer };
                Applied[r.Slot] = g;
            }
            if (g.AppliedSignature == want.Signature) continue;
            if (Apply(r, ghost, g, want)) g.AppliedSignature = want.Signature;
        }
    }

    /// <returns>False: the ghost isn't ready yet (no model animator), try again next frame.</returns>
    private static bool Apply(RemotePlayer r, GameObject ghost, GhostModels g, Wanted want)
    {
        if (g.BaseModel == null)
        {
            var anim = r.ModelAnimator;
            if (anim == null) return false; // not built yet: next frame
            var t = anim.transform;
            while (t.parent != null && t.parent.Pointer != ghost.transform.Pointer) t = t.parent;
            if (t.parent == null) return false;
            g.BaseModel = t.gameObject;
            g.BaseAnimator = anim;
            g.BaseName = Enemies.EnemyPrefabKeys.StripClone(t.gameObject.name);
        }

        bool baseWanted = want.Models.Count == 0;
        var show = new List<(GameObject Go, Animator? Anim, string Name)>();
        foreach (var m in want.Models)
        {
            if (m.Name == g.BaseName) { baseWanted = true; continue; }
            if (!g.Extra.TryGetValue(m.Name, out var extra))
            {
                extra = BuildExtra(r, ghost, m);
                g.Extra[m.Name] = extra;
            }
            if (extra.Go == null) continue;
            extra.Go.transform.localScale = Vector3.one * (m.Scale > 0.01f ? m.Scale : 1f);
            show.Add((extra.Go, extra.Anim, m.Name));
        }
        // Nothing of what the partner wears could be shown here: keep the main model.
        if (show.Count == 0) baseWanted = true;

        if (g.BaseModel != null && g.BaseModel.activeSelf != baseWanted) g.BaseModel.SetActive(baseWanted);
        foreach (var kv in g.Extra)
        {
            var go = kv.Value.Go;
            if (go == null) continue;
            bool on = false;
            foreach (var s in show) if (s.Go.Pointer == go.Pointer) { on = true; break; }
            if (go.activeSelf != on) go.SetActive(on);
        }

        // Locomotion and cast animations follow the model that is on (the main one first when both are).
        Animator? driver = baseWanted ? g.BaseAnimator : null;
        if (driver == null) foreach (var s in show) if (s.Anim != null) { driver = s.Anim; break; }
        driver ??= g.BaseAnimator;
        string shown = baseWanted ? g.BaseName : (show.Count > 0 ? show[0].Name : g.BaseName);
        if (r.ModelAnimator == null || driver == null || r.ModelAnimator.Pointer != driver.Pointer)
        {
            r.ModelAnimator = driver;
            r.ModelVersion++;
        }

        float rs = Mathf.Clamp(want.RootScale, 0.3f, 4f);
        ghost.transform.localScale = Vector3.one * rs;
        _applied++;
        if (shown != g.ShownName)
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] Slot {r.Slot} now shows '{shown}' (wanted: {want.Signature}).");
            g.ShownName = shown;
        }
        return true;
    }

    private static (GameObject? Go, Animator? Anim) BuildExtra(RemotePlayer r, GameObject ghost, ModelInfo m)
    {
        var prefab = ResolvePrefab(m, out string via);
        if (prefab == null)
        {
            _buildFailures++;
            if (LoggedMissing.Add(m.Name))
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ascension] No prefab for slot {r.Slot}'s model '{m.Name}' (path '{m.Path}'): its main model stays.");
            return (null, null);
        }
        var go = VisualGhostFactory.BuildExtraModel(prefab, r, ghost.transform, m.Scale > 0.01f ? m.Scale : 1f, out var anim, out string what);
        if (go == null) _buildFailures++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] Slot {r.Slot} model '{m.Name}' via {via}: {(go != null ? "built " + what : "FAILED " + what)}.");
        return (go, anim);
    }

    private static readonly Dictionary<string, PlayerPrefabReferences?> PrefabCache = new();

    private static PlayerPrefabReferences? ResolvePrefab(ModelInfo m, out string via)
    {
        via = "cache";
        if (PrefabCache.TryGetValue(m.Name + "|" + m.Path, out var cached) && cached != null) return cached;
        PlayerPrefabReferences? found = null;
        try
        {
            if (m.Path.Length > 0) { found = ResourcesUtil.Load<PlayerPrefabReferences>(m.Path); via = $"path '{m.Path}'"; }
            if (found == null) { found = ResourcesUtil.Load<PlayerPrefabReferences>("Characters/" + m.Name); via = $"'Characters/{m.Name}'"; }
            if (found == null)
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<PlayerPrefabReferences>());
                for (int i = 0; i < all.Length && found == null; i++)
                {
                    var p = all[i]?.TryCast<PlayerPrefabReferences>();
                    if (p != null && !p.gameObject.scene.IsValid() && p.gameObject.name == m.Name) found = p;
                }
                via = "loaded prefab";
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ascension] Resolving '{m.Name}': {ex.Message}"); }
        PrefabCache[m.Name + "|" + m.Path] = found;
        return found;
    }

    /// <summary>A player left the session: its wishes and models go (the models with its ghost).</summary>
    internal static void Forget(byte slot)
    {
        WantedBySlot.Remove(slot);
        Applied.Remove(slot);
    }

    // =====================================================================================
    // Patch (by hand: a failure only turns the path capture off; names still work)
    // =====================================================================================

    internal static void ApplyPatch(Harmony harmony)
    {
        Players.Left += Forget;
        try
        {
            MethodInfo? target = null;
            foreach (var mi in typeof(PlayerPrefabReferences).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (mi.Name == nameof(PlayerPrefabReferences.InstantiateAscensionModelRoutine) && mi.GetParameters().Length == 4) { target = mi; break; }
            if (target == null) throw new MissingMethodException(nameof(PlayerPrefabReferences), nameof(PlayerPrefabReferences.InstantiateAscensionModelRoutine));
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(AscensionPatch), nameof(AscensionPatch.Prefix)));
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][ascension] InstantiateAscensionModelRoutine patched.");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][ascension] Could not patch InstantiateAscensionModelRoutine (models are found by name only): {ex.Message}");
        }
    }
}

/// <summary>Signature: three references and the out wrapper (a reference too): trampoline-safe.</summary>
public static class AscensionPatch
{
    public static void Prefix(Entity entity, string ascensionModelPath)
    {
        try { AscensionSync.OnAscensionModelRequested(entity, ascensionModelPath); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] AscensionPatch: {ex}"); }
    }
}
