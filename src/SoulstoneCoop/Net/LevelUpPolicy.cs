using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop.Net;

/// <summary>
/// Level-ups in co-op never stop the time (issue #1, docs/levelup-notas.md): every upgrade choice opens as the
/// compact overlay (Ui/LevelUpOverlay: smaller, see-through, the player keeps moving and attacking). That covers all
/// the game's in-run choices, because they all go through the same popup (LevelUpPanelManager.PowerUpRequests):
/// level-ups (ExperienceComponent.LevelUp, queued), elite / boss / chest power-ups (CollectablePowerUpRequest), the
/// first pick at the match start (GameManagerUtil.StartMatch) and Mastery's ResetPlayer; reroll / banish / lock / skip
/// are buttons of that popup.
/// The popup (LevelUpPanelManager.OnUpdate) writes TimeUtil.LevelUpTime = 0 while it's visible, and nothing but
/// TimeUtil.SetTime reads it; so "no pause" is PauseSync recomputing timeScale without that factor. Esc and the
/// death guard factors are untouched. Solo (nobody else in the run) keeps the game's own pause.
/// The old [Coop] LevelUpPause (Always / NotWhenFullBuild / Never / Overlay) is gone: an install upgraded from 0.8
/// still had NotWhenFullBuild saved, and BepInEx never replaces a saved value. The key left in the .cfg is unused.
/// </summary>
public static class LevelUpPolicy
{
    /// <summary>Test only: the game's full-screen popup instead of the overlay (still no pause; you stand still).</summary>
    internal static ConfigEntry<bool> FullPanel = null!;

    private static bool _open, _skip, _overlay, _coop;
    private static float _openedAt;
    private static int _queueAtOpen;
    // Session telemetry (reset with the run).
    private static int _popups, _popupsNoPause;
    private static float _secondsPaused, _secondsNoPause;

    private static bool _prevF6;

    internal static void Bind(ConfigFile cfg)
    {
        FullPanel = cfg.Bind("Debug", "LevelUpFullPanel", false,
            "Test only: in co-op, level-ups use the game's full-screen panel (no pause either, but you stand still while " +
            "choosing) instead of the small panel.");
        Ui.LevelUpOverlay.Bind(cfg);
    }

    /// <summary>A new run: forget the session counters.</summary>
    internal static void ResetForRun()
    {
        _open = false;
        _popups = _popupsNoPause = 0;
        _secondsPaused = _secondsNoPause = 0f;
        Ui.LevelUpOverlay.ResetForRun();
    }

    /// <summary>This frame's decision for an open popup: skip the pause (in co-op, always)? and as the overlay?</summary>
    internal static bool Decide(out bool overlay, out bool coop)
    {
        coop = (NetworkManager.HasPeer && Lobby.CoopSession.LocalInRun && Lobby.CoopSession.AnyRemoteInRun)
               || Ui.LevelUpShot.ForceCoop; // debug screenshots (--coop-ui-shot-levelup)
        overlay = coop && !FullPanel.Value;
        return coop;
    }

    /// <summary>Called every frame from the TimeUtil.SetTime postfix. True when this frame's level-up popup must
    /// NOT stop the time; <paramref name="overlay"/> = it shows as the compact overlay.</summary>
    internal static bool SkipPause(bool popupOpen, out bool overlay)
    {
        overlay = false;
        float now = Time.realtimeSinceStartup;
        if (!popupOpen)
        {
            if (_open) OnClosed(now);
            return false;
        }

        bool skip = Decide(out overlay, out bool coop);
        if (!_open)
        {
            _open = true;
            _openedAt = now;
            _queueAtOpen = QueueCount();
            _skip = skip;
            _overlay = overlay;
            _coop = coop;
            if (NetworkManager.Role != NetRole.None)
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Popup opened: coop={coop} -> {Describe(skip, overlay)} " +
                    $"(queued={_queueAtOpen}, level={LocalLevel()}).");
        }
        else if (skip != _skip || overlay != _overlay || coop != _coop)
        {
            // E.g. the others reached the run while the match-start pick was open, or everyone else left.
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Policy changed with the popup open: coop={coop} -> {Describe(skip, overlay)}.");
            _skip = skip;
            _overlay = overlay;
            _coop = coop;
        }
        return skip;
    }

    private static string Describe(bool skip, bool overlay) => !skip ? "pause (solo)" : overlay ? "NO PAUSE (overlay)" : "NO PAUSE (full panel, debug)";

    private static void OnClosed(float now)
    {
        _open = false;
        float secs = now - _openedAt;
        _popups++;
        if (_skip) { _popupsNoPause++; _secondsNoPause += secs; }
        else _secondsPaused += secs;
        if (NetworkManager.Role == NetRole.None) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] Popup closed after {secs:F1} s ({(_skip ? (_overlay ? "overlay" : "full panel") : "paused, solo")}, " +
            $"queued at open={_queueAtOpen}, level={LocalLevel()}). Run so far: {_popups} popups, " +
            $"{_popups - _popupsNoPause} paused ({_secondsPaused:F0} s), {_popupsNoPause} without pause ({_secondsNoPause:F0} s).");
    }

    private static int QueueCount()
    {
        try
        {
            var ui = GameManagerUtil.GameplayUIManager;
            var q = ui != null && ui.LevelUpPanel != null ? ui.LevelUpPanel.PowerUpRequests : null;
            return q != null ? q.Count : -1;
        }
        catch { return -1; }
    }

    private static int LocalLevel()
    {
        try
        {
            var xp = GameState.LocalPlayerEntity != null ? GameState.LocalPlayerEntity._experience : null;
            return xp != null ? xp.Level : -1;
        }
        catch { return -1; }
    }

    /// <summary>Debug hotkey (co-op only, in a match): F6 = +1 level for the local player, Shift+F6 = +5. Goes
    /// straight through ExperienceComponent.LevelUpMultipleTimes, so it is NOT shared as team XP.</summary>
    internal static void DebugHotkey()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        bool f6 = kb.f6Key.isPressed;
        bool pressed = f6 && !_prevF6;
        _prevF6 = f6;
        if (!pressed || NetworkManager.Role == NetRole.None) return;
        try
        {
            var local = GameState.LocalPlayerEntity;
            var xp = local != null ? local._experience : null;
            if (xp == null || !GameManagerUtil.HasGameManager) return;
            int n = kb.shiftKey.isPressed ? 5 : 1;
            int before = xp.Level;
            xp.LevelUpMultipleTimes(n);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][levelup] F6 (debug): +{n} level(s), {before} -> {xp.Level}.");
        }
        catch (System.Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][levelup] F6: {ex.Message}");
        }
    }
}
