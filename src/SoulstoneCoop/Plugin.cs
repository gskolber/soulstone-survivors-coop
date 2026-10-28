using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace SoulstoneCoop;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "com.kolber.soulstonecoop";
    public const string PluginName = "Soulstone Coop";
    // Checked in the connection handshake (phase 6): host and client must match exactly.
    public const string PluginVersion = "1.0.0";

    internal static ManualLogSource StaticLog = null!;
    internal static BepInEx.Configuration.ConfigEntry<bool> AutoHost = null!;
    internal static BepInEx.Configuration.ConfigEntry<string> LastAddress = null!;
    internal static BepInEx.Configuration.ConfigEntry<bool> AllowRejoin = null!;
    internal static BepInEx.Configuration.ConfigEntry<bool> EndRunWhenHostLeaves = null!;
    internal static BepInEx.Configuration.ConfigEntry<bool> SenderClock = null!;
    internal static BepInEx.Configuration.ConfigEntry<int> MaxPlayers = null!;
    internal static BepInEx.Configuration.ConfigEntry<SteamLobbyType> LobbyType = null!;
    internal static BepInEx.Configuration.ConfigEntry<float> EnemyScalePerPlayer = null!;
    internal static BepInEx.Configuration.ConfigEntry<float> ReplicaCastRadius = null!;

    /// <summary>Phase 9: who may join the Steam lobby ([Coop] LobbyType).</summary>
    public enum SteamLobbyType { FriendsOnly, Public, Private }
    private static int _simLatency, _simJitter, _simLoss;

    public override void Load()
    {
        StaticLog = Log;
        Log.LogInfo($"{PluginName} {PluginVersion} loading...");
        Net.LevelUpPolicy.Bind(Config);
        Ui.Loc.Init(Config); // docs/i18n-notas.md
        Ui.UpdateCheck.Bind(Config); // 1.0: "new version" notice
        Ui.PartyHud.Bind(Config); // issue #7: party panel

        AutoHost = Config.Bind("Debug", "AutoHost", false,
            "Start hosting automatically when the main menu opens (instead of the Co-op panel or Insert).");
        LastAddress = Config.Bind("Coop", "LastAddress", "127.0.0.1",
            "Host address last typed in the Co-op panel (filled in automatically next time).");

        // Phase 8 (docs/fase8-notas.md).
        var simLatency = Config.Bind("Debug", "SimulateLatencyMs", 0,
            "Test only: extra delay in ms on one direction of the co-op link (turn it on in both games: 75 + 75 = about 150 ms of ping). 0 = off.");
        var simJitter = Config.Bind("Debug", "SimulateJitterMs", 0,
            "Test only: random +- variation in ms on top of SimulateLatencyMs. 0 = off.");
        var simLoss = Config.Bind("Debug", "SimulatePacketLossPercent", 0,
            "Test only: percent of packets lost (0-50). Reliable messages are resent, so they arrive late instead. 0 = off.");
        AllowRejoin = Config.Bind("Coop", "AllowRejoin", false,
            "Host only, experimental: a partner who connects while you are in a run is brought into it (as a new character). Off = they wait in the menu.");
        EndRunWhenHostLeaves = Config.Bind("Coop", "EndRunWhenHostLeaves", true,
            "Client: when the host disconnects mid-run, end the run (surrender) a few seconds later instead of staying in an empty map.");
        SenderClock = Config.Bind("Coop", "SenderClock", false,
            "Experimental: place the partner and the enemies by the sender's clock instead of the arrival time (smoother with jitter).");
        // Phase 9 (docs/fase9-notas.md).
        MaxPlayers = Config.Bind("Coop", "MaxPlayers", 8, new BepInEx.Configuration.ConfigDescription(
            "Host: how many players fit in your co-op session, you included (2-8). Clients past that are refused.",
            new BepInEx.Configuration.AcceptableValueRange<int>(2, 8)));
        LobbyType = Config.Bind("Coop", "LobbyType", SteamLobbyType.FriendsOnly,
            "Host, Steam: who can join your lobby. FriendsOnly = friends and invitees of the players in it (anyone in the group can invite). " +
            "Public = anyone with the lobby code. Private = only who gets an invite.");
        EnemyScalePerPlayer = Config.Bind("Coop", "EnemyScalePerPlayer", 0f, new BepInEx.Configuration.ConfigDescription(
            "Host: more enemies alive at once for each extra player in the run (the game's limits x (1 + this x (players - 1)), up to 200). " +
            "0 = off (the game's own numbers). Suggested: 0.25 (8 players: x2.75).", new BepInEx.Configuration.AcceptableValueRange<float>(0f, 2f)));
        ReplicaCastRadius = Config.Bind("Coop", "ReplicaCastRadius", 60f,
            "Spells of the other players are only replayed (visual only) when they are within this distance of you; farther ones are off screen anyway. " +
            "Saves the effects of 7 builds on every PC. 0 = always replay.");
        _simLatency = simLatency.Value;
        _simJitter = simJitter.Value;
        _simLoss = simLoss.Value;
        ParseLaunchArgs();
        Ui.UpdateCheck.Start(); // worker thread, fails silently
        Net.NetSim.Configure(_simLatency, _simJitter, _simLoss);
        Net.NetSim.LogBanner();

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);

        // Patched by hand: its by-value struct parameter (EntityByEnemyPrefabData) is the kind of signature
        // Il2CppInterop trampolines can choke on, and a failure here must not take the other patches down.
        // Without it, enemy keys fall back to the prefab name and tier/elite are unknown.
        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(EnemyPrefabsUtil), nameof(EnemyPrefabsUtil.SpawnEnemyEntity)),
                prefix: new HarmonyMethod(typeof(Enemies.HostSpawnEnemyEntityPatch), nameof(Enemies.HostSpawnEnemyEntityPatch.Prefix)),
                postfix: new HarmonyMethod(typeof(Enemies.HostSpawnEnemyEntityPatch), nameof(Enemies.HostSpawnEnemyEntityPatch.Postfix)));
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Could not patch EnemyPrefabsUtil.SpawnEnemyEntity (enemy keys fall back to prefab names): {ex}");
        }

        // Phase 6: the client swaps the host's Survivors affixes in while its AffixGameplayData is built. A
        // constructor, so patched by hand: if this fails, runs still start, just with the client's own affixes.
        try
        {
            var ctor = AccessTools.Constructor(typeof(AffixGameplayData),
                new[] { typeof(Il2CppSystem.Collections.Generic.List<string>), typeof(int) });
            harmony.Patch(ctor,
                prefix: new HarmonyMethod(typeof(Lobby.RunSync), nameof(Lobby.RunSync.AffixCtorPrefix)),
                postfix: new HarmonyMethod(typeof(Lobby.RunSync), nameof(Lobby.RunSync.AffixCtorPostfix)));
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Could not patch AffixGameplayData..ctor (the client keeps its own affixes): {ex}");
        }

        // Phase 7: take over the Steam callback pump (lobby invites, P2P sessions). By hand: if it fails, the game
        // keeps pumping its own callbacks and only the Steam invite is lost.
        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(Steamworks.SteamAPI), nameof(Steamworks.SteamAPI.RunCallbacks)),
                prefix: new HarmonyMethod(typeof(Steam.SteamRunCallbacksPatch), nameof(Steam.SteamRunCallbacksPatch.Prefix)));
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Could not patch SteamAPI.RunCallbacks (Steam invites off, IP still works): {ex}");
        }

        // Force full stack traces for exceptions so we can actually tell which system is
        // throwing (ghost-clone side effects otherwise show up as a bare "NullReferenceException"
        // with no trace in the log at all).
        UnityEngine.Application.SetStackTraceLogType(UnityEngine.LogType.Exception, UnityEngine.StackTraceLogType.Full);

        // NOTE: CoopBehaviour is intentionally NOT created here. Creating a persistent
        // GameObject this early (during Chainloader startup, before Unity's scene system
        // is fully up) causes the engine to silently disable it before Update ever ticks.
        // It's created instead on the first real gameplay event (see GameplayUIManagerSetupPatch).

        Log.LogInfo($"{PluginName} loaded, harmony patches applied.");
    }

    // Launch args take precedence over the shared config file, so two instances of the game on
    // one PC can play different roles: --coop-host or --coop-connect <address>.
    internal static bool HostArg;
    internal static string? ConnectAddressArg;

    /// <summary>Test only: pretend to be another mod version in the handshake (--coop-fake-version 0.5.0), to check
    /// the refusal with two instances that load the same DLL.</summary>
    internal static string? FakeVersionArg;

    /// <summary>Phase 9, test only: the name sent in the Hello (--coop-name B), so several instances on one PC with the
    /// same Steam account can be told apart.</summary>
    internal static string? NameArg;

    /// <summary>Version sent and checked in the connection handshake.</summary>
    internal static string HandshakeVersion => FakeVersionArg ?? PluginVersion;

    private void ParseLaunchArgs()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--coop-host") HostArg = true;
            else if (args[i] == "--coop-connect" && i + 1 < args.Length) ConnectAddressArg = args[i + 1];
            else if (args[i] == "--coop-fake-version" && i + 1 < args.Length) FakeVersionArg = args[i + 1];
            else if (args[i] == "--coop-name" && i + 1 < args.Length) NameArg = args[i + 1];
            // Phase 8: network simulation per instance (two games on one PC share the .cfg).
            else if (args[i] == "--coop-sim-latency" && i + 1 < args.Length && int.TryParse(args[i + 1], out var lat)) _simLatency = lat;
            else if (args[i] == "--coop-sim-jitter" && i + 1 < args.Length && int.TryParse(args[i + 1], out var jit)) _simJitter = jit;
            else if (args[i] == "--coop-sim-loss" && i + 1 < args.Length && int.TryParse(args[i + 1], out var loss)) _simLoss = loss;
        }
        Ui.UiShot.ParseArgs(args);
        Ui.PartyShot.ParseArgs(args); // issue #7, debug
        if (HostArg || ConnectAddressArg != null || FakeVersionArg != null || NameArg != null)
            Log.LogInfo($"Launch args: host={HostArg} connect={ConnectAddressArg ?? "-"} fakeVersion={FakeVersionArg ?? "-"} name={NameArg ?? "-"}");
    }
}

