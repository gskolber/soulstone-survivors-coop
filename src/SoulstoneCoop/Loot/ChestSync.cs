using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Loot;

/// <summary>
/// Chests and ore deposits for every player (docs/baus-notas.md, docs/proximos-investigacao.md section 1).
///
/// Both are an InteractableRewardObject (InteractableGuardedRewardObject for the chest with guardians). The host owns
/// them: they come from GameManagerInteractableGuardedRewardSpawnerComponent and MapTask.CompleteTask, which only run
/// there (StopSpawning on the guest). Opening one is the InteractableOneTimeTrigger on it: OnTriggerEnter (0x456060)
/// accepts only an Entity with IsPlayer(), and OnUpdate (0x4562C0) adds deltaTime to TimeInteracting while
/// InteractingEntity isn't null, until InteractionTime (state Triggered). InteractableRewardObject.UpdateAll (0x456A50)
/// then consumes the trigger and runs LootUtil.ProcessLoot, which LootSync already replays on every guest.
///
/// Host:
///   - tracks each reward object from its Setup and tells the guests where it is (RewardSpawn) and when it's gone,
///     opened or not (RewardDespawn, sent from RemoveRewardObject, right after the ProcessLoot of the opening);
///   - every frame, before the trigger's OnUpdate, counts who is on it: the host (the game's own InteractingEntity) plus
///     every living guest that said it is inside (RewardPresence). With only guests inside, the first one's proxy
///     becomes InteractingEntity, so the game's own timer runs; each extra player adds [Coop] ChestOpenBonusPerPlayer
///     of speed (up to x3);
///   - sends the progress and the number of players on each active chest (RewardStates, ~10 Hz, Sequenced).
/// Guest:
///   - builds a copy of the same prefab where the host has it (Instantiate, InteractableTriggerObject.Setup and a minimap
///     icon), so it looks and animates like a local chest;
///   - its own InteractableRewardObject.UpdateAll never runs (no local loot, no guardians, no removal): the loot comes
///     from the host's ProcessLoot, the guardians as puppets;
///   - the copy's trigger shows the host's progress (TimeInteracting and InteractingEntity written before its OnUpdate,
///     kept below InteractionTime), and its OnTriggerEnter/Exit only say whether our own player is inside;
///   - on RewardDespawn(opened) it plays the opening (ConsumeTrigger) and removes the copy.
/// If a guest can't find the prefab, it still opens the chest by standing near the spot (FallbackRadius).
/// Every game patch is made by hand, each in its own try/catch (ApplyPatches): a target that changed in a game update
/// only turns this feature off.
/// </summary>
public static class ChestSync
{
    private const float StateInterval = 0.1f;
    private const float RecentWindow = 1f;          // keep sending a chest's state this long after it changed
    private const float MaxSpeedMultiplier = 3f;
    private const float ShowCap = 0.97f;            // the guest's copy never completes by itself
    private const float FallbackRadius = 2.2f;      // no copy: "inside" = this close to the chest
    private const float StaleInsideDistance = 8f;   // missed OnTriggerExit (collider gone): we're surely out
    private const float TelemetryInterval = 10f;
    private const int MaxStatesPerPacket = 64;

    private static bool _hooked;
    private static float _stateTimer, _telemetryTimer;
    private static int _spawnsSent, _despawnsSent, _statesSent, _presenceRecv, _boostedFrames, _proxyDriven;
    private static int _spawnsRecv, _built, _buildFailures, _fallbacks, _presenceSent, _opened, _errors;

    /// <summary>Something to look at in the per-frame trigger patch (cheap early out for every other trigger).</summary>
    internal static bool Active => Tracked.Count > 0 || Mirrors.Count > 0;

    public static void Tick(float dt)
    {
        EnsureHooked();
        try
        {
            if (CoopRole.IsHostSide) HostTick(dt);
            else if (CoopRole.IsClientSide) ClientTick(dt);
        }
        catch (Exception ex)
        {
            if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][chests] Tick: {ex}");
        }

