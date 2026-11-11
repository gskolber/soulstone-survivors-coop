using ECM2.Characters;
using HarmonyLib;
using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop.Modes;

/// <summary>Which Crusade portal a guest walked into (CrusadePortalRequest).</summary>
public enum CrusadePortal : byte
{
    Unstable = 0,   // the lobby's or the goblin cave's: opens the map selection
    Primordial = 1, // the lobby's: straight to the final boss map (needs the titan blood)
    NextMap = 2,    // a Crusade map's exit portal: opens the map selection
    GoblinCave = 3, // a Crusade map's portal to the goblin cave
}

/// <summary>
/// 1.9.0: Crusade (Titan Hunt) in co-op (docs/cruzada-investigacao.md): the lobby, the map changes, map tasks and the
/// boss room. Still the host's alone: the titan blood and exit portals after a map's boss, the goblin cave's chests and
/// the final boss.
/// How the mode moves between scenes (GameAssembly 1.5.3.2):
///   - Fight (MapSelectionCrusadeScreenPanel.OnFightPressed) writes Global.SelectedGameMode = Crusade,
///     CrusadeDifficultyTier and CrusadeDynamicLevel and loads the lobby (CrusadeConfigSO.MapId). The lobby's Start makes
///     a fresh CrusadeData from those when no InitialData came along: RunStart carries them (RunSync).
///   - Every later scene change is GameManagerUtil.LoadNextScene(map, delay, false), right after
///     GameManagerUtil.SetCrusadeEndlessData(PowerUpData, PlayerEntity) (the player's build for the next scene) and
///     IngameCurrencyPanelReferences.CopyInventoryData. Three places do that: the ShowMapSelectionUI coroutine of the
///     lobby / Crusade map / goblin cave (after CrusadeMapSelectionPanel.Open, once a map is picked:
///     RequestedSceneLoadForMapId), the lobby's primordial portal (fixed final boss map) and the Crusade map's goblin
///     cave portal.
/// In co-op the host decides: a guest walking into one of those portals asks the host (CrusadePortalRequest), whose own
/// portal code runs (the map selection opens on the host's screen). Whatever the host's game then loads is sent
/// (CrusadeTransition, from a LoadNextScene prefix) and the guest does what the game does before loading, with its own
/// player (SetCrusadeEndlessData, CopyInventoryData), then LoadNextScene(host's map). Each player keeps its own build,
/// titan blood and inventory, as in Endless. The guest's own map choice and LoadNextScene are refused while connected.
/// Packets:
///   CrusadeTransition (160, host -> clients, ReliableOrdered): map str | delay f32 | clearLoad u8 | viaPanel u8 |
///     run id i32 | stage i32 (the stage it leads to).
///   CrusadePortalRequest (161, guest -> host, ReliableOrdered): portal u8 (CrusadePortal).
///   CrusadeChoosing (162, host -> clients, ReliableOrdered): the host's map selection just opened (no payload).
///   CrusadeBarrier (163, host -> clients, ReliableOrdered): a CrusadePlayerBarrier went up / down on the host: id u16 |
///     on u8 | free u8 | name str | task address str | pos 3f | rot 4f | center 3f | radius f32.
///   TaskAreaCountdown (164, host -> clients, ReliableOrdered): seconds left u8 | reset u8.
///   CrusadeDoorState (165, host -> clients, ReliableOrdered): the boss door's holders active u8 (1 door colliders,
///     2 boss room obstacles, 4 arena obstacles), on change and every 3 s.
///   CrusadeDebugJump (166, host -> clients, ReliableOrdered): test key F8, go to pos 3f (the boss door).
///   CrusadeMapState (167, host -> clients, ReliableOrdered): the host's map progress, on change + every 2 s:
///     key fragments i32 | door trigger state u8 | door time f32 | room trigger state u8 | room time f32 |
///     kill objective id str | total i32 (-1: none yet) | current i32.
///   CrusadeTriggerPresence (168, guest -> host, ReliableOrdered): our player is on (1) / off (0) the boss door (0) or
///     the boss room entrance (1): which u8 | on u8.
///   CrusadeLobbyState (169, host -> clients, ReliableOrdered): the lobby: unstable portal taken u8 | runes u8 |
///     runes * (interacted u8 | timer f32 | active u8), on change (5 Hz) + every 2 s.
///   CrusadeRunePresence (170, guest -> host, ReliableOrdered): our player is on (1) / off (0) titan blood rune: index u8 | on u8.
/// The lobby: its titan blood runes (TitanBloodRune: a player standing on one fills it, RuneVfx / RuneActivateVfx /
/// sparkles; all filled -> the primordial portal opens) answered only to the local player, and only the portal of
/// whoever went through closed. Now the host's runes count every player on them (a guest's presence, as on the boss
/// door), the guest's runes show the host's (interaction, fill, activation, through their own Update and effects),
/// and the guest's unstable portal closes with the host's.
/// Map progress (third Crusade test, F8): the guest's objectives stayed at "Collect void fragments 0/15" (the fragments
/// are each player's own, and only the host's game runs the tasks), its door never played its opening and the guest
/// was left outside the arena when the host walked in. Now the guest's key fragments are the host's (its own UpdateAll
/// then shows the same objectives and turns its own door trigger on), the guest's door and room triggers take the
/// host's state instead of their own (so the guest's CrusadeDoorObject plays the same opening / closing, and its own
/// UpdateAll plays the boss presentation), a guest standing on them counts on the host (its proxy, as on chests),
/// and when the room closes whoever is on the wrong side of the door is moved in. The guest's UpdateAll would then
/// spawn real bosses of its own: before that, its KillLastBossObjective is filled with a copy of the host's (same id,
/// total, current), so the bosses it sees are the host's puppets and the objective completes with the host's.
/// Task areas: a Crusade map task with UsePlayerBarrier closes a ring of colliders (CrusadePlayerBarrier) around the
/// players when it starts. Solo that keeps the player in the fight; in co-op it shut out whoever was elsewhere (first
/// test: the host couldn't reach the task the guest had started). In co-op the ring is only a marker of the task's area:
/// its colliders are turned off (host, and the guest's copy), so each player can run their own task or join another's.
/// Instead, a task whose area is empty counts down ([Coop] TaskAreaEmptySeconds, 5 s): the count pauses while anyone
/// is back inside and goes on from where it stopped when the area empties again; at 0 the task goes back to its marker
/// (its enemies removed without loot, its objective failed, the ring down, the marker up again where it was).
/// Other CrusadePlayerBarriers (none seen outside task rings so far: the boss room closes with its door's own obstacles,
/// see Map progress) still hold: whoever is outside when one goes up is moved inside, and a guest without it in its
/// scene is kept inside by position.
/// </summary>
public static class CrusadeSync
{
    private const float RequestInterval = 1.5f; // the portal triggers fire every frame while the player stands in them

    private static float _lastRequest = -10f;
    private static bool _sentThisScene;      // host: this scene's LoadNextScene was already sent
    private static string? _allowedLoad;     // guest: the host's map, the one LoadNextScene we let through
    private static bool _prevF7, _prevF8;
    private static int _sent, _followed, _requests, _refused;

    internal static bool IsCrusade
    {
        get { try { return Global.SelectedGameMode == GameModes.Crusade; } catch { return false; } }
    }

    private static CrusadeLobbyGameManager? LobbyGm
    {
        get
        {
            try
            {
                if (!GameManagerUtil.HasGameManager) return null;
                var m = GameManagerUtil.GameData?.CrusadeLobbyManager;
                return m != null ? m : null;
            }
            catch { return null; }
        }
    }

    private static CrusadeGameManager? Map
    {
        get
        {
            try
            {
                if (!GameManagerUtil.HasGameManager) return null;
                var m = GameManagerUtil.GameData?.CrusadeManager;
                return m != null ? m : null;
            }
            catch { return null; }
        }
    }

    private static GoblinCaveGameManager? Cave
    {
        get
        {
            try
            {
                if (!GameManagerUtil.HasGameManager) return null;
                var m = GameManagerUtil.GameData?.GoblinCaveManager;
                return m != null ? m : null;
            }
            catch { return null; }
        }
    }

    /// <summary>The Crusade map's own manager (for the Home portal: PortalSync.EnterHomePortal).</summary>
    internal static CrusadeGameManager? MapManager => Map;

    private static CrusadeMapSelectionPanel? Panel
    {
        get
        {
            try
            {
                var ui = GameManagerUtil.GameplayUIManager;
                var p = ui != null ? ui.CrusadeMapSelection : null;
                return p != null ? p : null;
            }
            catch { return null; }
        }
    }

    private static bool GuestConnected => NetworkManager.Role == NetRole.Client && CoopSession.Connected;

    private static string Where() => LobbyGm != null ? "lobby" : Map != null ? "map" : Cave != null ? "goblin cave" : "?";

    // =====================================================================================
    // Guest: a portal of ours becomes a request to the host
    // =====================================================================================