[HarmonyLib.HarmonyPatch(typeof(GameplayUIManager), nameof(GameplayUIManager.Setup))]
public static class GameplayUIManagerSetupPatch
{
    [HarmonyLib.HarmonyPostfix]
    public static void Postfix(GameplayUIManager __instance, Entity playerEntity, Camera gameplayCamera)
    {
        if (playerEntity == null)
        {
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] GameplayUIManager.Setup fired with null playerEntity");
            return;
        }

        GameState.LocalPlayerEntity = playerEntity;
        CoopBehaviour.EnsureCreated();

        var go = playerEntity.gameObject;
        var pos = go.transform.position;
        string prefabRef = playerEntity.PlayerPrefabReference != null ? playerEntity.PlayerPrefabReference.name : "<none>";
        string characterGo = playerEntity._character != null ? playerEntity._character.gameObject.name : "<none>";

        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop] playerEntity found: name={playerEntity.EntityName} id={playerEntity.Id} " +
            $"gameObject={go.name} pos={pos} isPlayer={playerEntity.IsPlayer()} " +
            $"character={characterGo} playerPrefabRef={prefabRef} " +
            $"allEntities={EntityRegistry.AllEntities?.Count ?? -1} camera={gameplayCamera?.name}");
    }
}

/// <summary>Phase 6: the co-op object (network pump, session, UI) is created in the main menu, so the connection is
/// made there and survives runs. Awake: the menu scene is fully up by then (the Load()-time problem doesn't apply).</summary>
[HarmonyLib.HarmonyPatch(typeof(MenuManager), nameof(MenuManager.Awake))]
public static class MenuManagerAwakePatch
{
    [HarmonyLib.HarmonyPostfix]
    public static void Postfix()
    {
        try { CoopBehaviour.EnsureCreated(); }
        catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] MenuManagerAwakePatch: {ex}"); }
    }
}
