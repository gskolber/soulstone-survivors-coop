using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Lobby;

/// <summary>Where a player is, as the others see it (LobbyState packet).</summary>
public enum LobbyPlace : byte
{
    Menu = 0,
    Loading = 1, // between RunStart and the local StartMatch
    InRun = 2,   // from StartMatch until the scene's game manager is gone (end screen included)
    Transition = 3, // still in the run's scene, but a portal (Endless/BossRush) or Restart was taken: the next scene is coming
}

/// <summary>Identity of one player: what the Hello packet carries. Phase 5 builds the ghost from this.</summary>
public sealed class PlayerInfo
{
    public bool Known;
    public string Name = "";
    public byte CharacterId;      // CharactersType (1-byte enum)
    public string CharacterName = "";
    public string WeaponId = "";
    public string SkinId = "";
    public string WeaponSkinId = "";
    public string ModVersion = "";

    public bool SameAs(PlayerInfo o) =>
        Name == o.Name && CharacterId == o.CharacterId && WeaponId == o.WeaponId && SkinId == o.SkinId && WeaponSkinId == o.WeaponSkinId;

    public void Write(NetDataWriter w)
    {
        w.Put(Name);
        w.Put(CharacterId);
        w.Put(CharacterName);
        w.Put(WeaponId);
        w.Put(SkinId);
        w.Put(WeaponSkinId);
        w.Put(ModVersion);
    }

    public static PlayerInfo Read(NetDataReader r) => new()
    {
        Known = true,
        Name = r.GetString(),
        CharacterId = r.GetByte(),
        CharacterName = r.GetString(),
        WeaponId = r.GetString(),
        SkinId = r.GetString(),
        WeaponSkinId = r.GetString(),
        ModVersion = r.GetString(),
    };
}

/// <summary>
/// Phase 6: the co-op session as a whole, independent of any one run. The connection is made in the menu and
/// survives several runs, so "a player connected" is no longer "a player entered my match": this keeps who the
/// others are (Hello) and where each one is (LobbyState), and owns the per-run lifecycle that used to hang off
/// PeerConnected (the remote ghosts, the host's enemy resync). See docs/fase6-investigacao.md.
/// Phase 9: one RemotePlayer per other slot. The host forwards each client's Hello and LobbyState to the others
/// (PacketRouter) and hands a newcomer everybody's.
/// </summary>
public static class CoopSession
{
    public static readonly PlayerInfo Local = new();

    private static readonly RemotePlayer?[] BySlot = new RemotePlayer?[Players.MaxSlots];
    private static readonly List<RemotePlayer> RemoteList = new();

    /// <summary>The other players this side knows about, in slot order.</summary>
    public static IReadOnlyList<RemotePlayer> Remotes => RemoteList;

    public static RemotePlayer? Get(byte slot) => slot < Players.MaxSlots ? BySlot[slot] : null;

    /// <summary>Client: the host's record (slot 0).</summary>
    public static RemotePlayer? Host => BySlot[Players.HostSlot];

    public static bool Connected => NetworkManager.HasPeer;

    /// <summary>Our match is running (StartMatch seen, game manager still alive).</summary>
    public static bool LocalInRun { get; private set; }
    public static bool LocalLoading => _loadingSince >= 0f;
    public static float LocalRunStartTime { get; private set; }

    /// <summary>Some other player's match is running.</summary>
    public static bool AnyRemoteInRun
    {
        get
        {
            if (!Connected) return false;
            foreach (var r in RemoteList) if (r.InRun) return true;
            return false;
        }
    }

    /// <summary>That player's match is running (and we're still connected).</summary>
    public static bool InRun(byte slot) => Connected && Get(slot)?.InRun == true;

    /// <summary>Client: the host's match is running.</summary>
    public static bool HostInRun => InRun(Players.HostSlot);

    /// <summary>This run was started as the co-op client: keeps CoopRole.IsClientSide true for the rest of the
    /// run even if the connection drops (the spawners are already stopped; turning host-like mid-run is phase 8).</summary>
    internal static bool ClientRunLatched;

    /// <summary>A scene change inside the run is under way (Endless/BossRush portal or co-op Restart): from the portal
    /// until the next StartMatch. While the old scene still lives the others see Transition, then Loading.</summary>
    public static bool StageTransition { get; private set; }

    public static LobbyPlace LocalPlace =>
        LocalInRun ? (StageTransition ? LobbyPlace.Transition : LobbyPlace.InRun)
        : LocalLoading ? LobbyPlace.Loading : LobbyPlace.Menu;

    /// <summary>Some other player is on its way to the next scene (still in the old one, or loading).</summary>
    public static bool AnyRemoteChangingScene
    {
        get
        {
            if (!Connected) return false;
            foreach (var r in RemoteList) if (r.ChangingScene) return true;
            return false;
        }
    }