    /// <summary>Prefix of the Crusade portals. False = skip the game's code (the guest asked the host instead).</summary>
    internal static bool OnPortalPrefix(CrusadePortal kind)
    {
        if (!GuestConnected) return true;
        if (CoopSession.StageTransition) return false;
        if (Time.unscaledTime - _lastRequest < RequestInterval) return false;
        _lastRequest = Time.unscaledTime;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadePortalRequest);
        w.Put((byte)kind);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _requests++;
        CoopSession.Notice(Ui.Loc.Msg(kind == CrusadePortal.Primordial || kind == CrusadePortal.GoblinCave ? "nt.portal_calling" : "nt.crusade_asked",
            ("portal", Ui.Loc.Msg("portal.crusade"))), 4f);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Guest walked into the {kind} portal ({Where()}): asking the host.");
        return false;
    }

    /// <summary>Host: a guest walked into a portal: the host's own portal code runs (the map selection opens here).</summary>
    public static void OnPortalRequest(NetDataReader r)
    {
        var kind = (CrusadePortal)r.GetByte();
        if (NetworkManager.Role != NetRole.Host || !IsCrusade) return;
        string name = CoopSession.NameOf(PacketRouter.From);
        if (!Life.CoopLife.InMatchNow || CoopSession.StageTransition || Life.CoopLife.LeftAlone)
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: {name} asked for the {kind} portal - ignored (not in a match or already changing scene).");
            return;
        }
        var panel = Panel;
        if (panel != null && panel.IsOpen) return; // already choosing
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: {name} walked into the {kind} portal ({Where()}).");
        bool ran = EnterPortal(kind);
        if (!ran) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Host: no {kind} portal in this scene ({Where()}).");
        else if (kind == CrusadePortal.Unstable || kind == CrusadePortal.NextMap)
            CoopSession.Notice(Ui.Loc.Msg("nt.crusade_pick", ("name", name)), 6f);
    }

    private static bool EnterPortal(CrusadePortal kind)
    {
        switch (kind)
        {
            case CrusadePortal.Unstable:
                var lobby = LobbyGm;
                if (lobby != null) { lobby.OnPlayerEnterUnstablePortal(); return true; }
                var cave = Cave;
                if (cave != null) { cave.OnPlayerEnterUnstablePortal(); return true; }
                return false;
            case CrusadePortal.Primordial:
                var l = LobbyGm;
                if (l == null) return false;
                l.OnPlayerEnterPrimordialPortal();
                return true;
            case CrusadePortal.NextMap:
                var m = Map;
                if (m == null) return false;
                m.OnPlayerEnterPortal();
                return true;
            case CrusadePortal.GoblinCave:
                var g = Map;
                if (g == null) return false;
                g.OnPlayerGoblinCavePortal();
                return true;
        }
        return false;
    }

    // =====================================================================================
    // The scene change itself (GameManagerUtil.LoadNextScene prefix)
    // =====================================================================================

    /// <summary>Prefix of GameManagerUtil.LoadNextScene. False = skip it (a guest's own Crusade scene change).</summary>
    internal static bool OnLoadNextScene(string mapId, float delay, bool clearLoad)
    {
        if (!IsCrusade || string.IsNullOrEmpty(mapId) || mapId == CoopSession.MenuScene) return true;
        var role = NetworkManager.Role;
        if (role == NetRole.Client)
        {
            if (!CoopSession.Connected) return true; // on our own now
            if (_allowedLoad != null && mapId == _allowedLoad) { _allowedLoad = null; return true; }
            _refused++;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Guest: our own LoadNextScene({mapId}) refused - the host decides where the Crusade goes.");
            CoopSession.Notice(Ui.Loc.Msg("nt.crusade_host_picks"), 4f);
            return false;
        }
        if (role != NetRole.Host || _sentThisScene) return true;
        _sentThisScene = true;

        var panel = Panel;
        bool viaPanel = false;
        try { viaPanel = panel != null && panel.RequestedSceneLoadForMapId == mapId; } catch { }
        if (CoopSession.Connected) Life.CoopLife.PrepareForStageTransition();
        RunSync.OnHostStageChanged(mapId);
        if (!CoopSession.Connected) return true;

        RunSync.LocalMatch(out int runId, out int stage);
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeTransition);
        w.Put(mapId);
        w.Put(delay);
        w.Put(clearLoad);
        w.Put(viaPanel);
        w.Put(runId);
        w.Put(stage);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _sent++;
        PauseSync.ResetForRun();
        CoopSession.BeginStageTransition("Crusade map change");
        CoopSession.Notice(Ui.Loc.Msg("nt.portal_going", ("portal", Ui.Loc.Msg("portal.crusade")), ("others", CoopSession.OthersLabel), ("map", mapId)), 5f);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: LoadNextScene({mapId}, {delay:F1}s) from the {Where()} (map selection: {viaPanel}) - partner told (run {runId}/{stage}).");
        return true;
    }

    /// <summary>Guest: the host's game is loading the next Crusade scene: do what the game does before it, with our player.</summary>
    public static void OnTransition(NetDataReader r)
    {
        string map = r.GetString();
        float delay = r.GetFloat();
        bool clearLoad = r.GetBool();
        bool viaPanel = r.GetBool();
        int runId = r.GetInt(), stage = r.GetInt();
        if (NetworkManager.Role != NetRole.Client) return;

        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Guest: host is loading {map} (map selection: {viaPanel}, {Where()}, local {CoopSession.LocalPlace}).");
        if (!Life.CoopLife.InMatchNow || CoopSession.StageTransition || Life.CoopLife.LeftAlone)
        {
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][crusade] Guest: not in a match (or already changing scene) - ignored.");
            return;
        }
        if (string.IsNullOrEmpty(map) || !Application.CanStreamedLevelBeLoaded(map))
        {
            string why = Ui.Loc.Msg("why.no_scene", ("map", map));
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Guest can't load {map}.");
            RunSync.Refuse(Ui.Loc.Msg("nt.cant_join_run", ("reason", why)), why);
            return;
        }

        CoopSession.BeginStageTransition("following the host's Crusade map change");
        RunSync.OnClientFollowedStage(runId, stage);
        Life.CoopLife.PrepareForStageTransition();
        try
        {
            var panel = Panel;
            if (panel != null && panel.IsOpen)
            {
                // Our own ShowMapSelectionUI is waiting on this panel: hand it the host's map, the game does the rest.
                _allowedLoad = map;
                panel.RequestedSceneLoadForMapId = map;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Guest: our map selection was open - it takes the host's {map}.");
            }
            else
            {
                // What the ShowMapSelectionUI coroutines / the primordial and goblin cave portals do before loading.
                GameManagerUtil.SetCrusadeEndlessData(GameManagerUtil.PowerUpData, GameManagerUtil.PlayerEntity);
                var ui = GameManagerUtil.GameplayUIManager;
                var gp = ui != null ? ui.GameplayPanel : null;
                var currency = gp != null ? gp.IngameCurrencyPanel : null;
                if (currency != null) currency.CopyInventoryData();
                if (viaPanel && ui != null)
                {
                    ui.ForceHideGameplayElements = true;
                    if (gp != null) gp.FadeAnimationGroupDesiredVisibility = 0f;
                }
                _allowedLoad = map;
                GameManagerUtil.LoadNextScene(map, delay, clearLoad);
            }
            _followed++;
            CoopSession.Notice(Ui.Loc.Msg("nt.portal_follow", ("portal", Ui.Loc.Msg("portal.crusade"))), 5f);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Guest followed the host: LoadNextScene({map}, {delay:F1}s) (run {runId}/{stage}).");
        }
        catch (Exception ex)
        {
            _allowedLoad = null;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Guest: following the host to {map}: {ex}");
            CoopSession.CancelStageTransition("couldn't follow");
            CoopSession.Notice(Ui.Loc.Msg("nt.portal_follow_failed"), 6f);
        }
    }

    // =====================================================================================
    // Map selection panel
    // =====================================================================================

    /// <summary>Postfix of CrusadeMapSelectionPanel.Open: the host tells the guests it is choosing.</summary>
    internal static void OnPanelOpened()
    {
        if (!IsCrusade || !CoopSession.Connected) return;
        if (NetworkManager.Role == NetRole.Host)
        {
            var w = new NetDataWriter();
            w.Put((byte)PacketType.CrusadeChoosing);
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Host: map selection open ({Where()}).");
        }
        else if (NetworkManager.Role == NetRole.Client)
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Guest: our own map selection opened ({Where()}) - the host's choice decides.");
        }
    }

    public static void OnChoosing(NetDataReader r)
    {
        if (NetworkManager.Role != NetRole.Client) return;
        CoopSession.Notice(Ui.Loc.Msg("nt.crusade_choosing"), 8f);
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][crusade] Guest: the host is choosing the next map.");
    }

    /// <summary>Prefix of the panel's buttons (a map, reroll, primordial teleport). False on a connected guest.</summary>
    internal static bool AllowPanelButton(string what)
    {
        if (!GuestConnected || !IsCrusade) return true;
        CoopSession.Notice(Ui.Loc.Msg("nt.crusade_host_picks"), 4f);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Guest: {what} on the map selection refused - the host chooses.");
        return false;
    }

    // =====================================================================================
    // Tick, scene teardown, debug
    // =====================================================================================

    public static void Tick(float unscaledDeltaTime)
    {
        try
        {
            HandleHotkey();
            LeashTick();
            DoorTick();
            MapStateTick();
            LobbyTick();
            if (NetworkManager.Role == NetRole.Host) AreaTick();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Tick: {ex}");
        }
    }

    /// <summary>Prefix of LevelUpPanelManager.OnUpdate. Second test: a level-up popup open (the co-op overlay keeps
    /// the game running) when the Crusade map change started threw ArgumentOutOfRangeException in OnUpdate every frame
    /// until the old scene was gone (List indexer: SetCrusadeEndlessData had already taken the run's state for the
    /// next map). Solo, the map selection pauses the game and no popup is open then. The old scene's popup is dropped
    /// with it; the next scene's is fresh (a pending level-up opens again there).</summary>
    internal static bool AllowLevelUpUpdate()
    {
        if (!CoopSession.StageTransition || !IsCrusade) return true;
        if (_levelUpSkipped++ == 0) Log("Level-up popup paused until the next map (map change under way).");
        return false;
    }

    private static int _levelUpSkipped;

    internal static void OnLocalSceneGone()
    {
        if (_levelUpSkipped > 0) Log($"Level-up popup updates skipped during the map change: {_levelUpSkipped}.");
        _levelUpSkipped = 0;
        BarrierIds.Clear();
        GuestBarriers.Clear();
        Leashes.Clear();
        Areas.Clear();
        Sweeps.Clear();
        _doorSent = -1;
        if (_stateRecv + _killCopies + _proxyDriven + _resets > 0)
            Log($"Scene gone: map states received {_stateRecv}, boss objective copies {_killCopies}, door frames driven by a guest {_proxyDriven}, task resets {_resets} ({_removedEnemies} enemies removed).");
        _stateRecv = _killCopies = _proxyDriven = _resets = _removedEnemies = 0;
        ResetMapState();
        ResetLobby();
        if (_sent + _followed + _requests + _refused > 0)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] Scene gone: sent {_sent}, followed {_followed}, portal requests {_requests}, own loads refused {_refused}.");
        _sentThisScene = false;
        _allowedLoad = null;
    }

    /// <summary>Test keys, host, Crusade: F7 opens the map selection in the lobby, a Crusade map or the goblin cave,
    /// without the portal (on a map the exit portal only opens after the map's bosses).</summary>
    private static void HandleHotkey()
    {
        if (!Plugin.TestKeysOn) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        bool f7 = kb.f7Key.isPressed, f8 = kb.f8Key.isPressed;
        bool p7 = f7 && !_prevF7, p8 = f8 && !_prevF8;
        _prevF7 = f7;
        _prevF8 = f8;
        if ((!p7 && !p8) || NetworkManager.Role != NetRole.Host || !IsCrusade || Ui.CoopUi.IsTyping) return;
        if (p7) DebugOpenMapSelection();
        if (p8) DebugBossFight();
    }

    /// <summary>Test keys, host, on a Crusade map: F8 skips to the boss room. The host gets the map's key fragments
    /// (InventoryData.CrusadeMapKeyFragments up to CrusadeConfigSO.GetRequiredKeyMapFragments: the map's own UpdateAll
    /// then opens the door objective and the door trigger), and everyone is moved to the door (CrusadeDebugJump). Standing
    /// on the door opens it; walking into the room closes the barrier and spawns the map's bosses, as solo. The door's
    /// open / closed state follows the host's on every machine (CrusadeDoorState).</summary>
    internal static void DebugBossFight()
    {
        var m = Map;
        if (m == null) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] F8: only on a Crusade map (here: {Where()})."); return; }
        try
        {
            var cfg = m._crusadeConfig != null ? m._crusadeConfig : Global.CrusadeConfig;
            int need = cfg != null ? cfg.GetRequiredKeyMapFragments() : 0;
            var player = GameManagerUtil.PlayerEntity;
            var inv = player != null && player._inventory != null ? player._inventory.InventoryData : null;
            int had = inv != null ? inv.CrusadeMapKeyFragments : -1;
            if (inv != null && need > 0 && inv.CrusadeMapKeyFragments < need) inv.CrusadeMapKeyFragments = need;
            var door = m.SceneReferences != null ? m.SceneReferences.ScenarioDoor : null;
            var trigger = door != null ? door.DoorTrigger : null;
            if (trigger == null)
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] F8: key fragments {had} -> {need}, but this map has no boss door (final boss map: {m.FinalBossMap}).");
                return;
            }
            var at = trigger.transform.position;
            TeleportLocal(at, "F8");
            if (CoopSession.Connected)
            {
                var w = new NetDataWriter();
                w.Put((byte)PacketType.CrusadeDebugJump);
                PutV3(w, at);
                NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            }
            Log($"F8: key fragments {had} -> {need}; everyone to the boss door at {at} (final boss map: {m.FinalBossMap}). Stand on the door to open it.");
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] F8: {ex}"); }
    }

    public static void OnDebugJump(NetDataReader r)
    {
        var at = GetV3(r);
        if (NetworkManager.Role != NetRole.Client) return;
        TeleportLocal(at + new Vector3(1.5f, 0f, 1.5f), "the host's F8");
    }

    private static void TeleportLocal(Vector3 to, string why)
    {
        var local = GameState.LocalPlayerEntity;
        var ch = local != null ? local._character : null;
        if (ch == null) return;
        ch.Teleport(to);
        ch.SetMovementMode(MovementMode.Walking);
        Log($"{why}: moved to {to}.");
    }

    // =====================================================================================
    // The boss room door: its open / closed state follows the host's (CrusadeDoorState)
    // =====================================================================================

    private static int _doorSent = -1;
    private static float _doorSentAt, _nextDoorCheck;

    private static int DoorFlags(CrusadeDoorObject door)
    {
        int f = 0;
        if (door.DoorColliderHolder != null && door.DoorColliderHolder.activeSelf) f |= 1;
        if (door.BossRoomObstaclesHolder != null && door.BossRoomObstaclesHolder.activeSelf) f |= 2;
        if (door.ArenaObstaclesHolder != null && door.ArenaObstaclesHolder.activeSelf) f |= 4;
        return f;
    }

    private static CrusadeDoorObject? Door()
    {
        var m = Map;
        var refs = m != null ? m.SceneReferences : null;
        var door = refs != null ? refs.ScenarioDoor : null;
        return door != null ? door : null;
    }

    /// <summary>Host, 4 Hz: the door's colliders and obstacles (DoorColliderHolder, BossRoomObstaclesHolder,
    /// ArenaObstaclesHolder), sent on change and every 3 s. Each guest's own door never opens by itself (its key
    /// fragments are its own), so without this a guest stays shut out of the room the host opened.</summary>
    private static void DoorTick()
    {
        if (NetworkManager.Role != NetRole.Host || !CoopSession.Connected || Time.unscaledTime < _nextDoorCheck) return;
        _nextDoorCheck = Time.unscaledTime + 0.25f;
        var door = Door();
        if (door == null) return;
        int f = DoorFlags(door);
        if (f == _doorSent && Time.unscaledTime - _doorSentAt < 3f) return;
        if (f != _doorSent) Log($"Host: boss door state {_doorSent} -> {f} (1 door collider, 2 room obstacles, 4 arena obstacles).");
        _doorSent = f;
        _doorSentAt = Time.unscaledTime;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeDoorState);
        w.Put((byte)f);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnDoorState(NetDataReader r)
    {
        int f = r.GetByte();
        if (NetworkManager.Role != NetRole.Client) return;
        var door = Door();
        if (door == null) return;
        int had = DoorFlags(door);
        if (had == f) return;
        if (door.DoorColliderHolder != null) door.DoorColliderHolder.SetActive((f & 1) != 0);
        if (door.BossRoomObstaclesHolder != null) door.BossRoomObstaclesHolder.SetActive((f & 2) != 0);
        if (door.ArenaObstaclesHolder != null) door.ArenaObstaclesHolder.SetActive((f & 4) != 0);
        Log($"Guest: boss door state {had} -> {f} (the host's).");
    }

    internal static bool DebugOpenMapSelection()
    {
        var panel = Panel;
        if (panel != null && panel.IsOpen) return true;
        var map = Map;
        if (map != null) { map.StartCoroutine(map.ShowMapSelectionUI()); Log("F7: map selection opened on the Crusade map."); return true; }
        var cave = Cave;
        if (cave != null) { cave.StartCoroutine(cave.ShowMapSelectionUI()); Log("F7: map selection opened in the goblin cave."); return true; }
        var lobby = LobbyGm;
        if (lobby != null) { lobby.StartCoroutine(lobby.ShowMapSelectionUI()); Log("F7: map selection opened in the lobby."); return true; }
        Plugin.StaticLog.LogWarning("[SoulstoneCoop][crusade] F7: no Crusade scene manager here.");
        return false;
    }

    private static void Log(string s) => Plugin.StaticLog.LogInfo($"[SoulstoneCoop][crusade] {s}");

    // =====================================================================================
    // Player barriers (task areas, boss room)
    // =====================================================================================

    private static readonly Dictionary<IntPtr, ushort> BarrierIds = new();          // host
    private static ushort _nextBarrierId;
    private static readonly Dictionary<ushort, CrusadePlayerBarrier> GuestBarriers = new(); // guest: the barrier we put up
    private static readonly Dictionary<ushort, (Vector3 center, float radius)> Leashes = new(); // guest: barriers on the host
    private static float _nextLeash;

    /// <summary>Postfix of CrusadePlayerBarrier.EnableBarrier / DissipateBarrier, host.</summary>
    internal static void OnHostBarrier(CrusadePlayerBarrier b, bool on)
    {
        if (b == null) return;
        if (NetworkManager.Role == NetRole.None)
        {
            if (on) SetColliders(b, true); // a pooled ring we freed in a co-op match holds again solo
            return;
        }
        if (NetworkManager.Role != NetRole.Host) return;
        if (!BarrierIds.TryGetValue(b.Pointer, out var id)) { id = ++_nextBarrierId; BarrierIds[b.Pointer] = id; }
        var task = Loot.ChestSync.TaskWithBarrier(b.Pointer);
        bool free = task != null;
        if (on) SetColliders(b, true);
        Measure(b, out var center, out float radius);
        if (on && free) SetColliders(b, false);
        string name = Enemies.EnemyPrefabKeys.StripClone(b.gameObject.name);
        string addr = Loot.ChestSync.AddressOf(task);
        Log($"Host: barrier #{id} '{name}' {(on ? "up" : "down")} at {center}, radius {radius:F1}" +
            $"{(task != null ? $", task {Enemies.EnemyPrefabKeys.StripClone(task.gameObject.name)} '{addr}' (area only, nobody held)" : "")}.");
        if (on && free) AddArea(task!, b, center, radius);
        else if (!on) Areas.Remove(b.Pointer);
        if (on && !free) PullInside(center, radius, "host");
        if (!CoopSession.Connected) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeBarrier);
        w.Put(id);
        w.Put(on);
        w.Put(free);
        w.Put(name);
        w.Put(addr);
        var t = b.transform;
        PutV3(w, t.position);
        var q = t.rotation;
        w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w);
        PutV3(w, center);
        w.Put(radius);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnBarrier(NetDataReader r)
    {
        ushort id = r.GetUShort();
        bool on = r.GetBool();
        bool free = r.GetBool();
        string name = r.GetString(), addr = r.GetString();
        var pos = GetV3(r);
        var rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
        var center = GetV3(r);
        float radius = r.GetFloat();
        if (NetworkManager.Role != NetRole.Client) return;
        try
        {
            if (on)
            {
                if (!free) PullInside(center, radius, "guest");
                var b = FindSceneBarrier(name, pos);
                string via = "scene";
                if (b == null) { b = BuildBarrier(name, addr, pos, rot); via = "built"; }
                if (b != null)
                {
                    GuestBarriers[id] = b;
                    if (!b._barriersOn) b.EnableBarrier();
                    SetColliders(b, !free);
                }
                if (!free && radius >= 3f) Leashes[id] = (center, radius);
                Log($"Guest: barrier #{id} '{name}' up at {center}, radius {radius:F1}{(free ? ", task area (nobody held)" : "")} " +
                    $"({(b != null ? via : free ? "not shown here" : "none here: kept inside by position")}).");
            }
            else
            {
                if (GuestBarriers.TryGetValue(id, out var b) && b != null && b._barriersOn) b.DissipateBarrier();
                GuestBarriers.Remove(id);
                Leashes.Remove(id);
                Log($"Guest: barrier #{id} '{name}' down.");
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Guest: barrier #{id} '{name}': {ex}"); }
    }

    /// <summary>The ring's middle and inner size, from its colliders (active after EnableBarrier).</summary>
    private static void Measure(CrusadePlayerBarrier b, out Vector3 center, out float radius)
    {
        center = b.transform.position;
        radius = 0f;
        try
        {
            var holder = b._colliderHolder;
            var all = holder != null ? holder.GetComponentsInChildren(Il2CppType.Of<Collider>(), true)
                                     : b.gameObject.GetComponentsInChildren(Il2CppType.Of<Collider>(), true);
            bool any = false;
            var bb = new Bounds();
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i]?.TryCast<Collider>();
                if (c == null) continue;
                var cb = c.bounds;
                if (cb.size.sqrMagnitude < 0.0001f) continue;
                if (!any) { bb = cb; any = true; } else bb.Encapsulate(cb);
            }
            if (!any) return;
            center = new Vector3(bb.center.x, b.transform.position.y, bb.center.z);
            radius = Mathf.Min(bb.extents.x, bb.extents.z);
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Barrier size: {ex.Message}"); }
    }

    /// <summary>The local player, if outside the ring, is moved just inside it (as the game does for whoever is on the task).</summary>
    private static void PullInside(Vector3 center, float radius, string who)
    {
        if (radius < 3f || Life.CoopLife.LocalDowned) return;
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        var p = local.transform.position;
        var d = new Vector3(p.x - center.x, 0f, p.z - center.z);
        float dist = d.magnitude;
        if (dist <= radius - 2f) return;
        float inner = Mathf.Max(0f, Mathf.Min(radius - 4f, radius * 0.8f));
        var to = dist > 0.01f ? center + d / dist * inner : center;
        to.y = p.y;
        var ch = local._character;
        if (ch == null) return;
        ch.Teleport(to);
        ch.SetMovementMode(MovementMode.Walking);
        Log($"{who}: was outside the barrier ({dist:F1} u from its middle, radius {radius:F1}): moved inside to {to}.");
    }

    /// <summary>Guest, a few times a second: a barrier we couldn't put up here still keeps us inside.</summary>
    private static void LeashTick()
    {
        if (Leashes.Count == 0 || NetworkManager.Role != NetRole.Client || Time.unscaledTime < _nextLeash) return;
        _nextLeash = Time.unscaledTime + 0.25f;
        foreach (var kv in Leashes)
        {
            if (GuestBarriers.TryGetValue(kv.Key, out var b) && b != null && b._barriersOn) continue; // the real one holds us
            PullInside(kv.Value.center, kv.Value.radius, "guest (leash)");
        }
    }

    private static CrusadePlayerBarrier? FindSceneBarrier(string name, Vector3 pos)
    {
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<CrusadePlayerBarrier>());
        for (int i = 0; i < all.Length; i++)
        {
            var b = all[i]?.TryCast<CrusadePlayerBarrier>();
            if (b == null || !b.gameObject.scene.IsValid()) continue;
            if (Enemies.EnemyPrefabKeys.StripClone(b.gameObject.name) != name) continue;
            if ((b.transform.position - pos).sqrMagnitude > 9f) continue;
            return b;
        }
        return null;
    }

    private static CrusadePlayerBarrier? BuildBarrier(string name, string addr, Vector3 pos, Quaternion rot)
    {
        CrusadePlayerBarrier? prefab = null;
        if (addr.Length > 0)
        {
            var task = ResourcesUtil.Load<MapTask>(addr);
            if (task != null)
                prefab = task.TryCast<MapKillBossesInAreaTask>()?.PlayerBarrierPrefab
                         ?? task.TryCast<MapKillElitesInAreaTask>()?.PlayerBarrierPrefab
                         ?? task.TryCast<MapSurviveForSecondsTask>()?.PlayerBarrierPrefab;
        }
        if (prefab == null)
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<CrusadePlayerBarrier>());
            for (int i = 0; i < all.Length && prefab == null; i++)
            {
                var b = all[i]?.TryCast<CrusadePlayerBarrier>();
                if (b != null && !b.gameObject.scene.IsValid() && Enemies.EnemyPrefabKeys.StripClone(b.gameObject.name) == name) prefab = b;
            }
        }
        if (prefab == null) return null;
        var go = UnityEngine.Object.Instantiate(prefab.gameObject, pos, rot);
        go.SetActive(true);
        return go.GetComponent<CrusadePlayerBarrier>();
    }

    private static void SetColliders(CrusadePlayerBarrier b, bool enabled)
    {
        try
        {
            var holder = b._colliderHolder;
            var all = holder != null ? holder.GetComponentsInChildren(Il2CppType.Of<Collider>(), true)
                                     : b.gameObject.GetComponentsInChildren(Il2CppType.Of<Collider>(), true);
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i]?.TryCast<Collider>();
                if (c != null && c.enabled != enabled) c.enabled = enabled;
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Barrier colliders: {ex.Message}"); }
    }

    // =====================================================================================
    // Task areas (host): an empty area counts down, then the task goes back to its marker
    // =====================================================================================

    private sealed class TaskArea
    {
        public MapTask Task = null!;
        public CrusadePlayerBarrier Barrier = null!;
        public string Name = "";
        public Vector3 Center;
        public float Radius, Left;
        public int Shown = -1;
        public bool Empty;
    }

    private static readonly Dictionary<IntPtr, TaskArea> Areas = new();   // by barrier
    private static readonly List<IntPtr> AreaScratch = new();
    private static readonly List<TaskArea> ToReset = new();
    private static readonly List<(EliminateSpecificEntitiesObjective obj, float until)> Sweeps = new();
    private static int _resets, _removedEnemies;

    private static void AddArea(MapTask task, CrusadePlayerBarrier b, Vector3 center, float radius)
    {
        if (radius < 1f) radius = 12f; // couldn't measure: a typical ring
        Areas[b.Pointer] = new TaskArea
        {
            Task = task,
            Barrier = b,
            Name = Enemies.EnemyPrefabKeys.StripClone(task.gameObject.name),
            Center = center,
            Radius = radius,
            Left = Mathf.Max(1f, Plugin.TaskAreaEmptySeconds.Value),
        };
    }

    private static void AreaTick()
    {
        SweepTick();
        if (Areas.Count == 0) return;
        float dt = Time.deltaTime; // scaled: a paused game doesn't count down
        AreaScratch.Clear();
        foreach (var kv in Areas)
        {
            var a = kv.Value;
            MapTaskCompletionStage stage = MapTaskCompletionStage.None;
            try { stage = a.Task != null ? a.Task.Stage : MapTaskCompletionStage.None; } catch { }
            if (stage != MapTaskCompletionStage.Triggered) { AreaScratch.Add(kv.Key); continue; }
            bool empty = !AnyPlayerInside(a.Center, a.Radius);
            if (!empty)
            {
                if (a.Empty) Log($"Host: task {a.Name}: a player is back in the area ({a.Left:F1} s left on its count).");
                a.Empty = false;
                a.Shown = -1;
                continue;
            }
            if (!a.Empty) Log($"Host: task {a.Name}: area empty, {a.Left:F1} s before it resets.");
            a.Empty = true;
            a.Left -= dt;
            int secs = Mathf.CeilToInt(Mathf.Max(0f, a.Left));
            if (a.Left <= 0f)
            {
                AreaScratch.Add(kv.Key);
                ToReset.Add(a); // after the loop: the reset takes the ring down, which edits Areas
                continue;
            }
            if (secs != a.Shown)
            {
                a.Shown = secs;
                SendCountdown(secs, false);
            }
        }
        foreach (var p in AreaScratch) Areas.Remove(p);
        if (ToReset.Count == 0) return;
        foreach (var a in ToReset)
        {
            ResetTask(a);
            SendCountdown(0, true);
        }
        ToReset.Clear();
    }

    private static bool AnyPlayerInside(Vector3 center, float radius)
    {
        float r2 = radius * radius;
        bool Inside(Vector3 p) { float dx = p.x - center.x, dz = p.z - center.z; return dx * dx + dz * dz <= r2; }
        var local = GameState.LocalPlayerEntity;
        if (local != null && Inside(local.transform.position)) return true;
        foreach (var r in CoopSession.Remotes)
            if (r.Ghost != null && Inside(r.Ghost.transform.position)) return true;
        return false;
    }

    private static void SendCountdown(int secs, bool reset)
    {
        ShowCountdown(secs, reset);
        if (!CoopSession.Connected) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.TaskAreaCountdown);
        w.Put((byte)Mathf.Clamp(secs, 0, 255));
        w.Put(reset);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static void ShowCountdown(int secs, bool reset)
    {
        if (reset) CoopSession.Notice(Ui.Loc.Msg("nt.task_reset"), 5f);
        else CoopSession.Notice(Ui.Loc.Msg("nt.task_empty", ("s", secs)), 1.5f);
    }

    public static void OnTaskAreaCountdown(NetDataReader r)
    {
        int secs = r.GetByte();
        bool reset = r.GetBool();
        if (NetworkManager.Role != NetRole.Client) return;
        ShowCountdown(secs, reset);
    }

    /// <summary>The task back to its marker: enemies out (no loot), objective failed, ring down, marker up again.</summary>
    private static void ResetTask(TaskArea a)
    {
        var t = a.Task;
        int removed = 0;
        try
        {
            EliminateSpecificEntitiesObjective? kill = null;
            var bosses = t.TryCast<MapKillBossesInAreaTask>();
            if (bosses != null) kill = bosses._eliminationObjective;
            var elites = t.TryCast<MapKillElitesInAreaTask>();
            if (elites != null) kill = elites._eliminationObjective;
            if (kill != null)
            {
                kill.Failed = true;
                removed += RemoveAll(kill.EntitiesToEliminate);
                // A boss still coming out of its statue (SpawnBossRoutine, on the JobRunner) joins this objective later.
                Sweeps.Add((kill, Time.unscaledTime + 30f));
            }
            var survive = t.TryCast<MapSurviveForSecondsTask>();
            if (survive != null && survive._timerObjective != null) survive._timerObjective.Failed = true;
            var against = t.TryCast<MapSurviveForSecondsAgainstEnemiesTask>();
            if (against != null) removed += RemoveAll(against._specialEntities);
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Host: resetting task {a.Name} (enemies/objective): {ex}"); }

        try { if (a.Barrier != null && a.Barrier._barriersOn) a.Barrier.DissipateBarrier(); }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Host: task {a.Name} ring: {ex.Message}"); }

        try
        {
            var icon = t.MinimapIconId;
            if (icon != null && icon.HasValue) MinimapManager.RequestRemoveIcon(icon.Value);
        }
        catch { /* StartTaskAtPosition asks for a new icon anyway */ }

        try
        {
            if (!Loot.ChestSync.TryGetTaskPose(t, out var pos, out var rot)) { pos = a.Center; rot = Quaternion.identity; }
            t.StartTaskAtPosition(pos, rot);
            _resets++;
            _removedEnemies += removed;
            Log($"Host: task {a.Name} reset: nobody in its area for {Plugin.TaskAreaEmptySeconds.Value:F0} s; {removed} enemie(s) removed, " +
                $"marker back at {pos} (stage {t.Stage}). Resets so far: {_resets}.");
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][crusade] Host: putting task {a.Name}'s marker back: {ex}"); }
    }

    private static int RemoveAll(Il2CppSystem.Collections.Generic.List<Entity>? list)
    {
        if (list == null) return 0;
        var copy = new List<Entity>();
        for (int i = 0; i < list.Count; i++) { var e = list[i]; if (e != null) copy.Add(e); }
        int n = 0;
        foreach (var e in copy) if (RemoveEnemy(e)) n++;
        return n;
    }

    /// <summary>An enemy taken out of the match without dying (no loot, no XP): back to its pool.</summary>
    private static bool RemoveEnemy(Entity e)
    {
        try
        {
            if (e == null || !e.gameObject.activeInHierarchy) return false;
            var h = e._health;
            if (h != null && h.HasBeenKilled) return false;
            var bossList = GameManagerUtil.BossEntities;
            if (bossList != null && bossList.Contains(e)) GameManagerUtil.DeregisterBossEntity(e);
            e.Recycle();
            return true;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Host: removing an enemy of a reset task: {ex.Message}");
            return false;
        }
    }

    private static float _nextSweep;

    private static void SweepTick()
    {
        if (Sweeps.Count == 0 || Time.unscaledTime < _nextSweep) return;
        _nextSweep = Time.unscaledTime + 0.5f;
        for (int i = Sweeps.Count - 1; i >= 0; i--)
        {
            var (obj, until) = Sweeps[i];
            if (Time.unscaledTime > until || obj == null) { Sweeps.RemoveAt(i); continue; }
            int n = 0;
            try { n = RemoveAll(obj.EntitiesToEliminate); } catch { Sweeps.RemoveAt(i); continue; }
            if (n > 0)
            {
                _removedEnemies += n;
                Log($"Host: {n} late enemie(s) of a reset task removed.");
            }
        }
    }

    // =====================================================================================
    // Map progress: key fragments, the boss door / room triggers, the kill objective (CrusadeMapState)
    // =====================================================================================

    private const byte TriggerDoor = 0, TriggerRoom = 1;

    // Both sides: this scene's door triggers (refreshed once a second).
    private static IntPtr _doorTrigPtr, _roomTrigPtr;
    private static float _nextTrigLookup;

    // Host: what was last sent, and the guests standing on each trigger.
    private static string _stateSent = "";
    private static float _stateSentAt, _nextStateCheck;
    private static readonly HashSet<byte> OnDoor = new(), OnRoom = new();
    private static int _lastRoomState;

    // Guest: the host's state.
    private static int _hFragments = -1, _hDoorState, _hRoomState, _hKillTotal = -1, _hKillCurrent;
    private static float _hDoorTime, _hRoomTime;
    private static string _hKillId = "";
    private static bool _onDoorSent, _onRoomSent;
    private static EliminateSpecificEntitiesObjective? _killCopy;
    private static int _stateRecv, _killCopies, _proxyDriven;

    private static void ResetMapState()
    {
        _doorTrigPtr = _roomTrigPtr = IntPtr.Zero;
        _nextTrigLookup = 0f;
        _stateSent = "";
        OnDoor.Clear(); OnRoom.Clear();
        _lastRoomState = 0;
        _hFragments = -1; _hDoorState = _hRoomState = 0; _hKillTotal = -1; _hKillCurrent = 0; _hKillId = "";
        _onDoorSent = _onRoomSent = false;
        _killCopy = null;
    }

    private static void RefreshTriggers()
    {
        if (Time.unscaledTime < _nextTrigLookup) return;
        _nextTrigLookup = Time.unscaledTime + 1f;
        var door = Door();
        _doorTrigPtr = door != null && door.DoorTrigger != null ? door.DoorTrigger.Pointer : IntPtr.Zero;
        _roomTrigPtr = door != null && door.BossRoomTrigger != null ? door.BossRoomTrigger.Pointer : IntPtr.Zero;
    }

    private static int LocalFragments()
    {
        var player = GameManagerUtil.PlayerEntity;
        var inv = player != null && player._inventory != null ? player._inventory.InventoryData : null;
        return inv != null ? inv.CrusadeMapKeyFragments : -1;
    }

    private static void MapStateTick()
    {
        if (NetworkManager.Role == NetRole.None || !IsCrusade) return;
        var m = Map;
        if (m == null) return;
        RefreshTriggers();
        if (NetworkManager.Role != NetRole.Host || Time.unscaledTime < _nextStateCheck) return;
        _nextStateCheck = Time.unscaledTime + 0.25f;
        var door = Door();
        int doorState = 0, roomState = 0;
        float doorTime = 0f, roomTime = 0f;
        if (door != null)
        {
            if (door.DoorTrigger != null) { doorState = (int)door.DoorTrigger.State; doorTime = door.DoorTrigger.TimeInteracting; }
            if (door.BossRoomTrigger != null) { roomState = (int)door.BossRoomTrigger.State; roomTime = door.BossRoomTrigger.TimeInteracting; }
        }
        if (roomState == 2 && _lastRoomState != 2) PullIntoArena("host");
        _lastRoomState = roomState;
        if (!CoopSession.Connected) return;
        int fragments = LocalFragments();
        var kill = m.KillLastBossObjective;
        string killId = kill != null ? kill.Id ?? "" : "";
        int killTotal = kill != null ? kill.Total : -1, killCurrent = kill != null ? kill.Current : 0;
        string key = $"{fragments}|{doorState}|{roomState}|{killId}|{killTotal}|{killCurrent}";
        bool changed = key != _stateSent;
        bool progressing = (doorState == 1) || (roomState == 1);
        if (!changed && !progressing && Time.unscaledTime - _stateSentAt < 2f) return;
        if (changed) Log($"Host: map state {key} (fragments|door|room|kill id|total|current).");
        _stateSent = key;
        _stateSentAt = Time.unscaledTime;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeMapState);
        w.Put(fragments);
        w.Put((byte)doorState);
        w.Put(doorTime);
        w.Put((byte)roomState);
        w.Put(roomTime);
        w.Put(killId);
        w.Put(killTotal);
        w.Put(killCurrent);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnMapState(NetDataReader r)
    {
        int fragments = r.GetInt();
        int doorState = r.GetByte();
        float doorTime = r.GetFloat();
        int roomState = r.GetByte();
        float roomTime = r.GetFloat();
        string killId = r.GetString();
        int killTotal = r.GetInt(), killCurrent = r.GetInt();
        if (NetworkManager.Role != NetRole.Client) return;
        _stateRecv++;
        if (fragments != _hFragments || doorState != _hDoorState || roomState != _hRoomState || killTotal != _hKillTotal)
            Log($"Guest: host's map state fragments {fragments} door {doorState} room {roomState} kill '{killId}' {killCurrent}/{killTotal}.");
        bool roomClosing = roomState == 2 && _hRoomState != 2;
        _hFragments = fragments; _hDoorState = doorState; _hDoorTime = doorTime;
        _hRoomState = roomState; _hRoomTime = roomTime;
        _hKillId = killId; _hKillTotal = killTotal; _hKillCurrent = killCurrent;
        if (roomClosing) PullIntoArena("guest");
        ApplyKillCopy();
    }

    /// <summary>The room is closing: a player on the wrong side of the door (farther from the room than the door is)
    /// goes in, next to the room's entrance trigger.</summary>
    private static void PullIntoArena(string who)
    {
        try
        {
            var door = Door();
            if (door == null || door.DoorTrigger == null || door.BossRoomTrigger == null) return;
            var local = GameState.LocalPlayerEntity;
            if (local == null) return;
            Vector3 doorPos = door.DoorTrigger.transform.position, roomPos = door.BossRoomTrigger.transform.position;
            var inward = roomPos - doorPos;
            inward.y = 0f;
            if (inward.sqrMagnitude < 0.01f) return;
            var p = local.transform.position;
            var rel = p - doorPos;
            rel.y = 0f;
            bool inside = Vector3.Dot(rel, inward) > 0f;
            if (inside) { Log($"{who}: the boss room closed with us inside."); return; }
            var to = roomPos + inward.normalized * 3f;
            to.y = p.y;
            TeleportLocal(to, $"{who}: the boss room closed with us outside");
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] {who}: into the boss room: {ex.Message}"); }
    }

    /// <summary>Prefix of InteractableOneTimeTrigger.OnUpdate (every trigger: chests, tasks, the door). Only the boss
    /// door's two triggers are touched. Guest: the trigger takes the host's state and time instead of running (false =
    /// skip the game's OnUpdate), and our player standing on it is reported to the host. Host: a guest standing on it
    /// drives it through its proxy, as on chests.</summary>
    internal static bool DoorTriggerPrefix(InteractableOneTimeTrigger trig)
    {
        if (NetworkManager.Role == NetRole.None || trig == null) return true;
        var ptr = trig.Pointer;
        if (ptr == IntPtr.Zero || (ptr != _doorTrigPtr && ptr != _roomTrigPtr)) return true;
        byte which = ptr == _doorTrigPtr ? TriggerDoor : TriggerRoom;
        if (NetworkManager.Role == NetRole.Client)
        {
            if (!CoopSession.Connected) return true;
            var local = GameState.LocalPlayerEntity;
            var cur = trig.InteractingEntity;
            bool onIt = cur != null && local != null && cur.Pointer == local.Pointer;
            ref bool sent = ref (which == TriggerDoor ? ref _onDoorSent : ref _onRoomSent);
            if (onIt != sent)
            {
                sent = onIt;
                var w = new NetDataWriter();
                w.Put((byte)PacketType.CrusadeTriggerPresence);
                w.Put(which);
                w.Put(onIt);
                NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            }
            int state = which == TriggerDoor ? _hDoorState : _hRoomState;
            float time = which == TriggerDoor ? _hDoorTime : _hRoomTime;
            trig.State = (InteractableTriggerState)state;
            trig.TimeInteracting = state == 2 ? trig.InteractionTime : Mathf.Min(time, trig.InteractionTime * 0.97f);
            // The door fires void fragments from InteractingEntity while it's being opened: none from an empty one.
            if (which == TriggerDoor && cur == null)
            {
                var door = Door();
                if (door != null) door._lastTimeFiredProjectile = Time.time;
            }
            return false;
        }
        if (NetworkManager.Role != NetRole.Host) return true;
        var set = which == TriggerDoor ? OnDoor : OnRoom;
        var hostCur = trig.InteractingEntity;
        var me = GameState.LocalPlayerEntity;
        bool hostOn = hostCur != null && me != null && hostCur.Pointer == me.Pointer;
        if (hostOn || trig.State == InteractableTriggerState.Triggered) return true;
        Entity? proxy = null;
        foreach (byte slot in set)
        {
            if (!Enemies.RemoteProxy.IsAlive(slot)) continue;
            proxy = Enemies.RemoteProxy.ProxyOf(slot);
            if (proxy != null) break;
        }
        if (proxy != null)
        {
            if (hostCur == null || hostCur.Pointer != proxy.Pointer)
            {
                trig.InteractingEntity = proxy;
                trig.InteractingEntityTargetTransform = proxy.transform;
            }
            _proxyDriven++;
        }
        else if (!ReferenceEquals(hostCur, null) && (hostCur == null || Enemies.RemoteProxy.IsProxy(hostCur)))
        {
            trig.InteractingEntity = null;
            trig.InteractingEntityTargetTransform = null;
        }
        return true;
    }

    public static void OnTriggerPresence(NetDataReader r)
    {
        byte which = r.GetByte();
        bool on = r.GetBool();
        if (NetworkManager.Role != NetRole.Host) return;
        byte from = PacketRouter.From;
        var set = which == TriggerDoor ? OnDoor : OnRoom;
        if (on) set.Add(from); else set.Remove(from);
        Log($"Host: {CoopSession.NameOf(from)} is {(on ? "on" : "off")} the boss {(which == TriggerDoor ? "door" : "room entrance")}.");
    }

    /// <summary>Prefix of CrusadeGameManager.UpdateAll, guest: the host's key fragments, and the host's kill objective in
    /// place of the bosses its own UpdateAll would spawn once the presentation is over.</summary>
    internal static void GuestBeforeMapUpdate()
    {
        if (NetworkManager.Role != NetRole.Client || !CoopSession.Connected || !IsCrusade) return;
        var m = Map;
        if (m == null) return;
        if (_hFragments >= 0)
        {
            var player = GameManagerUtil.PlayerEntity;
            var inv = player != null && player._inventory != null ? player._inventory.InventoryData : null;
            if (inv != null && inv.CrusadeMapKeyFragments != _hFragments) inv.CrusadeMapKeyFragments = _hFragments;
        }
        if (m.FinalBossMap) return; // the final boss is step 4
        if (m.KillLastBossObjective == null && m.EnterBossRoomObjective != null && m._hasPlayedPresentation)
        {
            var copy = new EliminateSpecificEntitiesObjective(_hKillId, Mathf.Max(1, _hKillTotal));
            m.KillLastBossObjective = copy;
            _killCopy = copy;
            _killCopies++;
            try
            {
                var ui = GameManagerUtil.GameplayUIManager;
                var panel = ui != null && ui.GameplayPanel != null ? ui.GameplayPanel.ObjectivesPanel : null;
                if (panel != null) panel.TrackObjective(copy);
            }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][crusade] Guest: showing the boss objective: {ex.Message}"); }
            Log($"Guest: boss room presentation over - the host's boss objective '{_hKillId}' ({_hKillCurrent}/{_hKillTotal}) instead of our own bosses.");
        }
        ApplyKillCopy();
    }

    private static void ApplyKillCopy()
    {
        var copy = _killCopy;
        if (copy == null) return;
        try
        {
            if (_hKillId.Length > 0 && copy.Id != _hKillId) copy.Id = _hKillId;
            if (_hKillTotal > 0) copy.Total = _hKillTotal;
            copy.Current = Mathf.Min(_hKillCurrent, copy.Total);
        }
        catch { _killCopy = null; }
    }

    // =====================================================================================
    // The lobby: titan blood runes and the unstable portal (CrusadeLobbyState)
    // =====================================================================================

    private static readonly HashSet<IntPtr> LocalOnRune = new();                  // both: runes our own player stands on
    private static readonly Dictionary<int, HashSet<byte>> GuestsOnRune = new();  // host: rune index -> guests on it
    private static string _lobbySent = "";
    private static float _lobbySentAt, _nextLobbyCheck;
    // Guest: the host's lobby.
    private static bool _hPortalTaken, _portalClosedHere, _hLobbyKnown;
    private static bool[] _hRuneOn = Array.Empty<bool>(), _hRuneActive = Array.Empty<bool>();
    private static float[] _hRuneTimer = Array.Empty<float>();

    private static void ResetLobby()
    {
        LocalOnRune.Clear();
        GuestsOnRune.Clear();
        _lobbySent = "";
        _hPortalTaken = _portalClosedHere = _hLobbyKnown = false;
        _hRuneOn = Array.Empty<bool>(); _hRuneActive = Array.Empty<bool>(); _hRuneTimer = Array.Empty<float>();
    }

    private static int RuneIndex(TitanBloodRune rune)
    {
        var lobby = LobbyGm;
        var runes = lobby != null ? lobby.TitanBloodRunes : null;
        if (runes == null) return -1;
        for (int i = 0; i < runes.Length; i++)
            if (runes[i] != null && runes[i].Pointer == rune.Pointer) return i;
        return -1;
    }

    /// <summary>Postfix of TitanBloodRune.OnTriggerEnter / OnTriggerExit: our own player on / off it (a guest tells the host).</summary>
    internal static void OnRuneContact(TitanBloodRune rune, Collider other, bool enter)
    {
        if (NetworkManager.Role == NetRole.None || rune == null || other == null) return;
        var local = GameState.LocalPlayerEntity;
        var rb = other.attachedRigidbody;
        var e = rb != null ? rb.GetComponent<Entity>() : null;
        if (local == null || e == null || e.Pointer != local.Pointer) return;
        bool changed = enter ? LocalOnRune.Add(rune.Pointer) : LocalOnRune.Remove(rune.Pointer);
        if (!changed || NetworkManager.Role != NetRole.Client || !CoopSession.Connected) return;
        int index = RuneIndex(rune);
        if (index < 0) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeRunePresence);
        w.Put((byte)index);
        w.Put(enter);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnRunePresence(NetDataReader r)
    {
        int index = r.GetByte();
        bool on = r.GetBool();
        if (NetworkManager.Role != NetRole.Host) return;
        if (!GuestsOnRune.TryGetValue(index, out var set)) GuestsOnRune[index] = set = new HashSet<byte>();
        if (on) set.Add(PacketRouter.From); else set.Remove(PacketRouter.From);
        Log($"Host: {CoopSession.NameOf(PacketRouter.From)} is {(on ? "on" : "off")} titan blood rune {index}.");
    }

    /// <summary>Prefix of TitanBloodRune.Update. Host: the rune is being interacted with while anyone stands on it.
    /// Guest: it shows the host's rune (its own Update then plays the same fill and activation).</summary>
    internal static void BeforeRuneUpdate(TitanBloodRune rune)
    {
        if (NetworkManager.Role == NetRole.None || rune == null || !CoopSession.Connected) return;
        if (rune.RuneActive) return;
        if (NetworkManager.Role == NetRole.Host)
        {
            int index = RuneIndex(rune);
            bool guests = index >= 0 && GuestsOnRune.TryGetValue(index, out var set) && set.Count > 0;
            rune.IsBeingInteracted = LocalOnRune.Contains(rune.Pointer) || guests;
            return;
        }
        if (!_hLobbyKnown) return;
        int i = RuneIndex(rune);
        if (i < 0 || i >= _hRuneOn.Length) return;
        rune.IsBeingInteracted = _hRuneOn[i] || _hRuneActive[i];
        // Activated on the host: let our Update fill the last bit itself, so the activation effects play here too.
        rune._currentTimer = _hRuneActive[i] ? rune.TimeToEnter : Mathf.Min(_hRuneTimer[i], rune.TimeToEnter - 0.05f);
    }

    private static void LobbyTick()
    {
        var lobby = LobbyGm;
        if (lobby == null || NetworkManager.Role == NetRole.None) return;
        if (NetworkManager.Role == NetRole.Client)
        {
            // The host went through its unstable portal: ours closes too (it never opens again in this lobby).
            if (_hPortalTaken && !_portalClosedHere && lobby.UnstablePortal != null)
            {
                _portalClosedHere = true;
                lobby.UnstablePortal.ClosePortal(false);
                Log("Guest: the host went through the unstable portal - ours closes.");
            }
            return;
        }
        if (!CoopSession.Connected || Time.unscaledTime < _nextLobbyCheck) return;
        _nextLobbyCheck = Time.unscaledTime + 0.2f;
        bool taken = false;
        try { var obj = GameManagerUtil.GameData?.EnterPortalObjective; taken = obj != null && obj.Completed; } catch { }
        var runes = lobby.TitanBloodRunes;
        int n = runes != null ? Math.Min(runes.Length, 16) : 0;
        var sb = new System.Text.StringBuilder(taken ? "T" : "o");
        bool anyFilling = false;
        for (int i = 0; i < n; i++)
        {
            var rune = runes![i];
            bool on = rune != null && rune.IsBeingInteracted, active = rune != null && rune.RuneActive;
            float timer = rune != null ? rune._currentTimer : 0f;
            anyFilling |= on && !active;
            sb.Append(on ? '1' : '0').Append(active ? 'A' : '-').Append(Mathf.RoundToInt(timer * 10f)).Append('|');
        }
        string key = sb.ToString();
        if (key == _lobbySent && Time.unscaledTime - _lobbySentAt < 2f) return;
        if (key != _lobbySent && !anyFilling) Log($"Host: lobby state {key} (portal T taken; per rune: on, A active, fill x10).");
        _lobbySent = key;
        _lobbySentAt = Time.unscaledTime;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CrusadeLobbyState);
        w.Put(taken);
        w.Put((byte)n);
        for (int i = 0; i < n; i++)
        {
            var rune = runes![i];
            w.Put(rune != null && rune.IsBeingInteracted);
            w.Put(rune != null ? rune._currentTimer : 0f);
            w.Put(rune != null && rune.RuneActive);
        }
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnLobbyState(NetDataReader r)
    {
        bool taken = r.GetBool();
        int n = r.GetByte();
        var on = new bool[n]; var timer = new float[n]; var active = new bool[n];
        for (int i = 0; i < n; i++) { on[i] = r.GetBool(); timer[i] = r.GetFloat(); active[i] = r.GetBool(); }
        if (NetworkManager.Role != NetRole.Client) return;
        for (int i = 0; i < n; i++)
            if (active[i] && (i >= _hRuneActive.Length || !_hRuneActive[i])) Log($"Guest: the host's titan blood rune {i} is lit.");
        _hPortalTaken = taken;
        _hRuneOn = on; _hRuneTimer = timer; _hRuneActive = active;
        _hLobbyKnown = true;
    }

    /// <summary>A Crusade map's boss presentation is playing (CrusadeGameManager._isPlayingPresentation: set when the
    /// boss room's presentation starts, cleared when gameplay unlocks or the timeline is skipped).</summary>
    internal static bool PresentationRunning
    {
        get
        {
            try { var m = Map; return m != null && m._isPlayingPresentation; }
            catch { return false; }
        }
    }

    private static void PutV3(NetDataWriter w, Vector3 v) { w.Put(v.x); w.Put(v.y); w.Put(v.z); }
    private static Vector3 GetV3(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat());
}

