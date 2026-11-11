using SoulstoneCoop.Lobby;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoulstoneCoop;

/// <summary>
/// Phase 5: what marks a ghost as an ally.
/// - Name (Steam name from the Hello) and health bar (PartnerHealth packet) above the ghost, on a screen-space
///   overlay canvas of our own. Each frame it's positioned where the camera sees the top of the ghost, so it stays
///   readable over spell effects, at a fixed size. The game's combat text is out (16-character buffer, short-lived),
///   and so is OnGUI (stripped in this build). The game's own overhead bar needs a HealthComponent, and the ghost has none, so
///   only its ally sprite is borrowed. The canvas sorts below the game's own overlays and hides while the game is
///   frozen (pause, level-up, loading barrier, end screen).
/// - Minimap icon: MinimapManager.RequestIcon(Player, ghost transform), removed when the ghost goes. Its OnUpdate
///   checks the transform against null and falls back to the stored position, so a destroyed ghost can't throw
///   in there (decompiled 0x615430). SetupMinimap clears every item on a new map.
/// Phase 9: one name/bar panel and one minimap icon per ghost, on the same canvas. Each player has a color of its own
/// (by slot) for the name and the ground ring, so 7 allies can be told apart.
/// </summary>
public static class GhostOverhead
{
    internal static readonly Color AllyColor = new(0.35f, 1f, 0.5f, 1f);
    private static readonly Color DownColor = new(0.95f, 0.35f, 0.3f, 1f);

    /// <summary>Phase 9: one color per slot (0 = host). The host keeps the phase 5 ally green.</summary>
    private static readonly Color[] SlotColors =
    {
        AllyColor,                       // 0 host: green
        new(0.35f, 0.8f, 1f, 1f),        // 1 sky blue
        new(1f, 0.85f, 0.3f, 1f),        // 2 yellow
        new(1f, 0.5f, 0.85f, 1f),        // 3 pink
        new(1f, 0.6f, 0.25f, 1f),        // 4 orange
        new(0.72f, 0.55f, 1f, 1f),       // 5 lilac
        new(0.6f, 1f, 0.95f, 1f),        // 6 aqua
        new(0.92f, 0.92f, 0.92f, 1f),    // 7 white
    };

    internal static Color ColorFor(byte slot) => slot < SlotColors.Length ? SlotColors[slot] : AllyColor;

    private const int MaxChars = 24;
    private const float NameSize = 26f;
    private const float BarWidth = 130f, BarHeight = 14f;

    /// <summary>The name + bar of one ghost, and its minimap icon.</summary>
    private sealed class View
    {
        public byte Slot;
        public RectTransform? Panel;
        public TextMeshProUGUI? Name;
        public GameObject? Bar;
        public RectTransform? Fill;
        public Image? FillImage;
        public string ShownName = "";
        public IntPtr GhostPtr;
        public float Height = 2.6f;
        public bool Visible;
        public int IconId = -1;
        public IntPtr IconGhostPtr;
        public float IconRunStart;
    }

    private static GameObject? _root;
    private static bool _buildFailed;
    private static TMP_FontAsset? _font;
    private static Sprite? _allySprite;
    private static readonly Dictionary<byte, View> Views = new();
    private static bool _hooked;
    private static bool _iconFailed;
    private static float _telemetryTimer;
    private static int _locVersion = -1;