        _telemetryTimer += dt;
        if (_telemetryTimer < TelemetryInterval) return;
        _telemetryTimer = 0f;
        if (Tracked.Count + Mirrors.Count + _spawnsSent + _spawnsRecv + _presenceRecv + _presenceSent + _errors == 0) return;
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][chests] {NetworkManager.Role}: tracked={Tracked.Count} spawnsSent={_spawnsSent} despawnsSent={_despawnsSent} " +
            $"statesSent={_statesSent} presenceRecv={_presenceRecv} proxyDriven={_proxyDriven} boostedFrames={_boostedFrames} | " +
            $"mirrors={Mirrors.Count} spawnsRecv={_spawnsRecv} built={_built} buildFailures={_buildFailures} fallbacks={_fallbacks} " +
            $"presenceSent={_presenceSent} opened={_opened} errors={_errors}");
        _spawnsSent = _despawnsSent = _statesSent = _presenceRecv = _boostedFrames = _proxyDriven = 0;
        _spawnsRecv = _built = _presenceSent = _opened = 0;
    }

    private static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerDisconnected += _ =>
        {
            if (NetworkManager.HasPeer) return;
            ClearMirrors("everyone disconnected");
            foreach (var t in Tracked.Values) t.Inside.Clear();
        };
        Players.Left += slot => { foreach (var t in Tracked.Values) t.Inside.Remove(slot); };
    }

    /// <summary>PortalSync.OnLocalSceneGone: the scene's chests (ours and the copies) are gone with it.</summary>
    internal static void OnLocalSceneGone()
    {
        Tracked.Clear();
        TrackedByTrigger.Clear();
        ClearMirrors("local scene gone");
    }

    /// <summary>Client, PortalSync.OnHostLeftScene: the host's chests belong to the scene it left.</summary>
    internal static void OnHostLeftScene() => ClearMirrors("host left its scene");

    private static float SpeedMultiplier(int players)
    {
        if (players <= 1) return 1f;
        float bonus = Mathf.Max(0f, Plugin.ChestOpenBonusPerPlayer?.Value ?? 0.5f);
        return Mathf.Min(MaxSpeedMultiplier, 1f + bonus * (players - 1));
    }

    // =====================================================================================
    // Host
    // =====================================================================================

    private sealed class Chest
    {
        public ushort Id;
        public InteractableRewardObject? Reward;
        public InteractableOneTimeTrigger? Trigger;
        public IntPtr RewardPtr, TriggerPtr;
        public string Name = "", MetaAddress = "", TaskAddress = "";
        public int SetupFrame;
        public readonly HashSet<byte> Inside = new();
        public int PlayersOn;        // on it this frame (host + living guests inside)
        public float SetupTime;
        public float RecentUntil;
    }

    private static readonly Dictionary<IntPtr, Chest> Tracked = new();          // by reward object
    private static readonly Dictionary<IntPtr, Chest> TrackedByTrigger = new(); // by its InteractableOneTimeTrigger
    private static ushort _nextId = 1;

    /// <summary>MapTask.CompleteTask is running: the reward it sets up comes from that task's prefab.</summary>
    internal static string CurrentTaskAddress = "";

    private static ushort NextId()
    {
        ushort id = _nextId++;
        if (_nextId == 0) _nextId = 1;
        return id;
    }

    /// <summary>InteractableRewardObject.Setup / InteractableGuardedRewardObject.Setup postfix.</summary>
    internal static void HostOnSetup(InteractableRewardObject reward)
    {
        if (!CoopRole.IsHostSide || reward == null) return;
        var trigger = reward.TriggerObject?.OneTimeTrigger;
        if (trigger == null) return;
        var ptr = reward.Pointer;
        if (Tracked.TryGetValue(ptr, out var old))
        {
            if (old.SetupFrame == Time.frameCount) return; // both Setups of the same spawn
            Forget(old, opened: false, why: "pooled instance reused"); // missed its removal
        }
        string meta = "";
        try { meta = reward.Metadata?.RewardObjectAddress ?? ""; } catch { }
        var c = new Chest
        {
            Id = NextId(),
            Reward = reward,
            Trigger = trigger,
            RewardPtr = ptr,
            TriggerPtr = trigger.Pointer,
            Name = Enemies.EnemyPrefabKeys.StripClone(reward.gameObject.name),
            MetaAddress = meta,
            TaskAddress = CurrentTaskAddress ?? "",
            SetupFrame = Time.frameCount,
            SetupTime = Time.unscaledTime,
        };
        Tracked[ptr] = c;
        TrackedByTrigger[c.TriggerPtr] = c;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: #{c.Id} '{c.Name}' ({reward.MinimapIconType}) at {reward.transform.position}" +
                                 $"{(c.TaskAddress.Length > 0 ? $" from task '{c.TaskAddress}'" : "")}{(meta.Length > 0 ? $" address '{meta}'" : "")}.");
        if (NetworkManager.HasPeer) { SendSpawn(c, Players.NoSlot); }
    }

    /// <summary>InteractableRewardObject.RemoveRewardObject prefix: opened (right after its ProcessLoot) or discarded.</summary>
    internal static void HostOnRemove(InteractableRewardObject reward)
    {
        if (!CoopRole.IsHostSide || reward == null) return;
        if (!Tracked.TryGetValue(reward.Pointer, out var c)) return;
        bool opened = false;
        try { opened = reward.TriggerObject != null && reward.TriggerObject.TriggerConsumed; } catch { }
        Forget(c, opened, opened ? "opened" : "removed");
    }

    private static void Forget(Chest c, bool opened, string why)
    {
        Tracked.Remove(c.RewardPtr);
        TrackedByTrigger.Remove(c.TriggerPtr);
        if (NetworkManager.HasPeer)
        {
            var w = new NetDataWriter();
            w.Put((byte)PacketType.RewardDespawn);
            w.Put(c.Id);
            w.Put((byte)(opened ? 1 : 0));
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            _despawnsSent++;
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: #{c.Id} '{c.Name}' {why}{(c.Inside.Count > 0 ? $" (guests on it: {c.Inside.Count})" : "")}.");
    }

    /// <summary>A player's match started next to ours (CoopSession.OnLobbyState): every chest still up, to that player.</summary>
    internal static void ResyncToPeer(byte slot)
    {
        if (!CoopRole.IsHostSide) return;
        int n = 0;
        foreach (var c in Tracked.Values)
        {
            if (!IsUp(c)) continue;
            SendSpawn(c, slot);
            n++;
        }
        if (n > 0) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: {n} chest(s) resent to slot {slot}.");
    }

    private static bool IsUp(Chest c) => c.Reward != null && c.Trigger != null && c.Reward.gameObject.activeInHierarchy;

    private static void SendSpawn(Chest c, byte toSlot)
    {
        if (c.Reward == null || c.Trigger == null) return;
        var t = c.Reward.transform;
        var p = t.position;
        var q = t.rotation;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.RewardSpawn);
        w.Put(c.Id);
        w.Put(c.Name);
        w.Put(c.MetaAddress);
        w.Put(c.TaskAddress);
        w.Put(p.x); w.Put(p.y); w.Put(p.z);
        w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w);
        w.Put(c.Trigger.TimeInteracting);
        w.Put((byte)Math.Min(c.PlayersOn, 255));
        if (toSlot == Players.NoSlot) NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        else NetworkManager.SendTo(toSlot, w, DeliveryMethod.ReliableOrdered);
        _spawnsSent++;
    }

    /// <summary>A guest stood on (or left) one of our chests.</summary>
    public static void OnPresence(NetDataReader r)
    {
        ushort id = r.GetUShort();
        bool inside = r.GetByte() != 0;
        _presenceRecv++;
        if (NetworkManager.Role != NetRole.Host) return;
        byte from = PacketRouter.From;
        foreach (var c in Tracked.Values)
        {
            if (c.Id != id) continue;
            if (inside) c.Inside.Add(from); else c.Inside.Remove(from);
            c.RecentUntil = Time.unscaledTime + RecentWindow;
            return;
        }
    }

    /// <summary>Host, before the game's InteractableOneTimeTrigger.OnUpdate of one of our chests.</summary>
    private static void HostBeforeTriggerUpdate(InteractableOneTimeTrigger trig, Chest c)
    {
        var state = trig.State;
        if (state == InteractableTriggerState.Triggered || state == InteractableTriggerState.Cancelled) { c.PlayersOn = 0; return; }

        var cur = trig.InteractingEntity;
        var local = GameState.LocalPlayerEntity;
        bool hostInside = cur != null && local != null && cur.Pointer == local.Pointer;

        // Guests only count while the chest can be used at all (its collider is up: not before DelayToEnable, not after).
        int guests = 0;
        Entity? firstProxy = null;
        bool curCounted = false; // the proxy already in InteractingEntity is one of the living guests on it
        var holder = trig.ColliderHolder;
        if (c.Inside.Count > 0 && (holder == null || holder.activeInHierarchy))
        {
            foreach (byte slot in c.Inside)
            {
                if (!Enemies.RemoteProxy.IsAlive(slot)) continue;
                var px = Enemies.RemoteProxy.ProxyOf(slot);
                if (px == null) continue;
                guests++;
                firstProxy ??= px;
                if (cur != null && px.Pointer == cur.Pointer) curCounted = true;
            }
        }

        if (!hostInside)
        {
            bool curIsProxy = cur != null && Enemies.RemoteProxy.IsProxy(cur);
            if (guests > 0 && firstProxy != null)
            {
                if (cur == null || (curIsProxy && !curCounted))
                {
                    trig.InteractingEntity = firstProxy;
                    trig.InteractingEntityTargetTransform = firstProxy.transform;
                }
                _proxyDriven++;
            }
            else if (curIsProxy)
            {
                trig.InteractingEntity = null;
                trig.InteractingEntityTargetTransform = null;
            }
        }

        int players = (hostInside ? 1 : 0) + guests;
        if (players != c.PlayersOn) c.RecentUntil = Time.unscaledTime + RecentWindow;
        c.PlayersOn = players;
        if (players >= 2)
        {
            // The game adds deltaTime right after this; the extra players add their share on top.
            trig.TimeInteracting += Time.deltaTime * (SpeedMultiplier(players) - 1f);
            _boostedFrames++;
        }
    }

    private static readonly List<Chest> Gone = new();

    private static void HostTick(float dt)
    {
        if (Tracked.Count == 0) return;

        // Safety net: a chest that vanished without RemoveRewardObject (scene objects destroyed, pool reset).
        Gone.Clear();
        float t0 = Time.unscaledTime;
        foreach (var c in Tracked.Values)
            if (c.Reward == null || c.Trigger == null || (t0 - c.SetupTime > 2f && !c.Reward.gameObject.activeInHierarchy)) Gone.Add(c);
        foreach (var c in Gone) Forget(c, opened: false, why: "gone");

        if (!NetworkManager.HasPeer) return;
        _stateTimer += dt;
        if (_stateTimer < StateInterval) return;
        _stateTimer = 0f;

        float now = Time.unscaledTime;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.RewardStates);
        int countPos = w.Length;
        w.Put((byte)0);
        int n = 0;
        foreach (var c in Tracked.Values)
        {
            if (n >= MaxStatesPerPacket) break;
            if (c.Trigger == null) continue;
            float time = c.Trigger.TimeInteracting;
            bool busy = c.PlayersOn > 0 || time > 0f || now < c.RecentUntil;
            if (!busy) continue;
            w.Put(c.Id);
            w.Put(time);
            w.Put((byte)Math.Min(c.PlayersOn, 255));
            w.Put(SpeedMultiplier(c.PlayersOn));
            n++;
        }
        if (n == 0) return;
        w.Data[countPos] = (byte)n;
        NetworkManager.Send(w, DeliveryMethod.Sequenced);
        _statesSent++;
    }

    // =====================================================================================
    // Guest
    // =====================================================================================

    private sealed class Mirror
    {
        public ushort Id;
        public string Name = "", MetaAddress = "", TaskAddress = "";
        public Vector3 Pos;
        public Quaternion Rot;
        public GameObject? Go;
        public InteractableRewardObject? Reward;
        public InteractableTriggerObject? TriggerObject;
        public InteractableOneTimeTrigger? Trigger;
        public IntPtr TriggerPtr;
        public int IconId = -1;
        public bool BuildTried, Failed;
        public float Progress;       // the host's TimeInteracting, moved on locally between packets
        public int PlayersOn;        // on it, per the host
        public float Multiplier = 1f;
        public bool LocalInside;     // from the copy's OnTriggerEnter/Exit
        public bool SentInside;
        public bool Opening;
        public float DestroyAt;
    }

    private static readonly Dictionary<ushort, Mirror> Mirrors = new();
    private static readonly Dictionary<IntPtr, Mirror> MirrorByTrigger = new();
    private static readonly List<Mirror> Dying = new();
    private static readonly Dictionary<string, GameObject?> TemplateCache = new();

    public static void OnSpawn(NetDataReader r)
    {
        ushort id = r.GetUShort();
        string name = r.GetString(), meta = r.GetString(), task = r.GetString();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        var rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
        float time = r.GetFloat();
        int players = r.GetByte();
        _spawnsRecv++;
        if (NetworkManager.Role != NetRole.Client || PacketRouter.From != Players.HostSlot) return;
        if (Mirrors.TryGetValue(id, out var m))
        {
            m.Progress = time; m.PlayersOn = players; // resync duplicate: just refresh
            return;
        }
        m = new Mirror { Id = id, Name = name, MetaAddress = meta, TaskAddress = task, Pos = pos, Rot = rot, Progress = time, PlayersOn = players };
        Mirrors[id] = m;
        if (CanBuildNow()) Build(m); // otherwise ClientTick builds it once our match is up
    }

    public static void OnDespawn(NetDataReader r)
    {
        ushort id = r.GetUShort();
        bool opened = r.GetByte() != 0;
        if (NetworkManager.Role != NetRole.Client || PacketRouter.From != Players.HostSlot) return;
        if (!Mirrors.TryGetValue(id, out var m)) return;
        Mirrors.Remove(id);
        if (m.TriggerPtr != IntPtr.Zero) MirrorByTrigger.Remove(m.TriggerPtr);
        if (opened) _opened++;
        Dispose(m, opened);
    }

    public static void OnStates(NetDataReader r)
    {
        int n = r.GetByte();
        if (NetworkManager.Role != NetRole.Client || PacketRouter.From != Players.HostSlot) return;
        for (int i = 0; i < n; i++)
        {
            ushort id = r.GetUShort();
            float time = r.GetFloat();
            int players = r.GetByte();
            float mult = r.GetFloat();
            if (!Mirrors.TryGetValue(id, out var m)) continue;
            m.Progress = time;
            m.PlayersOn = players;
            m.Multiplier = mult > 0f ? mult : 1f;
        }
    }

    private static bool CanBuildNow() => CoopSession.LocalInRun && !CoopSession.StageTransition && GameManagerUtil.HasGameManager;

    private static void ClientTick(float dt)
    {
        float now = Time.unscaledTime;
        for (int i = Dying.Count - 1; i >= 0; i--)
        {
            var d = Dying[i];
            if (now < d.DestroyAt) continue;
            if (d.Go != null) UnityEngine.Object.Destroy(d.Go);
            d.Go = null;
            Dying.RemoveAt(i);
        }
        if (Mirrors.Count == 0) return;

        bool canBuild = CanBuildNow();
        var local = GameState.LocalPlayerEntity;
        bool localOut = Life.CoopLife.LocalOut;
        float gameDt = Time.deltaTime; // the trigger's own clock (0 while paused)
        foreach (var m in Mirrors.Values)
        {
            if (!m.BuildTried && canBuild) Build(m);

            // Between the host's packets the progress keeps moving at the host's speed.
            if (m.PlayersOn > 0 && m.Trigger != null)
                m.Progress = Mathf.Min(m.Progress + gameDt * m.Multiplier, m.Trigger.InteractionTime * ShowCap);

            // Are we on it? From the copy's own trigger; without a copy, from the distance.
            bool inside = false;
            if (local != null && !localOut && canBuild)
            {
                var d = local.transform.position - m.Pos;
                d.y = 0f;
                float dist = d.magnitude;
                if (m.Go != null && m.Trigger != null)
                {
                    if (m.LocalInside && dist > StaleInsideDistance) m.LocalInside = false; // OnTriggerExit missed
                    inside = m.LocalInside;
                }
                else inside = dist <= FallbackRadius;
            }
            if (inside != m.SentInside)
            {
                m.SentInside = inside;
                var w = new NetDataWriter();
                w.Put((byte)PacketType.RewardPresence);
                w.Put(m.Id);
                w.Put((byte)(inside ? 1 : 0));
                NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
                _presenceSent++;
            }
        }
    }

    private static void Build(Mirror m)
    {
        m.BuildTried = true;
        var template = ResolveTemplate(m);
        if (template == null)
        {
            m.Failed = true;
            _buildFailures++;
            _fallbacks++;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Client: no prefab for #{m.Id} '{m.Name}' (address '{m.MetaAddress}', task '{m.TaskAddress}'): " +
                                        $"not shown, still opened by standing within {FallbackRadius} u of {m.Pos}.");
            return;
        }
        try
        {
            var go = UnityEngine.Object.Instantiate(template, m.Pos, m.Rot);
            go.name = $"SoulstoneCoop_Chest_{m.Id}_{m.Name}";
            if (!go.activeSelf) go.SetActive(true);
            m.Go = go;
            var reward = go.GetComponent<InteractableRewardObject>();
            var to = reward != null ? reward.TriggerObject : null;
            var trig = to != null ? to.OneTimeTrigger : null;
            if (reward == null || to == null || trig == null)
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Client: '{m.Name}' has no reward/trigger component: removed, opened by distance.");
                UnityEngine.Object.Destroy(go);
                m.Go = null;
                m.Failed = true;
                _buildFailures++;
                return;
            }
            m.Reward = reward;
            m.TriggerObject = to;
            m.Trigger = trig;
            m.TriggerPtr = trig.Pointer;
            MirrorByTrigger[m.TriggerPtr] = m;
            // What InteractableRewardObject.Setup does that we want: the trigger (collider, delay, visuals) and the icon.
            // Not the rest: no yields (the loot is the host's), no spawn sound for chests far away.
            to.Setup();
            try { m.IconId = MinimapManager.RequestIcon(reward.MinimapIconType, go.transform, m.Pos); }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Client: minimap icon for #{m.Id}: {ex.Message}"); }
            _built++;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Client: #{m.Id} '{m.Name}' ({reward.MinimapIconType}) shown at {m.Pos}.");
        }
        catch (Exception ex)
        {
            m.Failed = true;
            _buildFailures++;
            if (m.Go != null) UnityEngine.Object.Destroy(m.Go);
            m.Go = null; m.Reward = null; m.TriggerObject = null; m.Trigger = null;
            if (m.TriggerPtr != IntPtr.Zero) MirrorByTrigger.Remove(m.TriggerPtr);
            m.TriggerPtr = IntPtr.Zero;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][chests] Client: building #{m.Id} '{m.Name}': {ex}");
        }
    }

    private static void Dispose(Mirror m, bool opened)
    {
        if (m.IconId >= 0)
        {
            try { MinimapManager.RequestRemoveIcon(m.IconId); } catch { }
            m.IconId = -1;
        }
        if (m.Go == null) return;
        if (opened && m.TriggerObject != null && m.Trigger != null)
        {
            try
            {
                // What the host's frame did: the trigger fired (collider off), then ConsumeTrigger (fades, particles, sound).
                m.Trigger.State = InteractableTriggerState.Triggered;
                m.Trigger.InteractingEntity = null;
                var holder = m.Trigger.ColliderHolder;
                if (holder != null) holder.SetActive(false);
                m.TriggerObject.ConsumeTrigger();
                m.Opening = true;
                m.DestroyAt = Time.unscaledTime + Mathf.Clamp(m.TriggerObject.DelayToStore, 1f, 6f) + 0.5f;
                Dying.Add(m);
                return;
            }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Client: opening #{m.Id}: {ex.Message}"); }
        }
        UnityEngine.Object.Destroy(m.Go);
        m.Go = null;
    }

    private static void ClearMirrors(string why)
    {
        if (Mirrors.Count == 0 && Dying.Count == 0) return;
        foreach (var m in Mirrors.Values)
        {
            if (m.IconId >= 0) { try { MinimapManager.RequestRemoveIcon(m.IconId); } catch { } }
            if (m.Go != null) UnityEngine.Object.Destroy(m.Go);
        }
        foreach (var d in Dying) if (d.Go != null) UnityEngine.Object.Destroy(d.Go);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Client: {Mirrors.Count} chest copies cleared ({why}).");
        Mirrors.Clear();
        MirrorByTrigger.Clear();
        Dying.Clear();
    }

    /// <summary>Guest, before the game's OnUpdate of a copy's trigger: show the host's progress, never finish locally.</summary>
    private static void ClientBeforeTriggerUpdate(InteractableOneTimeTrigger trig, Mirror m)
    {
        if (m.Opening) return;
        var state = trig.State;
        if (state == InteractableTriggerState.Triggered || state == InteractableTriggerState.Cancelled) return;
        float total = Mathf.Max(0.01f, trig.InteractionTime);
        float cap = total * ShowCap;
        float dt = Time.deltaTime;
        if (m.PlayersOn > 0 && GameState.LocalPlayerEntity != null)
        {
            // Someone is on it: "being triggered" (bar and animation). The game adds deltaTime right after this.
            trig.InteractingEntity = GameState.LocalPlayerEntity;
            trig.TimeInteracting = Mathf.Max(0f, Mathf.Min(m.Progress, cap) - dt);
        }
        else
        {
            trig.InteractingEntity = null;
            trig.TimeInteracting = Mathf.Clamp(m.Progress, 0f, cap);
        }
    }

    /// <summary>Guest, a copy's OnTriggerEnter/Exit: only note whether it's our own player; the game's code is skipped.</summary>
    private static void ClientTriggerContact(Mirror m, Collider other, bool enter)
    {
        var local = GameState.LocalPlayerEntity;
        if (other == null || local == null) return;
        var rb = other.attachedRigidbody;
        if (rb == null) return;
        var e = rb.GetComponent<Entity>();
        if (e == null || e.Pointer != local.Pointer) return;
        m.LocalInside = enter;
    }

    // ---------------- finding the prefab on the guest ----------------

    private static GameObject? ResolveTemplate(Mirror m)
    {
        string key = $"{m.Name}|{m.MetaAddress}|{m.TaskAddress}";
        if (TemplateCache.TryGetValue(key, out var cached) && cached != null) return cached;
        GameObject? found = null;
        string via = "";
        try
        {
            if (m.MetaAddress.Length > 0)
            {
                found = ResourcesUtil.Load<InteractableRewardObject>(m.MetaAddress)?.gameObject;
                via = "address";
            }
            if (found == null && m.TaskAddress.Length > 0)
            {
                // A MapTask's reward is a direct reference on the task's prefab (MapTask.RewardTriggerObjectPrefab).
                found = ResourcesUtil.Load<MapTask>(m.TaskAddress)?.RewardTriggerObjectPrefab?.gameObject;
                via = "task prefab";
            }
            if (found == null) { found = FindLoadedRewardAsset(m.Name); via = "loaded asset"; }
            if (found == null)
            {
                string addr = FindAddressInMapCollections(m.Name);
                if (addr.Length > 0) { found = ResourcesUtil.Load<InteractableRewardObject>(addr)?.gameObject; via = $"map collection '{addr}'"; }
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Client: resolving '{m.Name}': {ex.Message}"); }
        if (found != null) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Client: prefab '{m.Name}' found ({via}).");
        TemplateCache[key] = found;
        return found;
    }

    private static GameObject? FindLoadedRewardAsset(string name)
    {
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<InteractableRewardObject>());
        for (int i = 0; i < all.Length; i++)
        {
            var r = all[i]?.TryCast<InteractableRewardObject>();
            if (r == null || r.gameObject.scene.IsValid()) continue; // prefabs have no valid scene
            if (Enemies.EnemyPrefabKeys.StripClone(r.gameObject.name) == name) return r.gameObject;
        }
        return null;
    }

    /// <summary>The spawner's chests come from MapCollectionSO.RewardObjects (address per entry): match the name.</summary>
    private static string FindAddressInMapCollections(string name)
    {
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<MapCollectionSO>());
        for (int i = 0; i < all.Length; i++)
        {
            var so = all[i]?.TryCast<MapCollectionSO>();
            var list = so?.RewardObjects;
            if (list == null) continue;
            foreach (var meta in list)
            {
                string? addr = meta?.RewardObjectAddress;
                if (string.IsNullOrEmpty(addr)) continue;
                int slash = addr.LastIndexOf('/');
                string last = slash >= 0 ? addr.Substring(slash + 1) : addr;
                if (last == name) return addr;
            }
        }
        return "";
    }

    // =====================================================================================
    // Game patches (by hand: each one fails alone)
    // =====================================================================================

    internal static void ApplyPatches(Harmony harmony)
    {
        int ok = 0, failed = 0;
        void Patch(Type type, string method, int argc, string? prefix, string? postfix, string? finalizer = null)
        {
            try
            {
                MethodInfo? target = null;
                foreach (var mi in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (mi.Name == method && mi.GetParameters().Length == argc) { target = mi; break; }
                if (target == null) throw new MissingMethodException(type.Name, method);
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(typeof(ChestPatches), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(ChestPatches), postfix) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(typeof(ChestPatches), finalizer) : null);
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                Plugin.StaticLog.LogError($"[SoulstoneCoop][chests] Could not patch {type.Name}.{method}: {ex.Message}");
            }
        }

        Patch(typeof(InteractableRewardObject), nameof(InteractableRewardObject.Setup), 5, null, nameof(ChestPatches.RewardSetupPostfix));
        Patch(typeof(InteractableGuardedRewardObject), nameof(InteractableGuardedRewardObject.Setup), 1, null, nameof(ChestPatches.GuardedSetupPostfix));
        Patch(typeof(InteractableRewardObject), nameof(InteractableRewardObject.RemoveRewardObject), 0, nameof(ChestPatches.RemovePrefix), null);
        Patch(typeof(InteractableRewardObject), nameof(InteractableRewardObject.UpdateAll), 0, nameof(ChestPatches.UpdateAllPrefix), null);
        Patch(typeof(InteractableOneTimeTrigger), nameof(InteractableOneTimeTrigger.OnUpdate), 0, nameof(ChestPatches.TriggerUpdatePrefix), null);
        Patch(typeof(InteractableOneTimeTrigger), nameof(InteractableOneTimeTrigger.OnTriggerEnter), 1, nameof(ChestPatches.TriggerEnterPrefix), null);
        Patch(typeof(InteractableOneTimeTrigger), nameof(InteractableOneTimeTrigger.OnTriggerExit), 1, nameof(ChestPatches.TriggerExitPrefix), null);
        Patch(typeof(MapTask), nameof(MapTask.CompleteTask), 0, nameof(ChestPatches.CompleteTaskPrefix), null, nameof(ChestPatches.CompleteTaskFinalizer));
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Patches: {ok} applied, {failed} failed.");
    }

    // Entry points for ChestPatches (the per-frame ones look the trigger up by pointer).
    internal static void BeforeTriggerUpdate(InteractableOneTimeTrigger trig)
    {
        var ptr = trig.Pointer;
        if (CoopRole.IsHostSide) { if (TrackedByTrigger.TryGetValue(ptr, out var c)) HostBeforeTriggerUpdate(trig, c); }
        else if (CoopRole.IsClientSide && MirrorByTrigger.TryGetValue(ptr, out var m)) ClientBeforeTriggerUpdate(trig, m);
    }

    /// <summary>True: let the game's OnTriggerEnter/Exit run. False on a guest's copy (we only note our own player).</summary>
    internal static bool OnTriggerContact(InteractableOneTimeTrigger trig, Collider other, bool enter)
    {
        if (!CoopRole.IsClientSide || !MirrorByTrigger.TryGetValue(trig.Pointer, out var m)) return true;
        ClientTriggerContact(m, other, enter);
        return false;
    }

    internal static void CountError() => _errors++;
}

