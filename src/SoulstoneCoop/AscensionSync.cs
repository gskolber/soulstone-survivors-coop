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

    /// <summary>An object an ascension adds to the player (SiegeExpertComponent._modelOnActivate: the Legionnaire's
    /// ModelToAddOnActivatePrefab), under one of the model's transforms.</summary>
    private readonly struct Attachment
    {
        public readonly string Name, Parent;
        public readonly Vector3 Pos;
        public readonly Quaternion Rot;
        public readonly float Scale;
        public Attachment(string name, string parent, Vector3 pos, Quaternion rot, float scale) { Name = name; Parent = parent; Pos = pos; Rot = rot; Scale = scale; }
    }

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
    private static readonly List<Attachment> AttachScratch = new();
    private static IntPtr _siegeOwner;
    private static SiegeExpertComponent? _siege;
    private static float _siegeRetryAt;
    private static int _sent, _recv, _applied, _buildFailures, _errors;

    // ---- receiver ----
    private sealed class Wanted
    {
        public readonly List<ModelInfo> Models = new();
        public readonly List<Attachment> Attachments = new();
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
        public readonly Dictionary<string, GameObject?> Attached = new();
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
        CollectAttachments(local);
        var sb = new System.Text.StringBuilder();
        foreach (var m in Scratch) sb.Append(m.Name).Append('@').Append(m.Scale.ToString("F2")).Append(';');
        sb.Append("root@").Append(rootScale.ToString("F2"));
        foreach (var a in AttachScratch) sb.Append(";+").Append(a.Name).Append('>').Append(a.Parent);
        string sig = sb.ToString();
        bool changed = sig != _sentSignature;
        if (!changed && _refreshTimer < RefreshInterval) return;

        var w = new NetDataWriter();
        w.Put((byte)PacketType.ModelState);
        w.Put((byte)Scratch.Count);
        foreach (var m in Scratch) { w.Put(m.Name); w.Put(m.Path); w.Put(m.Scale); }
        w.Put(rootScale);
        w.Put((byte)AttachScratch.Count);
        foreach (var a in AttachScratch)
        {
            w.Put(a.Name); w.Put(a.Parent);
            w.Put(a.Pos.x); w.Put(a.Pos.y); w.Put(a.Pos.z);
            w.Put(a.Rot.x); w.Put(a.Rot.y); w.Put(a.Rot.z); w.Put(a.Rot.w);
            w.Put(a.Scale);
        }
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _sent++;
        _refreshTimer = 0f;
        if (changed) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] Own models now: {sig}.");
        _sentSignature = sig;
    }

    /// <summary>What an ascension adds to our player while it's on (the Legionnaire's ModelToAddOnActivatePrefab).</summary>
    private static void CollectAttachments(Entity local)
    {
        AttachScratch.Clear();
        // Looked up again every 5 s while missing: the component can come after the entity (talent setup).
        if (_siegeOwner != local.Pointer || (_siege == null && Time.unscaledTime >= _siegeRetryAt))
        {
            _siegeOwner = local.Pointer;
            _siegeRetryAt = Time.unscaledTime + 5f;
            _siege = null;
            var c = local.GetComponentInChildren(Il2CppType.Of<SiegeExpertComponent>(), true);
            if (c != null) _siege = new SiegeExpertComponent(c.Pointer);
        }
        var go = _siege != null ? _siege._modelOnActivate : null;
        if (go == null || !go.activeInHierarchy) return;
        var t = go.transform;
        AttachScratch.Add(new Attachment(Enemies.EnemyPrefabKeys.StripClone(go.name), t.parent != null ? t.parent.name : "",
            t.localPosition, t.localRotation, t.localScale.x));
    }

    // =====================================================================================
    // Receiver
    // =====================================================================================

    // Receiver bounds: ModelState is relayed from any peer. The sender sends at most MaxModels models and one
    // attachment (SiegeExpertComponent's); past that it's dropped. Each ghost builds at most MaxBuiltPerGhost extra
    // models / attachments, and the prefab caches stop at MaxCachedNames distinct names (the rest are not shown).
    private const int MaxModels = 4, MaxAttachments = 1, MaxNameLength = 256;
    private const int MaxBuiltPerGhost = 16, MaxCachedNames = 256;
    private const float MaxModelScale = 10f, MaxAttachOffset = 10f;

    private static float SaneScale(float s) => float.IsFinite(s) ? Mathf.Clamp(s, 0f, MaxModelScale) : 1f;

    private static bool SaneName(string s) => s.Length <= MaxNameLength;

    public static void OnPacket(NetDataReader r)
    {
        int n = r.GetByte();
        var w = new Wanted();
        for (int i = 0; i < n; i++)
        {
            string name = r.GetString(), path = r.GetString();
            float scale = r.GetFloat();
            if (w.Models.Count >= MaxModels || name.Length == 0 || !SaneName(name) || !SaneName(path)) continue;
            w.Models.Add(new ModelInfo(name, path, SaneScale(scale)));
        }
        float root = r.GetFloat();
        w.RootScale = float.IsFinite(root) ? root : 1f; // clamped to 0.3..4 where it's applied
        int na = r.GetByte();
        for (int i = 0; i < na; i++)
        {
            string name = r.GetString(), parent = r.GetString();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            var rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
            float scale = r.GetFloat();
            if (w.Attachments.Count >= MaxAttachments || name.Length == 0 || !SaneName(name) || !SaneName(parent)) continue;
            if (!float.IsFinite(pos.x) || !float.IsFinite(pos.y) || !float.IsFinite(pos.z)) continue;
            float rl = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
            rot = float.IsFinite(rl) && rl > 1e-6f ? Quaternion.Normalize(rot) : Quaternion.identity;
            w.Attachments.Add(new Attachment(name, parent, Vector3.ClampMagnitude(pos, MaxAttachOffset), rot, SaneScale(scale)));
        }
        var sb = new System.Text.StringBuilder();
        foreach (var m in w.Models) sb.Append(m.Name).Append('@').Append(m.Scale.ToString("F2")).Append(';');
        sb.Append("root@").Append(w.RootScale.ToString("F2"));
        foreach (var a in w.Attachments) sb.Append(";+").Append(a.Name).Append('>').Append(a.Parent);
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
            if (m.Name == g.BaseName)
            {
                baseWanted = true;
                // 1.3.1: the Legionnaire's ascension keeps its model and grows it (ActiveScaleFactor): its scale counts too.
                if (g.BaseModel != null && m.Scale > 0.01f) g.BaseModel.transform.localScale = Vector3.one * m.Scale;
                continue;
            }
            if (!g.Extra.TryGetValue(m.Name, out var extra))
            {
                if (g.Extra.Count >= MaxBuiltPerGhost) continue; // far more names than any character has
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
        ApplyAttachments(r, ghost, g, want);
        _applied++;
        if (shown != g.ShownName)
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] Slot {r.Slot} now shows '{shown}' (wanted: {want.Signature}).");
            g.ShownName = shown;
        }
        return true;
    }

    private static void ApplyAttachments(RemotePlayer r, GameObject ghost, GhostModels g, Wanted want)
    {
        foreach (var a in want.Attachments)
        {
            if (!g.Attached.TryGetValue(a.Name, out var go))
            {
                if (g.Attached.Count >= MaxBuiltPerGhost) continue;
                go = BuildAttachment(r, a);
                g.Attached[a.Name] = go;
            }
            if (go == null) continue;
            // Under the transform of the same name in the model that's on (same rig), else on the ghost's root.
            var shownModel = r.ModelAnimator != null ? r.ModelAnimator.transform : ghost.transform;
            var parent = FindDeep(shownModel, a.Parent) ?? FindDeep(ghost.transform, a.Parent) ?? ghost.transform;
            if (go.transform.parent == null || go.transform.parent.Pointer != parent.Pointer) go.transform.SetParent(parent, false);
            go.transform.localPosition = a.Pos;
            go.transform.localRotation = a.Rot;
            go.transform.localScale = Vector3.one * (a.Scale > 0.01f ? a.Scale : 1f);
            if (!go.activeSelf) go.SetActive(true);
        }
        foreach (var kv in g.Attached)
        {
            if (kv.Value == null) continue;
            bool wanted = false;
            foreach (var a in want.Attachments) if (a.Name == kv.Key) { wanted = true; break; }
            if (!wanted && kv.Value.activeSelf) kv.Value.SetActive(false);
        }
    }

    private static Transform? FindDeep(Transform root, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var found = FindDeep(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>The attachment's prefab: SiegeExpertData.ModelToAddOnActivatePrefab of the loaded skill trees.</summary>
    private static GameObject? BuildAttachment(RemotePlayer r, Attachment a)
    {
        GameObject? prefab = null;
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SkillTreeSiegeExpertTalentSO>());
            for (int i = 0; i < all.Length && prefab == null; i++)
            {
                var so = all[i] != null ? new SkillTreeSiegeExpertTalentSO(all[i].Pointer) : null;
                var p = so?.Data?.ModelToAddOnActivatePrefab;
                if (p != null && p.name == a.Name) prefab = p;
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ascension] Resolving attachment '{a.Name}': {ex.Message}"); }
        if (prefab == null)
        {
            if (LoggedMissing.Count < MaxCachedNames && LoggedMissing.Add("+" + a.Name)) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ascension] No prefab for slot {r.Slot}'s attachment '{a.Name}'.");
            return null;
        }
        var holder = new GameObject("SoulstoneCoop_AttachmentBuild");
        holder.SetActive(false);
        try
        {
            var go = UnityEngine.Object.Instantiate(prefab, holder.transform);
            VisualGhostFactory.StripToVisualOnly(go, null, keepEffects: true);
            go.SetActive(false);
            go.transform.SetParent(null, false);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] Slot {r.Slot} attachment '{a.Name}' built (on '{a.Parent}').");
            return go;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ascension] Building attachment '{a.Name}': {ex.Message}");
            return null;
        }
        finally { UnityEngine.Object.Destroy(holder); }
    }

    private static (GameObject? Go, Animator? Anim) BuildExtra(RemotePlayer r, GameObject ghost, ModelInfo m)
    {
        var prefab = ResolvePrefab(m, out string via);
        if (prefab == null)
        {
            _buildFailures++;
            if (LoggedMissing.Count < MaxCachedNames && LoggedMissing.Add(m.Name))
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ascension] No prefab for slot {r.Slot}'s model '{m.Name}' (path '{m.Path}'): its main model stays.");
            return (null, null);
        }
        var go = VisualGhostFactory.BuildExtraModel(prefab, r, ghost.transform, m.Scale > 0.01f ? m.Scale : 1f, out var anim, out string what);
        if (go == null) _buildFailures++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] Slot {r.Slot} model '{m.Name}' via {via}: {(go != null ? "built " + what : "FAILED " + what)}.");
        return (go, anim);
    }

    private static readonly Dictionary<string, PlayerPrefabReferences?> PrefabCache = new();
    private static readonly Dictionary<string, float> PrefabMissAt = new();

    private static PlayerPrefabReferences? ResolvePrefab(ModelInfo m, out string via)
    {
        via = "cache";
        string key = m.Name + "|" + m.Path;
        if (PrefabCache.TryGetValue(key, out var cached))
        {
            if (cached != null) return cached;
            if (PrefabMissAt.TryGetValue(key, out float at) && Time.unscaledTime - at < 20f) return null; // scanned lately
        }
        else if (PrefabCache.Count >= MaxCachedNames) return null; // too many distinct names: not shown
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
        PrefabCache[key] = found;
        if (found == null) PrefabMissAt[key] = Time.unscaledTime;
        return found;
    }

    /// <summary>ModSkinLoader replaced the ghost's main model: work its models out again on the next tick.
    /// 1.3.2: the extra models and attachments built for the old one are destroyed (they stayed under the ghost: an
    /// ascended partner swapping skins kept the old ascension model on too).</summary>
    internal static void InvalidateGhost(byte slot)
    {
        if (Applied.TryGetValue(slot, out var g)) DestroyBuilt(g);
        Applied.Remove(slot);
    }

    private static void DestroyBuilt(GhostModels g)
    {
        foreach (var e in g.Extra.Values) { try { if (e.Go != null) UnityEngine.Object.Destroy(e.Go); } catch { } }
        foreach (var a in g.Attached.Values) { try { if (a != null) UnityEngine.Object.Destroy(a); } catch { } }
        g.Extra.Clear();
        g.Attached.Clear();
    }

    /// <summary>StartMatch (EnemySyncStartMatchPatch): last run's wishes go, so the new run's ghost doesn't show last
    /// run's ascension model until the partner's first ModelState (on change + every 3 s) says what it wears now.</summary>
    internal static void OnMatchStarted() => WantedBySlot.Clear();

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
