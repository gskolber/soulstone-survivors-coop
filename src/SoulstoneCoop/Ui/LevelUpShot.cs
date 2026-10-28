using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace SoulstoneCoop.Ui;

/// <summary>Debug only (--coop-ui-shot &lt;folder&gt; --coop-ui-shot-levelup): issue #5. From the menu it starts a
/// Survivors run alone, treats it as co-op for the level-up policy (so the compact overlay shows), levels up and saves
/// PNGs of the overlay over the HUD. Then it plugs a virtual gamepad (Input System device fed with state events) and
/// drives the overlay with it: D-pad, the game's confirm button, reroll, hide / show. Logs the gameplay / UI bindings
/// and the HUD / overlay geometry, then quits. --coop-ui-shot-map &lt;scene&gt; picks the map (default SurvivorsDesert).</summary>
internal static class LevelUpShot
{
    internal static bool Active;
    internal static string Map = "SurvivorsDesert";

    private static int _step;
    private static float _inRunAt = -1f;
    private static Gamepad? _pad;
    private static bool _forcePad;

    /// <summary>Every frame: a real pad switches the game to its gamepad scheme (PlayerInput) and keeps it there; the
    /// virtual one doesn't, and the game falls back to the keyboard, so hold it on the pad meanwhile.</summary>
    internal static void PerFrame()
    {
        if (!_forcePad) return;
        try
        {
            var im = MainUIManager.Instance != null ? MainUIManager.Instance.InputManager : null;
            if (im != null && im.ActiveInputType != ActiveInputType.Gamepad) im.ActiveInputType = ActiveInputType.Gamepad;
        }
        catch { }
    }

