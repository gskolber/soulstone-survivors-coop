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
public static class PassiveObjectSync
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
    }

    private static readonly Dictionary<IntPtr, Owned> Own = new();
    private static readonly List<Owned> Gone = new();
    private static ushort _nextId = 1;
    private static float _goneTimer, _stateTimer, _refreshTimer;
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
        if (Own.TryGetValue(comp.Pointer, out var old)) Forget(old);
        if (Own.Count >= MaxOwned) return;
        ushort id = _nextId++;
        if (_nextId == 0) _nextId = 1;
        var o = new Owned { Id = id, Type = (byte)type, Name = name, Comp = comp, Ptr = comp.Pointer };
        Own[o.Ptr] = o;
        if (NetworkManager.HasPeer) SendSpawn(o);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][passives] Own {Types[type].Name} #{id} '{name}' at {comp.transform.position}.");
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
            return;
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

    // ---------------- the others' ----------------

    private sealed class Mirror
    {
        public byte Slot;
        public ushort Id;
        public byte Type;
        public string Name = "";
        public Vector3 Target;
        public float Yaw, Scale = 1f;
        public GameObject? Go;
        public bool Failed;
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
        _recvSpawns++;
        byte from = PacketRouter.From;
        if (type >= Types.Length || !Players.IsPresent(from) || !CoopSession.LocalInRun) return;
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
        if (template == null)
        {
            m.Failed = true;
            _missing++;
            if (LoggedMissing.Add(m.Name))
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][passives] No prefab for slot {m.Slot}'s {Types[m.Type].Name} '{m.Name}': not shown.");
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

    private static GameObject? ResolveTemplate(byte type, string name)
    {
        string key = type + "|" + name;
        if (Templates.TryGetValue(key, out var cached) && cached != null) return cached;
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
        return found;
    }

    // ---------------- tick, lifecycle, patches ----------------

    public static void Tick(float dt)
    {
        if (NetworkManager.Role == NetRole.None) return;
        try { TickOwn(dt); }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][passives] Own: {ex}"); }
        try { TickRemote(dt); }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][passives] Remote: {ex}"); }

        _telemetryTimer += dt;
        if (_telemetryTimer < 10f) return;
        _telemetryTimer = 0f;
        if (Own.Count + Remote.Count + _sentSpawns + _recvSpawns + _missing + _errors == 0) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][passives] own={Own.Count} sentSpawns={_sentSpawns} sentStates={_sentStates} | others={Remote.Count} recvSpawns={_recvSpawns} built={_built} missing={_missing} errors={_errors}");
        _sentSpawns = _sentStates = _recvSpawns = _built = 0;
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
