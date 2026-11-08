using System.Reflection;
using HarmonyLib;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// God meteors for every player (docs/perigos-notas.md).
///
/// GameManagerGodMeteorsComponent drops its meteors around _playerEntity (set once in Setup to the host's player):
/// OnUpdate (0x80DA10, the kill-driven ones) and LaunchGodMeteorAroundPlayer (0x80D820, the map curse) cast the God's
/// "Meteor" skill on a ground point near that entity. It only runs on the host; GodCasts replays each meteor on the
/// guests (visual) and the hits on a guest's proxy reach it as DamageToClient. So the meteors only ever hunted the host,
/// and fell on its body while it was down.
/// Now, around those two calls, _playerEntity is one of the living players (the host or a guest's proxy, which stands
/// where that guest is), taking turns every TurnSeconds; with the host down, only the guests. Restored right after.
///
/// Not changed: the damage zones that chase "the player" (EnvironmentalDamageOnTriggerEnterFollowPlayerSkill, cast by
/// EnvironmentalEntityCastSkillsOnCooldownManager from GameLoop.Update) run on every machine against its own player
/// already, so each guest has its own.
/// </summary>
public static class HostHazards
{
    private const float TurnSeconds = 5f;
    private static readonly List<Entity> Living = new();
    private static int _redirected;
    private static float _telemetryTimer;

    /// <summary>Who the meteors go for right now, or null to leave the host's player.</summary>
    internal static Entity? MeteorTarget()
    {
        if (!CoopRole.IsHostSide || !NetworkManager.HasPeer || !RemoteProxy.Active) return null;
        RemoteProxy.CollectLivingPlayers(Living);
        if (Living.Count == 0) return null;
        int turn = Mathf.FloorToInt(Time.time / TurnSeconds);
        var pick = Living[((turn % Living.Count) + Living.Count) % Living.Count];
        var local = GameState.LocalPlayerEntity;
        return local != null && pick.Pointer == local.Pointer ? null : pick;
    }

    internal static void CountRedirect() => _redirected++;

    public static void Tick(float dt)
    {
        _telemetryTimer += dt;
        if (_telemetryTimer < 10f) return;
        _telemetryTimer = 0f;
        if (_redirected == 0) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hazards] Host: god meteor updates aimed at a guest: {_redirected}.");
        _redirected = 0;
    }

    internal static void ApplyPatches(Harmony harmony)
    {
        int ok = 0;
        foreach (var (name, argc) in new[] { (nameof(GameManagerGodMeteorsComponent.OnUpdate), 3), (nameof(GameManagerGodMeteorsComponent.LaunchGodMeteorAroundPlayer), 0) })
        {
            try
            {
                MethodInfo? target = null;
                foreach (var mi in typeof(GameManagerGodMeteorsComponent).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (mi.Name == name && mi.GetParameters().Length == argc) { target = mi; break; }
                if (target == null) throw new MissingMethodException(nameof(GameManagerGodMeteorsComponent), name);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(GodMeteorTargetPatch), nameof(GodMeteorTargetPatch.Prefix)),
                    finalizer: new HarmonyMethod(typeof(GodMeteorTargetPatch), nameof(GodMeteorTargetPatch.Finalizer)));
                ok++;
            }
            catch (Exception ex)
            {
                Plugin.StaticLog.LogError($"[SoulstoneCoop][hazards] Could not patch GameManagerGodMeteorsComponent.{name} (meteors stay on the host): {ex.Message}");
            }
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hazards] God meteor patches: {ok}/2.");
    }
}

/// <summary>Swap _playerEntity for the call, put the host's back after (finalizer: even if the game throws). Both
/// targets take an int/float or nothing: trampoline-safe.</summary>
public static class GodMeteorTargetPatch
{
    public static void Prefix(GameManagerGodMeteorsComponent __instance, out Entity? __state)
    {
        __state = null;
        try
        {
            var to = HostHazards.MeteorTarget();
            if (to == null || __instance == null) return;
            __state = __instance._playerEntity;
            if (__state == null) return;
            __instance._playerEntity = to;
            HostHazards.CountRedirect();
        }
        catch (Exception ex)
        {
            __state = null;
            Plugin.StaticLog.LogError($"[SoulstoneCoop] GodMeteorTargetPatch.Prefix: {ex.Message}");
        }
    }

    public static Exception? Finalizer(GameManagerGodMeteorsComponent __instance, Entity? __state, Exception? __exception)
    {
        try { if (__state != null && __instance != null) __instance._playerEntity = __state; }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] GodMeteorTargetPatch.Finalizer: {ex.Message}"); }
        return __exception;
    }
}
