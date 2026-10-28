using HarmonyLib;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Style = SoulstoneCoop.Ui.GameLook.Style;

namespace SoulstoneCoop.Ui;

/// <summary>
/// The Co-op UI (uGUI + TextMeshPro; IMGUI is partly stripped in this build). Built from the game's own pieces
/// (docs/ui-nativa-investigacao.md, docs/ui-nativa-notas.md):
/// - A "Co-op" tab at the end of the main menu's top bar, cloned from the Profile tab and registered in the menu's
///   FadeAnimationGroup, so the game slides and fades it with its own tabs. The tabs are moved half a step left to make
///   room, and the bar shrinks a little if it would reach the settings/quit icons. While the panel is open the tab is
///   drawn selected (MenuManager.AdjustSelectedButton postfix).
/// - The panel: a window in the style of the game's pop-ups (Menu_Frame_NotTransparent, Title_Frame, Close_Button) with
///   two sections (Steam invite, direct IP connection), the lobby's player list, the status and Disconnect. Its buttons
///   are game buttons (GameLook) and the IP field the newsletter form's. Esc (the game's cancel, which would open
///   "Quit game?") or the close button closes it.
/// - A banner on the same DontDestroyOnLoad canvas for notices, in the menu and in runs.
/// If the tab can't be cloned, a game-styled button in the top-right corner opens the panel instead.
/// </summary>
public static class CoopUi
{
    private const string MenuScene = "Menu-Survivors";
    private const string DefaultAddress = "127.0.0.1";
    private static string TabLabel => Loc.T("ui.tab");

    private static readonly Color DisabledTint = new(0.55f, 0.55f, 0.55f, 0.75f);
    private static readonly Color AccentColor = new(1f, 0.78f, 0.25f, 1f); // the game's highlight (#FFC700)
    private const string Gold = "#FFC700", Muted = "#BEB4A5", Bad = "#FF8080", Warn = "#FFD369", Good = "#8CE68C";
    private const float RefWidth = 1920f, RefHeight = 1080f;
    private const float WinW = 1200f, WinH = 740f;
    private const int MaxRows = 8;

    // overlay canvas
    private static GameObject? _root;
    private static TMP_FontAsset? _font;
    private static FontType _fontType = FontType.Regular; // the FightButton label's, for the per-language font
    // Texts set once at build time, redone on a language change (Loc.Version).
    private static readonly List<(TextMeshProUGUI Text, string Key)> _labels = new();
    private static int _locVersion = -1, _fontVersion = -1;
    /// <summary>The game's menu font once the UI found it (phase 5: the ghost's name tag uses it too).</summary>
    internal static TMP_FontAsset? GameFont => _font;
    private static GameObject? _fallbackButton;
    private static GameObject? _panel;          // the panel's canvas (fade + window)
    private static CanvasGroup? _panelGroup;
    private static RectTransform? _window;
    private static float _openT;                // 0 closed .. 1 open (fade/slide)
    private static TextMeshProUGUI? _status, _playersTitle;
    private static TMP_InputField? _ipField;
    private static Button? _hostButton, _joinButton, _leaveButton, _pasteButton;
    private static Button? _inviteButton, _copyCodeButton; // phase 7 (Steam)
    private static TextMeshProUGUI? _inviteLabel; // phase 9: "Convidar amigo (n/8)"
    private static readonly List<Row> _rows = new();
    private static RectTransform? _bannerRt;
    private static TextMeshProUGUI? _bannerText;

    // tab in the game's menu
    private static GameObject? _tab;          // our clone (Unity-null once the menu scene is gone)
    private static Image? _tabImage;
    private static RectTransform? _tabHolder, _tabRt, _optionsRt;
    private static float _tabRightEdge;       // holder-local x of our tab's right edge, unscaled
    private static TextMeshProUGUI? _tabText;
    private static MainMenuPanel? _menuPanel;
    private static MainMenuState _menuStateWhenOpened;
    private static float _tabRetryTimer;
    private static int _tabFailures;
    private static bool _tabLogged;

    private static bool _panelOpen;
    private static bool _loggedFocus;
    private static bool _failed;
    private static float _sceneCheckTimer;
    private static bool _inMenu;
    private static string _lastStatus = "";

    // ---------------- debug hooks (UiShot, --coop-ui-shot) ----------------
    internal enum FakeState { None, Hosting, Connected, Client, Full, Error }
    internal static FakeState DebugFake;
    internal static int DebugFakePlayers = 4;
    internal static bool MenuReady => _tab != null && _tab.activeInHierarchy && _tab.GetComponent<CanvasGroup>()?.alpha > 0.99f;
    internal static void DebugOpen(bool open) { if (_panelOpen != open) OnToggle(); }
    internal static void DebugHover(string what, bool on)
    {
        var go = what == "tab" ? _tab : what == "invite" ? _inviteButton?.gameObject : what == "join" ? _joinButton?.gameObject : null;
        if (go == null) return;
        var ped = new PointerEventData(EventSystem.current);
        var sel = go.GetComponent<Selectable>();
        if (sel != null) { if (on) sel.OnPointerEnter(ped); else sel.OnPointerExit(ped); }
    }
    /// <summary>The hover look without a pointer: the button's highlighted sprite as override.</summary>
    internal static void DebugHighlight(string what, bool on)
    {
        var go = what == "tab" ? _tab : what == "invite" ? _inviteButton?.gameObject : what == "join" ? _joinButton?.gameObject : null;
        var b = go != null ? go.GetComponent<Button>() : null;
        var img = b != null ? b.targetGraphic?.TryCast<Image>() : null;
        if (img != null) img.overrideSprite = on ? b!.spriteState.highlightedSprite : null;
    }
    internal static void DebugSetIp(string text) { if (_ipField != null) { _ipField.text = text; _ipField.caretPosition = text.Length; } }
    internal static void DebugFocusIp() { if (_ipField != null) { _ipField.Select(); _ipField.ActivateInputField(); } }

    /// <summary>The IP field has keyboard focus: debug hotkeys (Insert/Delete/PageDown) must not fire.</summary>
    public static bool IsTyping => _ipField != null && _ipField.isFocused;