    /// <summary>Phase 9: that player is on its way to the next scene (the transport allows a longer silence meanwhile).</summary>
    public static bool ChangingScene(byte slot) => Connected && Get(slot)?.ChangingScene == true;

    /// <summary>Name of that player for the banner and notices.</summary>
    public static string NameOf(byte slot) => Get(slot)?.Name ?? RemotePlayer.FallbackName(slot);

    /// <summary>"A", "A and B", "A, B and 2 more": the names of the other players that match, for a one-line banner
    /// (in the local language).</summary>
    public static string NamesWhere(Func<RemotePlayer, bool> pick, int show = 2)
    {
        var names = new List<string>();
        foreach (var r in RemoteList) if (pick(r)) names.Add(r.Name);
        return JoinNames(names, show);
    }

    public static string JoinNames(List<string> names, int show = 2)
    {
        if (names.Count == 0) return "";
        if (names.Count == 1) return names[0];
        if (names.Count <= show)
            return Ui.Loc.T("list.and", ("a", string.Join(", ", names.Take(names.Count - 1))), ("b", names[names.Count - 1]));
        return Ui.Loc.T("list.more", ("a", string.Join(", ", names.Take(show))), ("n", names.Count - show));
    }

    /// <summary>The others, as one name: the only partner's, or "the group" (as a message: Ui.Loc.Msg).</summary>
    public static string OthersLabel => RemoteList.Count == 1 ? RemoteList[0].Name : Ui.Loc.Msg(RemoteList.Count == 0 ? "name.partner" : "name.group");

    internal const string MenuScene = "Menu-Survivors";
    private const float LoadingTimeout = 90f;
    private const float InfoRefreshInterval = 1f;

    private static float _loadingSince = -1f;
    private static float _sceneTimer;
    private static bool _loggedStaleGameManager;

    /// <summary>The active scene is the main menu (checked every 0.25 s).</summary>
    public static bool InMenuScene { get; private set; }
    private static bool _hooked;
    private static bool _helloSent, _placeSent;
    private static LobbyPlace _lastSentPlace;
    private static float _infoTimer;

