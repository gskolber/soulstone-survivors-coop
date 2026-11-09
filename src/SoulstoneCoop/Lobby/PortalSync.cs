using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop.Lobby;

/// <summary>What PortalTransition carries (and PortalRequest, minus Restart).</summary>
public enum PortalKind : byte
{
    Endless = 0,
    BossRush = 1,
    Restart = 2, // end screen's Restart (RunSync): the run's parameters follow the map
}

/// <summary>
/// The Endless and BossRush portals in co-op: the host decides, both go to the same next stage, each keeps its own
/// progression. The game's own portal code already carries the player's level/skills/items to the next scene
/// (SetEndlessData -> InitialEndlessData) on each machine; the only thing drawn is the Endless map
/// (MapCollectionSO.GetRandomMapExcluding), so the host sends it and the client, after running the same portal
/// code, reschedules LoadNextScene with the host's map. The client's own portal entry becomes a request to the
/// host. See docs/portais-investigacao.md.
/// </summary>
public static class PortalSync
{
    private const float FollowTimeout = 30f;   // waiting for EnterPortalObjective on this side (victory still in flight)
    private const float RequestInterval = 1.5f; // the portal trigger fires every frame while the player stands in it
    private const float NextStageDelay = 2f;   // OnEnterEndlessPortal / OnEnterBossRushPortal: LoadNextScene(map, 2f, false)

    /// <summary>A portal entry made by this class for the partner: let it through without asking/mirroring.</summary>
    internal static bool FromNetwork;

    // Host: a portal entry in progress (prefix -> postfix) and the map the game drew during it.
    private static bool _capturing;
    private static string? _drawnMap;

    // Waiting for this side's portal to open: the client following the host, or the host serving a client request.
    private static float _pendingTimer;
    private static PortalKind _pendingKind;
    private static string? _pendingMap;
    private static int _pendingRunId, _pendingStage; // client: the host's run and the stage its portal leads to
    private static bool _pendingIsRequest;

    private static float _lastRequestTime = -10f;
    private static bool _prevF5;

    // =====================================================================================
    // The game's portal entry (SurvivorsGameManager.OnPlayerEnterEndlessPortal / OnPlayerEnterBossRushPortal)
    // =====================================================================================

    /// <summary>Prefix. <paramref name="wasCompleted"/>: the portal objective was already done (or missing), so the
    /// game's ProcessOnPlayerEnterPortal won't let anyone in. False = skip the game's code.</summary>
    internal static bool OnPortalPrefix(PortalKind kind, out bool wasCompleted)
    {
        var obj = GameManagerUtil.GameData?.EnterPortalObjective;
        wasCompleted = obj == null || obj.Completed;
        if (FromNetwork) return true;
        if (!CoopSession.Connected)
        {
            // Alone as host: only note the next map (phase 8 AllowRejoin sends it to a partner joining later).
            if (NetworkManager.Role == NetRole.Host) { _capturing = true; _drawnMap = null; }
            return true;
        }

        if (NetworkManager.Role == NetRole.Client)
        {
            if (!wasCompleted) AskHost(kind);
            return false;
        }

        // Host: entering for real (own trigger, F5 or the client's request).
        if (!wasCompleted) Life.CoopLife.PrepareForStageTransition();
        _capturing = true;
        _drawnMap = null;
        return true;
    }

