using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Phase 4D debug hotkeys, host only, to test bosses and special enemies without waiting for them:
///   F7: spawns near the host a VoidPresence-Survivors, a void hunter group, a mineral and a pillar (each through the
///       game's own spawner when the match has it, otherwise the same recipe by hand).
///   F8: completes the current kill objective (adds the missing kills to GameStats), so the game's own
///       SurvivorsGameManager.UpdateAll spawns the next boss (statue, objective and all).
/// </summary>
public static class SpecialsDebug
{
    private static bool _prevF7, _prevF8;

    public static void Tick(float unscaledDeltaTime)
    {
        if (NetworkManager.Role == NetRole.None || !Plugin.TestKeysOn) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        bool f7 = kb.f7Key.isPressed, f8 = kb.f8Key.isPressed;
        if (!CoopRole.IsHostSide)
        {
            if ((f7 && !_prevF7) || (f8 && !_prevF8))
                Plugin.StaticLog.LogWarning("[SoulstoneCoop][debug] F7/F8 only work in the HOST window.");
            _prevF7 = f7; _prevF8 = f8;
            return;
        }
        try
        {
            if (f7 && !_prevF7) SpawnSpecials();
            if (f8 && !_prevF8) ForceBoss();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][debug] Hotkey: {ex}");
        }
        _prevF7 = f7; _prevF8 = f8;
    }

    private static void ForceBoss()
    {
        var data = GameManagerUtil.GameData;
        var sgm = data?.SurvivorsGameManager;
        var objs = sgm != null ? sgm.ProgressObjectives : null;
        var stats = GameManagerUtil.GameStats;
        if (objs == null || objs.Count == 0 || stats == null)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][debug] F8: no kill objective to complete (objectives={objs?.Count ?? -1}).");
            return;
        }
        var o = objs[0];
        int missing = Math.Max(1, o.Total - o.Current);
        stats.EnemiesKilled += missing;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][debug] F8: kill objective {o.Current}/{o.Total} -> +{missing} kills; the game spawns the boss.");
    }

    private static void SpawnSpecials()
    {
        var player = GameState.LocalPlayerEntity;
        var data = GameManagerUtil.GameData;
        if (player == null || data == null) return;
        Vector3 basePos = player.transform.position;
        int tier = Math.Max(1, data.Tier);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][debug] F7: spawning specials near the host (tier {tier}).");

        Try("VoidPresence", () => SpawnManual("Creeps/VoidPresence-Survivors", Near(basePos, 0), true));

        GameManagerVoidHunterComponent? hunters = null;
        GameManagerMineralSpawnComponent? minerals = null;
        GameManagerPillarsOfDespairComponent? pillars = null;
        var comps = data.GameManagerComponents;
        if (comps != null)
            for (int i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                hunters ??= c.TryCast<GameManagerVoidHunterComponent>();
                minerals ??= c.TryCast<GameManagerMineralSpawnComponent>();
                pillars ??= c.TryCast<GameManagerPillarsOfDespairComponent>();
            }

        Try("void hunters", () =>
        {
            if (hunters != null && hunters._possibleVoidHunters != null && hunters._possibleVoidHunters.Count > 0 && hunters._affixGameplayData != null)
            {
                hunters.SpawnVoidHunter();
                return;
            }
            var all = Global.VoidHunterPrefabs?.VoidHunterPrefabs;
            if (all == null) throw new Exception("no Global.VoidHunterPrefabs");
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || !all[i].Active || all[i].PrefabAddresses == null) continue;
                var addrs = all[i].PrefabAddresses;
                for (int j = 0; j < addrs.Length; j++) SpawnManual("Creeps/" + addrs[j], Near(basePos, 1 + j), true);
                return;
            }
        });

        Try("mineral", () =>
        {
            if (minerals != null && minerals._mineralPrefabReferences != null) { minerals.SpawnMineral(tier); return; }
            SpawnManual("Minerals/CommonCopper-Ore", Near(basePos, 4), false);
        });

        Try("pillar", () =>
        {
            if (pillars != null && pillars._pillarPrefabs != null && pillars._pillarPrefabs.Length > 0)
            {
                pillars.SpawnPillar(tier, 1, Near(basePos, 5));
                return;
            }
            SpawnPillarManual("Pillars/Pillar-Chaos-Survivors", Near(basePos, 5), tier);
        });
    }

    private static void Try(string what, Action a)
    {
        try { a(); Plugin.StaticLog.LogInfo($"[SoulstoneCoop][debug] F7: {what} ok."); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][debug] F7: {what} failed: {ex.Message}"); }
    }

    private static Vector3 Near(Vector3 basePos, int slot)
    {
        float ang = slot * 60f * Mathf.Deg2Rad;
        var p = basePos + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * 9f;
        if (UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, 10f, -1)) p = hit.position;
        return p;
    }

    /// <summary>The recipe of GameManagerAffixSpecialEntitiesComponent.SpawnAffixSpecialEntities (0x80C0C0).</summary>
    private static Entity SpawnManual(string path, Vector3 pos, bool elite)
    {
        var prefab = ResourcesUtil.Load<Entity>(path) ?? throw new Exception($"no prefab at '{path}'");
        ResourcesUtil.GetPrefabInstance<Entity>(prefab, pos, Quaternion.identity, out var inst);
        if (inst == null) throw new Exception("GetPrefabInstance returned null");
        PrepareStats(inst, prefab);
        UnitUtil.ApplyGenericEnemyScaling(inst, elite, true, true, true);
        UnitUtil.ApplyGenericGameModeEnemyScaling(inst);
        inst.Setup();
        RequestHealthBar(inst, elite);
        return inst;
    }

    /// <summary>The recipe of GameManagerPillarsOfDespairComponent.SpawnPillar (0x4189B0), without scene spawn points.</summary>
    private static void SpawnPillarManual(string path, Vector3 pos, int tier)
    {
        var prefab = ResourcesUtil.Load<Entity>(path) ?? throw new Exception($"no prefab at '{path}'");
        ResourcesUtil.GetPrefabInstance<Entity>(prefab, pos, Quaternion.identity, out var inst);
        if (inst == null) throw new Exception("GetPrefabInstance returned null");
        var pillar = inst.GetComponent<BasePillar>() ?? throw new Exception("no BasePillar");
        PrepareStats(inst, prefab);
        pillar.Setup(new Il2CppReferenceArray<Transform>(0), tier, 1);
        if (!inst.HasBeenSetUp) inst.Setup();
        RequestHealthBar(inst, true);
    }

    private static void PrepareStats(Entity inst, Entity prefab)
    {
        inst.CacheReferences();
        var stats = inst._stats;
        if (stats?.Stats != null && prefab._stats?.Stats != null) stats.Stats.CopyStats(prefab._stats.Stats);
        if (stats?.MultiplicativeStats != null) stats.MultiplicativeStats.SetToOne();
    }

    private static void RequestHealthBar(Entity e, bool elite)
    {
        var team = e._team != null ? e._team.Team : default;
        OverheadHealthBarPanelManager.RequestHealthBar(
            UnitUtil.GetTargetTransform(e, PlaceholderType.Overhead), Vector3.zero,
            e.Health, e.AbnormalStatus, e.BuffStatus, e.Ragdoll, e.Stunnable, team, elite);
    }
}
