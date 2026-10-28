using BepInEx.Configuration;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoulstoneCoop.Ui;

/// <summary>
/// Issue #7: the party panel, top left in a run. One row per other player (up to 7): character icon, Steam name in its
/// ghost's color, level, revives left (the game's death guard charges), health bar with numbers, XP bar to its next
/// level, and its state: alive, down (with the revive progress), dead, disconnected, or not in the run (menu/loading).
/// Data: PartnerHealth (hp, down/dead flags), CoopLife (revive progress, the host's count), PartyStatus (level, XP,
/// revives), CoopSession (name, character, place).
///
/// Built with the Co-op UI (in the menu, where the game's sprites and font are around) under its root, so the
/// per-language font swap of CoopUi covers it too. It is a nested canvas sorted UNDER the game's own canvases: the
/// level-up panel (and any other game pop-up) is always drawn over it. Config: [Coop] PartyHud.
/// </summary>
internal static class PartyHud
{
    internal static ConfigEntry<bool> Enabled = null!;

    internal static void Bind(ConfigFile cfg)
    {
        Enabled = cfg.Bind("Coop", "PartyHud", true,
            "In a run, show a panel top left with each partner's health, level, XP, revives left and state (down, dead, disconnected).");
    }

    // Layout, in the Co-op canvas units (1080 high, the width follows the aspect ratio).
    private const float Left = 18f;
    private const float Top = 64f;             // top left, under the game's version line and above its gem counters (~470)
    private const float RowW = 360f, RowH = 54f, Gap = 4f;
    private const float BarX = 64f;
    private const float Opacity = 0.9f;
    private const int MaxRows = Players.MaxSlots - 1;
    private const float GoneSeconds = 30f;     // a disconnected partner's row stays this long

    private const string Gold = "#FFC700", Muted = "#BEB4A5", Bad = "#FF8080";
    private static readonly Color HpHigh = new(0.36f, 0.82f, 0.36f, 1f);
    private static readonly Color HpMid = new(0.9f, 0.76f, 0.29f, 1f);
    private static readonly Color HpLow = new(0.9f, 0.35f, 0.29f, 1f);
    private static readonly Color DownBg = new(0.22f, 0.02f, 0.02f, 0.9f);
    private static readonly Color BarBg = new(0f, 0f, 0f, 0.75f);
    private static readonly Color ReviveColor = new(0.72f, 0.45f, 0.04f, 1f); // amber: the white text over it stays readable
    private static readonly Color XpColor = new(0.45f, 0.72f, 1f, 1f);

    internal enum State : byte { Alive, Down, Dead, Gone, Away }

    /// <summary>One row's content (real or, for the screenshot mode, fake).</summary>
    internal sealed class RowData
    {
        public byte Slot;
        public string Name = "", CharName = "";
        public State State;
        public string AwayText = "";
        public bool HpKnown;
        public float Hp, MaxHp;
        public bool StatusKnown;
        public int Level, Revives;
        public float Xp, Progress;
        public bool Unstable;
    }

    private sealed class Row
    {
        public RectTransform Rt = null!;
        public CanvasGroup Group = null!;
        public Image Strip = null!, Icon = null!, HpBg = null!, HpFill = null!, XpFill = null!, RevIcon = null!;
        public RectTransform HpFillRt = null!, ReviveFillRt = null!, XpFillRt = null!;
        public TextMeshProUGUI Name = null!, Level = null!, Revives = null!, BarText = null!;
        public string Key = "";
    }

    private static GameObject? _root;
    private static readonly List<Row> Rows = new();
    private static readonly List<RowData> Data = new();
    private static bool _failed, _hooked, _shownLogged;
    private static float _refreshTimer, _telemetryTimer;
    private static IntPtr _sceneSprites;   // the GameplayUIManager the in-run sprites were taken from
    private static Sprite? _revSprite;

    /// <summary>Partners who left during this run: their row stays a while as "disconnected".</summary>
    private sealed class Gone { public byte Slot; public string Name = "", CharName = ""; public float Since, RunStart; }
    private static readonly List<Gone> GoneList = new();
    private static readonly float[] LastInRun = new float[Players.MaxSlots]; // per slot, when it was last seen in our run
    private const float GoneWindow = 20f;