/// <summary>Patch bodies for ChestSync.ApplyPatches. Every one in try/catch: they run inside the game's UpdateAll loops.
/// Signatures: references, ints and a float only (no small structs by value, docs/fase4c-notas.md).</summary>
public static class ChestPatches
{
    private static bool _loggedSkip;

    public static void RewardSetupPostfix(InteractableRewardObject __instance)
    {
        try { ChestSync.HostOnSetup(__instance); }
        catch (Exception ex) { ChestSync.CountError(); Plugin.StaticLog.LogError($"[SoulstoneCoop] ChestPatches.RewardSetupPostfix: {ex}"); }
    }

    public static void GuardedSetupPostfix(InteractableGuardedRewardObject __instance)
    {
        try { ChestSync.HostOnSetup(__instance); }
        catch (Exception ex) { ChestSync.CountError(); Plugin.StaticLog.LogError($"[SoulstoneCoop] ChestPatches.GuardedSetupPostfix: {ex}"); }
    }

    public static void RemovePrefix(InteractableRewardObject __instance)
    {
        try { ChestSync.HostOnRemove(__instance); }
        catch (Exception ex) { ChestSync.CountError(); Plugin.StaticLog.LogError($"[SoulstoneCoop] ChestPatches.RemovePrefix: {ex}"); }
    }