    internal static void OnPortalPostfix(PortalKind kind, bool wasCompleted)
    {
        bool capturing = _capturing;
        _capturing = false;
        string? map = kind == PortalKind.BossRush ? BossRushMap() : _drawnMap;
        _drawnMap = null;
        var obj = GameManagerUtil.GameData?.EnterPortalObjective;
        if (wasCompleted || obj == null || !obj.Completed) return; // the game didn't take anyone through

        if (NetworkManager.Role == NetRole.Host && capturing && !string.IsNullOrEmpty(map))
            RunSync.OnHostStageChanged(map!);
        if (!CoopSession.Connected) return;

        if (NetworkManager.Role == NetRole.Host && capturing)
        {
            if (string.IsNullOrEmpty(map))
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][portal] Host took the {kind} portal but the next map is unknown - partner not told.");
                CoopSession.Notice(Ui.Loc.Msg("nt.portal_cant_take", ("portal", Label(kind)), ("others", CoopSession.OthersLabel)), 6f);
            }
            else
            {
                // PortalTransition (Endless/BossRush): kind u8 | map str | run id i32 | stage i32 (the stage it leads to)
                RunSync.LocalMatch(out int runId, out int stage);
                var w = new NetDataWriter();
                w.Put((byte)PacketType.PortalTransition);
                w.Put((byte)kind);
                w.Put(map!);
                w.Put(runId);
                w.Put(stage);
                NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
                CoopSession.Notice(Ui.Loc.Msg("nt.portal_going", ("portal", Label(kind)), ("others", CoopSession.OthersLabel), ("map", map)), 5f);
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][portal] Host took the {kind} portal: next map {map}, partner told.");
            }
        }
        // Sent after PortalTransition (same ordered channel): the client knows why we're in Transition.
        PauseSync.ResetForRun();
        CoopSession.BeginStageTransition($"{kind} portal");
    }

    /// <summary>Postfix of MapCollectionSO.GetRandomMapExcluding (read only): the host's Endless draw.</summary>
    internal static void OnMapDrawn(MapDefinition? result)
    {
        if (!_capturing) return;
        _drawnMap = result?.MapId;
    }

    // =====================================================================================
    // The scene's portal owner: SurvivorsGameManager on the Survivors maps, SurvivorsBossRoomGameManager on the
    // BossRush map (BossRushPropertiesSO.BossRushMapId, no SurvivorsGameManager there). Both route their three
    // portals through the same GameManagerUtil code (ProcessOnPlayerEnterPortal + OnEnter*Portal).
    // =====================================================================================

    private static SurvivorsBossRoomGameManager? BossRoom()
    {
        var room = UnityEngine.Object.FindObjectOfType<SurvivorsBossRoomGameManager>();
        return room != null ? room : null;
    }

    internal static bool HasPortalOwner() =>
        GameManagerUtil.GameData?.SurvivorsGameManager != null || BossRoom() != null;

    /// <summary>Runs the scene manager's own portal entry (the UnityEvent target), so our patches apply.</summary>
    internal static bool EnterPortal(PortalKind kind)
    {
        var sgm = GameManagerUtil.GameData?.SurvivorsGameManager;
        if (sgm != null)
        {
            if (kind == PortalKind.BossRush) sgm.OnPlayerEnterBossRushPortal();
            else sgm.OnPlayerEnterEndlessPortal();
            return true;
        }
        var room = BossRoom();
        if (room == null) return false;
        if (kind == PortalKind.BossRush) room.OnPlayerEnterBossRushPortal();
        else room.OnPlayerEnterEndlessPortal();
        return true;
    }

    /// <summary>The Home portal of whichever manager owns this scene (Life.CoopLife mirrors it).</summary>
    internal static bool EnterHomePortal()
    {
        var sgm = GameManagerUtil.GameData?.SurvivorsGameManager;
        if (sgm != null) { sgm.OnPlayerEnterPortal(); return true; }
        var room = BossRoom();
        if (room == null)
        {
            // 1.8.0: the Mastery map's own Home portal.
            var mm = Modes.MasterySync.Manager;
            if (mm == null) return false;
            mm.OnPlayerEnterPortal();
            return true;
        }
        room.OnPlayerEnterPortal();
        return true;
    }

    private static string? BossRushMap()
    {
        try { return Global.BossRushProperties?.BossRushMapId; }
        catch { return null; }
    }

    /// <summary>"Challenge portal" / "Endless portal", as a message (rendered in the language of whoever reads it).</summary>
    private static string Label(PortalKind kind) => Ui.Loc.Msg(kind == PortalKind.BossRush ? "portal.challenge" : "portal.endless");

    // =====================================================================================
    // Client: asking the host / following it
    // =====================================================================================

    private static void AskHost(PortalKind kind)
    {
        if (CoopSession.StageTransition) return;
        if (Time.unscaledTime - _lastRequestTime < RequestInterval) return;
        _lastRequestTime = Time.unscaledTime;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.PortalRequest);
        w.Put((byte)kind);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        CoopSession.Notice(Ui.Loc.Msg("nt.portal_calling", ("portal", Label(kind))), 3f);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][portal] Client walked into the {kind} portal: asking the host.");
    }

    public static void OnPortalTransition(NetDataReader r)
    {
        var kind = (PortalKind)r.GetByte();
        string map = r.GetString();
        if (kind == PortalKind.Restart) { RunSync.OnHostRestart(r); return; }
        int runId = r.GetInt(), stage = r.GetInt();
        if (NetworkManager.Role != NetRole.Client) return;

        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][portal] Client: host took the {kind} portal to {map} (local {CoopSession.LocalPlace}).");
        if (!Life.CoopLife.InMatchNow || CoopSession.StageTransition || Life.CoopLife.LeftAlone)
        {
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][portal] Client: not in a match (or already changing scene) - ignored.");
            return;
        }
        // Committed from now on: no barrier in this scene (the portal may still need a few seconds to open here,
        // and LoadNextStageAfterDelay waits on scaled time), no ghost, and the host's next spawns wait.
        CoopSession.BeginStageTransition($"following the host's {kind} portal");
        _pendingKind = kind;
        _pendingMap = map;
        _pendingRunId = runId;
        _pendingStage = stage;
        _pendingIsRequest = false;
        _pendingTimer = FollowTimeout;
        TryPending();
    }

    private static void ClientFollow(PortalKind kind, string map, SimpleObjective obj)
    {
        string? missing = MissingMap(map);
        if (missing != null)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][portal] Client can't follow to {map}: {Ui.Loc.Plain(missing)}. Taking the Home portal.");
            RunSync.Refuse(Ui.Loc.Msg("nt.cant_follow_home", ("reason", missing)), missing);
            CoopSession.CancelStageTransition("can't follow");
            try { Life.CoopLife.FromNetwork = true; EnterHomePortal(); }
            finally { Life.CoopLife.FromNetwork = false; }
            return;
        }

        Life.CoopLife.PrepareForStageTransition();
        try
        {
            FromNetwork = true;
            EnterPortal(kind);
        }
        finally { FromNetwork = false; }

        if (!obj.Completed)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][portal] Client: the game's {kind} portal code didn't run.");
            CoopSession.CancelStageTransition("portal didn't run");
            return;
        }
        // The game just drew its own Endless map: LoadNextScene cancels that load and schedules the host's.
        GameManagerUtil.LoadNextScene(map, NextStageDelay, false);
        RunSync.OnClientFollowedStage(_pendingRunId, _pendingStage); // the next match is that stage of the host's run
        CoopSession.Notice(Ui.Loc.Msg("nt.portal_follow", ("portal", Label(kind))), 5f);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][portal] Client followed the host's {kind} portal: LoadNextScene({map}).");
    }

    private static string? MissingMap(string map)
    {
        if (string.IsNullOrEmpty(map)) return Ui.Loc.Msg("why.unknown_map");
        var maps = Global.MapCollection;
        if (maps != null && maps.GetMapById(map) == null) return Ui.Loc.Msg("why.no_map", ("map", map));
        if (!Application.CanStreamedLevelBeLoaded(map)) return Ui.Loc.Msg("why.no_scene", ("map", map));
        return null;
    }

    // =====================================================================================
    // Host: the client walked into a portal
    // =====================================================================================

    public static void OnPortalRequest(NetDataReader r)
    {
        var kind = (PortalKind)r.GetByte();
        if (NetworkManager.Role != NetRole.Host) return;
        if (kind != PortalKind.Endless && kind != PortalKind.BossRush) return;
        if (!Life.CoopLife.InMatchNow || CoopSession.StageTransition || Life.CoopLife.LeftAlone)
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][portal] Host: client asked for the {kind} portal - ignored (not in a match or already changing scene).");
            return;
        }
        if (_pendingTimer > 0f && _pendingIsRequest) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][portal] Host: client asked for the {kind} portal.");
        _pendingKind = kind;
        _pendingMap = null;
        _pendingIsRequest = true;
        _pendingTimer = FollowTimeout;
        TryPending();
    }

    private static void HostEnter(PortalKind kind, string why)
    {
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][portal] Host enters the {kind} portal ({why}).");
        // Not FromNetwork: the prefix/postfix above revive, capture the drawn map and tell the client.
        if (!EnterPortal(kind))
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][portal] Host: no scene manager with portals here.");
    }

    // =====================================================================================
    // Pending (this side's portal objective not created yet) and the debug hotkey
    // =====================================================================================

    private static void TryPending()
    {
        var data = GameManagerUtil.GameData;
        var obj = data != null ? data.EnterPortalObjective : null;
        if (obj == null) return; // the portal isn't open here yet: Tick retries

        var kind = _pendingKind;
        string? map = _pendingMap;
        bool isRequest = _pendingIsRequest;
        _pendingTimer = 0f;
        _pendingMap = null;

        if (obj.Completed)
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][portal] {kind}: a portal was already taken on this side - not following.");
            if (!isRequest)
            {
                CoopSession.CancelStageTransition("portal already taken here");
                RunSync.Refuse(Ui.Loc.Msg("nt.host_other_portal"), Ui.Loc.Msg("why.other_portal"));
            }
            return;
        }
        if (isRequest) HostEnter(kind, "a client walked in");
        else ClientFollow(kind, map ?? "", obj);
    }

    public static void Tick(float unscaledDeltaTime)
    {
        try
        {
            if (_pendingTimer > 0f)
            {
                _pendingTimer -= unscaledDeltaTime;
                if (!GameManagerUtil.HasGameManager || !CoopSession.Connected) GiveUp("run or connection gone");
                else if (_pendingTimer <= 0f) GiveUp("the portal never opened on this side");
                else TryPending();
            }
            HandleHotkey();
        }
        catch (Exception ex)
        {
            _pendingTimer = 0f;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][portal] Tick: {ex}");
        }
    }

    private static void GiveUp(string why)
    {
        _pendingTimer = 0f;
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][portal] Gave up the pending {_pendingKind} portal ({(_pendingIsRequest ? "client request" : "following the host")}): {why}.");
        if (!_pendingIsRequest)
        {
            CoopSession.CancelStageTransition(why);
            CoopSession.Notice(Ui.Loc.Msg("nt.portal_follow_failed"), 6f);
        }
        _pendingMap = null;
    }

    /// <summary>Debug, host: F5 enters the Endless portal, Shift+F5 the BossRush one, once the portals are open (F10
    /// forces the victory). Needed to test: early in a match the game doesn't show those portals at all
    /// (MinTimeForEndlessRush / MinTimeForBossRush) or the profile hasn't unlocked them.</summary>
    private static void HandleHotkey()
    {
        if (!Plugin.TestKeysOn) return; // 1.6.2
        var kb = Keyboard.current;
        if (kb == null) return;
        bool f5 = kb.f5Key.isPressed;
        bool pressed = f5 && !_prevF5;
        _prevF5 = f5;
        if (!pressed || NetworkManager.Role != NetRole.Host || Ui.CoopUi.IsTyping) return;

        var kind = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed ? PortalKind.BossRush : PortalKind.Endless;
        var data = GameManagerUtil.GameData;
        if (data == null || Global.SelectedGameMode != GameModes.Survivor || !HasPortalOwner())
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][portal] F5: only in a Survivors-mode match.");
            return;
        }
        if (data.EnterPortalObjective == null)
        {
            CoopSession.Notice(Ui.Loc.Msg("nt.portal_debug"), 4f);
            return;
        }
        HostEnter(kind, "F5");
    }

    // =====================================================================================
    // Scene teardown
    // =====================================================================================

    /// <summary>Client: the host's old scene is gone (its LobbyState went to Loading). Spawns and loot still around
    /// from it are stale; the host's next scene comes after this on the same ordered channel.</summary>
    internal static void OnHostLeftScene()
    {
        try
        {
            bool newMatchUp = CoopSession.LocalInRun && !CoopSession.StageTransition;
            Enemies.ClientPuppets.DropStaleFromHostScene(newMatchUp);
            Loot.LootSync.DropPendingFromOldScene();
            Enemies.ObjectiveSync.ResetForNewScene();
            Loot.ChestSync.OnHostLeftScene();
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][portal] Client: host left its scene - stale puppets/loot dropped (our next match up: {newMatchUp}).");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][portal] OnHostLeftScene: {ex}");
        }
    }

    /// <summary>GameManagerUtil.UnsetGameManager postfix (the scene's game manager's OnDestroy).</summary>
    internal static void OnLocalSceneGone()
    {
        _capturing = false;
        if (_pendingTimer > 0f) GiveUp("scene destroyed");
        CoopSession.OnGameManagerUnset();
        Enemies.ClientPuppets.OnLocalSceneGone();
        Enemies.EnemyCasts.ClearMatchCaches();
        Enemies.ObjectiveSync.ResetForNewScene();
        Summons.SummonSync.OnLocalSceneGone();
        Loot.ChestSync.OnLocalSceneGone();
    }
}

