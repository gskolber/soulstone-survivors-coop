using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoulstoneCoop.Ui;

/// <summary>Debug only (--coop-ui-shot &lt;folder&gt;): lets a session see the Co-op UI without a person at the
/// keyboard. Once the menu is up it gets past the title screen, opens the Co-op tab, saves PNGs of a few states (the
/// network ones faked through <see cref="CoopUi.DebugFake"/>), dumps the menu's UI hierarchy and quits the game.
/// Optional: --coop-ui-shot-tag &lt;text&gt; prefixes the files (e.g. a resolution), --coop-ui-shot-lang &lt;code&gt;
/// forces the mod's language for the run.</summary>
public static class UiShot
{
    internal static string? Dir;
    internal static string Tag = "";
    internal static string? Lang;
    internal static bool Active => Dir != null;

    private static int _step;
    private static float _wait, _clock, _splashTimer;
    private static bool _done;
    private const float Deadline = 180f;

    internal static void ParseArgs(string[] args)
    {
        foreach (var a in args) if (a == "--coop-ui-demo") Demo = true;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--coop-ui-shot") Dir = args[i + 1];
            else if (args[i] == "--coop-ui-shot-tag") Tag = args[i + 1] + "-";
            else if (args[i] == "--coop-ui-shot-lang") Lang = args[i + 1];
            else if (args[i] == "--coop-ui-shot-gallery") Gallery = args[i + 1].Split(',');
        }
        LevelUpShot.ParseArgs(args);
        if (Dir != null)
        {
            try { Directory.CreateDirectory(Dir); } catch { }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] UI screenshot mode: {Dir} (tag '{Tag}', lang {Lang ?? "-"}).");
        }
    }

    public static void Tick()
    {
        if (Dir == null || _done) return;
        try
        {
            float dt = Time.unscaledDeltaTime;
            _clock += dt;
            if (_clock > (LevelUpShot.Active ? 420f : Deadline))
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][shot] Deadline at step {_step}: dumping and quitting.");
                Finish();
                return;
            }
            if (LevelUpShot.Active) LevelUpShot.PerFrame();
            if (_wait > 0f) { _wait -= dt; return; }
            Step();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][shot] step {_step}: {ex}");
            _step++;
            _wait = 1f;
        }
    }

    private static void Next(float wait) { _step++; _wait = wait; }

    private static void Step()
    {
        switch (_step)
        {
            case 0: // the menu scene, past the title screen
                if (CoopUi.MenuReady)
                {
                    Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Menu ready at {_clock:F0}s.");
                    Next(SetGameLanguage() ? 5f : 3f);
                    return;
                }
                _splashTimer -= Time.unscaledDeltaTime;
                if (_splashTimer <= 0f) { _splashTimer = 3f; PassSplash(); }
                return;
            case 1:
                if (Gallery != null) { ShowGallery(); _step = 100; _wait = 1f; return; }
                if (LevelUpShot.Active) { _step = 500; return; }
                if (Demo) { Time.captureFramerate = DemoFps; _step = 200; _frame = 0; return; }
                if (CoopUi.UpdatePopupOpen && !_updateShot)
                {
                    // --coop-fake-latest: the new-version pop-up is up first; shoot it, "Not now", go on.
                    _updateShot = true;
                    Shot("00-update-popup");
                    _step = 300;
                    _wait = 0.3f;
                    return;
                }
                Shot("01-menu-tab"); Next(0.5f); return;
            case 300: // Esc = "Not now": the game asks for its Quit pop-up, the update pop-up closes instead
                UnityEngine.Object.FindObjectOfType<MenuManager>()?.QuitScreenPanel?.Open();
                if (CoopUi.UpdatePopupOpen) CoopUi.DebugCloseUpdate();
                _step = 1; _wait = 1f; return;
            case 2: CoopUi.DebugHover("tab", true); Next(0.6f); return;
            case 3: Shot("02-menu-tab-hover"); CoopUi.DebugHover("tab", false); Next(0.3f); return;
            case 4: CoopUi.DebugOpen(true); Next(1.5f); return;
            case 5: Shot("03-panel-idle"); CoopUi.DebugHover("invite", true); CoopUi.DebugFocusIp(); Next(0.6f); return;
            case 6: Shot("04-panel-hover-focus"); CoopUi.DebugHover("invite", false); CoopUi.DebugFake = CoopUi.FakeState.Hosting; Next(1f); return;
            case 7: Shot("05-panel-hosting"); CoopUi.DebugFake = CoopUi.FakeState.Connected; Next(1f); return;
            case 8: Shot("06-panel-connected"); CoopUi.DebugFake = CoopUi.FakeState.Client; Next(1f); return;
            case 9: Shot("07-panel-client"); CoopUi.DebugFake = CoopUi.FakeState.Full; Next(1f); return;
            case 10: Shot("08-panel-full"); CoopUi.DebugFake = CoopUi.FakeState.Error; Next(1f); return;
            case 11:
                Shot("09-panel-error");
                CoopUi.DebugFake = CoopUi.FakeState.None;
                Lobby.CoopSession.Notice(Loc.Msg("bn.paused", ("names", "Amigo")), 30f);
                Next(1f);
                return;
            case 12: Shot("10-panel-banner"); Next(0.2f); return;
            case 13:
                // What Esc does in the menu: the game asks its QuitScreenPanel to open. With our panel open it must
                // close ours instead (QuitScreenOpenPatch) and the quit pop-up must not show.
                UnityEngine.Object.FindObjectOfType<MenuManager>()?.QuitScreenPanel?.Open();
                Next(1f);
                return;
            case 14: Shot("11-menu-banner"); Next(0.5f); return;
            case 15: Finish(); return;
            case 200: DemoFrame(); return;
            case 100: Shot("gallery"); Next(0.5f); return;
            case 101: Finish(); return;
            case 500: // --coop-ui-shot-levelup (issue #5): its own sequence
            {
                float w = LevelUpShot.Step(out bool finished);
                if (finished) { Finish(); return; }
                if (w > 0f) _wait = w;
                return;
            }
        }
    }

    /// <summary>--coop-ui-shot-gallery a,b,c: draws those sprites (by name, as loaded) in a grid with their names,
    /// shoots it and quits. To pick the game's frames and buttons by eye.</summary>
    internal static string[]? Gallery;

    private static void ShowGallery()
    {
        var byName = new Dictionary<string, Sprite>();
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var s = o.TryCast<Sprite>();
            if (s != null && !byName.ContainsKey(s.name)) byName[s.name] = s;
        }
        var root = new GameObject("UiShotGallery");
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 9000;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 1f;
        var bg = new GameObject("bg").AddComponent<Image>();
        bg.transform.SetParent(root.transform, false);
        bg.color = new Color(0.25f, 0.22f, 0.2f, 1f);
        var brt = bg.rectTransform; brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = brt.offsetMax = Vector2.zero;
        int cols = 4;
        float cw = 1920f / cols, ch = 1080f / Mathf.Ceil(Gallery!.Length / (float)cols);
        for (int i = 0; i < Gallery.Length; i++)
        {
            string name = Gallery[i];
            float x = (i % cols) * cw, y = (i / cols) * ch;
            var img = new GameObject(name).AddComponent<Image>();
            img.transform.SetParent(root.transform, false);
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x + cw / 2f, -(y + ch / 2f) + 10f);
            if (byName.TryGetValue(name, out var sp))
            {
                img.sprite = sp;
                bool sliced = sp.border.sqrMagnitude > 0f;
                img.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
                float w = sp.rect.width, h = sp.rect.height;
                float k = Mathf.Min(1f, (cw - 30f) / w, (ch - 50f) / h);
                // Sliced ones drawn at a "panel" size to see how they stretch.
                rt.sizeDelta = sliced ? new Vector2(cw - 40f, ch - 60f) : new Vector2(w * k, h * k);
            }
            else img.color = Color.magenta;
            var label = new GameObject("l").AddComponent<TextMeshProUGUI>();
            label.transform.SetParent(root.transform, false);
            label.font = CoopUi.GameFont;
            label.fontSize = 20f;
            label.alignment = TextAlignmentOptions.Bottom;
            label.text = name;
            var lrt = label.rectTransform;
            lrt.anchorMin = lrt.anchorMax = new Vector2(0f, 1f);
            lrt.pivot = new Vector2(0.5f, 0f);
            lrt.anchoredPosition = new Vector2(x + cw / 2f, -(y + ch));
            lrt.sizeDelta = new Vector2(cw, 26f);
        }

    }

    // ---------------- demo video (--coop-ui-demo) ----------------

    /// <summary>--coop-ui-demo (with --coop-ui-shot &lt;folder&gt;): a scripted ~13 s tour of the tab and the panel,
    /// one PNG per frame with the game's clock locked to <see cref="DemoFps"/> (Time.captureFramerate), so the frames
    /// make a smooth video whatever the capture costs (ffmpeg -framerate 30 -i frame_%04d.png).</summary>
    internal static bool Demo;
    private const int DemoFps = 30;
    private static int _frame;
    private static bool _updateShot;
    private const string DemoIp = "192.168.0.15";

    private static void DemoFrame()
    {
        int f = _frame;
        if (f == 45) CoopUi.DebugHighlight("tab", true);
        if (f == 62) { CoopUi.DebugHighlight("tab", false); CoopUi.DebugOpen(true); }
        if (f == 110) { CoopUi.DebugFocusIp(); CoopUi.DebugSetIp(""); }
        if (f > 110 && f <= 110 + DemoIp.Length * 3 && (f - 110) % 3 == 0) CoopUi.DebugSetIp(DemoIp.Substring(0, (f - 110) / 3));
        if (f == 165) CoopUi.DebugHighlight("invite", true);
        if (f == 180) { CoopUi.DebugHighlight("invite", false); CoopUi.DebugFake = CoopUi.FakeState.Hosting; }
        if (f == 225) { CoopUi.DebugFakePlayers = 2; CoopUi.DebugFake = CoopUi.FakeState.Connected; }
        if (f == 250) CoopUi.DebugFakePlayers = 3;
        if (f == 275) CoopUi.DebugFakePlayers = 4;
        if (f == 300) CoopUi.DebugFakePlayers = 5;
        if (f == 355) UnityEngine.Object.FindObjectOfType<MenuManager>()?.QuitScreenPanel?.Open(); // = Esc
        if (f == 400)
        {
            Time.captureFramerate = 0;
            CoopUi.DebugFake = CoopUi.FakeState.None;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Demo: {f} frames.");
            Finish();
            return;
        }
        ScreenCapture.CaptureScreenshot(Path.Combine(Dir!, $"frame_{f:D4}.png"));
        _frame++;
    }

    /// <summary>--coop-ui-shot-lang: the game's own language switch (its fonts too), not saved to its prefs.</summary>
    private static bool SetGameLanguage()
    {
        if (Lang == null) return false;
        try
        {
            var cfg = LocalizationManager.LocalizationConfig;
            if (cfg == null || !cfg.TryGetLanguageConfigByLanguageCode(Lang, out var lc) || lc == null)
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][shot] No game language '{Lang}'.");
                return false;
            }
            var mm = UnityEngine.Object.FindObjectOfType<MenuManager>();
            mm!.StartCoroutine(LocalizationManager.LoadAndSetupWithLanguage(lc, false));
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Game language -> {Lang} (not saved).");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][shot] Switching the game's language: {ex.Message}");
            return false;
        }
    }

    private static void PassSplash()
    {
        var mm = UnityEngine.Object.FindObjectOfType<MenuManager>();
        var splash = mm != null ? mm.SplashScreenPanel : null;
        if (splash == null || !splash.gameObject.activeInHierarchy) return;
        // First "Play" (current save), then whatever "next" the save popup wants.
        try
        {
            if (splash.SavePopup != null && splash.SavePopup.activeInHierarchy)
            {
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][shot] Save popup: OnNextPressed.");
                splash.OnNextPressed();
            }
            else if (splash.PlayButton != null)
            {
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][shot] Title screen: Play.");
                splash.PlayButton.onClick.Invoke();
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][shot] Title screen: {ex.Message}"); }
    }

    internal static void Shot(string name)
    {
        string path = Path.Combine(Dir!, $"{Tag}{name}.png");
        ScreenCapture.CaptureScreenshot(path);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] {path} ({Screen.width}x{Screen.height}).");
    }

    private static void Finish()
    {
        _done = true;
        try { DumpUi(Path.Combine(Dir!, $"{Tag}ui-dump.txt")); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][shot] dump: {ex}"); }
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][shot] Done: quitting.");
        Application.Quit();
    }

    // ---------------- hierarchy dump ----------------

    private static void DumpUi(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Screen {Screen.width}x{Screen.height}, language {Loc.Codes[(int)Loc.Current]}");
        // Every canvas root of the loaded scenes (inactive too) and ours.
        var seen = new HashSet<IntPtr>();
        foreach (var c in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Canvas>()))
        {
            var canvas = c.TryCast<Canvas>();
            if (canvas == null || !canvas.gameObject.scene.IsValid()) continue;
            var root = canvas.transform.root;
            if (!seen.Add(root.Pointer)) continue;
            sb.AppendLine();
            sb.AppendLine($"===== {root.name} (scene {root.gameObject.scene.name})");
            Walk(sb, root, 0);
        }
        // Loaded sprites by name (what a UI could borrow).
        sb.AppendLine();
        sb.AppendLine("===== Sprites in memory");
        var names = new SortedSet<string>();
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var s = o.TryCast<Sprite>();
            if (s == null) continue;
            var r = s.rect;
            var b = s.border;
            names.Add($"{s.name} {r.width:F0}x{r.height:F0} border({b.x:F0},{b.y:F0},{b.z:F0},{b.w:F0}) tex={s.texture?.name}");
        }
        foreach (var n in names) sb.AppendLine(n);
        sb.AppendLine();
        sb.AppendLine("===== Fonts in memory");
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMP_FontAsset>()))
        {
            var f = o.TryCast<TMP_FontAsset>();
            if (f != null) sb.AppendLine(f.name);
        }
        File.WriteAllText(path, sb.ToString());
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] UI dump: {path} ({sb.Length / 1024} KB).");
    }

    private static void Walk(StringBuilder sb, Transform t, int depth)
    {
        string ind = new string(' ', depth * 2);
        var go = t.gameObject;
        sb.Append(ind).Append(t.name);
        if (!go.activeSelf) sb.Append(" (off)");
        var rt = t.TryCast<RectTransform>();
        if (rt != null)
        {
            var r = rt.rect;
            sb.Append($"  {r.width:F0}x{r.height:F0} a({rt.anchorMin.x:F2},{rt.anchorMin.y:F2})-({rt.anchorMax.x:F2},{rt.anchorMax.y:F2}) p({rt.pivot.x:F2},{rt.pivot.y:F2}) pos({rt.anchoredPosition.x:F0},{rt.anchoredPosition.y:F0})");
            if (t.localScale != Vector3.one) sb.Append($" scale({t.localScale.x:F2},{t.localScale.y:F2})");
        }
        var comps = new List<string>();
        foreach (var c in go.GetComponents<Component>())
        {
            if (c == null) continue;
            string n = c.GetIl2CppType().Name;
            if (n == "RectTransform" || n == "CanvasRenderer" || n == "Transform") continue;
            comps.Add(Describe(c, n));
        }
        if (comps.Count > 0) sb.Append("  [").Append(string.Join("; ", comps)).Append(']');
        sb.AppendLine();
        for (int i = 0; i < t.childCount; i++) Walk(sb, t.GetChild(i), depth + 1);
    }

    private static string Col(Color c) => $"#{ColorUtility.ToHtmlStringRGBA(c)}";

    private static string Describe(Component c, string n)
    {
        try
        {
            switch (n)
            {
                case "Image":
                {
                    var i = c.Cast<Image>();
                    return $"Image sprite={i.sprite?.name ?? "-"} type={i.type} col={Col(i.color)}{(i.raycastTarget ? "" : " noRay")}{(i.enabled ? "" : " (disabled)")}";
                }
                case "TextMeshProUGUI":
                {
                    var x = c.Cast<TextMeshProUGUI>();
                    string text = x.text ?? "";
                    if (text.Length > 40) text = text.Substring(0, 40) + "…";
                    text = text.Replace("\n", "\\n");
                    return $"TMP \"{text}\" font={x.font?.name} size={x.fontSize:F0}{(x.enableAutoSizing ? $"(auto {x.fontSizeMin:F0}-{x.fontSizeMax:F0})" : "")} col={Col(x.color)} align={x.alignment} style={x.fontStyle} mat={x.fontSharedMaterial?.name}";
                }
                case "Button":
                {
                    var b = c.Cast<Button>();
                    var cb = b.colors;
                    var ss = b.spriteState;
                    string tr = b.transition switch
                    {
                        Selectable.Transition.ColorTint => $"tint n={Col(cb.normalColor)} h={Col(cb.highlightedColor)} p={Col(cb.pressedColor)} s={Col(cb.selectedColor)} d={Col(cb.disabledColor)}",
                        Selectable.Transition.SpriteSwap => $"swap h={ss.highlightedSprite?.name} p={ss.pressedSprite?.name} s={ss.selectedSprite?.name} d={ss.disabledSprite?.name}",
                        _ => b.transition.ToString(),
                    };
                    return $"Button {tr} target={b.targetGraphic?.name} onClick(persistent)={b.onClick.GetPersistentEventCount()}{Persistent(b.onClick)}";
                }
                case "Toggle":
                {
                    var tg = c.Cast<Toggle>();
                    return $"Toggle on={tg.isOn} graphic={tg.graphic?.name} target={tg.targetGraphic?.name}";
                }
                case "EventTrigger":
                {
                    var et = c.Cast<UnityEngine.EventSystems.EventTrigger>();
                    var parts = new List<string>();
                    foreach (var e in et.triggers) parts.Add($"{e.eventID}{Persistent(e.callback)}");
                    return $"EventTrigger [{string.Join(", ", parts)}]";
                }
                case "UIButtonSound": return $"UIButtonSound '{c.Cast<UIButtonSound>().soundEvent}'";
                case "LabelReferences": return $"LabelReferences {c.Cast<LabelReferences>().Type}";
                case "LocalizedLabel":
                {
                    var l = c.Cast<LocalizedLabel>();
                    return $"LocalizedLabel '{l.Key}'";
                }
                case "CanvasGroup":
                {
                    var g = c.Cast<CanvasGroup>();
                    return $"CanvasGroup a={g.alpha:F2}{(g.interactable ? "" : " noInteract")}{(g.blocksRaycasts ? "" : " noRay")}";
                }
                case "Canvas":
                {
                    var cv = c.Cast<Canvas>();
                    return $"Canvas mode={cv.renderMode} order={cv.sortingOrder}{(cv.overrideSorting ? " override" : "")}";
                }
                case "CanvasScaler":
                {
                    var s = c.Cast<CanvasScaler>();
                    return $"CanvasScaler {s.uiScaleMode} ref={s.referenceResolution.x:F0}x{s.referenceResolution.y:F0} match={s.matchWidthOrHeight:F2}";
                }
                case "HorizontalLayoutGroup":
                case "VerticalLayoutGroup":
                {
                    var h = c.Cast<HorizontalOrVerticalLayoutGroup>();
                    return $"{n} spacing={h.spacing:F0} pad=({h.padding.left},{h.padding.right},{h.padding.top},{h.padding.bottom}) align={h.childAlignment} ctrl=({h.childControlWidth},{h.childControlHeight}) expand=({h.childForceExpandWidth},{h.childForceExpandHeight})";
                }
                case "LayoutElement":
                {
                    var le = c.Cast<LayoutElement>();
                    return $"LayoutElement ignore={le.ignoreLayout} min=({le.minWidth:F0},{le.minHeight:F0}) pref=({le.preferredWidth:F0},{le.preferredHeight:F0}) flex=({le.flexibleWidth:F0},{le.flexibleHeight:F0})";
                }
                case "TMP_InputField":
                {
                    var f = c.Cast<TMP_InputField>();
                    return $"TMP_InputField text='{f.text}' content={f.contentType} limit={f.characterLimit} target={f.targetGraphic?.name}";
                }
                case "ScrollRect":
                {
                    var s = c.Cast<ScrollRect>();
                    return $"ScrollRect content={s.content?.name} viewport={s.viewport?.name} v={s.vertical} h={s.horizontal} vbar={s.verticalScrollbar?.name}";
                }
            }
        }
        catch (Exception ex) { return $"{n}(err {ex.Message})"; }
        return n;
    }

    private static string Persistent(UnityEngine.Events.UnityEventBase e)
    {
        int n = e.GetPersistentEventCount();
        if (n == 0) return "";
        var parts = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var target = e.GetPersistentTarget(i);
            parts.Add($"{(target != null ? target.GetIl2CppType().Name : "?")}.{e.GetPersistentMethodName(i)}");
        }
        return " {" + string.Join(",", parts) + "}";
    }
}