    public static void Tick()
    {
        if (_failed) return;
        try
        {
            try
            {
                var found = UpdateCheck.Poll();
                if (found != null) _update = found;
            }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][update] {ex.Message}"); }
            _sceneCheckTimer -= Time.unscaledDeltaTime;
            if (_sceneCheckTimer <= 0f)
            {
                _sceneCheckTimer = 0.5f;
                _inMenu = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == MenuScene;
            }
            if (_root == null)
            {
                if (!_inMenu) return; // build in the menu, where the game's font and sprites are around
                Build();
                if (_root == null) return;
            }

            if (_locVersion != Loc.Version) ApplyLanguage();
            if (_inMenu) UpdateMenuTab();
            else _panelOpen = false;

            bool tabOk = _tab != null;
            _fallbackButton!.SetActive(_inMenu && !tabOk && _tabFailures >= 3);
            AnimatePanel(_inMenu && _panelOpen);
            if (_panel!.activeSelf) RefreshPanel();
            if (!_loggedFocus && _ipField != null && _ipField.isFocused)
            {
                _loggedFocus = true;
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][ui] IP field got keyboard focus.");
            }
            RefreshBanner();
            TickUpdatePopup();
        }
        catch (Exception ex)
        {
            _failed = true;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][ui] Co-op UI disabled: {ex}");
        }
    }

    /// <summary>Fade and slide like the game's pop-ups (0.15 s).</summary>
    private static void AnimatePanel(bool open)
    {
        float target = open ? 1f : 0f;
        if (_openT == target && _panel!.activeSelf == open) return;
        _openT = Mathf.MoveTowards(_openT, target, Time.unscaledDeltaTime / 0.15f);
        bool show = _openT > 0f;
        if (_panel!.activeSelf != show) _panel.SetActive(show);
        if (_panelGroup != null)
        {
            _panelGroup.alpha = _openT;
            _panelGroup.interactable = open;
            _panelGroup.blocksRaycasts = open;
        }
        if (_window != null)
        {
            float e = 1f - (1f - _openT) * (1f - _openT);
            _window.anchoredPosition = new Vector2(0f, WindowY - (1f - e) * 40f);
        }
    }

    // ---------------- the tab in the game's top menu ----------------

    private static void UpdateMenuTab()
    {
        if (_tab == null)
        {
            _tabRetryTimer -= Time.unscaledDeltaTime;
            if (_tabRetryTimer > 0f || _tabFailures >= 3) return;
            _tabRetryTimer = 1f;
            if (!TryCreateMenuTab()) _tabFailures++;
            return;
        }

        // The game shows, hides, slides and fades the tab (it's in its animation group): just follow.
        if (!_tab.activeInHierarchy) _panelOpen = false;
        FitTabBar();
        if (!_tabLogged && MenuReady)
        {
            _tabLogged = true;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Co-op tab visible: pos={_tabRt!.anchoredPosition} size={_tabRt.rect.size} " +
                $"bar scale={_tabHolder!.localScale.x:F3} (screen {Screen.width}x{Screen.height}).");
        }
        if (_tabText != null && _tabText.text != TabLabel) _tabText.text = TabLabel;

        // Another tab picked in the game's menu: get out of the way.
        if (_panelOpen && _menuPanel != null && _menuPanel.CurrentState != _menuStateWhenOpened) _panelOpen = false;
    }

    /// <summary>With 8 tabs the bar is 195 px wider: shrink it (not the game's icons) if its right end would reach the
    /// settings button on this aspect ratio (16:9 at 1080 px of height: about 1%).</summary>
    private static void FitTabBar()
    {
        if (_tabHolder == null || _menuPanel == null) return;
        var panelRt = _menuPanel.GetComponent<RectTransform>();
        float half = panelRt.rect.width / 2f;
        float limit = _optionsRt != null ? half + _optionsRt.anchoredPosition.x - _optionsRt.rect.width / 2f - 6f : half - 190f;
        float s = _tabRightEdge > 1f ? Mathf.Clamp(limit / _tabRightEdge, 0.6f, 1f) : 1f;
        if (Mathf.Abs(_tabHolder.localScale.x - s) > 0.001f) _tabHolder.localScale = new Vector3(s, s, 1f);
    }

    private static bool TryCreateMenuTab()
    {
        try
        {
            var mm = UnityEngine.Object.FindObjectOfType<MenuManager>();
            var panel = mm != null ? mm.MainMenuPanel : null;
            var src = panel != null && panel.HistoryButton != null ? panel.HistoryButton.gameObject : null;
            if (src == null) return false;
            var holder = src.transform.parent;
            var existing = holder.Find("Coop-Button");
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject); // half-made by a failed try

            var clone = UnityEngine.Object.Instantiate(src, holder);
            clone.name = "Coop-Button";
            clone.transform.SetAsLastSibling();
            // Keeps: Image + Button (sprite swap), UIButtonSound (click), EventTrigger (hover sound via
            // UIButtonSound.onSelect, retargeted to the clone's own), CanvasGroup and the slide/fade/enable animations.
            foreach (var l in clone.GetComponentsInChildren<LocalizedMonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(l);

            var button = clone.GetComponent<Button>();
            if (button == null) { UnityEngine.Object.Destroy(clone); return false; }
            button.onClick = new Button.ButtonClickedEvent(); // drops the persistent OnHistoryPressed
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            AddClick(button, OnToggle, "tab");

            // The holder's HorizontalLayoutGroup isn't what places the tabs at run time (their slide-in animation
            // is), and adding a child would make it re-place them all with top-left anchors: off.
            var hlg = holder.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null) hlg.enabled = false;
            var fitter = holder.GetComponent<ContentSizeFitter>();
            if (fitter != null) fitter.enabled = false;

            // Into the menu's own animation group, so the game slides it in and fades it with the other tabs.
            var group = panel!.FadeAnimationGroup;
            var mine = clone.GetComponents<GameSmithing.UIAnimation>();
            if (group != null && mine.Length > 0)
            {
                var old = group.Animations;
                int n = old?.Length ?? 0;
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameSmithing.UIAnimation>(n + mine.Length);
                for (int i = 0; i < n; i++) arr[i] = old![i];
                for (int i = 0; i < mine.Length; i++) arr[n + i] = mine[i];
                group.Animations = arr;
            }

            // Room for one more: every tab half a step left, ours one step right of Profile.
            var srcAnim = src.GetComponent<GameSmithing.RectTransformAnchoredPositionAnimation>();
            float step = 195f;
            int idx = src.transform.GetSiblingIndex();
            if (srcAnim != null && idx > 0)
            {
                var prevAnim = holder.GetChild(idx - 1).GetComponent<GameSmithing.RectTransformAnchoredPositionAnimation>();
                if (prevAnim != null)
                {
                    float d = srcAnim.TargetAnchoredPosition.x - prevAnim.TargetAnchoredPosition.x;
                    if (d > 1f) step = d;
                }
            }
            var shift = new Vector2(-step / 2f, 0f);
            for (int i = 0; i < holder.childCount; i++)
            {
                var ch = holder.GetChild(i);
                if (ch.gameObject == clone) continue;
                var a = ch.GetComponent<GameSmithing.RectTransformAnchoredPositionAnimation>();
                if (a == null) continue;
                a.StartAnchoredPosition += shift;
                a.TargetAnchoredPosition += shift;
                var crt = ch.GetComponent<RectTransform>();
                crt.anchoredPosition += shift;
            }
            var right = new Vector2(step, 0f);
            var cAnim = clone.GetComponent<GameSmithing.RectTransformAnchoredPositionAnimation>();
            if (cAnim != null && srcAnim != null)
            {
                cAnim.StartAnchoredPosition = srcAnim.StartAnchoredPosition + right;
                cAnim.TargetAnchoredPosition = srcAnim.TargetAnchoredPosition + right;
            }
            _tabRt = clone.GetComponent<RectTransform>();
            _tabRt.anchoredPosition = src.GetComponent<RectTransform>().anchoredPosition + right;
            _tabRightEdge = (cAnim != null ? cAnim.TargetAnchoredPosition.x : _tabRt.anchoredPosition.x) + _tabRt.rect.width / 2f;
            _tabHolder = holder.GetComponent<RectTransform>();
            _optionsRt = panel.transform.Find("Options-Button")?.GetComponent<RectTransform>();

            _tabText = clone.GetComponentInChildren<TextMeshProUGUI>(true);
            if (_tabText != null) _tabText.text = TabLabel;
            _tabImage = clone.GetComponent<Image>();
            _menuPanel = panel;
            _tab = clone;
            _tabLogged = false;
            clone.SetActive(src.activeSelf);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Co-op tab added to the main menu ({holder.name}, step {step}, {mine.Length} animations in the menu's group).");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ui] Could not add the Co-op tab: {ex}");
            return false;
        }
    }

    /// <summary>MenuManager.AdjustSelectedButton (every frame) gives the current state's tab the selected sprite and
    /// the others the regular one. Ours: selected while the panel is open, and then it's the only one.</summary>
    internal static void AfterAdjustSelectedButton(MainMenuPanel panel)
    {
        if (_tabImage == null || panel == null) return;
        var regular = panel.ButtonRegularSprite;
        bool open = _panelOpen && _inMenu;
        if (open)
        {
            foreach (var img in new[] { panel.FightButton, panel.CharacterButton, panel.CraftingButton, panel.TalentsButton,
                         panel.SkillTreeButton, panel.AchievementsButton, panel.HistoryButton })
                if (img != null && img.sprite != regular) img.sprite = regular;
        }
        var want = open ? panel.ButtonSelectedSprite : regular;
        if (want != null && _tabImage.sprite != want) _tabImage.sprite = want;
    }

    /// <summary>The game's cancel input (Esc, gamepad B) or its quit button asks for "Quit game?": with the panel
    /// open, close the panel instead. True = let the game open its quit panel.</summary>
    internal static bool BeforeQuitPanelOpen()
    {
        if (_updateOpen)
        {
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][update] Cancel pressed: not now.");
            CloseUpdate();
            return false;
        }
        if (!_inMenu || !_panelOpen) return true;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][ui] Cancel pressed: closing the Co-op panel.");
        _panelOpen = false;
        return false;
    }

    // ---------------- state -> widgets ----------------

    private sealed class Row
    {
        public RectTransform Rt = null!;
        public Image Bg = null!, Frame = null!, Icon = null!;
        public TextMeshProUGUI Name = null!, Sub = null!, Place = null!, Ping = null!;
        public string Key = "";
    }

    private struct RowData
    {
        public bool Empty, IsHost, IsYou, Unstable;
        public string Name, CharName;
        public int Ping;
        public LobbyPlace Place;
    }

    private static float _localInfoTimer;

    private static void RefreshPanel()
    {
        var role = NetworkManager.Role;
        // CoopSession only re-reads our name and character while connected: the list shows them before that too.
        _localInfoTimer -= Time.unscaledDeltaTime;
        if (role == NetRole.None && _localInfoTimer <= 0f)
        {
            _localInfoTimer = 2f;
            CoopSession.RefreshLocalInfo();
        }
        var status = NetworkManager.Status;
        bool steam = NetworkManager.Kind == "Steam";
        var lobbyState = Steam.SteamLobby.State;
        string text;
        if (role == NetRole.None)
        {
            string? error = NetworkManager.LastError ?? Steam.SteamLobby.LastError;
            if (lobbyState == Steam.SteamLobby.LobbyState.Creating) text = Loc.T("st.creating_lobby");
            else if (lobbyState == Steam.SteamLobby.LobbyState.Joining) text = Loc.T("st.joining_lobby");
            else if (error != null) text = $"<color={Bad}>{Loc.Show(error)}</color>";
            else text = Loc.T(Steam.SteamLobby.Available ? "st.idle" : "st.idle_no_steam") + $"\n<color={Muted}>{Loc.T("ui.alone")}</color>";
        }
        else if (status == NetStatus.Hosting)
            text = steam
                ? Loc.T("st.lobby_open", ("max", NetworkManager.MaxPlayers))
                : Loc.T("st.hosting_port", ("port", NetworkManager.Port), ("max", NetworkManager.MaxPlayers));
        else if (status == NetStatus.Connecting)
            text = steam
                ? Loc.T("st.connecting_steam", ("name", Steam.SteamLobby.FriendName(Steam.SteamLobby.HostId)))
                : Loc.T("st.connecting_ip", ("addr", $"{NetworkManager.TargetAddress}:{NetworkManager.Port}"));
        else if (status == NetStatus.Connected)
        {
            if (role == NetRole.Client)
            {
                var h = CoopSession.Host;
                int ping = NetworkManager.PingMs;
                text = Loc.T("st.connected", ("name", $"<color={Gold}>{h?.Name ?? Loc.T("name.host")}</color>"),
                    ("link", $"{NetworkManager.Kind}{(ping >= 0 ? $", {ping} ms" : "")}"));
                if (NetworkManager.Unstable)
                    text += $"\n<color={Bad}>{Loc.T("st.unstable", ("s", NetworkManager.SilenceSeconds.ToString("0")))}</color>";
                text += "\n" + Loc.T(h == null || h.Place == LobbyPlace.Menu ? "st.wait_host" : "st.host_in_run");
            }
            else
            {
                text = Loc.T("st.hosting", ("kind", NetworkManager.Kind), ("n", Players.Count), ("max", NetworkManager.MaxPlayers));
                bool allInMenu = true;
                foreach (var r in CoopSession.Remotes) allInMenu &= r.Place == LobbyPlace.Menu;
                text += "\n" + Loc.T(allInMenu ? "st.all_in_menu" : "st.wait_all_menu");
            }
        }
        else text = "...";
        if (role == NetRole.Host && NetworkManager.LastError != null && status == NetStatus.Hosting)
            text += $"\n<color={Bad}>{Loc.Show(NetworkManager.LastError)}</color>";
        if (role == NetRole.Host && steam && Steam.SteamLobby.LastError != null)
            text += $"\n<color={Bad}>{Loc.Show(Steam.SteamLobby.LastError)}</color>";
        if (NetSim.Enabled) text += $"\n<color=#ffb060>{Loc.T("st.netsim", ("desc", NetSim.Describe(local: true)))}</color>";

        bool busy = lobbyState == Steam.SteamLobby.LobbyState.Creating || lobbyState == Steam.SteamLobby.LobbyState.Joining;
        bool idle = role == NetRole.None && !busy;
        bool steamHosting = role == NetRole.Host && steam;
        // Phase 9: anyone in the Steam lobby can invite its own friends, while there's a free seat.
        bool steamMember = steamHosting || (role == NetRole.Client && steam && Steam.SteamLobby.State == Steam.SteamLobby.LobbyState.Joined);
        int count = Players.Count, max = Players.SessionMax;
        var rows = RealRows(role);
        if (DebugFake != FakeState.None) (text, rows, idle, steamMember, count, max) = Fake(DebugFake);

        if (text != _lastStatus)
        {
            _lastStatus = text;
            _status!.text = text;
        }
        bool full = count >= max;
        SetInteractable(_inviteButton!, (Steam.SteamLobby.Available || DebugFake != FakeState.None) && (idle || (steamMember && !full)));
        SetInteractable(_copyCodeButton!, steamMember && (Steam.SteamLobby.LobbyId != 0 || DebugFake != FakeState.None));
        string inviteLabel = steamMember ? Loc.T("ui.invite_count", ("n", count), ("max", max)) : Loc.T("ui.invite");
        if (_inviteLabel != null && _inviteLabel.text != inviteLabel) _inviteLabel.text = inviteLabel;
        SetInteractable(_hostButton!, idle);
        SetInteractable(_joinButton!, idle);
        SetInteractable(_pasteButton!, idle);
        SetInteractable(_leaveButton!, !idle);
        if (_leaveButton!.gameObject.activeSelf == idle) _leaveButton.gameObject.SetActive(!idle); // nothing to leave
        _ipField!.interactable = idle;

        // Player list: everyone in the session, then the free seats.
        int shown = 0;
        foreach (var r in rows) if (!r.Empty) shown++;
        int seats = idle ? NetworkManager.MaxPlayers : max;
        string title = Loc.T("ui.players", ("n", shown), ("max", seats));
        if (_playersTitle!.text != title) _playersTitle.text = title;
        for (int i = 0; i < _rows.Count; i++) FillRow(_rows[i], i < rows.Count ? rows[i] : (RowData?)null);
    }

    private static List<RowData> RealRows(NetRole role)
    {
        var list = new List<RowData>();
        var me = CoopSession.Local;
        var mine = new RowData
        {
            IsYou = true,
            IsHost = role == NetRole.Host,
            Name = me.Name,
            CharName = me.CharacterName,
            Ping = -1,
            Place = CoopSession.LocalPlace,
        };
        var others = new List<RowData>();
        foreach (var r in CoopSession.Remotes)
        {
            others.Add(new RowData
            {
                IsHost = r.IsHost,
                Name = r.Name,
                CharName = r.Info.Known ? r.Info.CharacterName : "",
                Ping = role == NetRole.Host ? NetworkManager.PingOf(r.Slot) : r.IsHost ? NetworkManager.PingMs : -1,
                Unstable = role == NetRole.Host ? NetworkManager.IsUnstable(r.Slot) : r.IsHost && NetworkManager.Unstable,
                Place = r.Place,
            });
        }
        // Host first, then by slot (CoopSession.Remotes is in slot order; ours is Players.MySlot).
        foreach (var o in others) if (o.IsHost) list.Add(o);
        if (mine.IsHost) list.Insert(0, mine);
        int mySlot = Players.MySlot;
        bool placed = mine.IsHost;
        int k = 0;
        foreach (var r in CoopSession.Remotes)
        {
            var o = others[k++];
            if (o.IsHost) continue;
            if (!placed && r.Slot > mySlot) { list.Add(mine); placed = true; }
            list.Add(o);
        }
        if (!placed) list.Add(mine);
        if (role != NetRole.None)
            for (int i = list.Count; i < Math.Min(Players.SessionMax, MaxRows); i++) list.Add(new RowData { Empty = true });
        return list;
    }

    private static void FillRow(Row row, RowData? data)
    {
        bool on = data != null;
        if (row.Rt.gameObject.activeSelf != on) row.Rt.gameObject.SetActive(on);
        if (!on) return;
        var d = data!.Value;
        string key = $"{d.Empty}|{d.IsHost}|{d.IsYou}|{d.Unstable}|{d.Name}|{d.CharName}|{d.Ping}|{d.Place}|{Loc.Version}";
        if (key == row.Key) return;
        row.Key = key;

        row.Bg.color = d.Empty ? new Color(1f, 1f, 1f, 0.4f) : d.IsHost ? new Color(1f, 0.9f, 0.62f, 1f) : Color.white;
        row.Frame.color = d.Empty ? new Color(1f, 1f, 1f, 0.35f) : Color.white;
        var icon = d.Empty ? null : GameLook.CharacterIcon(d.CharName);
        row.Icon.sprite = icon;
        row.Icon.enabled = icon != null;
        if (d.Empty)
        {
            row.Name.text = $"<color={Muted}>{Loc.T("ui.empty_slot")}</color>";
            row.Sub.text = "";
            row.Place.text = "";
            row.Ping.text = "";
            return;
        }
        string you = d.IsYou && d.Name.Length > 0 ? $" <size=75%><color={Muted}>({Loc.T("ui.you")})</color></size>" : "";
        if (d.Name.Length == 0) d.Name = d.IsYou ? Loc.T("ui.you") : "?";
        row.Name.text = d.IsHost ? $"<color={Gold}>{d.Name}</color>{you}" : d.Name + you;
        string ch = GameLook.CharacterDisplayName(d.CharName);
        string host = d.IsHost ? $"<color={Gold}>{Loc.T("ui.host_tag")}</color>" : "";
        row.Sub.text = host.Length > 0 && ch.Length > 0 ? $"{host} <color={Muted}>· {ch}</color>" : host + $"<color={Muted}>{ch}</color>";
        string place = PlaceLabel(d.Place);
        row.Place.text = d.Unstable ? $"<color={Bad}>{Loc.T("st.unstable_tag")}</color>"
            : d.Place == LobbyPlace.Menu ? place : $"<color={Warn}>{place}</color>";
        row.Ping.text = d.IsYou || d.Ping < 0 ? $"<color={Muted}>-</color>"
            : $"<color={(d.Ping < 80 ? Good : d.Ping < 150 ? Warn : Bad)}>{d.Ping} ms</color>";
    }

    private static (string, List<RowData>, bool, bool, int, int) Fake(FakeState s)
    {
        RowData P(string name, string ch, bool host, bool you, int ping, LobbyPlace place, bool unstable = false) =>
            new() { Name = name, CharName = ch, IsHost = host, IsYou = you, Ping = ping, Place = place, Unstable = unstable };
        var rows = new List<RowData>();
        string me = CoopSession.Local.Name.Length > 0 ? CoopSession.Local.Name : "Kolber";
        string myChar = CoopSession.Local.CharacterName.Length > 0 ? CoopSession.Local.CharacterName : "MonkeyKing";
        void Pad(int max) { for (int i = rows.Count; i < max; i++) rows.Add(new RowData { Empty = true }); }
        switch (s)
        {
            case FakeState.Hosting:
                rows.Add(P(me, myChar, true, true, -1, LobbyPlace.Menu)); Pad(8);
                return (Loc.T("st.lobby_open", ("max", 8)), rows, false, true, 1, 8);
            case FakeState.Connected:
            {
                // DebugFakePlayers: how many have joined so far (the demo video adds them one by one).
                var all = new[]
                {
                    P(me, myChar, true, true, -1, LobbyPlace.Menu),
                    P("Amigo", "Barbarian", false, false, 42, LobbyPlace.Menu),
                    P("Outra pessoa", "Pyromancer", false, false, 118, LobbyPlace.Loading),
                    P("Longe", "Necromancer", false, false, 260, LobbyPlace.InRun, unstable: true),
                    P("Marina", "Paladin", false, false, 35, LobbyPlace.Menu),
                };
                int n = Mathf.Clamp(DebugFakePlayers, 1, all.Length);
                for (int i = 0; i < n; i++) rows.Add(all[i]);
                Pad(8);
                bool allMenu = true;
                foreach (var r in rows) allMenu &= r.Empty || r.Place == LobbyPlace.Menu;
                return (Loc.T("st.hosting", ("kind", "Steam"), ("n", n), ("max", 8)) + "\n" + Loc.T(allMenu ? "st.all_in_menu" : "st.wait_all_menu"), rows, false, true, n, 8);
            }
            case FakeState.Client:
                rows.Add(P("Anfitrião", "Paladin", true, false, 64, LobbyPlace.Menu));
                rows.Add(P(me, myChar, false, true, -1, LobbyPlace.Menu));
                Pad(4);
                return (Loc.T("st.connected", ("name", $"<color={Gold}>Anfitrião</color>"), ("link", "Steam, 64 ms")) + "\n" + Loc.T("st.wait_host"), rows, false, true, 2, 4);
            case FakeState.Full:
                string[] chars = { "Barbarian", "Pyromancer", "Necromancer", "Paladin", "Rogue", "Samurai", "Pirate" };
                rows.Add(P(me, myChar, true, true, -1, LobbyPlace.Menu));
                for (int i = 0; i < 7; i++) rows.Add(P($"Jogador {i + 2}", chars[i], false, false, 30 + i * 30, LobbyPlace.Menu));
                return (Loc.T("st.hosting", ("kind", "Steam"), ("n", 8), ("max", 8)) + "\n" + Loc.T("st.all_in_menu"), rows, false, true, 8, 8);
            default: // Error
                rows.Add(P(me, myChar, false, true, -1, LobbyPlace.Menu));
                return ($"<color={Bad}>{Loc.T("st.connecting_ip", ("addr", "10.0.0.5:7777"))} Timeout.</color>", rows, true, false, 1, 8);
        }
    }

    private static string PlaceLabel(LobbyPlace p) => p switch
    {
        LobbyPlace.Menu => Loc.T("place.menu"),
        LobbyPlace.Loading => Loc.T("place.loading"),
        LobbyPlace.InRun => Loc.T("place.in_run"),
        LobbyPlace.Transition => Loc.T("place.transition"),
        _ => "?",
    };

    private static void RefreshBanner()
    {
        string? text;
        string names;
        // Phase 8: silence well before the transport gives up (10 s IP, 15 s Steam). Not while someone is loading
        // a run: a scene load freezes the game, and the Steam pings with it.
        if (NetworkManager.Unstable && !CoopSession.LocalLoading && !CoopSession.AnyRemoteChangingScene)
        {
            names = NetworkManager.Role == NetRole.Host
                ? CoopSession.NamesWhere(r => NetworkManager.IsUnstable(r.Slot))
                : CoopSession.NameOf(Players.HostSlot);
            text = Loc.T("bn.unstable", ("names", names), ("s", NetworkManager.SilenceSeconds.ToString("0")));
        }
        else if (RunSync.HoldForPartner) text = Loc.T("bn.loading", ("names", CoopSession.NamesWhere(r => r.ChangingScene)));
        else if ((names = CoopSession.NamesWhere(r => PauseSync.Freezes(r.Slot) && PauseSync.ReasonOf(r.Slot) == PauseReason.Pause)).Length > 0)
            text = Loc.T(NetworkManager.Role == NetRole.Host ? "bn.paused_host" : "bn.paused", ("names", names));
        else if ((names = CoopSession.NamesWhere(r => PauseSync.IsChoosing(r.Slot))).Length > 0)
            text = Loc.T("bn.choosing", ("names", names));
        else if (LevelUpOverlay.BannerText != null) text = LevelUpOverlay.BannerText;
        else if (CoopSession.ActiveNotice != null) text = CoopSession.ActiveNotice;
        else if ((names = CoopSession.NamesWhere(r => PauseSync.IsPausedAlone(r.Slot))).Length > 0)
            text = Loc.T("bn.pause_menu", ("names", names));
        else text = null;

        bool show = text != null;
        var go = _bannerRt!.gameObject;
        if (go.activeSelf != show) go.SetActive(show);
        if (!show) return;
        if (_bannerText!.text != text) _bannerText.text = text;
        // Under the panel's window when it's open, under the currency bar in the menu, near the top in a run.
        bool low = _inMenu && _panelOpen;
        var anchor = new Vector2(0.5f, low ? 0f : 1f);
        float y = low ? 24f : _inMenu ? -165f : -140f;
        // Issue #5: the level-up panel at the top would sit on the banner (the pad hint): the banner goes under it.
        if (!_inMenu && LevelUpOverlay.PanelScreenSpan is Vector2 span && Screen.height > 0)
        {
            float k = 1080f / Screen.height; // this canvas scales with the height (ref 1080)
            float panelTop = (Screen.height - span.y) * k, panelBottom = (Screen.height - span.x) * k;
            if (panelTop < -y + _bannerRt.rect.height && panelBottom > -y) y = -(panelBottom + 8f);
        }
        if (_bannerRt.anchorMin != anchor)
        {
            _bannerRt.anchorMin = anchor;
            _bannerRt.anchorMax = anchor;
            _bannerRt.pivot = anchor;
        }
        if (Mathf.Abs(_bannerRt.anchoredPosition.y - y) > 0.5f) _bannerRt.anchoredPosition = new Vector2(0f, y);
    }

    private static void SetInteractable(Button b, bool on)
    {
        if (b.interactable == on) return;
        b.interactable = on;
        var img = b.targetGraphic;
        if (img != null) img.color = on ? Color.white : DisabledTint;
        var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
        if (t != null) t.alpha = on ? 1f : 0.55f;
    }

    // ---------------- actions ----------------

    private static void OnToggle()
    {
        _panelOpen = !_panelOpen;
        if (_panelOpen && _menuPanel != null) _menuStateWhenOpened = _menuPanel.CurrentState;
        if (_panelOpen)
        {
            bool flag = false;
            try { flag = Global.ForceCleanEventSystemSelectedObjectKeyboard; } catch { }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Panel opened (ForceCleanEventSystemSelectedObjectKeyboard={flag}).");
        }
    }

    private static void OnHost()
    {
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][ui] Host pressed.");
        NetworkManager.StartHost();
    }

    private static void OnJoin()
    {
        string addr = _ipField != null ? _ipField.text.Trim() : "";
        if (addr.Length == 0) addr = DefaultAddress;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Join pressed: {addr}");
        try { Plugin.LastAddress.Value = addr; } catch { }
        // Phase 7: a long number is a Steam lobby code ("Copiar código" on the host), anything else an IP.
        if (Steam.SteamLobby.TryParseCode(addr, out var lobby)) Steam.SteamLobby.Join(lobby);
        else NetworkManager.StartClient(addr);
    }

    private static void OnInvite()
    {
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][ui] Invite pressed.");
        Steam.SteamLobby.HostAndInvite();
    }

    private static void OnCopyCode()
    {
        ulong lobby = Steam.SteamLobby.LobbyId;
        if (lobby == 0) return;
        bool ok = WindowsClipboard.TrySetText(lobby.ToString());
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Copy code pressed: {lobby} (clipboard {(ok ? "ok" : "failed")}).");
        CoopSession.Notice(Loc.Msg(ok ? "nt.code_copied" : "nt.copy_failed"), 5f);
    }

    private static void OnPaste()
    {
        string? clip = WindowsClipboard.TryGetText();
        string text = clip?.Trim() ?? "";
        // Only the first line / word: an address, not a whole paragraph.
        int cut = text.IndexOfAny(new[] { '\r', '\n', ' ', '\t' });
        if (cut > 0) text = text.Substring(0, cut);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Paste pressed: '{text}' (clipboard {(clip == null ? "unavailable" : $"{clip.Length} chars")}).");
        if (text.Length == 0) { CoopSession.Notice(Loc.Msg("nt.clipboard_empty")); return; }
        if (text.Length > 64) text = text.Substring(0, 64);
        _ipField!.text = text;
    }

    /// <summary>GameLoop.Update clears the EventSystem selection every frame while
    /// Global.ForceCleanEventSystemSelectedObjectKeyboard is set and the input is keyboard/mouse, which kept the IP
    /// field from ever keeping focus (third test). While the panel is open, GameLoopKeepSelectionPatch turns the flag
    /// off for that call only.</summary>
    internal static bool KeepSelection => _inMenu && _panelOpen && _ipField != null && _panel != null && _panel.activeSelf;

    private static void OnLeave()
    {
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][ui] Disconnect pressed.");
        NetworkManager.Stop();
        Steam.SteamLobby.Leave(); // also cancels a lobby still being created or joined
    }

    private static void OnClose() => _panelOpen = false;

    // ---------------- build ----------------

    // The window's center, below the top bar and the currency bar.
    private const float WindowY = -40f;

    private static void Build()
    {
        FindGameLook();
        if (_font == null)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][ui] No TMP font found yet - retrying.");
            return;
        }
        GameLook.Font = _font;

        var root = new GameObject("SoulstoneCoop_UI");
        UnityEngine.Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;
        var scaler = root.AddComponent<CanvasScaler>();
        // Same scaling as the game's menu canvases: 1080 units of height, the width follows the aspect ratio.
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(RefWidth, RefHeight);
        scaler.matchWidthOrHeight = 1f;
        root.AddComponent<GraphicRaycaster>();
        var rootRt = root.GetComponent<RectTransform>();

        _labels.Clear();
        _rows.Clear();
        _fontVersion = Loc.Version;

        // Fallback opener, top right (only if the tab can't be added).
        var open = Track(GameLook.Button(rootRt, "CoopButton", Style.Primary, TabLabel, OnToggle, 34f, "UIHighlight"), "ui.tab");
        Place(open.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(-220f, -24f), new Vector2(240f, 50f));
        _fallbackButton = open.gameObject;
        _fallbackButton.SetActive(false);

        BuildPanel(rootRt);

        // Banner (menu and runs): a strip of the game's transparent menu frame.
        _bannerRt = GameLook.Img(rootRt, "CoopBanner", "Menu_Frame_Transparent", null, new Color(0f, 0f, 0f, 0.7f)).rectTransform;
        Place(_bannerRt, new Vector2(0.5f, 1f), new Vector2(0f, -140f), new Vector2(860f, 74f));
        _bannerText = GameLook.Text(_bannerRt, "Text", "", 32f, TextAlignmentOptions.Center);
        GameLook.AutoSize(_bannerText, 32f);
        GameLook.Stretch(_bannerText.rectTransform, 28f, 8f, 28f, 8f);
        _bannerRt.gameObject.SetActive(false);

        _root = root;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Co-op UI built (font {_font.name} [{_fontType}], language {Loc.Codes[(int)Loc.Current]}, " +
            $"frame={GameLook.Sprite("Menu_Frame_NotTransparent")?.name ?? "-"}, button={GameLook.Sprite("Fight_Button1")?.name ?? "-"}, " +
            $"input={(_ipField != null && _ipField.name == "Address" && _ipField.GetComponent<Image>()?.sprite != null ? "game" : "own")}).");
        Loc.CheckGlyphs(_font, "co-op UI");
    }

    // ---------------- "new version" pop-up (1.0, Ui/UpdateCheck.cs) ----------------

    private static UpdateCheck.Release? _update;
    private static bool _updateOpen, _updateDone;
    private static float _menuIdle, _updT;
    private static GameObject? _updPopup;
    private static CanvasGroup? _updGroup;
    private static RectTransform? _updWindow;
    private static TextMeshProUGUI? _updBody;
    internal static bool UpdatePopupOpen => _updateOpen;
    internal static void DebugCloseUpdate() => CloseUpdate();

    /// <summary>Shown once per launch, the first time the main menu is idle for a second: not in a run or while
    /// loading one, not over the Co-op panel (it waits for it to close).</summary>
    private static void TickUpdatePopup()
    {
        bool idle = _inMenu && MenuReady && !_panelOpen && _openT <= 0f && !CoopSession.LocalInRun && !CoopSession.LocalLoading;
        _menuIdle = idle ? _menuIdle + Time.unscaledDeltaTime : 0f;
        if (_update != null && !_updateDone && !_updateOpen && _menuIdle > 1f) OpenUpdate();
        if (_updateOpen && (!_inMenu || CoopSession.LocalLoading)) CloseUpdate();

        if (_updPopup == null) return;
        float target = _updateOpen ? 1f : 0f;
        if (_updT == target) return;
        _updT = Mathf.MoveTowards(_updT, target, Time.unscaledDeltaTime / 0.15f);
        if (_updPopup.activeSelf != _updT > 0f) _updPopup.SetActive(_updT > 0f);
        _updGroup!.alpha = _updT;
        _updGroup.interactable = _updateOpen;
        _updGroup.blocksRaycasts = _updateOpen;
        float e = 1f - (1f - _updT) * (1f - _updT);
        _updWindow!.anchoredPosition = new Vector2(0f, -(1f - e) * 40f);
    }

    private static void OpenUpdate()
    {
        if (_updPopup == null) BuildUpdatePopup(_root!.GetComponent<RectTransform>());
        _updateOpen = true;
        _updateDone = true;
        SetUpdateBody();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][update] Showing the new-version pop-up ({_update!.Tag}).");
    }

    private static void CloseUpdate() => _updateOpen = false;

    private static void SetUpdateBody()
    {
        if (_updBody == null || _update == null) return;
        _updBody.text = Loc.T("up.body", ("latest", $"<color={Gold}>{_update.Version}</color>"), ("current", Plugin.PluginVersion));
    }

    private static void OnUpdateOpenPage()
    {
        string url = _update?.Url ?? "";
        if (!url.StartsWith(UpdateCheck.RepoUrl, StringComparison.Ordinal)) url = UpdateCheck.RepoUrl + "releases/latest";
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][update] Open page: {url}");
        Application.OpenURL(url);
        CloseUpdate();
    }

    private static void OnUpdateSkip()
    {
        if (_update != null) UpdateCheck.Skip(_update);
        CloseUpdate();
    }

    private static void BuildUpdatePopup(RectTransform rootRt)
    {
        const float W = 1000f, H = 420f;
        var pr = GameLook.Rect(rootRt, "CoopUpdate");
        GameLook.Stretch(pr, 0f, 0f, 0f, 0f);
        var pc = pr.gameObject.AddComponent<Canvas>();
        pc.overrideSorting = true;
        pc.sortingOrder = 501; // over the Co-op panel, under the game's Quit (502) and Options (505)
        pr.gameObject.AddComponent<GraphicRaycaster>();
        _updGroup = pr.gameObject.AddComponent<CanvasGroup>();
        _updGroup.alpha = 0f;
        _updPopup = pr.gameObject;

        var fade = GameLook.Rect(pr, "Black-Fade");
        GameLook.Stretch(fade, 0f, 0f, 0f, 0f);
        fade.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.82f);

        var win = GameLook.Img(pr, "Window", "Menu_Frame_NotTransparent").rectTransform;
        win.GetComponent<Image>().raycastTarget = true;
        Place(win, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W, H));
        win.pivot = new Vector2(0.5f, 0.5f);
        _updWindow = win;

        var glow = GameLook.Img(win, "Glow", "Yellow_Glow_01", Image.Type.Simple, Color.clear).rectTransform;
        glow.anchorMin = new Vector2(0f, 1f);
        glow.anchorMax = new Vector2(1f, 1f);
        glow.pivot = new Vector2(0.5f, 1f);
        glow.anchoredPosition = new Vector2(0f, -6f);
        glow.sizeDelta = new Vector2(-12f, 260f);
        glow.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.55f);

        var titleFrame = GameLook.Img(win, "Title-Frame", "Title_Frame", Image.Type.Simple, Color.clear).rectTransform;
        titleFrame.anchorMin = titleFrame.anchorMax = new Vector2(0.5f, 1f);
        titleFrame.pivot = new Vector2(0.5f, 0.5f);
        titleFrame.anchoredPosition = new Vector2(0f, 4f);
        titleFrame.sizeDelta = new Vector2(560f, 94f);
        var title = GameLook.Text(titleFrame, "Title", Loc.T("up.title"), 40f, TextAlignmentOptions.Center);
        GameLook.AutoSize(title, 40f);
        GameLook.Stretch(title.rectTransform, 80f, 14f, 80f, 14f);
        _labels.Add((title, "up.title"));

        var close = GameLook.Button(win, "Close", Style.Close, "", CloseUpdate, 30f, "UIClose");
        Place(close.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(-22f, -22f), new Vector2(52f, 52f));

        _updBody = GameLook.Text(win, "Body", "", 32f, TextAlignmentOptions.Center);
        _updBody.enableWordWrapping = true;
        GameLook.AutoSize(_updBody, 32f, 0.6f);
        GameLook.TopLeft(_updBody.rectTransform, 70f, 70f, W - 140f, 150f);

        // Open page | Not now | Don't remind me for this version
        var open = Track(GameLook.Button(win, "Open", Style.Primary, Loc.T("up.open"), OnUpdateOpenPage, 30f), "up.open");
        GameLook.TopLeft(open.GetComponent<RectTransform>(), 233f, 244f, 280f, 64f);
        var later = Track(GameLook.Button(win, "Later", Style.Secondary, Loc.T("up.later"), CloseUpdate, 26f), "up.later");
        GameLook.TopLeft(later.GetComponent<RectTransform>(), 537f, 250f, 230f, 52f);
        var skip = Track(GameLook.Button(win, "Skip", Style.Text, Loc.T("up.skip"), OnUpdateSkip, 24f), "up.skip");
        GameLook.TopLeft(skip.GetComponent<RectTransform>(), 200f, 334f, 600f, 40f);

        if (_font != null) foreach (var t in pr.GetComponentsInChildren<TextMeshProUGUI>(true)) t.font = _font;
        _updPopup.SetActive(false);
    }

    private static void BuildPanel(RectTransform rootRt)
    {
        // Own canvas under the game's Options (505) and Quit (502) pop-ups, over the menu (5).
        var pr = GameLook.Rect(rootRt, "CoopPanel");
        GameLook.Stretch(pr, 0f, 0f, 0f, 0f);
        var pc = pr.gameObject.AddComponent<Canvas>();
        pc.overrideSorting = true;
        pc.sortingOrder = 500;
        pr.gameObject.AddComponent<GraphicRaycaster>();
        _panelGroup = pr.gameObject.AddComponent<CanvasGroup>();
        _panel = pr.gameObject;

        // Dims the menu like the game's pop-ups, but leaves the top bar (tabs, settings, quit) usable.
        var fade = GameLook.Rect(pr, "Black-Fade");
        GameLook.Stretch(fade, 0f, 0f, 0f, 150f);
        var fimg = fade.gameObject.AddComponent<Image>();
        fimg.color = new Color(0f, 0f, 0f, 0.82f);

        var win = GameLook.Img(pr, "Window", "Menu_Frame_NotTransparent").rectTransform;
        win.GetComponent<Image>().raycastTarget = true;
        Place(win, new Vector2(0.5f, 0.5f), new Vector2(0f, WindowY), new Vector2(WinW, WinH));
        win.pivot = new Vector2(0.5f, 0.5f);
        _window = win;

        var glow = GameLook.Img(win, "Glow", "Yellow_Glow_01", Image.Type.Simple, Color.clear).rectTransform;
        glow.anchorMin = new Vector2(0f, 1f);
        glow.anchorMax = new Vector2(1f, 1f);
        glow.pivot = new Vector2(0.5f, 1f);
        glow.anchoredPosition = new Vector2(0f, -6f);
        glow.sizeDelta = new Vector2(-12f, 320f);
        glow.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.55f);

        var titleFrame = GameLook.Img(win, "Title-Frame", "Title_Frame", Image.Type.Simple, Color.clear).rectTransform;
        titleFrame.anchorMin = titleFrame.anchorMax = new Vector2(0.5f, 1f);
        titleFrame.pivot = new Vector2(0.5f, 0.5f);
        titleFrame.anchoredPosition = new Vector2(0f, 4f);
        titleFrame.sizeDelta = new Vector2(491f, 94f);
        var title = GameLook.Text(titleFrame, "Title", TabLabel, 44f, TextAlignmentOptions.Center);
        GameLook.AutoSize(title, 44f);
        GameLook.Stretch(title.rectTransform, 80f, 14f, 80f, 14f);
        _labels.Add((title, "ui.tab"));

        var close = GameLook.Button(win, "Close", Style.Close, "", OnClose, 30f, "UIClose");
        var crt = close.GetComponent<RectTransform>();
        Place(crt, new Vector2(1f, 1f), new Vector2(-22f, -22f), new Vector2(52f, 52f));

        // ---- left column: how to connect ----
        const float L = 50f, LW = 520f;
        Header(win, "Header-Steam", "ui.sec_steam", L, 66f, LW);
        _inviteButton = GameLook.Button(win, "Invite", Style.Primary, Loc.T("ui.invite"), OnInvite, 30f); // label set by RefreshPanel
        _inviteLabel = _inviteButton.GetComponentInChildren<TextMeshProUGUI>(true);
        GameLook.TopLeft(_inviteButton.GetComponent<RectTransform>(), L, 126f, 310f, 64f);
        _copyCodeButton = Track(GameLook.Button(win, "CopyCode", Style.Secondary, Loc.T("ui.copy_code"), OnCopyCode, 26f), "ui.copy_code");
        GameLook.TopLeft(_copyCodeButton.GetComponent<RectTransform>(), L + 324f, 135f, 196f, 46f);

        Header(win, "Header-IP", "ui.sec_ip", L, 222f, LW);
        string lastAddress = DefaultAddress;
        try { if (!string.IsNullOrWhiteSpace(Plugin.LastAddress.Value)) lastAddress = Plugin.LastAddress.Value.Trim(); } catch { }
        _ipField = GameLook.CloneInput(win, "Address", Loc.T("ui.ip_hint"), 28f) ?? MakeInputField(win, "Address");
        var ph = _ipField.placeholder?.TryCast<TextMeshProUGUI>();
        if (ph != null) _labels.Add((ph, "ui.ip_hint"));
        _ipField.text = lastAddress;
        if (_ipField.fontAsset != _font && _font != null) _ipField.fontAsset = _font;
        _ipField.caretColor = AccentColor;
        _ipField.customCaretColor = true;
        GameLook.TopLeft(_ipField.GetComponent<RectTransform>(), L + 4f, 282f, 306f, 50f);
        _pasteButton = Track(GameLook.Button(win, "Paste", Style.Secondary, Loc.T("ui.paste"), OnPaste, 26f), "ui.paste");
        GameLook.TopLeft(_pasteButton.GetComponent<RectTransform>(), L + 324f, 284f, 196f, 46f);
        _joinButton = Track(GameLook.Button(win, "Join", Style.Primary, Loc.T("ui.join"), OnJoin, 30f), "ui.join");
        GameLook.TopLeft(_joinButton.GetComponent<RectTransform>(), L, 346f, 250f, 60f);
        _hostButton = Track(GameLook.Button(win, "Host", Style.Secondary, Loc.T("ui.host_ip"), OnHost, 26f), "ui.host_ip");
        GameLook.TopLeft(_hostButton.GetComponent<RectTransform>(), L + 264f, 351f, 256f, 50f);

        var statusBox = GameLook.Img(win, "Status-Frame", "Menu_Frame_Transparent", null, new Color(0f, 0f, 0f, 0.35f)).rectTransform;
        GameLook.TopLeft(statusBox, L, 432f, LW, 150f);
        _status = GameLook.Text(statusBox, "Status", "", 25f, TextAlignmentOptions.TopLeft);
        _status.enableWordWrapping = true;
        GameLook.AutoSize(_status, 25f, 0.65f);
        GameLook.Stretch(_status.rectTransform, 22f, 16f, 22f, 16f);

        // ---- right column: the lobby ----
        const float R = 620f, RW = 530f;
        _playersTitle = Header(win, "Header-Players", null, R, 66f, RW);
        for (int i = 0; i < MaxRows; i++) _rows.Add(MakeRow(win, i, R, 124f + i * 60f, RW));

        _leaveButton = Track(GameLook.Button(win, "Leave", Style.Secondary, Loc.T("ui.disconnect"), OnLeave, 28f), "ui.disconnect");
        var lrt = _leaveButton.GetComponent<RectTransform>();
        Place(lrt, new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(300f, 58f));

        var version = GameLook.Text(win, "Version", $"mod {Plugin.HandshakeVersion}", 18f, TextAlignmentOptions.BottomLeft);
        version.color = new Color(1f, 1f, 1f, 0.4f);
        GameLook.TopLeft(version.rectTransform, 26f, WinH - 40f, 200f, 24f);

        _panel.SetActive(false);
    }

    /// <summary>Section title on the game's list-title band.</summary>
    private static TextMeshProUGUI Header(RectTransform parent, string name, string? key, float x, float y, float w)
    {
        var band = GameLook.Img(parent, name, "UI_Frame_Title_list", Image.Type.Simple, new Color(0f, 0f, 0f, 0.4f)).rectTransform;
        GameLook.TopLeft(band, x - 10f, y, w + 20f, 46f);
        var t = GameLook.Text(band, "Label", key != null ? Loc.T(key) : "", 32f, TextAlignmentOptions.MidlineLeft);
        GameLook.AutoSize(t, 32f);
        GameLook.Stretch(t.rectTransform, 22f, 2f, 22f, 2f);
        if (key != null) _labels.Add((t, key));
        return t;
    }

    private static Row MakeRow(RectTransform parent, int i, float x, float y, float w)
    {
        var row = new Row();
        row.Bg = GameLook.Img(parent, $"Player-{i + 1}", "Craft_Background_List_Frame", Image.Type.Simple, new Color(0f, 0f, 0f, 0.5f));
        row.Rt = row.Bg.rectTransform;
        GameLook.TopLeft(row.Rt, x, y, w, 56f);
        row.Frame = GameLook.Img(row.Rt, "Icon-Frame", "Skill_Tree_Character_BGFrame", Image.Type.Simple, new Color(0f, 0f, 0f, 0.6f));
        GameLook.TopLeft(row.Frame.rectTransform, 18f, 3f, 50f, 50f);
        row.Icon = GameLook.Img(row.Frame.rectTransform, "Icon", "-", Image.Type.Simple, Color.white);
        row.Icon.preserveAspect = true;
        GameLook.Stretch(row.Icon.rectTransform, 4f, 4f, 4f, 4f);
        row.Name = GameLook.Text(row.Rt, "Name", "", 26f, TextAlignmentOptions.BottomLeft);
        GameLook.AutoSize(row.Name, 26f);
        GameLook.TopLeft(row.Name.rectTransform, 80f, 2f, 250f, 30f);
        row.Sub = GameLook.Text(row.Rt, "Character", "", 19f, TextAlignmentOptions.TopLeft);
        GameLook.AutoSize(row.Sub, 19f, 0.7f);
        GameLook.TopLeft(row.Sub.rectTransform, 80f, 31f, 250f, 22f);
        row.Place = GameLook.Text(row.Rt, "Place", "", 22f, TextAlignmentOptions.Center);
        GameLook.AutoSize(row.Place, 22f, 0.6f);
        GameLook.TopLeft(row.Place.rectTransform, 334f, 8f, 110f, 40f);
        row.Ping = GameLook.Text(row.Rt, "Ping", "", 22f, TextAlignmentOptions.MidlineRight);
        GameLook.AutoSize(row.Ping, 22f, 0.7f);
        GameLook.TopLeft(row.Ping.rectTransform, 446f, 8f, 66f, 40f);
        row.Rt.gameObject.SetActive(false);
        return row;
    }

    private static Button Track(Button b, string key)
    {
        var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
        if (t != null) _labels.Add((t, key));
        return b;
    }

    /// <summary>The language (or the game's font for it) changed: texts set at build time, the font of every text
    /// of ours (the game swaps fonts per language: Cyrillic, CJK) and the tab clone's own font.</summary>
    private static void ApplyLanguage()
    {
        _locVersion = Loc.Version;
        if (_root == null) return;
        if (_fontVersion != Loc.Version)
        {
            _fontVersion = Loc.Version;
            var font = Loc.GameFont(_fontType);
            if (font != null && (_font == null || font.Pointer != _font.Pointer))
            {
                _font = font;
                GameLook.Font = font;
                foreach (var t in _root.GetComponentsInChildren<TextMeshProUGUI>(true)) t.font = font;
                if (_ipField != null) _ipField.fontAsset = font;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Font for {Loc.Codes[(int)Loc.Current]}: {font.name}.");
            }
            if (_tab != null)
                foreach (var lr in _tab.GetComponentsInChildren<LabelReferences>(true))
                    try { lr.UpdateFont(); } catch { /* the game's own label: leave it */ }
            Loc.CheckGlyphs(_font, "co-op UI");
        }
        foreach (var (t, key) in _labels) if (t != null) t.text = Loc.T(key);
        foreach (var r in _rows) r.Key = "";
        if (_updateOpen) SetUpdateBody();
        _lastStatus = "";
    }

    /// <summary>The game's main font (the one on the menu tabs) and the templates GameLook clones.</summary>
    private static void FindGameLook()
    {
        GameLook.Reset();
        try
        {
            var mm = UnityEngine.Object.FindObjectOfType<MenuManager>();
            var panel = mm != null ? mm.MainMenuPanel : null;
            if (panel != null)
            {
                var label = panel.FightButton != null ? panel.FightButton.GetComponentInChildren<TextMeshProUGUI>(true) : null;
                if (label != null && label.font != null) _font = label.font;
                // The game picks that label's font per language by its LabelReferences.Type (LocalizationManager).
                var refs = panel.FightButton != null ? panel.FightButton.GetComponentInChildren<LabelReferences>(true) : null;
                if (refs != null) _fontType = refs.Type;
            }
            GameLook.SetTemplates(panel != null && panel.HistoryButton != null ? panel.HistoryButton.gameObject : null,
                mm != null && mm.MailingPanel != null ? mm.MailingPanel.EmailInput : null);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ui] Reading the game's UI look: {ex.Message}");
        }

        if (_font == null)
        {
            // Most used font in the scene.
            var counts = new Dictionary<IntPtr, (TMP_FontAsset Font, int N)>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>(true))
            {
                var f = t != null ? t.font : null;
                if (f == null) continue;
                counts[f.Pointer] = counts.TryGetValue(f.Pointer, out var c) ? (f, c.N + 1) : (f, 1);
            }
            int bestN = 0;
            foreach (var kv in counts.Values) if (kv.N > bestN) { _font = kv.Font; bestN = kv.N; }
        }
        _font ??= TMP_Settings.defaultFontAsset;
    }

    private static void AddClick(Button b, Action onClick, string name)
    {
        b.onClick.AddListener((UnityEngine.Events.UnityAction)new Action(() =>
        {
            try { onClick(); }
            catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][ui] {name}: {ex}"); }
        }));
    }

    /// <summary>Fallback when the game's form isn't there: a TMP_InputField wired by hand (background, masked text
    /// area, placeholder, text), enabled only once its references are set.</summary>
    private static TMP_InputField MakeInputField(Transform parent, string name)
    {
        var rt = GameLook.Rect(parent, name);
        rt.gameObject.SetActive(false);
        var bg = rt.gameObject.AddComponent<Image>();
        var sp = GameLook.Sprite("Achievement_Frame");
        if (sp != null) bg.sprite = sp;
        else bg.color = new Color(0f, 0f, 0f, 0.65f);

        var area = GameLook.Rect(rt, "TextArea");
        GameLook.Stretch(area, 16f, 4f, 16f, 4f);
        area.gameObject.AddComponent<RectMask2D>();

        var placeholder = GameLook.Text(area, "Placeholder", Loc.T("ui.ip_hint"), 28f, TextAlignmentOptions.MidlineLeft);
        placeholder.color = new Color(1f, 1f, 1f, 0.35f);
        placeholder.fontStyle = FontStyles.Italic;
        GameLook.Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);

        var text = GameLook.Text(area, "Text", "", 28f, TextAlignmentOptions.MidlineLeft);
        GameLook.Stretch(text.rectTransform, 0f, 0f, 0f, 0f);

        var input = rt.gameObject.AddComponent<TMP_InputField>();
        input.targetGraphic = bg;
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.fontAsset = _font;
        input.pointSize = 28f;
        input.characterLimit = 64;
        input.contentType = TMP_InputField.ContentType.Standard;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.navigation = new Navigation { mode = Navigation.Mode.None };
        rt.gameObject.SetActive(true);
        return input;
    }

    private static void Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }
}

[HarmonyPatch(typeof(MenuManager), nameof(MenuManager.AdjustSelectedButton))]
public static class MenuAdjustSelectedButtonPatch
{
    // AdjustSelectedButton(MainMenuState, MainMenuPanel): an int enum and a reference, nothing small by value.
    [HarmonyPostfix]
    public static void Postfix(MainMenuPanel panel)
    {
        try { CoopUi.AfterAdjustSelectedButton(panel); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] MenuAdjustSelectedButtonPatch: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(QuitScreenPanel), nameof(QuitScreenPanel.Open))]
public static class QuitScreenOpenPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return CoopUi.BeforeQuitPanelOpen(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] QuitScreenOpenPatch: {ex.Message}"); return true; }
    }
}