[HarmonyPatch(typeof(CrusadePlayerBarrier), nameof(CrusadePlayerBarrier.EnableBarrier))]
public static class CrusadeBarrierEnablePatch
{
    [HarmonyPostfix]
    public static void Postfix(CrusadePlayerBarrier __instance)
    {
        try { CrusadeSync.OnHostBarrier(__instance, true); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeBarrierEnablePatch: {ex}"); }
    }
}

[HarmonyPatch(typeof(CrusadePlayerBarrier), nameof(CrusadePlayerBarrier.DissipateBarrier))]
public static class CrusadeBarrierDissipatePatch
{
    [HarmonyPostfix]
    public static void Postfix(CrusadePlayerBarrier __instance)
    {
        try { CrusadeSync.OnHostBarrier(__instance, false); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeBarrierDissipatePatch: {ex}"); }
    }
}

[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.LoadNextScene))]
public static class CrusadeLoadNextScenePatch
{
    [HarmonyPrefix]
    public static bool Prefix(string mapId, float delay, bool clearLoad)
    {
        try { return CrusadeSync.OnLoadNextScene(mapId, delay, clearLoad); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeLoadNextScenePatch: {ex}");
            return true;
        }
    }
}

[HarmonyPatch(typeof(CrusadeLobbyGameManager), nameof(CrusadeLobbyGameManager.OnPlayerEnterUnstablePortal))]
public static class CrusadeLobbyUnstablePortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return CrusadeSync.OnPortalPrefix(CrusadePortal.Unstable); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeLobbyUnstablePortalPatch: {ex}"); return true; }
    }
}

