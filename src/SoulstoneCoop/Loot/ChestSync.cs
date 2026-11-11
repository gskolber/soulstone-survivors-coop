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
///
/// Map tasks (docs/tarefas-mapa-notas.md) go through the same machinery. A MapTask's start marker is an
/// InteractableTriggerObject with the same InteractableOneTimeTrigger: MapTask.StartTaskAtPosition (0x45CDA0) puts it in
/// _taskTriggerObjectInstance and sets Stage = Spawned; MapTask.UpdateAll (0x45D040) sees its ReadyToTrigger, consumes it
/// and calls OnTriggerInteracted (Stage = Triggered, the task runs: timer, bosses or elites, all on the host). The host
/// finds the markers by polling the MapTasks it has (MapTask.OnEnable/OnDisable patches; StartTaskAtPosition itself takes
/// two structs by value, so it isn't patched) and sends TaskTriggerSpawn instead of RewardSpawn; despawn, states and
/// presence are the chests' packets, with the same ids. The guest's copy comes from the task prefab's
/// TaskTriggerObjectPrefab, with the task's minimap icon (MinimapIconType.TaskEasiest..TaskHardest by difficulty).
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
        if (Tracked.Count + Mirrors.Count + _spawnsSent + _spawnsRecv + _presenceRecv + _presenceSent + _errors + _tasksSpawned == 0) return;
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][chests] {NetworkManager.Role}: tracked={Tracked.Count} spawnsSent={_spawnsSent} despawnsSent={_despawnsSent} " +
            $"statesSent={_statesSent} presenceRecv={_presenceRecv} proxyDriven={_proxyDriven} boostedFrames={_boostedFrames} " +
            $"tasks={KnownTasks.Count} taskMarkers={_tasksSpawned} tasksStarted={_tasksStarted} | " +
            $"mirrors={Mirrors.Count} spawnsRecv={_spawnsRecv} built={_built} buildFailures={_buildFailures} fallbacks={_fallbacks} " +
            $"presenceSent={_presenceSent} opened={_opened} errors={_errors}");
        _spawnsSent = _despawnsSent = _statesSent = _presenceRecv = _boostedFrames = _proxyDriven = 0;
        _spawnsRecv = _built = _presenceSent = _opened = 0;
        _tasksSpawned = _tasksStarted = 0;
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
        TrackedByTrigger.Clear(); // KnownTasks: the destroyed ones drop out of the poll by themselves
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
        public bool IsTask;          // a map task's start marker (Task, TriggerObject), not a reward object
        public InteractableRewardObject? Reward;
        public MapTask? Task;
        public InteractableTriggerObject? TriggerObject;
        public InteractableOneTimeTrigger? Trigger;
        public IntPtr Key, TriggerObjectPtr, TriggerPtr; // Key: the reward object, or the MapTask
        public string Name = "", MetaAddress = "", TaskAddress = "";
        public int Difficulty;       // tasks: MapTaskDifficulty
        public int SetupFrame;
        public readonly HashSet<byte> Inside = new();
        public int PlayersOn;        // on it this frame (host + living guests inside)
        public float SetupTime;
        public float RecentUntil;
    }

    private static readonly Dictionary<IntPtr, Chest> Tracked = new();          // by reward object / MapTask (Chest.Key)
    private static readonly Dictionary<IntPtr, Chest> TrackedByTrigger = new(); // by its InteractableOneTimeTrigger
    private static readonly Dictionary<IntPtr, MapTask> KnownTasks = new();     // every enabled MapTask (host)
    private static readonly Dictionary<string, string> SpawnedTaskAddresses = new(); // 1.9.0: task prefab name -> address (cache)
    private static readonly Dictionary<IntPtr, (Vector3 pos, Quaternion rot)> TaskPoses = new(); // 1.9.0: where each task's marker stood

    /// <summary>1.9.0: where this task's marker stood (Modes.CrusadeSync puts it back there when the task resets).</summary>
    internal static bool TryGetTaskPose(MapTask task, out Vector3 pos, out Quaternion rot)
    {
        pos = default; rot = Quaternion.identity;
        if (task == null || !TaskPoses.TryGetValue(task.Pointer, out var p)) return false;
        pos = p.pos; rot = p.rot;
        return true;
    }

    /// <summary>The address the guest loads this task from: its metadata's, or the one the spawner loaded it with.</summary>
    internal static string AddressOf(MapTask? task)
    {
        if (task == null) return "";
        string a = "";
        try { a = task.TaskMetadata?.TaskAddress ?? ""; } catch { }
        if (a.Length > 0) return a;
        string name = "";
        try
        {
            // The spawner's own record of the task (MapTaskInstance): its metadata comes from the task collection, with
            // the address the task was loaded from. The prefab's TaskMetadata (Crusade maps) has none.
            name = Enemies.EnemyPrefabKeys.StripClone(task.gameObject.name);
            var comps = GameManagerUtil.GameData?.GameManagerComponents;
            if (comps != null)
                for (int i = 0; i < comps.Count; i++)
                {
                    var spawner = comps[i]?.TryCast<GameManagerMapTaskSpawnerComponent>();
                    var active = spawner?.ActiveTasks;
                    if (active == null) continue;
                    for (int k = 0; k < active.Count; k++)
                    {
                        var inst = active[k];
                        if (inst == null || inst.TaskInstance == null || inst.TaskInstance.Pointer != task.Pointer) continue;
                        string addr = inst.taskMetadata?.TaskAddress ?? "";
                        if (addr.Length == 0) continue;
                        if (!SpawnedTaskAddresses.ContainsKey(name))
                            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: task {name} loaded from '{addr}' (spawner).");
                        SpawnedTaskAddresses[name] = addr;
                        return addr;
                    }
                }
            if (SpawnedTaskAddresses.TryGetValue(name, out var s)) return s;
            // The map collection's task list (MapCollectionSO.MapTasks: what the spawner draws from), by prefab name.
            string fromList = FindTaskAddressInCollection(name);
            if (fromList.Length > 0)
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: task {name} is '{fromList}' (map collection).");
                SpawnedTaskAddresses[name] = fromList;
                return fromList;
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Host: address of task {name}: {ex.Message}"); }
        return "";
    }

    /// <summary>1.9.0, host: the running task whose player barrier this is (Modes.CrusadeSync), if any.</summary>
    internal static MapTask? TaskWithBarrier(IntPtr barrier)
    {
        foreach (var t in KnownTasks.Values)
        {
            if (t == null) continue;
            try
            {
                var b = t.TryCast<MapKillBossesInAreaTask>()?._playerBarrierInstance
                        ?? t.TryCast<MapKillElitesInAreaTask>()?._playerBarrierInstance
                        ?? t.TryCast<MapSurviveForSecondsTask>()?._playerBarrierInstance;
                if (b != null && b.Pointer == barrier) return t;
            }
            catch { }
        }
        return null;
    }

    /// <summary>The host's MapTasks (TaskObjectiveSync reads their objectives).</summary>
    internal static IEnumerable<MapTask> HostTasks => KnownTasks.Values;
    private static ushort _nextId = 1;
    private static int _tasksSpawned, _tasksStarted;

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
            TriggerObject = reward.TriggerObject,
            Trigger = trigger,
            Key = ptr,
            TriggerObjectPtr = reward.TriggerObject!.Pointer,
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
        Tracked.Remove(c.Key);
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
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: {(c.IsTask ? "task marker " : "")}#{c.Id} '{c.Name}' {why}{(c.Inside.Count > 0 ? $" (guests on it: {c.Inside.Count})" : "")}.");
    }

    // ---------------- map task markers (host) ----------------

    /// <summary>MapTask.OnEnable / OnDisable postfixes: the tasks the poll looks at.</summary>
    internal static void HostTaskEnabled(MapTask task, bool enabled)
    {
        if (!CoopRole.IsHostSide || task == null) return;
        if (enabled) KnownTasks[task.Pointer] = task;
        else KnownTasks.Remove(task.Pointer); // its marker (if any) is dropped by the next poll
    }

    private static readonly List<IntPtr> DeadTasks = new();
    private static readonly List<Chest> TaskScratch = new();

    /// <summary>Every frame: a task with Stage Spawned and its marker up is tracked; the marker goes away when the task
    /// starts (Stage Triggered: "opened"), is cancelled or the task is gone.</summary>
    private static void HostPollTasks()
    {
        DeadTasks.Clear();
        foreach (var kv in KnownTasks)
        {
            var task = kv.Value;
            if (task == null) { DeadTasks.Add(kv.Key); continue; }
            Tracked.TryGetValue(kv.Key, out var c);
            InteractableTriggerObject? marker = null;
            bool spawned = false;
            try
            {
                spawned = task.Stage == MapTaskCompletionStage.Spawned;
                marker = spawned ? task._taskTriggerObjectInstance : null;
            }
            catch { DeadTasks.Add(kv.Key); }
            bool up = spawned && marker != null && marker.gameObject.activeInHierarchy && marker.OneTimeTrigger != null;
            if (c != null)
            {
                if (up && c.TriggerObjectPtr == marker!.Pointer) continue;
                MapTaskCompletionStage stage = MapTaskCompletionStage.None;
                try { stage = task.Stage; } catch { }
                bool started = stage == MapTaskCompletionStage.Triggered || stage == MapTaskCompletionStage.Completed;
                if (started) _tasksStarted++;
                Forget(c, opened: started, why: started ? "started" : $"gone (stage {stage})");
            }
            if (up && !AddressPending(kv.Key, task)) TrackTask(kv.Key, task, marker!);
        }
        foreach (var p in DeadTasks)
        {
            KnownTasks.Remove(p);
            if (Tracked.TryGetValue(p, out var c)) Forget(c, opened: false, why: "task gone");
        }
        // A marker of a task that left KnownTasks (disabled) while it was up.
        TaskScratch.Clear();
        foreach (var c in Tracked.Values) if (c.IsTask && !KnownTasks.ContainsKey(c.Key)) TaskScratch.Add(c);
        foreach (var c in TaskScratch) Forget(c, opened: false, why: "task disabled");
    }

    /// <summary>1.9.0: the address in Global.MapCollection.MapTasks whose file is this task prefab (exact name first,
    /// then any address containing it). Both sides have the collection.</summary>
    internal static string FindTaskAddressInCollection(string taskName)
    {
        if (string.IsNullOrEmpty(taskName)) return "";
        try
        {
            var tasks = Global.MapCollection?.MapTasks;
            if (tasks == null) return "";
            string contains = "";
            for (int i = 0; i < tasks.Count; i++)
            {
                string addr = tasks[i]?.TaskAddress ?? "";
                if (addr.Length == 0) continue;
                string file = addr;
                int slash = file.LastIndexOf('/');
                if (slash >= 0) file = file.Substring(slash + 1);
                int dot = file.LastIndexOf('.');
                if (dot > 0) file = file.Substring(0, dot);
                if (string.Equals(file, taskName, StringComparison.OrdinalIgnoreCase)) return addr;
                if (contains.Length == 0 && addr.IndexOf(taskName, StringComparison.OrdinalIgnoreCase) >= 0) contains = addr;
            }
            return contains;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Task address of '{taskName}' in the map collection: {ex.Message}");
            return "";
        }
    }

    private static readonly Dictionary<IntPtr, float> AddressWaitSince = new();

    /// <summary>1.9.0: a marker whose task address isn't known yet (the spawner lists the task right after the marker
    /// goes up) waits up to 2 s, so the guest's TaskTriggerSpawn carries it.</summary>
    private static bool AddressPending(IntPtr key, MapTask task)
    {
        if (AddressOf(task).Length > 0) { AddressWaitSince.Remove(key); return false; }
        if (!AddressWaitSince.TryGetValue(key, out float since)) { AddressWaitSince[key] = Time.unscaledTime; return true; }
        if (Time.unscaledTime - since < 2f) return true;
        AddressWaitSince.Remove(key);
        return false;
    }

    private static void TrackTask(IntPtr key, MapTask task, InteractableTriggerObject marker)
    {
        var trigger = marker.OneTimeTrigger!;
        string address = "";
        int difficulty = 0;
        try
        {
            var meta = task.TaskMetadata;
            if (meta != null) { address = meta.TaskAddress ?? ""; difficulty = (int)meta.Difficulty; }
        }
        catch { }
        if (address.Length == 0) address = AddressOf(task); // 1.9.0: Crusade tasks
        TaskPoses[key] = (marker.transform.position, marker.transform.rotation);
        var c = new Chest
        {
            Id = NextId(),
            IsTask = true,
            Task = task,
            TriggerObject = marker,
            Trigger = trigger,
            Key = key,
            TriggerObjectPtr = marker.Pointer,
            TriggerPtr = trigger.Pointer,
            Name = Enemies.EnemyPrefabKeys.StripClone(marker.gameObject.name),
            TaskAddress = address,
            Difficulty = difficulty,
            SetupFrame = Time.frameCount,
            SetupTime = Time.unscaledTime,
        };
        Tracked[key] = c;
        TrackedByTrigger[c.TriggerPtr] = c;
        _tasksSpawned++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Host: task marker #{c.Id} '{c.Name}' ({Enemies.EnemyPrefabKeys.StripClone(task.gameObject.name)}, " +
                                 $"difficulty {difficulty}, task '{address}') at {marker.transform.position}.");
        if (NetworkManager.HasPeer) SendSpawn(c, Players.NoSlot);
    }

    /// <summary>The minimap icon MapTask.StartTaskAtPosition picks: Easy..Hardest -> TaskEasy..TaskHardest, else TaskEasiest.</summary>
    private static MinimapIconType TaskIconType(int difficulty) =>
        difficulty >= 2 && difficulty <= 5 ? (MinimapIconType)((int)MinimapIconType.TaskEasiest + difficulty - 1) : MinimapIconType.TaskEasiest;

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

    private static GameObject? Root(Chest c) => c.IsTask ? (c.TriggerObject != null ? c.TriggerObject.gameObject : null)
                                                       : (c.Reward != null ? c.Reward.gameObject : null);

    private static bool IsUp(Chest c)
    {
        var root = Root(c);
        return root != null && c.Trigger != null && root.activeInHierarchy;
    }

    private static void SendSpawn(Chest c, byte toSlot)
    {
        var root = Root(c);
        if (root == null || c.Trigger == null) return;
        var t = root.transform;
        var p = t.position;
        var q = t.rotation;
        var w = new NetDataWriter();
        if (c.IsTask)
        {
            // TaskTriggerSpawn: id u16 | marker name, task address (str) | difficulty u8 | pos 3f | rot 4f | progress f32 | players u8
            w.Put((byte)PacketType.TaskTriggerSpawn);
            w.Put(c.Id);
            w.Put(c.Name);
            w.Put(c.TaskAddress);
            w.Put((byte)Mathf.Clamp(c.Difficulty, 0, 255));
        }
        else
        {
            w.Put((byte)PacketType.RewardSpawn);
            w.Put(c.Id);
            w.Put(c.Name);
            w.Put(c.MetaAddress);
            w.Put(c.TaskAddress);
        }
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
            // Without the host on it, only the host's player is ever put here by the game (OnTriggerEnter wants
            // IsPlayer()), so anything else is ours: a living guest's proxy, or a stale one. A proxy recycled or
            // rebuilt mid-open (guest left, surrendered, reconnecting) has already left RemoteProxy's lookup, and a
            // destroyed one reads as null to Unity while the field still holds it (the game's OnUpdate only tests the
            // raw pointer): both would keep the game's timer running with nobody on it.
            bool curSet = !ReferenceEquals(cur, null);
            if (guests > 0 && firstProxy != null)
            {
                if (!curCounted)
                {
                    trig.InteractingEntity = firstProxy;
                    trig.InteractingEntityTargetTransform = firstProxy.transform;
                }
                _proxyDriven++;
            }
            else if (curSet && (cur == null || local != null || Enemies.RemoteProxy.IsProxy(cur))) // no local player: can't tell, proxies only
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
        if (KnownTasks.Count > 0) HostPollTasks();
        if (Tracked.Count == 0) return;

        // Safety net: a chest that vanished without RemoveRewardObject (scene objects destroyed, pool reset).
        Gone.Clear();
        float t0 = Time.unscaledTime;
        foreach (var c in Tracked.Values)
        {
            var root = Root(c);
            if (root == null || c.Trigger == null || (t0 - c.SetupTime > 2f && !root.activeInHierarchy)) Gone.Add(c);
        }
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
        public bool IsTask;          // a map task's start marker
        public int Difficulty;
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
        public bool VoicePlayed;     // our character's "opening a chest" / "mining" line, once per object
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

    /// <summary>A map task's start marker is up on the host (same ids as the chests: despawn/states/presence are shared).</summary>
    public static void OnTaskSpawn(NetDataReader r)
    {
        ushort id = r.GetUShort();
        string name = r.GetString(), task = r.GetString();
        int difficulty = r.GetByte();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        var rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
        float time = r.GetFloat();
        int players = r.GetByte();
        _spawnsRecv++;
        if (NetworkManager.Role != NetRole.Client || PacketRouter.From != Players.HostSlot) return;
        if (Mirrors.TryGetValue(id, out var m))
        {
            m.Progress = time; m.PlayersOn = players;
            return;
        }
        m = new Mirror { Id = id, IsTask = true, Difficulty = difficulty, Name = name, TaskAddress = task, Pos = pos, Rot = rot, Progress = time, PlayersOn = players };
        Mirrors[id] = m;
        if (CanBuildNow()) Build(m);
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
                if (inside && !m.IsTask && !m.VoicePlayed) PlayOpeningVoice(m, local!);
                var w = new NetDataWriter();
                w.Put((byte)PacketType.RewardPresence);
                w.Put(m.Id);
                w.Put((byte)(inside ? 1 : 0));
                NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
                _presenceSent++;
            }
        }
    }

    /// <summary>1.3.2: on the host, InteractableRewardObject.UpdateAll has the player's character say its line when
    /// someone starts opening (VoiceOverManager.TreasureChestOpen, or PlayMiningStart for ore), always for the host's
    /// character. Here the guest's own character says it, the first time it steps on the copy.</summary>
    private static void PlayOpeningVoice(Mirror m, Entity local)
    {
        m.VoicePlayed = true;
        try
        {
            var ct = (CharactersType)Lobby.CoopSession.Local.CharacterId;
            if (m.Name.Contains("-Mine") || m.Name.Contains("Mineral")) VoiceOverManager.PlayMiningStart(ct, local);
            else VoiceOverManager.TreasureChestOpen(ct, local);
        }
        catch (Exception ex) { if (_voiceErrors++ < 3) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Opening voice for '{m.Name}': {ex.Message}"); }
    }

    private static int _voiceErrors;

    private static void Build(Mirror m)
    {
        m.BuildTried = true;
        var template = ResolveTemplate(m);
        if (template == null)
        {
            m.Failed = true;
            _buildFailures++;
            _fallbacks++;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Client: no prefab for {(m.IsTask ? "task marker " : "")}#{m.Id} '{m.Name}' (address '{m.MetaAddress}', task '{m.TaskAddress}'): " +
                                        $"not shown, still {(m.IsTask ? "started" : "opened")} by standing within {FallbackRadius} u of {m.Pos}.");
            if (m.IsTask) RequestMirrorIcon(m, null); // at least the marker on the minimap
            return;
        }
        if (m.IsTask) { BuildTask(m, template); return; }
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

    /// <summary>A task marker's copy: the trigger object itself is the prefab's root (no reward object around it).</summary>
    private static void BuildTask(Mirror m, GameObject template)
    {
        try
        {
            var go = UnityEngine.Object.Instantiate(template, m.Pos, m.Rot);
            go.name = $"SoulstoneCoop_Task_{m.Id}_{m.Name}";
            if (!go.activeSelf) go.SetActive(true);
            m.Go = go;
            var to = go.GetComponent<InteractableTriggerObject>() ?? go.GetComponentInChildren<InteractableTriggerObject>(true);
            var trig = to != null ? to.OneTimeTrigger : null;
            if (to == null || trig == null)
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Client: task marker '{m.Name}' has no trigger component: removed, started by distance.");
                UnityEngine.Object.Destroy(go);
                m.Go = null;
                m.Failed = true;
                _buildFailures++;
                RequestMirrorIcon(m, null);
                return;
            }
            m.TriggerObject = to;
            m.Trigger = trig;
            m.TriggerPtr = trig.Pointer;
            MirrorByTrigger[m.TriggerPtr] = m;
            to.Setup(); // what StartTaskAtPosition does to it: collider after DelayToEnable, visuals
            RequestMirrorIcon(m, go.transform);
            _built++;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][chests] Client: task marker #{m.Id} '{m.Name}' (task '{m.TaskAddress}', difficulty {m.Difficulty}) shown at {m.Pos}.");
        }
        catch (Exception ex)
        {
            m.Failed = true;
            _buildFailures++;
            if (m.Go != null) UnityEngine.Object.Destroy(m.Go);
            m.Go = null; m.TriggerObject = null; m.Trigger = null;
            if (m.TriggerPtr != IntPtr.Zero) MirrorByTrigger.Remove(m.TriggerPtr);
            m.TriggerPtr = IntPtr.Zero;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][chests] Client: building task marker #{m.Id} '{m.Name}': {ex}");
        }
    }

    private static void RequestMirrorIcon(Mirror m, Transform? follow)
    {
        if (m.IconId >= 0) return;
        try { m.IconId = MinimapManager.RequestIcon(TaskIconType(m.Difficulty), follow, m.Pos); }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][chests] Client: minimap icon for task marker #{m.Id}: {ex.Message}"); }
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
        string key = $"{(m.IsTask ? "task" : "reward")}|{m.Name}|{m.MetaAddress}|{m.TaskAddress}";
        if (TemplateCache.TryGetValue(key, out var cached) && cached != null) return cached;
        GameObject? found = null;
        string via = "";
        try
        {
            if (m.IsTask)
            {
                // MapTask.TaskTriggerObjectPrefab, a direct reference on the task's prefab.
                if (m.TaskAddress.Length > 0)
                {
                    found = ResourcesUtil.Load<MapTask>(m.TaskAddress)?.TaskTriggerObjectPrefab?.gameObject;
                    via = "task prefab";
                }
                if (found == null && m.TaskAddress.Length == 0 && m.Name.StartsWith("Trigger-"))
                {
                    // 1.9.0: an older host (or one that couldn't tell): the task prefab is named like the marker.
                    string addr = FindTaskAddressInCollection("MapTask-" + m.Name.Substring("Trigger-".Length));
                    if (addr.Length > 0)
                    {
                        found = ResourcesUtil.Load<MapTask>(addr)?.TaskTriggerObjectPrefab?.gameObject;
                        via = $"map collection '{addr}'";
                    }
                }
                if (found == null) { found = FindLoadedTriggerAsset(m.Name); via = "loaded asset"; }
            }
            else if (m.MetaAddress.Length > 0)
            {
                found = ResourcesUtil.Load<InteractableRewardObject>(m.MetaAddress)?.gameObject;
                via = "address";
            }
            if (found == null && !m.IsTask && m.TaskAddress.Length > 0)
            {
                // A MapTask's reward is a direct reference on the task's prefab (MapTask.RewardTriggerObjectPrefab).
                found = ResourcesUtil.Load<MapTask>(m.TaskAddress)?.RewardTriggerObjectPrefab?.gameObject;
                via = "task prefab";
            }
            if (found == null && !m.IsTask) { found = FindLoadedRewardAsset(m.Name); via = "loaded asset"; }
            if (found == null && !m.IsTask)
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

    private static GameObject? FindLoadedTriggerAsset(string name)
    {
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<InteractableTriggerObject>());
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i]?.TryCast<InteractableTriggerObject>();
            if (t == null || t.gameObject.scene.IsValid()) continue; // prefabs have no valid scene
            if (t.transform.parent != null) continue;               // a chest's trigger, inside its reward prefab
            if (Enemies.EnemyPrefabKeys.StripClone(t.gameObject.name) == name) return t.gameObject;
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
        Patch(typeof(MapTask), nameof(MapTask.OnEnable), 0, null, nameof(ChestPatches.TaskEnablePostfix));
        Patch(typeof(MapTask), nameof(MapTask.OnDisable), 0, null, nameof(ChestPatches.TaskDisablePostfix));
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
        try { ChestSync.CurrentTaskAddress = ChestSync.AddressOf(__instance); }
        catch { ChestSync.CurrentTaskAddress = ""; }
    }

    public static Exception? CompleteTaskFinalizer(Exception? __exception)
    {
        ChestSync.CurrentTaskAddress = "";
        return __exception;
    }

    public static void TaskEnablePostfix(MapTask __instance)
    {
        try { ChestSync.HostTaskEnabled(__instance, enabled: true); }
        catch (Exception ex) { ChestSync.CountError(); Plugin.StaticLog.LogError($"[SoulstoneCoop] ChestPatches.TaskEnablePostfix: {ex}"); }
    }

    public static void TaskDisablePostfix(MapTask __instance)
    {
        try { ChestSync.HostTaskEnabled(__instance, enabled: false); }
        catch (Exception ex) { ChestSync.CountError(); Plugin.StaticLog.LogError($"[SoulstoneCoop] ChestPatches.TaskDisablePostfix: {ex}"); }
    }
}
