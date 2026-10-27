using Il2CppInterop.Runtime.Injection;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop;

/// <summary>
/// Injected IL2CPP MonoBehaviour that gives our managed code an Update() tick
/// inside the game's own loop (pumps LiteNetLib events, ticks player sync, debug hotkeys).
/// </summary>
public class CoopBehaviour : MonoBehaviour
{
    private static CoopBehaviour? _instance;
    private static bool _firstUpdateLogged;
    private static bool _networkEventsHooked;
    private bool _prevPageDown;
    private bool _prevInsert;
    private bool _prevDelete;

    public CoopBehaviour(System.IntPtr ptr) : base(ptr) { }

    private static bool _autoConnectDone;

    /// <summary>Creates the persistent tick object (first menu, or first run in an old save flow) and, once per game
    /// session, applies the launch args / AutoHost. Phase 6: this now runs in the main menu (MenuManager.Awake), so
    /// the connection is up before any run and survives the following ones.</summary>
    public static void EnsureCreated()
    {
        // Without this the unfocused instance stops updating and stops sending its position.
        Application.runInBackground = true;

        if (_instance == null)
        {
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp<CoopBehaviour>())
                ClassInjector.RegisterTypeInIl2Cpp<CoopBehaviour>();

            var go = new GameObject("SoulstoneCoop");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<CoopBehaviour>();
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] CoopBehaviour created and attached.");
        }

        if (!_networkEventsHooked)
        {
            _networkEventsHooked = true;
            Players.Left += slot => DestroyGhost(Lobby.CoopSession.Get(slot));
            Lobby.CoopSession.EnsureHooked();
            PauseSync.EnsureHooked();
            Enemies.HostEnemySync.EnsureHooked();
            Enemies.ClientPuppets.EnsureHooked();
            Enemies.RemoteProxy.EnsureHooked();
            Skills.RemoteCaster.EnsureHooked();
            Life.CoopLife.EnsureHooked();
            Life.PartnerHealth.EnsureHooked();
            Summons.SummonSync.EnsureHooked();
        }

        try { Steam.SteamLobby.EnsureInit(); }
        catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] SteamLobby.EnsureInit: {ex}"); }

        if (!_autoConnectDone && NetworkManager.Role == NetRole.None)
        {
            _autoConnectDone = true;
            if (Plugin.ConnectAddressArg != null)
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Auto-connect to {Plugin.ConnectAddressArg}.");
                NetworkManager.StartClient(Plugin.ConnectAddressArg);
            }
            else if (Plugin.HostArg || Plugin.AutoHost.Value)
            {
                Plugin.StaticLog.LogInfo("[SoulstoneCoop] Auto-host: starting host.");
                NetworkManager.StartHost();
            }
        }
    }

    /// <summary>Phase 6: a ghost lives exactly while we and that player are in a run together. It used to be created on
    /// PeerConnected, which no longer happens inside a run (the connection is made in the menu). Phase 9: one ghost
    /// per other player in the run with us.</summary>
    private static void UpdateGhostLifecycle()
    {
        var local = GameState.LocalPlayerEntity;
        // Not while our own scene is on its way out (portal/Restart taken): the others may already be in the next one.
        bool weCan = Lobby.CoopSession.Connected && Lobby.CoopSession.LocalInRun && !Lobby.CoopSession.StageTransition && local != null;
        foreach (var r in Lobby.CoopSession.Remotes)
        {
            bool want = weCan && r.InRun;
            if (want && r.Ghost == null)
            {
                PlayerSync.ResetForNewGhost(r);
                r.GhostRebuilds = 0;
                r.IdentityRebuilds = 0;
                Life.PartnerHealth.Reset(r.Slot); // its 1 s refresh fills it again, and ours goes out now
                var t = local!.gameObject.transform;
                var spawnPos = t.position + t.right * 2f;
                r.Ghost = VisualGhostFactory.Create(local, r, spawnPos, t.rotation, $"SoulstoneCoop_RemoteGhost{r.Slot}");
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Slot {r.Slot} ({r.Name}) is in the run with us - ghost created.");
            }
            else if (!want && r.Ghost is not null)
            {
                // Also covers a ghost destroyed with the previous scene (Unity-null but not C#-null).
                DestroyGhost(r);
            }
        }
    }

    private static void DestroyGhost(Lobby.RemotePlayer? r)
    {
        if (r == null) return;
        var ghost = r.Ghost;
        r.Ghost = null;
        r.ModelAnimator = null;
        if (ghost != null)
        {
            UnityEngine.Object.Destroy(ghost);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Ghost of slot {r.Slot} removed.");
        }
    }

    private void Update()
    {
        try
        {
            if (!_firstUpdateLogged)
            {
                _firstUpdateLogged = true;
                Plugin.StaticLog.LogInfo("[SoulstoneCoop] First Update() tick received.");
            }

            // The game may flip this back off (or pause via timeScale) when its window loses focus.
            Application.runInBackground = true;

            NetworkManager.PollEvents();
            try { Steam.SteamLobby.Tick(Time.unscaledDeltaTime); }
            catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] SteamLobby.Tick: {ex}"); }
            try { Lobby.CoopSession.Tick(Time.unscaledDeltaTime); }
            catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopSession.Tick: {ex}"); }
            UpdateGhostLifecycle();
            try { Ui.Loc.Tick(); }
            catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] Loc.Tick: {ex}"); }
            Ui.CoopUi.Tick();
            Ui.UiShot.Tick(); // debug: --coop-ui-shot
            PlayerSync.Tick(Time.unscaledDeltaTime);
            GhostOverhead.Tick(Time.unscaledDeltaTime);
            Life.PartnerHealth.Tick(Time.unscaledDeltaTime);
            Summons.SummonSync.Tick(Time.unscaledDeltaTime);
            ClientSpawnBlock.Tick(Time.unscaledDeltaTime);
            WorldTelemetry.Tick(Time.unscaledDeltaTime);
            Enemies.HostEnemySync.Tick(Time.unscaledDeltaTime);
            Enemies.ClientPuppets.Tick(Time.unscaledDeltaTime);
            Enemies.RemoteProxy.Tick(Time.unscaledDeltaTime);
            Enemies.ClientIncomingDamage.Tick(Time.unscaledDeltaTime);
            Skills.CastSync.Tick(Time.unscaledDeltaTime);
            Loot.LootSync.Tick(Time.unscaledDeltaTime);
            Life.CoopLife.Tick(Time.unscaledDeltaTime);
            Lobby.PortalSync.Tick(Time.unscaledDeltaTime);
            Enemies.EnemyCasts.Tick(Time.unscaledDeltaTime);
            Enemies.SpecialsDebug.Tick(Time.unscaledDeltaTime);
            Enemies.ObjectiveSync.Tick(Time.unscaledDeltaTime);
            Enemies.EnemyScaling.Tick(Time.unscaledDeltaTime);
            RebuildGhostIfModelInactive();
            HandleDebugHotkeys();
            LogSyncStats();
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] Exception in Update(): {ex}");
        }
    }

    private float _ghostCheckTimer;

    /// <summary>
    /// The client connects at GameplayUIManager.Setup, before its own StartMatch, while the game still has the
    /// player's model child (e.g. "HoundMasterRegular(Clone)", the one with the Animator) inactive - it's turned
    /// on later. The ghost cloned at that moment keeps the model off for good: only the blue ground ring shows
    /// clone again.
    /// Phase 5: the other player's model is built from its prefab and switched on explicitly, so this mostly matters for
    /// the local-clone fallback. A ghost is also rebuilt when its player's identity (Hello) differs from the one it was
    /// built for, e.g. the Hello arrived after the ghost was made from the fallback.
    /// </summary>
    private void RebuildGhostIfModelInactive()
    {
        _ghostCheckTimer += Time.unscaledDeltaTime;
        if (_ghostCheckTimer < 1f) return;
        _ghostCheckTimer = 0f;

        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        foreach (var r in Lobby.CoopSession.Remotes)
        {
            var ghost = r.Ghost;
            if (ghost == null) continue;

            string key = VisualGhostFactory.KeyFor(r.Info);
            if (r.Info.Known && key != r.GhostKey && r.IdentityRebuilds < 3)
            {
                r.IdentityRebuilds++;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Slot {r.Slot} identity is now {key} (ghost built for {r.GhostKey}) - rebuilding the ghost (#{r.IdentityRebuilds}).");
                RebuildGhost(r, ghost, local);
                continue;
            }

            if (r.GhostRebuilds >= 3) continue;
            var ghostAnimator = r.ModelAnimator;
            if (ghostAnimator == null)
            {
                var ghostAnimators = ghost.GetComponentsInChildren<Animator>(true);
                if (ghostAnimators.Length == 0) continue;
                ghostAnimator = ghostAnimators[0];
            }
            var localAnimators = local.gameObject.GetComponentsInChildren<Animator>(true);
            if (localAnimators.Length == 0) continue;
            if (ghostAnimator.gameObject.activeInHierarchy || !localAnimators[0].gameObject.activeInHierarchy) continue;

            r.GhostRebuilds++;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Ghost {r.Slot} model '{ghostAnimator.gameObject.name}' was cloned inactive - rebuilding the ghost (#{r.GhostRebuilds}).");
            RebuildGhost(r, ghost, local);
        }
    }

    private static void RebuildGhost(Lobby.RemotePlayer r, GameObject ghost, Entity local)
    {
        var t = ghost.transform;
        Vector3 pos = t.position;
        Quaternion rot = t.rotation;
        UnityEngine.Object.Destroy(ghost);
        r.Ghost = VisualGhostFactory.Create(local, r, pos, rot, $"SoulstoneCoop_RemoteGhost{r.Slot}");
    }
    // The pause notice (phase 1) moved to the co-op banner (Ui/CoopUi): IMGUI is partly stripped in this build.

    private float _statsTimer;

    private void LogSyncStats()
    {
        if (NetworkManager.Role == NetRole.None) return;
        _statsTimer += Time.unscaledDeltaTime;
        if (_statsTimer < 2f) return;
        _statsTimer = 0f;

        var local = GameState.LocalPlayerEntity;
        string localPos = local != null ? local.gameObject.transform.position.ToString() : "-";
        var ghosts = new List<string>();
        foreach (var r in Lobby.CoopSession.Remotes)
        {
            var g = r.Ghost;
            ghosts.Add($"{r.Slot}:{r.Place}/ping={NetworkManager.PingOf(r.Slot)}/{(g != null ? g.transform.position.ToString() : "-")}/recv={r.PositionsReceived}{(g != null && !g.activeInHierarchy ? "/inactive" : "")}");
        }
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][sync] role={NetworkManager.Role} slot={Players.MySlot} peers={NetworkManager.PeerSlots.Count} " +
            $"ping={NetworkManager.PingMs} silence={NetworkManager.SilenceSeconds:F1}s{(NetSim.Enabled ? " SIM" : "")} " +
            $"place={Lobby.CoopSession.LocalPlace} sent={PlayerSync.SentCount} recv={PlayerSync.ReceivedCount} dashes={PlayerSync.DashesSent}/{PlayerSync.DashesReceived} trail={GhostDashTrail.ImagesShown} local={localPos} " +
            $"others=[{string.Join(" ", ghosts)}] relayed={NetworkManager.RelayedPackets} ({NetworkManager.RelayedBytes} B) " +
            $"runInBg={Application.runInBackground} timeScale={Time.timeScale} focused={Application.isFocused}");
        NetworkManager.RelayedPackets = NetworkManager.RelayedBytes = 0;
    }

    private void HandleDebugHotkeys()
    {
        if (Keyboard.current == null) return;
        if (Ui.CoopUi.IsTyping) return; // Delete/Insert are also editing keys in the IP field

        // Insert = host, Delete = connect to localhost. Debug-only until there's a real UI.
        bool insertNow = Keyboard.current.insertKey.isPressed;
        if (insertNow && !_prevInsert)
        {
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] Insert: starting host.");
            NetworkManager.StartHost();
        }
        _prevInsert = insertNow;

        bool deleteNow = Keyboard.current.deleteKey.isPressed;
        if (deleteNow && !_prevDelete)
        {
            Plugin.StaticLog.LogInfo("[SoulstoneCoop] Delete: connecting to 127.0.0.1.");
            NetworkManager.StartClient("127.0.0.1");
        }
        _prevDelete = deleteNow;

        // PageDown: manual single-ghost spawn/respawn, independent of networking, for quick visual checks.
        bool pageDownNow = Keyboard.current.pageDownKey.isPressed;
        if (pageDownNow && !_prevPageDown)
        {
            DebugRespawnGhost();
        }
        _prevPageDown = pageDownNow;
    }

    private static GameObject? _debugGhost;

    private static void DebugRespawnGhost()
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop] PageDown: no local player entity captured yet.");
            return;
        }

        if (_debugGhost != null) UnityEngine.Object.Destroy(_debugGhost);
        var t = local.gameObject.transform;
        var spawnPos = t.position + t.right * 2f;
        _debugGhost = VisualGhostFactory.Create(local, null, spawnPos, t.rotation, "SoulstoneCoop_DebugGhost");
    }
}