[HarmonyPatch(typeof(CrusadeLobbyGameManager), nameof(CrusadeLobbyGameManager.OnPlayerEnterPrimordialPortal))]
public static class CrusadeLobbyPrimordialPortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return CrusadeSync.OnPortalPrefix(CrusadePortal.Primordial); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeLobbyPrimordialPortalPatch: {ex}"); return true; }
    }
}

[HarmonyPatch(typeof(GoblinCaveGameManager), nameof(GoblinCaveGameManager.OnPlayerEnterUnstablePortal))]
public static class CrusadeCaveUnstablePortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return CrusadeSync.OnPortalPrefix(CrusadePortal.Unstable); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeCaveUnstablePortalPatch: {ex}"); return true; }
    }
}

[HarmonyPatch(typeof(CrusadeGameManager), nameof(CrusadeGameManager.OnPlayerEnterPortal))]
public static class CrusadeNextMapPortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return CrusadeSync.OnPortalPrefix(CrusadePortal.NextMap); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeNextMapPortalPatch: {ex}"); return true; }
    }
}

[HarmonyPatch(typeof(CrusadeGameManager), nameof(CrusadeGameManager.OnPlayerGoblinCavePortal))]
public static class CrusadeGoblinCavePortalPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return CrusadeSync.OnPortalPrefix(CrusadePortal.GoblinCave); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeGoblinCavePortalPatch: {ex}"); return true; }
    }
}

