using Il2CppInterop.Runtime;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop.Loot;

/// <summary>
/// Debug hotkey, host only, to test ChestSync without waiting for a chest (docs/baus-notas.md):
///   F3: spawns a reward object 5 u in front of the host, through the game's own pool and Setup, so it goes through
///       exactly the path of a natural spawn (the Setup postfix tracks it and sends it to the guests). Each press takes
///       the next prefab of the list: the map collections' guarded chests (MapCollectionSO.RewardObjects, Setup(tier))
///       and the MapTask rewards (chests and ore, MapTask.RewardTriggerObjectPrefab, Setup with a little XP and
///       soulstones), so a few presses cover every kind.
/// </summary>
public static class ChestDebug
{
    private static bool _prevF3;
    private static readonly List<InteractableRewardObject> Prefabs = new();
    private static int _next;
    private static bool _collected;

    public static void Tick()
    {
        if (NetworkManager.Role == NetRole.None) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        bool f3 = kb.f3Key.isPressed;
        bool pressed = f3 && !_prevF3;
        _prevF3 = f3;
        if (!pressed || Ui.CoopUi.IsTyping) return;
        if (!CoopRole.IsHostSide)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][debug] F3 (spawn a chest) only works in the HOST window.");
            return;
        }
        try { Spawn(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][debug] F3: {ex}"); }
    }

    private static void Spawn()
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null || !GameManagerUtil.HasGameManager)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][debug] F3: only inside a match.");
            return;
        }
        if (!_collected) Collect();
        if (Prefabs.Count == 0)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][debug] F3: no chest/ore prefab found in the map collections.");
            return;
        }

        var prefab = Prefabs[_next++ % Prefabs.Count];
        var t = local.transform;
        var fwd = t.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        fwd.Normalize();
        var pos = t.position + fwd * 5f;
        var rot = Quaternion.LookRotation(-fwd); // facing the host

        ResourcesUtil.GetPrefabInstance<InteractableRewardObject>(prefab, pos, rot, out var inst);
        if (inst == null) throw new Exception("GetPrefabInstance returned null");
        var guarded = inst.TryCast<InteractableGuardedRewardObject>();
        if (guarded != null) guarded.Setup(GameManagerUtil.GetCurrentTierRating());
        else inst.Setup(50, 0f, 5, 0, null); // 50 XP and 5 minor soulstones, so the loot is easy to see
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][debug] F3: spawned '{prefab.name}' ({prefab.MinimapIconType}{(guarded != null ? ", guarded" : "")}) " +
                                 $"at {pos} - {(_next - 1) % Prefabs.Count + 1}/{Prefabs.Count}, press again for the next one.");
    }

    /// <summary>Every reward prefab the loaded map collections point at, each name once.</summary>
    private static void Collect()
    {
        _collected = true;
        var names = new HashSet<string>();
        void Add(InteractableRewardObject? r, string from)
        {
            if (r == null || !names.Add(r.name)) return;
            Prefabs.Add(r);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][debug] F3 list #{Prefabs.Count}: '{r.name}' ({r.MinimapIconType}) from {from}.");
        }

        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<MapCollectionSO>());
        for (int i = 0; i < all.Length; i++)
        {
            var so = all[i]?.TryCast<MapCollectionSO>();
            if (so == null) continue;
            var rewards = so.RewardObjects;
            if (rewards != null)
                foreach (var meta in rewards)
                {
                    string? addr = meta?.RewardObjectAddress;
                    if (string.IsNullOrEmpty(addr)) continue;
                    try { Add(ResourcesUtil.Load<InteractableRewardObject>(addr), $"reward '{addr}'"); }
                    catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][debug] F3: loading '{addr}': {ex.Message}"); }
                }
            var tasks = so.MapTasks;
            if (tasks != null)
                foreach (var task in tasks)
                {
                    string? addr = task?.TaskAddress;
                    if (string.IsNullOrEmpty(addr)) continue;
                    try { Add(ResourcesUtil.Load<MapTask>(addr)?.RewardTriggerObjectPrefab, $"task '{addr}'"); }
                    catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][debug] F3: loading '{addr}': {ex.Message}"); }
                }
        }
    }
}
