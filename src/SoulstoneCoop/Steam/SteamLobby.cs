using System.Runtime.InteropServices;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Steam;

/// <summary>
/// Phase 7: the Steam lobby is the signaling for a Steam co-op session. The host creates a friends-only lobby of
/// two, tags it with the mod version and its SteamID, sets the rich presence "connect" string and opens the
/// overlay invite dialog. The friend arrives by accepting the invite (GameLobbyJoinRequested_t with the game
/// open, +connect_lobby on the command line with it closed), by "Join game" in the friends list, or by pasting
/// the lobby code. Once in, the SteamTransport talks to the other member by SteamID.
/// </summary>
internal static unsafe class SteamLobby
{
    public enum LobbyState
    {
        None,
        Creating,  // host: CreateLobby sent
        Hosting,   // host: lobby ready, waiting for / playing with the friend
        Joining,   // friend: JoinLobby sent
        Joined,    // friend: inside the host's lobby
    }

    private const string KeyVersion = "scoop_ver";
    private const string KeyHost = "scoop_host";
    private const string KeyProto = "scoop_proto";
    internal const string Proto = "2"; // phase 9: slots, relay envelope (a two-player build is refused)

    public static LobbyState State { get; private set; } = LobbyState.None;
    public static ulong LobbyId { get; private set; }
    public static ulong HostId { get; private set; }
    public static ulong SelfId { get; private set; }

    /// <summary>Last Steam lobby problem, in Portuguese, for the panel (null = none).</summary>
    public static string? LastError { get; private set; }

    /// <summary>Steam is up and the flat API resolved.</summary>
    public static bool Available => _ready;

    public static bool OverlayEnabled
    {
        get
        {
            if (!_ready) return false;
            try { return SteamNative.SteamAPI_ISteamUtils_IsOverlayEnabled(_utils) != 0; }
            catch { return false; }
        }
    }

    public static bool InLobby => State == LobbyState.Hosting || State == LobbyState.Joined;

    private static bool _ready, _initTried;
    private static IntPtr _mm, _friends, _utils, _netUtils;
    private static ulong _joining;
    private static ulong _pendingJoin; // +connect_lobby or an invite accepted mid-run
    private static float _relayTimer;
    private static int _lastRelayStatus = int.MinValue;
    private static bool _openOverlayWhenReady;
    private static bool _closedForRun;

    // ---------------- setup ----------------