/// <summary>Home portal of a Crusade map: whoever enters takes the others along (as on the other maps).</summary>
[HarmonyPatch(typeof(CrusadeGameManager), nameof(CrusadeGameManager.OnPlayerEnterHomePortal))]
public static class CrusadeHomePortalPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try { if (!Life.CoopLife.FromNetwork) Life.CoopLife.OnLocalPortalEntered(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeHomePortalPatch: {ex}"); }
    }
}

[HarmonyPatch(typeof(CrusadeMapSelectionPanel), nameof(CrusadeMapSelectionPanel.Open))]
public static class CrusadePanelOpenPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try { CrusadeSync.OnPanelOpened(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadePanelOpenPatch: {ex}"); }
    }
}

[HarmonyPatch(typeof(CrusadeMapSelectionItem), nameof(CrusadeMapSelectionItem.OnPressed))]
public static class CrusadePanelItemPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return CrusadeSync.AllowPanelButton("a map"); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadePanelItemPatch: {ex}"); return true; }
    }
}

[HarmonyPatch(typeof(CrusadeMapSelectionPanel), nameof(CrusadeMapSelectionPanel.OnRerollPressed))]
public static class CrusadePanelRerollPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return CrusadeSync.AllowPanelButton("reroll"); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadePanelRerollPatch: {ex}"); return true; }
    }
}

