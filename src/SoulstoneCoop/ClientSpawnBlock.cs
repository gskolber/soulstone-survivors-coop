using HarmonyLib;
using UnityEngine;

namespace SoulstoneCoop;

/// <summary>
/// Phase 1: the client must not spawn enemies of its own - the host owns the world. Uses the game's
/// own GameManagerUtil.StopSpawning() (disables every GameManager*Component spawner and clears the
/// boss queue) plus blocks on the paths that bypass it. See docs/fase0-mapa-mundo.md, patches 1-4.
/// Every patch is wrapped in try/catch: these run inside GameLoop.Update, where one exception skips
/// the rest of the frame for every system.
/// </summary>
public static class ClientSpawnBlock
{
    /// <summary>Set while the network layer itself creates an enemy (phase 2 puppets), so the leak
    /// diagnostic doesn't flag it.</summary>
    public static bool NetworkSpawnInProgress;

    public static int LeakedEnemyCount;

    private static float _reapplyTimer;

    /// <summary>Some spawners get their Setup (which re-enables them) through virtual calls whose
    /// order relative to StartMatch isn't certain, so keep re-applying StopSpawning while in a match.
    /// It's idempotent: flag + DisableComponent + queue clears.</summary>
    public static void Tick(float unscaledDeltaTime)
    {
        if (!CoopRole.IsClientSide) return;
        _reapplyTimer += unscaledDeltaTime;
        if (_reapplyTimer < 2f) return;
        _reapplyTimer = 0f;

        if (!GameManagerUtil.HasGameManager) return;
        GameManagerUtil.StopSpawning();
    }

    internal static bool IsEnemyTeam(Entity entity)
    {
        var team = entity._team;
        if (team == null) return false;
        return ((int)team.Team & 12) != 0; // Team.Monsters = Creeps | Titans
    }
}

[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.StartMatch))]
public static class ClientStartMatchPatch
{
    [HarmonyPostfix]
    public static void Postfix(string mapId)
    {
        try
        {
            if (!CoopRole.IsClientSide) return;
            GameManagerUtil.StopSpawning();
            ClientSpawnBlock.LeakedEnemyCount = 0;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][spawn] Client: StartMatch({mapId}) -> StopSpawning() applied.");
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientStartMatchPatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.ResumeSpawning))]
public static class ClientResumeSpawningPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try
        {
            if (!CoopRole.IsClientSide) return true;
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][spawn] Client: blocked ResumeSpawning().");
            return false;
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientResumeSpawningPatch: {ex}");
            return true;
        }
    }
}

[HarmonyPatch(typeof(BossSpawnProcessor), nameof(BossSpawnProcessor.SpawnBosses))]
public static class ClientSpawnBossesPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try
        {
            if (!CoopRole.IsClientSide) return true;
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][spawn] Client: blocked BossSpawnProcessor.SpawnBosses().");
            return false;
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientSpawnBossesPatch: {ex}");
            return true;
        }
    }
}

/// <summary>The BossRush map (reached through the BossRush portal) has its own manager, which spawns each boss on a
/// timer from its UpdateAll, outside BossSpawnProcessor and StopSpawning. The host's bosses come as puppets; the
/// client's victory there comes from the host (CoopLife). No arguments: safe to patch. docs/portais-investigacao.md.</summary>
[HarmonyPatch(typeof(SurvivorsBossRoomGameManager), nameof(SurvivorsBossRoomGameManager.SpawnNextBoss))]
public static class ClientBossRoomSpawnPatch
{
    private static int _blocked;

    [HarmonyPrefix]
    public static bool Prefix()
    {
        try
        {
            if (!CoopRole.IsClientSide) return true;
            if (_blocked++ < 20)
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][spawn] Client: blocked SurvivorsBossRoomGameManager.SpawnNextBoss() (BossRush map).");
            return false;
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientBossRoomSpawnPatch: {ex}");
            return true;
        }
    }
}