    // Transient on-screen notice (CoopUi banner).
    private static string? _notice;
    private static float _noticeUntil;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        Players.Appeared += slot =>
        {
            var r = new RemotePlayer(slot);
            BySlot[slot] = r;
            RemoteList.Add(r);
            RemoteList.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        };
        Players.Forgotten += slot =>
        {
            var r = BySlot[slot];
            BySlot[slot] = null;
            if (r != null) RemoteList.Remove(r);
        };
        Players.Left += OnPlayerLeft;
        NetworkManager.PeerConnected += slot =>
        {
            // Ours goes out again to everyone (cheap), and the host hands a newcomer everybody else's.
            _helloSent = false;
            _placeSent = false;
            RefreshLocalInfo();
            if (NetworkManager.Role == NetRole.Host) SendOthersTo(slot);
            RunSync.OnPeerConnected(slot);
        };
    }

    private static void OnPlayerLeft(byte slot)
    {
        var r = Get(slot);
        if (r == null) return;
        string who = r.Name;
        if (NetworkManager.Role == NetRole.Host)
        {
            // Phase 8: the host keeps playing (a client's run ends with the host: CoopLife).
            bool alone = Players.OtherCount == 0;
            Notice(Ui.Loc.Msg(LocalInRun && r.Place != LobbyPlace.Menu && alone ? "nt.left_alone" : "nt.left", ("name", who)), 6f);
        }
        else if (slot == Players.HostSlot) Notice(Ui.Loc.Msg("nt.left", ("name", who)), 6f);
        else if (NetworkManager.HasPeer) Notice(Ui.Loc.Msg("nt.quit", ("name", who)), 5f); // with the host gone, everybody "leaves": no notice each
    }

    /// <summary>Host: a client just connected. It gets the Hello and LobbyState of every other client, as if they had
    /// been relayed, so it can build their ghosts right away.</summary>
    private static void SendOthersTo(byte newSlot)
    {
        foreach (var r in RemoteList)
        {
            if (r.Slot == newSlot) continue;
            if (r.Info.Known)
            {
                var w = new NetDataWriter();
                w.Put((byte)PacketType.Hello);
                r.Info.Write(w);
                NetworkManager.SendRelayedTo(newSlot, r.Slot, w, DeliveryMethod.ReliableOrdered);
            }
            var p = new NetDataWriter();
            p.Put((byte)PacketType.LobbyState);
            p.Put((byte)r.Place);
            NetworkManager.SendRelayedTo(newSlot, r.Slot, p, DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>A banner notice for a few seconds. <paramref name="text"/> is a Ui.Loc.Msg: it is shown in the
    /// current language (and follows a change) and logged in English.</summary>
    public static void Notice(string text, float seconds = 4f)
    {
        _notice = text;
        _noticeUntil = Time.unscaledTime + seconds;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Notice: {Ui.Loc.Plain(text)}");
    }

    internal static string? ActiveNotice => _notice != null && Time.unscaledTime < _noticeUntil ? Ui.Loc.Show(_notice) : null;

    // ---------------- local lifecycle ----------------

    /// <summary>RunStart sent (host) or accepted (client): we're on our way into a match.</summary>
    internal static void BeginLoading()
    {
        _loadingSince = Time.unscaledTime;
        SendPlace(force: true);
    }

    /// <summary>A portal or Restart was taken inside the run (PortalSync/RunSync): the game will change scene by itself
    /// (LoadNextScene). Counts as loading from now on, so the barrier holds whoever reaches the next StartMatch first.</summary>
    internal static void BeginStageTransition(string why)
    {
        if (!StageTransition)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Stage transition ({why}): next scene coming ({Players.OtherCount} other(s)).");
        StageTransition = true;
        _loadingSince = Time.unscaledTime;
        SendPlace(force: true);
    }

    /// <summary>The scene change announced by BeginStageTransition isn't happening after all (the client couldn't
    /// follow the host's portal). Only while the old scene is still up.</summary>
    internal static void CancelStageTransition(string why)
    {
        if (!StageTransition || !LocalInRun) return;
        StageTransition = false;
        _loadingSince = -1f;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Stage transition cancelled ({why}).");
        SendPlace(force: true);
    }

    /// <summary>GameManagerUtil.UnsetGameManager postfix: the scene's game manager was just destroyed. A direct
    /// scene-to-scene change (portal, Restart) may never show a frame without a game manager to the tick (the new
    /// scene's Start runs SetupGameData before the next Update), so the run is left here, at the exact moment.</summary>
    internal static void OnGameManagerUnset()
    {
        if (LocalInRun) OnLocalRunLeft();
    }

    /// <summary>GameManagerUtil.StartMatch postfix.</summary>
    internal static void OnLocalMatchStarted()
    {
        StageTransition = false;
        LocalInRun = true;
        InMenuScene = false; // the 0.25 s scene check may still hold the menu from before the load
        _sceneTimer = 0f;
        LocalRunStartTime = Time.unscaledTime;
        _loadingSince = -1f;
        ClientRunLatched = NetworkManager.Role == NetRole.Client;
        RunSync.OnLocalMatchStarted();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Local run started (role={NetworkManager.Role} connected={Connected} others={DescribeOthers()}).");
        SendPlace(force: true);
    }

    private static void OnLocalRunLeft()
    {
        LocalInRun = false;
        ClientRunLatched = false;
        GameState.LocalPlayerEntity = null;
        RunSync.OnLocalRunLeft();
        Plugin.StaticLog.LogInfo(StageTransition
            ? $"[SoulstoneCoop][session] Left the run's scene (stage transition, loading the next). connected={Connected}"
            : $"[SoulstoneCoop][session] Local run over (back to menu). connected={Connected}");
        SendPlace(force: true);
    }

    internal static string DescribeOthers()
    {
        if (RemoteList.Count == 0) return "-";
        return string.Join(",", RemoteList.Select(r => $"{r.Slot}:{r.Place}"));
    }

    public static void Tick(float dt)
    {
        _sceneTimer -= dt;
        if (_sceneTimer <= 0f)
        {
            _sceneTimer = 0.25f;
            InMenuScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == MenuScene;
            if (InMenuScene && GameManagerUtil.HasGameManager && !_loggedStaleGameManager)
            {
                _loggedStaleGameManager = true;
                Plugin.StaticLog.LogWarning("[SoulstoneCoop][session] HasGameManager is still true in the menu scene.");
            }
        }
        // Back in the menu scene counts too, in case HasGameManager stays true there (docs/fase6-investigacao.md, 5).
        if (LocalInRun && (!GameManagerUtil.HasGameManager || InMenuScene)) OnLocalRunLeft();
        if (LocalLoading && Time.unscaledTime - _loadingSince > LoadingTimeout)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][session] Loading never reached StartMatch - back to menu state (stageTransition={StageTransition}).");
            _loadingSince = -1f;
            StageTransition = false;
            RunSync.OnLocalRunLeft();
        }

        if (!Connected) return;
        SendPlace(force: false);

        _infoTimer += dt;
        if (_infoTimer >= InfoRefreshInterval || !_helloSent)
        {
            _infoTimer = 0f;
            // The character can change in the menu; in a run it's fixed (and the profile reads are wasted work).
            bool changed = !LocalInRun && RefreshLocalInfo();
            if (changed || !_helloSent) SendHello();
        }

        RunSync.HostRejoinTick(); // phase 8, AllowRejoin only
        RunSync.ClientRunTick();  // 1.6.0: a RunStart held until the menu has settled
    }

    private static void SendPlace(bool force)
    {
        if (!Connected) return;
        var place = LocalPlace;
        if (!force && _placeSent && place == _lastSentPlace) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.LobbyState);
        w.Put((byte)place);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _lastSentPlace = place;
        _placeSent = true;
    }

    // ---------------- identity ----------------

    /// <summary>Reads name, character, weapon and skins from the local profile. True if anything changed.</summary>
    internal static bool RefreshLocalInfo()
    {
        var info = new PlayerInfo { Known = true, ModVersion = Plugin.HandshakeVersion };
        // No name (no Steam, no --coop-name): sent empty, each receiver shows "Host"/"Player n" in its own language.
        info.Name = Plugin.NameArg ?? SteamName.TryGet() ?? "";
        try
        {
            var prof = Global.PlayerProfile;
            if (prof != null && prof.SelectedCharacter != null)
            {
                var ct = prof.SelectedCharacter.CharacterIdAsEnum;
                info.CharacterId = (byte)ct;
                info.CharacterName = ct.ToString();
                try { info.WeaponId = prof.SelectedWeaponPerCharacter?.GetWeaponIdByCharacterId(ct) ?? ""; } catch { }
                try { info.SkinId = prof.SelectedSkinPerCharacter?.GetCharacterSkinIdByCharacterId(ct) ?? ""; } catch { }
                try { info.WeaponSkinId = prof.SelectedSkinPerCharacter?.GetWeaponSkinIdByCharacterId(ct) ?? ""; } catch { }
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][session] Reading the profile's character: {ex.Message}");
        }

        bool changed = !Local.Known || !Local.SameAs(info);
        Local.Known = true;
        Local.Name = info.Name;
        Local.CharacterId = info.CharacterId;
        Local.CharacterName = info.CharacterName;
        Local.WeaponId = info.WeaponId;
        Local.SkinId = info.SkinId;
        Local.WeaponSkinId = info.WeaponSkinId;
        Local.ModVersion = info.ModVersion;
        return changed;
    }

    private static void SendHello()
    {
        if (!Connected) return;
        if (!Local.Known) RefreshLocalInfo();
        var w = new NetDataWriter();
        w.Put((byte)PacketType.Hello);
        Local.Write(w);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _helloSent = true;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Hello sent: {Local.Name} {Local.CharacterName} weapon={Local.WeaponId} skin={Local.SkinId}/{Local.WeaponSkinId}");
    }

    public static void OnHello(NetDataReader r)
    {
        var info = PlayerInfo.Read(r);
        var p = Get(PacketRouter.From);
        if (p == null) return;
        bool first = !p.Info.Known;
        p.Info = info;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Hello from slot {p.Slot}: {info.Name} {info.CharacterName} weapon={info.WeaponId} skin={info.SkinId}/{info.WeaponSkinId} mod={info.ModVersion}");
        if (!first) return;
        string who = info.CharacterName.Length > 0 ? $"{p.Name} ({info.CharacterName})" : p.Name;
        Notice(Ui.Loc.Msg(NetworkManager.Role == NetRole.Client && !p.IsHost ? "nt.joined_group" : "nt.connected", ("name", who)), 5f);
    }

    public static void OnLobbyState(NetDataReader r)
    {
        var place = (LobbyPlace)r.GetByte();
        var p = Get(PacketRouter.From);
        if (p == null) return;
        var prev = p.Place;
        p.Place = place;
        if (prev == place) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][session] Slot {p.Slot} ({p.Name}): {prev} -> {place} (local {LocalPlace}).");

        // That player's match just started next to ours: resend every live enemy to it (this used to happen on
        // PeerConnected, when the connection was made inside the run). Duplicates are ignored by the client.
        // Not while our own old scene is still up (portal taken): those enemies are about to be gone.
        if (place == LobbyPlace.InRun) RunSync.NoteInRun(p.Slot);
        if (prev == LobbyPlace.InRun && place == LobbyPlace.Menu) RunSync.NoteLeftRun(p.Slot); // 1.6.0: no auto pull-back
        if (place == LobbyPlace.InRun && LocalInRun && !StageTransition && NetworkManager.Role == NetRole.Host)
        {
            Enemies.HostEnemySync.ResyncToPeer(p.Slot, "player entered the run");
            Loot.ChestSync.ResyncToPeer(p.Slot); // and the chests / ore still up
        }

        // Client: the host's old scene is gone. Everything it sent from that scene came before this packet (same
        // ordered channel), so spawns/loot still around are stale (portal or Restart inside the run).
        if (p.IsHost && place == LobbyPlace.Loading && (prev == LobbyPlace.InRun || prev == LobbyPlace.Transition) && NetworkManager.Role == NetRole.Client)
            PortalSync.OnHostLeftScene();
    }
}