    /// <summary>Resolve the interfaces and register our callbacks. Called from CoopBehaviour.EnsureCreated (menu)
    /// and retried from Tick until SteamManager is initialized.</summary>
    public static void EnsureInit()
    {
        if (_ready || _initTried && !SteamManager.Initialized) return;
        if (!SteamManager.Initialized) { _initTried = true; return; }
        _initTried = true;
        try
        {
            _mm = SteamNative.SteamAPI_SteamMatchmaking_v009();
            _friends = SteamNative.SteamAPI_SteamFriends_v017();
            _utils = SteamNative.SteamAPI_SteamUtils_v010();
            _netUtils = SteamNative.SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
            var user = SteamNative.SteamAPI_SteamUser_v021();
            if (_mm == IntPtr.Zero || _friends == IntPtr.Zero || user == IntPtr.Zero)
            {
                Plugin.StaticLog.LogWarning("[SoulstoneCoop][steam] Steam interfaces missing, Steam co-op off.");
                return;
            }
            SelfId = SteamNative.SteamAPI_ISteamUser_GetSteamID(user);

            SteamCallbacks.On(SteamNative.GameLobbyJoinRequested, OnGameLobbyJoinRequested);
            SteamCallbacks.On(SteamNative.GameRichPresenceJoinRequested, OnRichPresenceJoinRequested);
            SteamCallbacks.On(SteamNative.LobbyEnter, (p, _) => OnLobbyEnter(p, fromCallResult: false));
            SteamCallbacks.On(SteamNative.LobbyChatUpdate, OnLobbyChatUpdate);
            SteamCallbacks.On(SteamNative.LobbyKicked, OnLobbyKicked);
            SteamCallbacks.On(SteamNative.LobbyDataUpdate, (_, _) => { });
            SteamCallbacks.On(SteamNative.SessionRequest, SteamTransport.OnSessionRequest);
            SteamCallbacks.On(SteamNative.SessionFailed, SteamTransport.OnSessionFailed);

            if (_netUtils != IntPtr.Zero) SteamNative.SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(_netUtils);

            _ready = true;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Steam co-op ready: me={SelfId} overlay={OverlayEnabled}.");
            ReadLaunchArgs();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][steam] Steam init failed, Steam co-op off: {ex.Message}");
        }
    }

    /// <summary>The Steam client starts the game with "+connect_lobby &lt;id&gt;" when an invite is accepted with the
    /// game closed (and with our rich presence connect string for "Join game").</summary>
    private static void ReadLaunchArgs()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out var id) && id != 0)
            {
                _pendingJoin = id;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Launched to join lobby {id}.");
            }
        }
    }

    public static void Tick(float dt)
    {
        if (!_ready)
        {
            if (SteamManager.Initialized) { _initTried = false; EnsureInit(); }
            return;
        }

        if (_pendingJoin != 0 && Lobby.CoopSession.InMenuScene && !Lobby.CoopSession.LocalInRun)
        {
            var id = _pendingJoin;
            _pendingJoin = 0;
            Join(id);
        }

        // Host: the friend can only come in from the menu (phase 6), so close the lobby during a run. Phase 8:
        // with AllowRejoin it stays open, so a friend who dropped can come back into the run.
        bool inRun = (Lobby.CoopSession.LocalInRun || Lobby.CoopSession.LocalLoading) && !Plugin.AllowRejoin.Value;
        if (State == LobbyState.Hosting && inRun != _closedForRun)
        {
            _closedForRun = inRun;
            SetJoinable(!inRun);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Lobby {(inRun ? "closed during the run" : "open again")}.");
        }

        _relayTimer += dt;
        if (_relayTimer >= 2f && _netUtils != IntPtr.Zero)
        {
            _relayTimer = 0f;
            int status = SteamNative.SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus(_netUtils, IntPtr.Zero);
            if (status != _lastRelayStatus)
            {
                _lastRelayStatus = status;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Relay network: {RelayName(status)} ({status}).");
            }
        }

        SteamTransport.SelfTestTick();
    }

    public static bool RelayReady => _lastRelayStatus == 100;

    private static string RelayName(int s) => s switch
    {
        100 => "ready",
        3 => "connecting",
        2 => "waiting",
        1 => "not started",
        -10 => "retrying",
        -100 => "failed before",
        -101 => "failed",
        -102 => "cannot try",
        _ => "?",
    };

    // ---------------- host ----------------

    /// <summary>"Convidar amigo": create the lobby if needed, then open the overlay invite dialog. Phase 9: a client in
    /// the lobby can invite its own friends too (FriendsOnly lobbies take friends and invitees of the members).</summary>
    public static void HostAndInvite()
    {
        if (!_ready) { LastError = Ui.Loc.Msg("steam.unavailable"); return; }
        LastError = null;
        if (State == LobbyState.Hosting || State == LobbyState.Joined) { OpenInviteOverlay(); return; }
        if (State == LobbyState.Creating) return;

        LeaveSilently();
        NetworkManager.Stop(); // leaving IP co-op, if any
        ulong call = SteamNative.SteamAPI_ISteamMatchmaking_CreateLobby(_mm, LobbyTypeValue(), NetworkManager.MaxPlayers);
        if (call == 0) { LastError = Ui.Loc.Msg("steam.create_failed"); return; }
        State = LobbyState.Creating;
        _openOverlayWhenReady = true;
        SteamCallbacks.Expect(call, SteamNative.LobbyCreated, OnLobbyCreated);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Creating lobby ({Plugin.LobbyType.Value}, {NetworkManager.MaxPlayers} players)...");
    }

    private static void OnLobbyCreated(IntPtr p, bool ioFailure)
    {
        int result = *(int*)p;
        ulong lobby = *(ulong*)((byte*)p + 8);
        if (ioFailure || result != 1 || lobby == 0)
        {
            State = LobbyState.None;
            LastError = Ui.Loc.Msg("steam.create_failed_code", ("code", result));
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][steam] CreateLobby failed: result={result} ioFailure={ioFailure}.");
            return;
        }

        LobbyId = lobby;
        HostId = SelfId;
        State = LobbyState.Hosting;
        SetData(KeyVersion, Plugin.HandshakeVersion);
        SetData(KeyHost, SelfId.ToString());
        SetData(KeyProto, Proto);
        SteamNative.SteamAPI_ISteamMatchmaking_SetLobbyMemberLimit(_mm, lobby, NetworkManager.MaxPlayers);
        SetRichPresence("connect", $"+connect_lobby {lobby}");
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Lobby {lobby} created (ver={GetData(lobby, KeyVersion)} host={GetData(lobby, KeyHost)}).");

        NetworkManager.StartSteamHost(lobby);
        if (_openOverlayWhenReady) OpenInviteOverlay();
        _openOverlayWhenReady = false;
    }

    /// <summary>[Coop] LobbyType: FriendsOnly (default: friends and invitees of the members, per the Steamworks docs),
    /// Public (anyone with the code can join; the lobby can also show up in searches) or Private (invite only).</summary>
    private static int LobbyTypeValue() => Plugin.LobbyType.Value switch
    {
        Plugin.SteamLobbyType.Public => SteamNative.LobbyPublic,
        Plugin.SteamLobbyType.Private => SteamNative.LobbyPrivate,
        _ => SteamNative.LobbyFriendsOnly,
    };

    public static void OpenInviteOverlay()
    {
        if (State != LobbyState.Hosting && State != LobbyState.Joined) return;
        // IsOverlayEnabled reads false with the game started from the exe (steam_appid.txt) and early on; ask
        // anyway (harmless without an overlay) and point to the code as the way that always works.
        bool overlay = OverlayEnabled;
        SteamNative.SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(_friends, LobbyId);
        LastError = overlay ? null : Ui.Loc.Msg("steam.overlay");
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Invite overlay requested (IsOverlayEnabled={overlay}).");
    }

    /// <summary>Host: no joining mid-run (phase 6 rule: the client comes in from the menu).</summary>
    public static void SetJoinable(bool joinable)
    {
        if (State != LobbyState.Hosting && State != LobbyState.Joined) return;
        SteamNative.SteamAPI_ISteamMatchmaking_SetLobbyJoinable(_mm, LobbyId, (byte)(joinable ? 1 : 0));
    }

    // ---------------- friend ----------------

    public static void Join(ulong lobby)
    {
        if (!_ready) { LastError = Ui.Loc.Msg("steam.unavailable"); return; }
        if (lobby == 0) return;
        if (InLobby && LobbyId == lobby) return;
        if (Lobby.CoopSession.LocalInRun || Lobby.CoopSession.LocalLoading)
        {
            _pendingJoin = lobby;
            Lobby.CoopSession.Notice(Ui.Loc.Msg("steam.invite_saved"), 6f);
            return;
        }

        JoinNow(lobby);
    }

    /// <summary>1.6.0: a dropped client getting back to its host (NetworkManager's auto-reconnect), even mid-run:
    /// still in that lobby -> straight to the host; else back into the lobby first (the host only takes members).</summary>
    internal static void ReconnectTo(ulong lobby, ulong host)
    {
        if (!_ready || lobby == 0) return;
        if (State == LobbyState.Joining && _joining == lobby) return; // the last attempt's join is still on its way
        if (InLobby && LobbyId == lobby && host != 0) { NetworkManager.StartSteamClient(lobby, host); return; }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Reconnect: joining lobby {lobby} again.");
        JoinNow(lobby);
    }

    private static void JoinNow(ulong lobby)
    {
        LastError = null;
        LeaveSilently();
        NetworkManager.Stop();
        ulong call = SteamNative.SteamAPI_ISteamMatchmaking_JoinLobby(_mm, lobby);
        if (call == 0) { LastError = Ui.Loc.Msg("steam.join_denied"); return; }
        _joining = lobby;
        State = LobbyState.Joining;
        SteamCallbacks.Expect(call, SteamNative.LobbyEnter, (p, failed) =>
        {
            if (failed) { FailJoin(Ui.Loc.Msg("steam.join_noreply")); return; }
            OnLobbyEnter(p, fromCallResult: true);
        });
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Joining lobby {lobby}...");
    }

    private static void OnLobbyEnter(IntPtr p, bool fromCallResult)
    {
        ulong lobby = *(ulong*)p;
        uint response = *(uint*)((byte*)p + 16);
        // LobbyEnter_t arrives both as call result and as broadcast callback; the host also gets one for its own
        // new lobby. Only the first one for the lobby we are joining counts.
        if (State != LobbyState.Joining || lobby != _joining) return;
        _joining = 0;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] LobbyEnter lobby={lobby} response={response} ({(fromCallResult ? "call result" : "callback")}).");

        if (response != 1)
        {
            FailJoin(response switch
            {
                2 => Ui.Loc.Msg("steam.lobby_gone"),
                3 => Ui.Loc.Msg("steam.no_permission"),
                4 => Ui.Loc.Msg("steam.lobby_full"),
                _ => Ui.Loc.Msg("steam.join_failed", ("code", response)),
            }, lobby);
            return;
        }

        string? version = GetData(lobby, KeyVersion);
        ulong owner = SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyOwner(_mm, lobby);
        if (string.IsNullOrEmpty(version)) { FailJoin(Ui.Loc.Msg("steam.not_coop"), lobby); return; }
        if (version != Plugin.HandshakeVersion)
        {
            FailJoin(Ui.Loc.Msg("net.version", ("host", version), ("you", Plugin.HandshakeVersion)), lobby);
            return;
        }
        if (owner == 0 || owner == SelfId) { FailJoin(Ui.Loc.Msg("steam.own_lobby"), lobby); return; }

        LobbyId = lobby;
        HostId = owner;
        State = LobbyState.Joined;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] In lobby {lobby}, host {owner} ({FriendName(owner)}).");
        NetworkManager.StartSteamClient(lobby, owner);
    }

    private static void FailJoin(string error, ulong lobby = 0)
    {
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][steam] Join failed: {Ui.Loc.Plain(error)}");
        if (lobby != 0) SteamNative.SteamAPI_ISteamMatchmaking_LeaveLobby(_mm, lobby);
        State = LobbyState.None;
        LobbyId = 0;
        HostId = 0;
        _joining = 0;
        LastError = error;
    }

    private static void OnGameLobbyJoinRequested(IntPtr p, int size)
    {
        ulong lobby = *(ulong*)p;
        ulong friend = *(ulong*)((byte*)p + 8);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Join requested: lobby {lobby} from {friend} ({FriendName(friend)}).");
        Join(lobby);
    }

    private static void OnRichPresenceJoinRequested(IntPtr p, int size)
    {
        ulong friend = *(ulong*)p;
        string connect = Marshal.PtrToStringUTF8((IntPtr)((byte*)p + 8)) ?? "";
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Rich presence join from {friend}: '{connect}'.");
        var parts = connect.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < parts.Length; i++)
            if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out var id)) { Join(id); return; }
    }

    // ---------------- members ----------------

    private static void OnLobbyChatUpdate(IntPtr p, int size)
    {
        ulong lobby = *(ulong*)p;
        ulong changed = *(ulong*)((byte*)p + 8);
        uint change = *(uint*)((byte*)p + 24);
        if (lobby != LobbyId || !InLobby) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Lobby member {changed} ({FriendName(changed)}) change=0x{change:X}.");
        if ((change & (SteamNative.ChatLeft | SteamNative.ChatDisconnected | SteamNative.ChatKicked | SteamNative.ChatBanned)) != 0)
            (NetworkManager.Transport as SteamTransport)?.OnMemberLeft(changed);
    }

    private static void OnLobbyKicked(IntPtr p, int size)
    {
        ulong lobby = *(ulong*)p;
        if (lobby != LobbyId) return;
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][steam] Removed from lobby {lobby}.");
        (NetworkManager.Transport as SteamTransport)?.OnMemberLeft(HostId);
    }

    public static bool IsMember(ulong id)
    {
        if (!InLobby) return false;
        int n = SteamNative.SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(_mm, LobbyId);
        for (int i = 0; i < n; i++)
            if (SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(_mm, LobbyId, i) == id) return true;
        return false;
    }

    // ---------------- leave ----------------

    /// <summary>Called when the Steam transport stops (Desconectar, partner gone on the client, refusal).</summary>
    public static void Leave()
    {
        if (State == LobbyState.None) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][steam] Leaving lobby {LobbyId} ({State}).");
        LeaveSilently();
    }

    private static void LeaveSilently()
    {
        if (!_ready) return;
        if (LobbyId != 0) SteamNative.SteamAPI_ISteamMatchmaking_LeaveLobby(_mm, LobbyId);
        if (_joining != 0) SteamNative.SteamAPI_ISteamMatchmaking_LeaveLobby(_mm, _joining);
        if (State == LobbyState.Hosting || State == LobbyState.Creating) SteamNative.SteamAPI_ISteamFriends_ClearRichPresence(_friends);
        State = LobbyState.None;
        LobbyId = 0;
        HostId = 0;
        _joining = 0;
        _openOverlayWhenReady = false;
    }

    // ---------------- helpers ----------------

    public static string FriendName(ulong id)
    {
        if (!_ready || id == 0) return "?";
        try { return SteamNative.FromUtf8(SteamNative.SteamAPI_ISteamFriends_GetFriendPersonaName(_friends, id)) ?? "?"; }
        catch { return "?"; }
    }

    private static void SetData(string key, string value)
    {
        var k = SteamNative.Utf8(key);
        var v = SteamNative.Utf8(value);
        fixed (byte* kp = k)
        fixed (byte* vp = v)
            SteamNative.SteamAPI_ISteamMatchmaking_SetLobbyData(_mm, LobbyId, kp, vp);
    }

    private static string? GetData(ulong lobby, string key)
    {
        var k = SteamNative.Utf8(key);
        fixed (byte* kp = k)
            return SteamNative.FromUtf8(SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyData(_mm, lobby, kp));
    }

    private static void SetRichPresence(string key, string value)
    {
        var k = SteamNative.Utf8(key);
        var v = SteamNative.Utf8(value);
        fixed (byte* kp = k)
        fixed (byte* vp = v)
            SteamNative.SteamAPI_ISteamFriends_SetRichPresence(_friends, kp, vp);
    }

    /// <summary>A lobby code typed or pasted in the panel: the 64-bit lobby SteamID (17 digits, starts with 1).</summary>
    public static bool TryParseCode(string text, out ulong lobby)
    {
        lobby = 0;
        text = text.Trim();
        return text.Length >= 15 && text.All(char.IsDigit) && ulong.TryParse(text, out lobby) && lobby != 0;
    }
}