    /// <summary>Guest: the reward objects here are the host's copies: never consume, loot, spawn guardians or remove them.</summary>
    public static bool UpdateAllPrefix()
    {
        try
        {
            if (!CoopRole.IsClientSide) return true;
            if (!_loggedSkip)
            {
                _loggedSkip = true;
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][chests] Client: InteractableRewardObject.UpdateAll skipped (chests are the host's).");
            }
            return false;
        }
        catch { return true; }
    }

    public static void TriggerUpdatePrefix(InteractableOneTimeTrigger __instance)
    {
        try
        {
            if (__instance == null || !ChestSync.Active) return;
            ChestSync.BeforeTriggerUpdate(__instance);
        }
        catch (Exception ex) { ChestSync.CountError(); Plugin.StaticLog.LogError($"[SoulstoneCoop] ChestPatches.TriggerUpdatePrefix: {ex}"); }
    }

    public static bool TriggerEnterPrefix(InteractableOneTimeTrigger __instance, Collider other)
    {
        try { return __instance == null || !ChestSync.Active || ChestSync.OnTriggerContact(__instance, other, enter: true); }
        catch (Exception ex) { ChestSync.CountError(); Plugin.StaticLog.LogError($"[SoulstoneCoop] ChestPatches.TriggerEnterPrefix: {ex}"); return true; }
    }

    public static bool TriggerExitPrefix(InteractableOneTimeTrigger __instance, Collider other)
    {
        try { return __instance == null || !ChestSync.Active || ChestSync.OnTriggerContact(__instance, other, enter: false); }
        catch (Exception ex) { ChestSync.CountError(); Plugin.StaticLog.LogError($"[SoulstoneCoop] ChestPatches.TriggerExitPrefix: {ex}"); return true; }
    }

    public static void CompleteTaskPrefix(MapTask __instance)
    {
        try { ChestSync.CurrentTaskAddress = __instance?.TaskMetadata?.TaskAddress ?? ""; }
        catch { ChestSync.CurrentTaskAddress = ""; }
    }

    public static Exception? CompleteTaskFinalizer(Exception? __exception)
    {
        ChestSync.CurrentTaskAddress = "";
        return __exception;
    }
}