// ---------------------------------------------------------------------------------------------
// Patches. All targets: instance or static, no arguments (or List<string> / reference return): safe to patch.
// ---------------------------------------------------------------------------------------------

[HarmonyPatch(typeof(SurvivorsGameManager), nameof(SurvivorsGameManager.OnPlayerEnterEndlessPortal))]
public static class PortalEndlessPatch
{
    [HarmonyPrefix]
    public static bool Prefix(out bool __state)
    {
        __state = true;
        try { return PortalSync.OnPortalPrefix(PortalKind.Endless, out __state); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] PortalEndlessPatch.Prefix: {ex}");
            return !NetworkManager.HasPeer; // in co-op, a half-handled portal would split the two runs
        }
    }

    [HarmonyPostfix]
    public static void Postfix(bool __state)
    {
        try { PortalSync.OnPortalPostfix(PortalKind.Endless, __state); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] PortalEndlessPatch.Postfix: {ex}"); }
    }
}

[HarmonyPatch(typeof(SurvivorsGameManager), nameof(SurvivorsGameManager.OnPlayerEnterBossRushPortal))]
public static class PortalBossRushPatch
{
    [HarmonyPrefix]
    public static bool Prefix(out bool __state)
    {
        __state = true;
        try { return PortalSync.OnPortalPrefix(PortalKind.BossRush, out __state); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] PortalBossRushPatch.Prefix: {ex}");
            return !NetworkManager.HasPeer;
        }
    }

    [HarmonyPostfix]
    public static void Postfix(bool __state)
    {
        try { PortalSync.OnPortalPostfix(PortalKind.BossRush, __state); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] PortalBossRushPatch.Postfix: {ex}"); }
    }
}