[HarmonyPatch(typeof(CrusadeMapSelectionPanel), nameof(CrusadeMapSelectionPanel.OnPressPrimordialTeleport))]
public static class CrusadePanelPrimordialPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return CrusadeSync.AllowPanelButton("primordial teleport"); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadePanelPrimordialPatch: {ex}"); return true; }
    }
}

[HarmonyPatch(typeof(InteractableOneTimeTrigger), nameof(InteractableOneTimeTrigger.OnUpdate))]
public static class CrusadeDoorTriggerPatch
{
    [HarmonyPrefix]
    public static bool Prefix(InteractableOneTimeTrigger __instance)
    {
        try { return CrusadeSync.DoorTriggerPrefix(__instance); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeDoorTriggerPatch: {ex.Message}"); return true; }
    }
}

[HarmonyPatch(typeof(CrusadeGameManager), nameof(CrusadeGameManager.UpdateAll))]
public static class CrusadeGuestMapUpdatePatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try { CrusadeSync.GuestBeforeMapUpdate(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeGuestMapUpdatePatch: {ex.Message}"); }
    }
}

/// <summary>The level-up popup: frozen during a Crusade map change (CrusadeSync.AllowLevelUpUpdate), and hidden and
/// frozen while a boss cutscene plays (Ui.Cutscene): it opens, or shows again, once the cutscene is over.</summary>
[HarmonyPatch(typeof(LevelUpPanelManager), nameof(LevelUpPanelManager.OnUpdate))]
public static class CrusadeLevelUpTransitionPatch
{
    [HarmonyPrefix]
    public static bool Prefix(LevelUpPanelManager __instance)
    {
        try
        {
            bool cutscene = Ui.Cutscene.Running;
            var canvas = __instance != null ? __instance.Canvas : null;
            if (canvas != null && canvas.enabled == cutscene) canvas.enabled = !cutscene;
            if (cutscene) return false;
            return CrusadeSync.AllowLevelUpUpdate();
        }
        catch { return true; }
    }
}

[HarmonyPatch(typeof(TitanBloodRune), nameof(TitanBloodRune.OnTriggerEnter))]
public static class CrusadeRuneEnterPatch
{
    [HarmonyPostfix]
    public static void Postfix(TitanBloodRune __instance, Collider other)
    {
        try { CrusadeSync.OnRuneContact(__instance, other, true); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeRuneEnterPatch: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(TitanBloodRune), nameof(TitanBloodRune.OnTriggerExit))]
public static class CrusadeRuneExitPatch
{
    [HarmonyPostfix]
    public static void Postfix(TitanBloodRune __instance, Collider other)
    {
        try { CrusadeSync.OnRuneContact(__instance, other, false); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeRuneExitPatch: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(TitanBloodRune), nameof(TitanBloodRune.Update))]
public static class CrusadeRuneUpdatePatch
{
    [HarmonyPrefix]
    public static void Prefix(TitanBloodRune __instance)
    {
        try { CrusadeSync.BeforeRuneUpdate(__instance); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeRuneUpdatePatch: {ex.Message}"); }
    }
}