    /// <summary>Debug (--coop-party-shot): rows shown instead of the real ones (null = real).</summary>
    internal static List<RowData>? DebugRows;
    internal static bool Visible => _root != null && _root.activeSelf;

    // ---------------- build ----------------

    internal static void Build(RectTransform parent)
    {
        try
        {
            var rt = GameLook.Rect(parent, "PartyHud");
            GameLook.Stretch(rt, 0f, 0f, 0f, 0f);
            var canvas = rt.gameObject.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = -90; // under the game's canvases (level-up, pause, end screen), over the name tags (-100)
            var group = rt.gameObject.AddComponent<CanvasGroup>();
            group.alpha = Opacity;
            group.interactable = false;
            group.blocksRaycasts = false;

            Rows.Clear();
            for (int i = 0; i < MaxRows; i++) Rows.Add(MakeRow(rt, i));
            KeepCharacterIcons(rt);
            rt.gameObject.SetActive(false);
            _root = rt.gameObject;
        }
        catch (Exception ex)
        {
            _failed = true;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][party] Party panel could not be built: {ex}");
        }
    }

    private static Row MakeRow(RectTransform parent, int i)
    {
        var row = new Row();
        var bg = GameLook.Img(parent, $"Partner-{i + 1}", "Craft_Background_List_Frame", Image.Type.Simple, new Color(0f, 0f, 0f, 0.55f));
        row.Rt = bg.rectTransform;
        GameLook.TopLeft(row.Rt, Left, Top + i * (RowH + Gap), RowW, RowH);
        row.Group = row.Rt.gameObject.AddComponent<CanvasGroup>();

        // The player's color (its ghost's ring and name tag), as a thin strip on the left.
        row.Strip = GameLook.Img(row.Rt, "Color", "-", Image.Type.Simple, Color.white);
        GameLook.TopLeft(row.Strip.rectTransform, 2f, 4f, 4f, RowH - 8f);

        var frame = GameLook.Img(row.Rt, "Icon-Frame", "Skill_Tree_Character_BGFrame", Image.Type.Simple, new Color(0f, 0f, 0f, 0.6f));
        GameLook.TopLeft(frame.rectTransform, 9f, 3f, 48f, 48f);
        row.Icon = GameLook.Img(frame.rectTransform, "Icon", "-", Image.Type.Simple, Color.white);
        row.Icon.preserveAspect = true;
        GameLook.Stretch(row.Icon.rectTransform, 3f, 3f, 3f, 3f);

        row.Name = GameLook.Text(row.Rt, "Name", "", 25f, TextAlignmentOptions.MidlineLeft);
        GameLook.AutoSize(row.Name, 25f, 0.7f);
        row.Name.overflowMode = TextOverflowModes.Ellipsis;
        GameLook.TopLeft(row.Name.rectTransform, BarX, 0f, 186f, 26f);

        row.RevIcon = GameLook.Img(row.Rt, "Revives-Icon", "-", Image.Type.Simple, Color.white);
        row.RevIcon.preserveAspect = true;
        GameLook.TopLeft(row.RevIcon.rectTransform, 254f, 4f, 21f, 21f);
        row.Revives = GameLook.Text(row.Rt, "Revives", "", 20f, TextAlignmentOptions.MidlineLeft);
        GameLook.TopLeft(row.Revives.rectTransform, 276f, 0f, 26f, 26f);

        row.Level = GameLook.Text(row.Rt, "Level", "", 21f, TextAlignmentOptions.MidlineRight);
        GameLook.AutoSize(row.Level, 21f, 0.7f);
        GameLook.TopLeft(row.Level.rectTransform, RowW - 58f, 0f, 52f, 26f);

        float barW = RowW - BarX - 8f;
        row.HpBg = GameLook.Img(row.Rt, "Health", "-", Image.Type.Simple, BarBg);
        var hp = row.HpBg.rectTransform;
        GameLook.TopLeft(hp, BarX, 27f, barW, 18f);
        row.HpFill = GameLook.Img(hp, "Fill", "-", Image.Type.Simple, HpHigh);
        row.HpFillRt = row.HpFill.rectTransform;
        Fill(row.HpFillRt, 1f);
        var rev = GameLook.Img(hp, "Revive", "-", Image.Type.Simple, ReviveColor);
        row.ReviveFillRt = rev.rectTransform;
        Fill(row.ReviveFillRt, 0f);
        row.BarText = GameLook.Text(hp, "Text", "", 17f, TextAlignmentOptions.Center);
        GameLook.AutoSize(row.BarText, 17f, 0.7f);
        GameLook.Stretch(row.BarText.rectTransform, 2f, -2f, 2f, -2f);
        try
        {
            row.BarText.outlineWidth = 0.25f;
            row.BarText.outlineColor = new Color32(0, 0, 0, 255);
        }
        catch { /* plain text is fine */ }

        var xp = GameLook.Img(row.Rt, "Experience", "-", Image.Type.Simple, BarBg);
        GameLook.TopLeft(xp.rectTransform, BarX, 47f, barW, 5f);
        row.XpFill = GameLook.Img(xp.rectTransform, "Fill", "-", Image.Type.Simple, XpColor);
        row.XpFillRt = row.XpFill.rectTransform;
        Fill(row.XpFillRt, 0f);

        row.Rt.gameObject.SetActive(false);
        return row;
    }

    private static readonly Dictionary<string, Sprite> Icons = new();

    /// <summary>The menu's 128 px character icons are unloaded when a run scene loads (only the playing character's
    /// bigger one stays), so one inactive Image per character holds each of them for the whole session.</summary>
    private static void KeepCharacterIcons(RectTransform parent)
    {
        var holder = GameLook.Rect(parent, "Icons");
        holder.gameObject.SetActive(false);
        foreach (var name in Enum.GetNames(typeof(CharactersType)))
        {
            var s = GameLook.CharacterIcon(name);
            if (s == null) continue;
            Icons[name] = s;
            var keep = GameLook.Rect(holder, name).gameObject.AddComponent<Image>();
            keep.sprite = s;
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][party] Party panel built, {Icons.Count} character icons kept.");
    }

    private static Sprite? IconOf(string character)
    {
        if (Icons.TryGetValue(character, out var s) && s != null) return s;
        return GameLook.CharacterIcon(character);
    }

    /// <summary>A bar's fill from its left edge, 1 px inside.</summary>
    private static void Fill(RectTransform rt, float frac)
    {
        frac = Mathf.Clamp01(frac);
        if (rt.anchorMin.x == 0f && Mathf.Approximately(rt.anchorMax.x, frac) && rt.gameObject.activeSelf == frac > 0f) return;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = new Vector2(frac, 1f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.offsetMin = new Vector2(1f, 1f);
        rt.offsetMax = new Vector2(frac >= 1f ? -1f : 0f, -1f);
        rt.gameObject.SetActive(frac > 0f);
    }

    // ---------------- tick ----------------

    internal static void Tick(float dt)
    {
        if (_failed || _root == null) return;
        try
        {
            EnsureHooked();
            _refreshTimer -= dt;
            if (_refreshTimer > 0f) return;
            _refreshTimer = 0.1f;

            bool show = ShouldShow();
            if (show) Collect();
            show &= Data.Count > 0;
            if (_root.activeSelf != show) _root.SetActive(show);
            if (!show) return;
            TakeSceneSprites();
            for (int i = 0; i < Rows.Count; i++) Apply(Rows[i], i < Data.Count ? Data[i] : null);
            if (!_shownLogged)
            {
                _shownLogged = true;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][party] Party panel shown: {Data.Count} row(s), revive icon={_revSprite?.name ?? "-"}, " +
                    $"screen {Screen.width}x{Screen.height}.");
            }
            Telemetry(dt);
        }
        catch (Exception ex)
        {
            _failed = true;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][party] Party panel disabled: {ex}");
            if (_root != null) _root.SetActive(false);
        }
    }

    private static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        Players.Left += slot =>
        {
            var r = CoopSession.Get(slot);
            // Closing the game first says "back to the menu" and the link drops a few seconds later: in the run recently is enough.
            if (r == null || !CoopSession.LocalInRun || !(r.InRun || Time.unscaledTime - LastInRun[slot] < GoneWindow)) return;
            GoneList.RemoveAll(g => g.Slot == slot);
            GoneList.Add(new Gone { Slot = slot, Name = r.Name, CharName = r.Info.CharacterName, Since = Time.unscaledTime, RunStart = CoopSession.LocalRunStartTime });
        };
        Players.Joined += slot => GoneList.RemoveAll(g => g.Slot == slot);
    }

    private static bool ShouldShow()
    {
        if (!Enabled.Value || !CoopSession.LocalInRun || CoopSession.StageTransition || GameState.LocalPlayerEntity == null) return false;
        if (DebugRows == null && Players.OtherCount == 0 && GoneList.Count == 0) return false;
        try
        {
            var ui = GameManagerUtil.GameplayUIManager;
            if (ui != null && ui.ForceHideGameplayElements) return false; // the game hides its own HUD (cutscene, boss intro)
        }
        catch { }
        return true;
    }

    private static void Collect()
    {
        Data.Clear();
        if (DebugRows != null)
        {
            for (int i = 0; i < DebugRows.Count && i < MaxRows; i++) Data.Add(DebugRows[i]);
            return;
        }
        float now = Time.unscaledTime;
        GoneList.RemoveAll(g => now - g.Since > GoneSeconds || !Mathf.Approximately(g.RunStart, CoopSession.LocalRunStartTime) || Players.IsPresent(g.Slot));

        foreach (var r in CoopSession.Remotes)
        {
            var d = new RowData { Slot = r.Slot, Name = r.Name, CharName = r.Info.CharacterName, Unstable = NetworkManager.IsUnstable(r.Slot) };
            if (r.InRun) LastInRun[r.Slot] = now;
            if (!r.InRun)
            {
                d.State = State.Away;
                d.AwayText = r.ChangingScene ? Loc.T(r.Place == LobbyPlace.Transition ? "place.transition" : "place.loading") : Loc.T("place.menu");
            }
            else
            {
                var h = Life.PartnerHealth.Of(r.Slot);
                d.HpKnown = h.Known;
                d.Hp = h.Health;
                d.MaxHp = h.MaxHealth;
                d.State = h.Dead ? State.Dead : h.Down || Life.CoopLife.IsRemoteDown(r.Slot) && !h.Known ? State.Down : State.Alive;
                if (d.State == State.Down) d.Progress = Life.CoopLife.ProgressOf(r.Slot);
                var s = Life.PartyStatus.Of(r.Slot);
                d.StatusKnown = s.Known;
                d.Level = s.Level;
                d.Xp = s.Xp;
                d.Revives = s.Revives;
            }
            Data.Add(d);
        }
        foreach (var g in GoneList)
            Data.Add(new RowData { Slot = g.Slot, Name = g.Name, CharName = g.CharName, State = State.Gone });
        Data.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        if (Data.Count > MaxRows) Data.RemoveRange(MaxRows, Data.Count - MaxRows);
    }

    private static void Apply(Row row, RowData? d)
    {
        var go = row.Rt.gameObject;
        if (d == null)
        {
            if (go.activeSelf) go.SetActive(false);
            row.Key = "";
            return;
        }
        if (!go.activeSelf) go.SetActive(true);

        bool inRun = d.State is State.Alive or State.Down or State.Dead;
        float alpha = d.State switch { State.Dead => 0.6f, State.Gone => 0.5f, State.Away => 0.65f, _ => 1f };
        if (!Mathf.Approximately(row.Group.alpha, alpha)) row.Group.alpha = alpha;

        // Texts only when something they show changed (TMP re-layouts on every assignment).
        string key = $"{d.Slot}|{d.Name}|{d.CharName}|{d.State}|{d.AwayText}|{d.Unstable}|{(d.HpKnown ? Mathf.CeilToInt(d.Hp) : -1)}|{Mathf.RoundToInt(d.MaxHp)}" +
                     $"|{(d.StatusKnown ? d.Level : -1)}|{d.Revives}|{Mathf.RoundToInt(d.Progress * 100f)}|{Loc.Version}";
        if (key != row.Key)
        {
            row.Key = key;
            var color = GhostOverhead.ColorFor(d.Slot);
            row.Strip.color = d.State is State.Gone or State.Away ? new Color(color.r, color.g, color.b, 0.4f) : color;
            var icon = IconOf(d.CharName);
            row.Icon.sprite = icon;
            row.Icon.enabled = icon != null;
            row.Icon.color = d.State is State.Dead or State.Gone ? new Color(0.45f, 0.45f, 0.45f, 1f) : Color.white;

            string hex = ColorUtility.ToHtmlStringRGB(d.State == State.Down || d.State == State.Dead ? HpLow : color);
            string unstable = d.Unstable && d.State != State.Gone ? $" <size=70%><color={Bad}>{Loc.T("st.unstable_tag")}</color></size>" : "";
            row.Name.text = $"<color=#{hex}>{d.Name}</color>{unstable}";

            row.Level.text = inRun && d.StatusKnown ? $"<color={Gold}>{Loc.T("party.level", ("n", d.Level))}</color>" : "";
            bool rev = inRun && d.StatusKnown && d.Revives > 0;
            row.RevIcon.gameObject.SetActive(rev && _revSprite != null);
            row.Revives.gameObject.SetActive(rev);
            if (rev) row.Revives.text = _revSprite != null ? $"x{d.Revives}" : $"<color={Gold}>+{d.Revives}</color>";

            row.BarText.text = d.State switch
            {
                State.Down when d.Progress > 0.005f => Loc.T("party.reviving", ("pct", Mathf.RoundToInt(d.Progress * 100f))), // white: the bar under it is gold
                State.Down => $"<color={Bad}>{Loc.T("party.down")}</color>",
                State.Dead => $"<color={Bad}>{Loc.T("party.dead")}</color>",
                State.Gone => $"<color={Muted}>{Loc.T("party.gone")}</color>",
                State.Away => $"<color={Muted}>{d.AwayText}</color>",
                _ => d.HpKnown ? $"{Mathf.CeilToInt(d.Hp)} / {Mathf.RoundToInt(d.MaxHp)}" : "-",
            };
        }

        // Bars every refresh (cheap): health, revive progress over it, XP.
        float hpFrac = d.State == State.Alive && d.HpKnown && d.MaxHp > 0f ? d.Hp / d.MaxHp : 0f;
        row.HpBg.color = d.State == State.Down ? DownBg : BarBg;
        row.HpFill.color = hpFrac > 0.5f ? HpHigh : hpFrac > 0.25f ? HpMid : HpLow;
        Fill(row.HpFillRt, hpFrac);
        Fill(row.ReviveFillRt, d.State == State.Down ? d.Progress : 0f);
        Fill(row.XpFillRt, inRun && d.StatusKnown ? d.Xp : 0f);
    }

    /// <summary>Once per run scene: the game's own pieces that only exist in a run (the death guard icon of its HUD).</summary>
    private static void TakeSceneSprites()
    {
        var ui = GameManagerUtil.GameplayUIManager;
        if (ui == null || ui.Pointer == _sceneSprites) return;
        _sceneSprites = ui.Pointer;
        try
        {
            var dg = UnityEngine.Object.FindObjectOfType<DeathGuardPanelReferences>(true);
            Sprite? best = null;
            if (dg != null)
                foreach (var img in dg.GetComponentsInChildren<Image>(true))
                {
                    var s = img != null ? img.sprite : null;
                    if (s == null) continue;
                    string n = s.name;
                    Plugin.StaticLog.LogInfo($"[SoulstoneCoop][party] Death guard panel image '{img!.name}': sprite '{n}' {s.rect.width:F0}x{s.rect.height:F0}.");
                    if (best == null && !n.Contains("Frame") && !n.Contains("Background") && !n.Contains("BG")) best = s;
                }
            if (best != null) _revSprite = best;
            foreach (var r in Rows)
            {
                r.RevIcon.sprite = _revSprite;
                r.Key = "";
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][party] Reading the run HUD's sprites: {ex.Message}"); }
    }

    private static void Telemetry(float dt)
    {
        _telemetryTimer += Mathf.Max(dt, 0.1f);
        if (_telemetryTimer < 5f) return;
        _telemetryTimer = 0f;
        var parts = new List<string>();
        foreach (var d in Data)
            parts.Add($"{d.Slot}:{d.State} hp={(d.HpKnown ? $"{d.Hp:F0}/{d.MaxHp:F0}" : "?")} lv={(d.StatusKnown ? d.Level.ToString() : "?")} xp={d.Xp:F2} rev={d.Revives}" +
                      (d.State == State.Down ? $" prog={d.Progress:F2}" : ""));
        string mine = Life.PartyStatus.ReadLocal(out int lv, out float xp, out int rv) ? $"me lv={lv} xp={xp:F2} rev={rv}" : "me ?";
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][party] {mine} | {string.Join(" | ", parts)} {Life.PartyStatus.TakeStats()}{(DebugRows != null ? " (fake rows)" : "")}");
    }
}