    /// <summary>Called by VisualGhostFactory for each new ghost, with the height of its model.</summary>
    public static void Attach(byte slot, GameObject ghost, float height)
    {
        EnsureHooked();
        EnsureRoot();
        var v = ViewOf(slot);
        v.GhostPtr = ghost.Pointer;
        v.Height = height;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hpbar] Overhead follows ghost {slot} {height:0.0} above its root (name '{CoopSession.NameOf(slot)}', ui={(v.Panel != null)}).");
    }

    private static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        Net.Players.Left += slot =>
        {
            if (!Views.TryGetValue(slot, out var v)) return;
            Views.Remove(slot);
            RemoveIcon(v);
            if (v.Panel != null) UnityEngine.Object.Destroy(v.Panel.gameObject);
        };
    }

    private static View ViewOf(byte slot)
    {
        if (Views.TryGetValue(slot, out var v)) return v;
        v = new View { Slot = slot };
        Views[slot] = v;
        BuildPanel(v);
        return v;
    }

    public static void Tick(float unscaledDeltaTime)
    {
        EnsureHooked();
        if (_locVersion != Ui.Loc.Version) ApplyLanguage();
        foreach (var r in CoopSession.Remotes)
        {
            var ghost = r.Ghost;
            if (ghost == null && !Views.ContainsKey(r.Slot)) continue;
            var v = ViewOf(r.Slot);
            try { UpdateMinimapIcon(v, ghost); }
            catch (Exception ex)
            {
                _iconFailed = true;
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][hpbar] Minimap icon disabled: {ex.Message}");
            }
            try { UpdateOverhead(v, ghost); }
            catch (Exception ex)
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][hpbar] Overhead tick ({r.Slot}): {ex.Message}");
                SetVisible(v, false);
            }
        }

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer < 5f) return;
        _telemetryTimer = 0f;
        if (Views.Count == 0) return;
        var parts = new List<string>();
        foreach (var v in Views.Values)
        {
            var h = Life.PartnerHealth.Of(v.Slot);
            parts.Add($"{v.Slot}: hp={h.Health:F0}/{h.MaxHealth:F0} known={h.Known} down={h.Down} shown={v.Visible} icon={v.IconId}");
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hpbar] {string.Join(" | ", parts)} {Life.PartnerHealth.TakeStats()}");
    }

    // ---------------- overhead ----------------

    private static void UpdateOverhead(View v, GameObject? ghost)
    {
        if (v.Panel == null || ghost == null || !CoopSession.LocalInRun || Time.timeScale < 0.01f || Ui.Cutscene.Running)
        {
            SetVisible(v, false);
            return;
        }
        if (ghost.Pointer != v.GhostPtr) { v.GhostPtr = ghost.Pointer; v.Height = 2.6f; } // a ghost Attach didn't see

        var cam = FindCamera();
        if (cam == null) { SetVisible(v, false); return; }
        Vector3 screen = cam.WorldToScreenPoint(ghost.transform.position + Vector3.up * v.Height);
        if (screen.z <= 0f) { SetVisible(v, false); return; }
        v.Panel.position = new Vector3(Mathf.Round(screen.x), Mathf.Round(screen.y), 0f);

        var health = Life.PartnerHealth.Of(v.Slot);
        bool down = Life.CoopLife.IsRemoteDown(v.Slot) || health.Down || health.Dead;
        string name = CoopSession.NameOf(v.Slot);
        if (name.Length > MaxChars) name = name.Substring(0, MaxChars - 3) + "...";
        if (down) name = Ui.Loc.T("gh.down", ("name", name));
        if (v.Name != null && name != v.ShownName)
        {
            v.ShownName = name;
            v.Name.text = name;
        }
        var color = ColorFor(v.Slot);
        if (v.Name != null) v.Name.color = down ? DownColor : color;

        bool hasHp = health.Known && health.MaxHealth > 0f;
        if (v.Bar != null && v.Bar.activeSelf != hasHp) v.Bar.SetActive(hasHp);
        if (hasHp && v.Fill != null)
        {
            float frac = down ? 0f : health.Fraction;
            v.Fill.sizeDelta = new Vector2((BarWidth - 4f) * frac, BarHeight - 4f);
            if (v.Fill.gameObject.activeSelf != frac > 0.005f) v.Fill.gameObject.SetActive(frac > 0.005f);
            if (v.FillImage != null) v.FillImage.color = _allySprite != null ? Color.white : color;
        }
        SetVisible(v, true);
    }

    /// <summary>The game's language (or its font) changed: the name tags are rebuilt with the co-op UI's font,
    /// which follows the language (Ui.CoopUi.GameFont).</summary>
    private static void ApplyLanguage()
    {
        _locVersion = Ui.Loc.Version;
        if (_root == null) return;
        var font = FindFont();
        if (font == null || (_font != null && font.Pointer == _font.Pointer)) return;
        _font = font;
        foreach (var v in Views.Values)
        {
            if (v.Panel != null) UnityEngine.Object.Destroy(v.Panel.gameObject);
            v.Panel = null;
            v.Name = null;
            v.ShownName = "";
            BuildPanel(v);
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hpbar] Name tags rebuilt with font {font.name}.");
        Ui.Loc.CheckGlyphs(font, "name tag");
    }

    private static void SetVisible(View v, bool on)
    {
        v.Visible = on;
        if (v.Panel != null && v.Panel.gameObject.activeSelf != on) v.Panel.gameObject.SetActive(on);
    }

    private static void EnsureRoot()
    {
        if (_root != null || _buildFailed) return;
        try
        {
            _root = new GameObject("SoulstoneCoop_Overhead");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -100; // under the game's own overlay panels
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _font = FindFont();
            _allySprite = FindAllyBarSprite();
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hpbar] Overhead UI built (font={(_font != null ? _font.name : "TMP default")}, ally sprite={(_allySprite != null ? _allySprite.name : "none, flat color")}).");
        }
        catch (Exception ex)
        {
            _buildFailed = true;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][hpbar] Overhead UI could not be built (the ghosts go without name/bar): {ex}");
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }
    }

    private static void BuildPanel(View v)
    {
        EnsureRoot();
        if (_root == null) return;
        try
        {
            var panel = MakeRect(_root.transform, $"Panel{v.Slot}");
            panel.sizeDelta = new Vector2(320f, 60f);
            panel.pivot = new Vector2(0.5f, 0f); // bottom center sits on the top of the ghost

            var nameRt = MakeRect(panel, "Name");
            nameRt.anchorMin = nameRt.anchorMax = new Vector2(0.5f, 0f);
            nameRt.pivot = new Vector2(0.5f, 0f);
            nameRt.anchoredPosition = new Vector2(0f, BarHeight + 4f);
            nameRt.sizeDelta = new Vector2(320f, 34f);
            var t = nameRt.gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            t.fontSize = NameSize;
            t.fontStyle = FontStyles.Bold;
            t.alignment = TextAlignmentOptions.Bottom;
            t.enableWordWrapping = false;
            t.richText = false;
            t.raycastTarget = false;
            t.color = ColorFor(v.Slot);
            try
            {
                t.outlineWidth = 0.3f;
                t.outlineColor = new Color32(0, 0, 0, 255);
                var mat = t.fontMaterial; // the instance the outline went into
                mat.EnableKeyword("UNDERLAY_ON");
                mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.8f));
                mat.SetFloat("_UnderlayOffsetX", 0.6f);
                mat.SetFloat("_UnderlayOffsetY", -0.6f);
                mat.SetFloat("_UnderlaySoftness", 0.3f);
            }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][hpbar] Name outline/shadow: {ex.Message}"); }
            v.Name = t;

            var barRt = MakeRect(panel, "HealthBar");
            barRt.anchorMin = barRt.anchorMax = new Vector2(0.5f, 0f);
            barRt.pivot = new Vector2(0.5f, 0f);
            barRt.anchoredPosition = Vector2.zero;
            barRt.sizeDelta = new Vector2(BarWidth, BarHeight);
            var bg = barRt.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.8f);
            bg.raycastTarget = false;

            var fill = MakeRect(barRt, "Fill");
            fill.anchorMin = fill.anchorMax = new Vector2(0f, 0.5f); // grows from the left edge
            fill.pivot = new Vector2(0f, 0.5f);
            fill.anchoredPosition = new Vector2(2f, 0f);
            fill.sizeDelta = new Vector2(BarWidth - 4f, BarHeight - 4f);
            var img = fill.gameObject.AddComponent<Image>();
            img.raycastTarget = false;
            if (_allySprite != null)
            {
                img.sprite = _allySprite;
                img.type = _allySprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
            }
            img.color = _allySprite != null ? Color.white : ColorFor(v.Slot);
            v.Fill = fill;
            v.FillImage = img;
            v.Bar = barRt.gameObject;
            v.Bar.SetActive(false);

            panel.gameObject.SetActive(false);
            v.Panel = panel;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][hpbar] Overhead panel for slot {v.Slot} could not be built: {ex}");
        }
    }

    private static RectTransform MakeRect(Transform parent, string name)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>The co-op UI's font (the game's menu font, found in the menu), else any TMP font in the scene.</summary>
    private static TMP_FontAsset? FindFont()
    {
        var f = Ui.CoopUi.GameFont;
        if (f != null) return f;
        try
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>(true))
            {
                if (t != null && t.font != null) return t.font;
            }
        }
        catch { /* TMP default below */ }
        return TMP_Settings.defaultFontAsset;
    }

    private static Camera? _gameCam;
    private static float _camSearchTimer;

    /// <summary>The camera the game's own overhead bars project with (OverheadHealthBarPanelManager.GameplayCamera),
    /// else Camera.main. Looked up again every 2 s while missing (each run has its own).</summary>
    private static Camera? FindCamera()
    {
        if (_gameCam != null && _gameCam.isActiveAndEnabled) return _gameCam;
        _camSearchTimer -= Time.unscaledDeltaTime;
        if (_camSearchTimer <= 0f)
        {
            _camSearchTimer = 2f;
            try
            {
                var mgr = UnityEngine.Object.FindObjectOfType<OverheadHealthBarPanelManager>();
                _gameCam = mgr != null ? mgr.GameplayCamera : null;
            }
            catch { _gameCam = null; }
            if (_gameCam != null) return _gameCam;
        }
        return Camera.main;
    }

    /// <summary>The sprite the game's own overhead bars use for allies (OverheadHealthBarPanelManager.AllyHealthBarSprite).</summary>
    private static Sprite? FindAllyBarSprite()
    {
        try
        {
            var mgr = UnityEngine.Object.FindObjectOfType<OverheadHealthBarPanelManager>(true);
            return mgr != null ? mgr.AllyHealthBarSprite : null;
        }
        catch { return null; }
    }

    // ---------------- minimap ----------------

    private static void RemoveIcon(View v)
    {
        if (v.IconId < 0) return;
        bool sameRun = Mathf.Approximately(v.IconRunStart, CoopSession.LocalRunStartTime);
        // Another map clears the list itself (SetupMinimap); removing there would only log a game error.
        try { if (sameRun && GameManagerUtil.HasGameManager) MinimapManager.RequestRemoveIcon(v.IconId); }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][hpbar] Removing minimap icon {v.IconId}: {ex.Message}"); }
        v.IconId = -1;
        v.IconGhostPtr = IntPtr.Zero;
    }

    private static void UpdateMinimapIcon(View v, GameObject? ghost)
    {
        if (_iconFailed) return;
        bool sameRun = Mathf.Approximately(v.IconRunStart, CoopSession.LocalRunStartTime);
        if (v.IconId >= 0 && (ghost == null || ghost.Pointer != v.IconGhostPtr || !sameRun)) RemoveIcon(v);
        if (v.IconId >= 0 || ghost == null || !CoopSession.LocalInRun || !GameManagerUtil.HasGameManager) return;
        v.IconId = MinimapManager.RequestIcon(MinimapIconType.Player, ghost.transform, ghost.transform.position);
        v.IconGhostPtr = ghost.Pointer;
        v.IconRunStart = CoopSession.LocalRunStartTime;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][hpbar] Minimap icon {v.IconId} (Player) requested for ghost {v.Slot}.");
    }
}