    internal static void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--coop-ui-shot-levelup") Active = true;
            else if (args[i] == "--coop-ui-shot-map" && i + 1 < args.Length) Map = args[i + 1];
        }
    }

    /// <summary>Overlay policy as if in co-op (Net/LevelUpPolicy.Decide).</summary>
    internal static bool ForceCoop => Active && UiShot.Active;

    private static void Log(string s) => Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] {s}");

    /// <summary>One step of the sequence; returns the wait before the next one (negative: same step next frame).</summary>
    internal static float Step(out bool finished)
    {
        finished = false;
        var panel = GameManagerUtil.HasGameManager && GameManagerUtil.GameplayUIManager != null
            ? GameManagerUtil.GameplayUIManager.LevelUpPanel : null;
        switch (_step)
        {
            case 0:
                Global.SelectedGameMode = GameModes.Survivor;
                MainUIManager.Instance!.ChangeScene(Map, 0f, LoadingScreenManager.LoadingScreenMode.Full);
                Log($"Level-up shots: ChangeScene({Map}).");
                _step++;
                return 5f;
            case 1: // in the run, the player and its panel up, a few seconds for the intro
                if (panel == null || GameState.LocalPlayerEntity == null) return -1f;
                if (_inRunAt < 0f) { _inRunAt = Time.unscaledTime; Log("In the run."); }
                if (Time.unscaledTime - _inRunAt < 6f) return -1f;
                _step++;
                return 0f;
            case 2:
                if (!PopupOpen(panel!))
                {
                    var xp = GameState.LocalPlayerEntity!._experience;
                    xp.LevelUpMultipleTimes(4);
                    Log("Level up x4.");
                }
                _step++;
                return 3f;
            case 3:
                UiShot.Shot("levelup-1-keyboard");
                LogBindings();
                LevelUpOverlay.DebugDescribe();
                _step++;
                return 0.3f;
            case 4: // the virtual pad: the game switches to its gamepad scheme on the first input
                // The window may not have the focus (a session runs this unattended): by default the Input System
                // drops device input then.
                try { InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus; } catch (Exception ex) { Log($"backgroundBehavior: {ex.Message}"); }
                Log($"Focus: {Application.isFocused}.");
                // An Xbox pad layout (the game has glyphs for it), else the generic one.
                try { _pad = InputSystem.AddDevice("XInputControllerWindows", "CoopShotPad", null).TryCast<Gamepad>(); } catch { }
                if (_pad == null) _pad = InputSystem.AddDevice("Gamepad", "CoopShotPad", null).TryCast<Gamepad>();
                Log($"Virtual gamepad: {(_pad != null ? _pad.deviceId + " " + _pad.layout : "failed")}, input update mode {InputSystem.settings.updateMode}.");
                if (_pad == null) { _step = 100; return 0f; }
                Press(_pad.dpad.right, true);
                _step++;
                return 0.15f;
            case 5:
                Press(_pad!.dpad.right, false);
                if (InputType() != "Gamepad")
                {
                    // A real pad switches the game's scheme (PlayerInput); this virtual one doesn't, so do it for it.
                    _forcePad = true;
                    PerFrame();
                    Log($"Game input type forced to Gamepad (was not switched by the virtual pad): now {InputType()}.");
                }
                LogBindings();
                _step++;
                return 0.6f;
            case 6: Press(_pad!.dpad.right, true); _step++; return 0.15f;
            case 7: Press(_pad!.dpad.right, false); _step++; return 1f;
            case 8:
                Log($"After D-pad right x2: index {SafeIndex(panel)}, input {InputType()}.");
                UiShot.Shot("levelup-2-pad-right");
                LevelUpOverlay.DebugDescribe();
                _step++;
                return 0.3f;
            case 9: // the game's confirm (Submit) on the pad
            {
                var b = PadButton(MainUIManager.Instance?.InputManager?.SubmitAction) ?? _pad!.buttonSouth;
                Log($"Pressing confirm '{b.path}' with {QueueCount(panel)} queued.");
                Press(b, true);
                _step++;
                return 0.15f;
            }
            case 10:
                Press(PadButton(MainUIManager.Instance?.InputManager?.SubmitAction) ?? _pad!.buttonSouth, false);
                _step++;
                return 2f;
            case 11:
                Log($"After confirm: {QueueCount(panel)} queued, open={PopupOpen(panel)}.");
                UiShot.Shot("levelup-3-pad-after-pick");
                _step++;
                return 0.3f;
            case 12:
            {
                var b = LevelUpOverlay.DebugPadReroll;
                Log($"Pressing reroll '{b?.path ?? "none"}' (rerolls '{panel?.RerollCount?.text}').");
                if (b != null) Press(b, true);
                _step++;
                return 0.15f;
            }
            case 13:
            {
                var b = LevelUpOverlay.DebugPadReroll;
                if (b != null) Press(b, false);
                _step++;
                return 1.5f;
            }
            case 14:
                Log($"After reroll: rerolls '{panel?.RerollCount?.text}'.");
                UiShot.Shot("levelup-4-pad-after-reroll");
                Press(_pad!.dpad.down, true);
                _step++;
                return 0.15f;
            case 15: Press(_pad!.dpad.down, false); _step++; return 1f;
            case 16: UiShot.Shot("levelup-5-pad-hidden"); Press(_pad!.dpad.down, true); _step++; return 0.15f;
            case 17: Press(_pad!.dpad.down, false); _step++; return 1f;
            case 18:
                UiShot.Shot("levelup-6-pad-shown");
                _step++;
                return 0.3f;
            case 19: // the keyboard's R (the game's reroll key): was off in the overlay before issue #5
                _forcePad = false;
                try { MainUIManager.Instance!.InputManager!.ActiveInputType = ActiveInputType.Keyboard; } catch { }
                Log($"Pressing keyboard R (rerolls '{panel?.RerollCount?.text}').");
                Press(Keyboard.current.rKey, true);
                _step++;
                return 0.15f;
            case 20: Press(Keyboard.current.rKey, false); _step++; return 1.5f;
            case 21:
                Log($"After keyboard R: rerolls '{panel?.RerollCount?.text}'.");
                UiShot.Shot("levelup-7-keyboard-after-reroll");
                LevelUpOverlay.DebugDescribe();
                _step = 100;
                return 0.3f;
            case 100:
                if (_pad != null) { try { InputSystem.RemoveDevice(_pad); } catch { } _pad = null; }
                finished = true;
                return 0f;
        }
        return 0f;
    }

    private static bool PopupOpen(LevelUpPanelManager? panel)
    {
        try { return panel != null && panel.PowerUpAnimation != null && panel.PowerUpAnimation.TargetVisibility > 0f; }
        catch { return false; }
    }

    private static int QueueCount(LevelUpPanelManager? panel)
    {
        try { return panel?.PowerUpRequests?.Count ?? -1; } catch { return -1; }
    }

    private static int SafeIndex(LevelUpPanelManager? panel)
    {
        try { return panel != null ? panel.SelectedLevelUpPanelItemIndex : -1; } catch { return -1; }
    }

    private static string InputType()
    {
        try { return MainUIManager.Instance!.InputManager!.ActiveInputType.ToString(); } catch { return "?"; }
    }

    /// <summary>The virtual pad's button bound to that action (first one), if any.</summary>
    private static ButtonControl? PadButton(InputAction? a)
    {
        if (a == null || _pad == null) return null;
        var ctrls = a.controls;
        for (int i = 0; i < ctrls.Count; i++)
        {
            var c = ctrls[i];
            if (c == null || c.device == null || c.device.deviceId != _pad.deviceId) continue;
            var b = c.TryCast<ButtonControl>();
            if (b != null) return b;
        }
        return null;
    }

    /// <summary>A state event for the whole pad (its current state with that button's bit flipped), queued like
    /// the native backend would.</summary>
    private static unsafe void Press(ButtonControl button, bool down)
    {
        Press(button.device, button, down);
    }

    private static unsafe void Press(InputDevice pad, ButtonControl button, bool down)
    {
        Log($"Before {button.path} {(down ? "down" : "up")}: pressed={button.isPressed}, device enabled={pad.enabled} canRunInBackground={pad.canRunInBackground} lastUpdate={pad.lastUpdateTime:F3} now={InputState.currentTime:F3} realtime={Time.realtimeSinceStartupAsDouble:F3}.");
        var devBlock = pad.stateBlock;
        var bBlock = button.stateBlock;
        int stateSize = (int)((devBlock._sizeInBits_k__BackingField + 7) / 8);
        int total = 24 + ((stateSize + 3) & ~3);
        var mem = Marshal.AllocHGlobal(total);
        try
        {
            byte* p = (byte*)mem;
            for (int i = 0; i < total; i++) p[i] = 0;
            *(int*)p = 0x53544154;                                   // FourCC 'STAT' ('S' << 24 | 'T' << 16 | ...)
            *(ushort*)(p + 4) = (ushort)total;
            *(ushort*)(p + 6) = (ushort)pad.deviceId;
            *(double*)(p + 8) = InputState.currentTime;               // NativeInputEvent: time at 8, eventId at 16
            *(int*)(p + 20) = devBlock._format_k__BackingField.m_Code; // 'GPAD'
            byte* state = p + 24;
            byte* cur = (byte*)pad.currentStatePtr + devBlock.m_ByteOffset;
            for (int i = 0; i < stateSize; i++) state[i] = cur[i];
            uint byteOff = bBlock.m_ByteOffset - devBlock.m_ByteOffset;
            uint bit = bBlock._bitOffset_k__BackingField;
            byte* target = state + byteOff + bit / 8;
            byte mask = (byte)(1 << (int)(bit % 8));
            if (down) *target |= mask; else *target &= (byte)~mask;
            var ev = default(InputEventPtr);
            *(IntPtr*)&ev = mem; // its only field, m_EventPtr (the InputEvent* ctor needs UnityEngine.InputModule)
            InputSystem.QueueEvent(ev);
            // Processed now, as a frame's own input update would before the game reads it (the panel reads it later in
            // this frame, so wasPressedThisFrame holds).
            InputSystem.Update(InputUpdateType.Dynamic);
        }
        finally { Marshal.FreeHGlobal(mem); }
        Log($"Pad {button.path} {(down ? "down" : "up")} (byte {bBlock.m_ByteOffset - devBlock.m_ByteOffset} bit {bBlock._bitOffset_k__BackingField}): now pressed={button.isPressed} wasPressedThisFrame={button.wasPressedThisFrame}, frame {Time.frameCount}, current pad {Gamepad.current?.deviceId}.");
    }

    /// <summary>Every gameplay / UI action with the controls it is bound to now.</summary>
    private static void LogBindings()
    {
        var im = MainUIManager.Instance != null ? MainUIManager.Instance.InputManager : null;
        if (im == null) return;
        foreach (var map in new[] { im.GameplayMap, im.UIMap })
        {
            if (map == null) continue;
            var sb = new System.Text.StringBuilder($"Bindings {map.name} (enabled {map.enabled}):");
            var actions = map.actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                var paths = new List<string>();
                var ctrls = a.controls;
                for (int c = 0; c < ctrls.Count; c++) if (ctrls[c] != null) paths.Add(ctrls[c].path);
                sb.Append($"\n  {a.name}{(a.enabled ? "" : " (off)")}: {string.Join(" ", paths)}");
            }
            Log(sb.ToString());
        }
    }
}