[HarmonyPatch(typeof(BossSpawnProcessor), nameof(BossSpawnProcessor.MasterySpawnBosses))]
public static class ClientMasterySpawnBossesPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try
        {
            if (!CoopRole.IsClientSide) return true;
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][spawn] Client: blocked BossSpawnProcessor.MasterySpawnBosses().");
            return false;
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientMasterySpawnBossesPatch: {ex}");
            return true;
        }
    }
}

// Only caller in the game is MasteryGameManager.<SpawnMainBossRoutine>; returning null there broke that coroutine
// (no main boss objective, no pillars). 1.8.0: in Mastery the guest waits for the host's main boss and gets its
// puppet here (Modes.MasterySync); anything else still gets null.
[HarmonyPatch(typeof(BossSpawnProcessor), nameof(BossSpawnProcessor.SpawnSpecificBoss))]
public static class ClientSpawnSpecificBossPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ref Entity __result, string bossName)
    {
        try
        {
            if (!CoopRole.IsClientSide) return true;
            if (Modes.MasterySync.TakeMainBossOverride(out var puppet) && puppet != null)
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][spawn] Client: SpawnSpecificBoss({bossName}) -> the host's main boss puppet.");
                __result = puppet;
                return false;
            }
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][spawn] Client: blocked BossSpawnProcessor.SpawnSpecificBoss({bossName}), returning null.");
            __result = null!;
            return false;
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientSpawnSpecificBossPatch: {ex}");
            return true;
        }
    }
}

/// <summary>Phase 4D: the client never creates ethereal bosses itself (they come as puppets). Reachable from
/// BossSpawnProcessor.UnregisterBossEntity, which the client runs when a boss puppet's bar is taken down.
/// (string, Vector3): the Vector3 is 12 bytes, passed by pointer, safe for the trampoline.</summary>
[HarmonyPatch(typeof(BossSpawnProcessor), nameof(BossSpawnProcessor.SpawnEtherealEntity))]
public static class ClientSpawnEtherealPatch
{
    [HarmonyPrefix]
    public static bool Prefix(string lastBossEntityName)
    {
        try
        {
            if (!CoopRole.IsClientSide) return true;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][spawn] Client: blocked BossSpawnProcessor.SpawnEtherealEntity({lastBossEntityName}).");
            return false;
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientSpawnEtherealPatch: {ex}");
            return true;
        }
    }
}

/// <summary>Diagnostic: any enemy-team Entity set up on the client that the network didn't create is
/// a spawn path we haven't blocked yet.</summary>
[HarmonyPatch(typeof(Entity), nameof(Entity.Setup))]
public static class ClientEntitySetupLeakPatch
{
    [HarmonyPostfix]
    public static void Postfix(Entity __instance)
    {
        try
        {
            if (!CoopRole.IsClientSide || ClientSpawnBlock.NetworkSpawnInProgress) return;
            if (__instance == null || __instance.IsPlayer() || !ClientSpawnBlock.IsEnemyTeam(__instance)) return;
            // Pooled instances can get a second Setup (Entity.Start) after the network created them.
            if (Enemies.ClientPuppets.IsPuppet(__instance)) return;
            if (!GameManagerUtil.HasGameManager) return;
            // The map's God-Entity (Creeps team, at the origin) casts environment skills; it's part of
            // the scene, not a spawned enemy.
            if (__instance.EntityName == "EntityNameGod" || __instance == GameManagerUtil.GameData?.GodEntity) return;

            ClientSpawnBlock.LeakedEnemyCount++;
            var pos = __instance.gameObject.transform.position;
            Plugin.StaticLog.LogWarning(
                $"[SoulstoneCoop][spawn] Client LEAK #{ClientSpawnBlock.LeakedEnemyCount}: enemy '{__instance.EntityName}' " +
                $"({__instance.gameObject.name}) team={__instance._team.Team} pos={pos} set up outside the network.");
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] ClientEntitySetupLeakPatch: {ex}");
        }
    }
}