/// <summary>The BossRush map's own manager has the same three portals (no SurvivorsGameManager in that scene).</summary>
[HarmonyPatch(typeof(SurvivorsBossRoomGameManager), nameof(SurvivorsBossRoomGameManager.OnPlayerEnterEndlessPortal))]
public static class PortalBossRoomEndlessPatch
{
    [HarmonyPrefix]
    public static bool Prefix(out bool __state)
    {
        __state = true;
        try { return PortalSync.OnPortalPrefix(PortalKind.Endless, out __state); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] PortalBossRoomEndlessPatch.Prefix: {ex}");
            return !NetworkManager.HasPeer;
        }
    }

    [HarmonyPostfix]
    public static void Postfix(bool __state)
    {
        try { PortalSync.OnPortalPostfix(PortalKind.Endless, __state); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] PortalBossRoomEndlessPatch.Postfix: {ex}"); }
    }
}

[HarmonyPatch(typeof(SurvivorsBossRoomGameManager), nameof(SurvivorsBossRoomGameManager.OnPlayerEnterBossRushPortal))]
public static class PortalBossRoomBossRushPatch
{
    [HarmonyPrefix]
    public static bool Prefix(out bool __state)
    {
        __state = true;
        try { return PortalSync.OnPortalPrefix(PortalKind.BossRush, out __state); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] PortalBossRoomBossRushPatch.Prefix: {ex}");
            return !NetworkManager.HasPeer;
        }
    }

    [HarmonyPostfix]
    public static void Postfix(bool __state)
    {
        try { PortalSync.OnPortalPostfix(PortalKind.BossRush, __state); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] PortalBossRoomBossRushPatch.Postfix: {ex}"); }
    }
}

/// <summary>The Endless portal's map draw (only read, while the host's portal entry is in progress).</summary>
[HarmonyPatch(typeof(MapCollectionSO), nameof(MapCollectionSO.GetRandomMapExcluding))]
public static class PortalMapDrawPatch
{
    [HarmonyPostfix]
    public static void Postfix(MapDefinition __result)
    {
        try { PortalSync.OnMapDrawn(__result); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] PortalMapDrawPatch: {ex}"); }
    }
}

/// <summary>Every game manager's OnDestroy (the scene is being unloaded).</summary>
[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.UnsetGameManager))]
public static class PortalSceneGonePatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try { PortalSync.OnLocalSceneGone(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] PortalSceneGonePatch: {ex}"); }
    }
}
