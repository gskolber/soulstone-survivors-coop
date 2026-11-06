using LiteNetLib;
using LiteNetLib.Utils;

namespace SoulstoneCoop.Net;

public enum NetRole
{
    None,
    Host,
    Client
}

/// <summary>What the co-op panel shows about the transport itself (the lobby state is in CoopSession).</summary>
public enum NetStatus
{
    Idle,
    Hosting,     // listening, no client yet
    Connecting,  // client: retrying until the host answers
    Connected,
    Rejected,    // client: the host refused us (version, full); LastError says why
}

/// <summary>
/// The one place the mod talks to the network through. Phase 7: the actual link is an ITransport (LiteNetLib by
/// IP, or Steam P2P); everything above (Send, the events, Role/Status for the panel) is the same for both.
/// Phase 9: a star around the host. The host holds up to MaxClients clients, each on a slot (Players); Send goes to
/// every client, SendTo/SendExcept to one or all but one, and Relay forwards a client's packet to the others inside
/// the Relayed envelope (PacketRouter). A client only ever talks to the host (slot 0).
/// </summary>
public static class NetworkManager
{
    public const int Port = LiteNetTransport.Port;

    public static NetRole Role { get; private set; } = NetRole.None;
    public static ITransport? Transport { get; private set; }

    /// <summary>Somebody is there (host: at least one client; client: the host). Replaces the old `RemotePeer != null`.</summary>
    public static bool HasPeer => Transport?.HasPeer == true;

    /// <summary>Slots of the connected peers (host: the clients; client: just 0).</summary>
    public static IReadOnlyList<byte> PeerSlots => Transport?.PeerSlots ?? Array.Empty<byte>();

    /// <summary>[Coop] MaxPlayers: how many players the host's session holds, the host included (2-8).</summary>
    public static int MaxPlayers => Math.Clamp(Plugin.MaxPlayers?.Value ?? 8, 2, Players.MaxSlots);

    public static int MaxClients => MaxPlayers - 1;

    public static NetStatus Status => Transport?.Status ?? _idleStatus;

    /// <summary>Last connection problem for the panel (null = none): a Ui.Loc.Msg (shown with Ui.Loc.Show), or the
    /// plain text an older host sent.</summary>
    public static string? LastError => Transport != null ? Transport.LastError : _idleError;

    // Handshake refusals, sent to the client as Ui.Loc.Msg (both transports). The wire format is unchanged (one
    // string); an older client shows the English text inside the message.
    internal static string VersionRefusal(string clientVersion) =>
        Ui.Loc.Msg("net.version", ("host", Plugin.HandshakeVersion), ("you", VersionOrOld(clientVersion)));

    internal static string FullRefusal() =>
        MaxPlayers <= 2 ? Ui.Loc.Msg("net.has_partner") : Ui.Loc.Msg("net.full", ("max", MaxPlayers));

    /// <summary>The host's own panel, after refusing a client of another version.</summary>
    internal static string RefusedClientError(string clientVersion) =>
        Ui.Loc.Msg("net.refused_client", ("v", VersionOrOld(clientVersion)), ("mine", Plugin.HandshakeVersion));

    private static string VersionOrOld(string version) => version.Length > 0 ? version : Ui.Loc.Msg("net.old");

    /// <summary>IP the client is trying to reach (for the panel).</summary>
    public static string? TargetAddress { get; private set; }

    /// <summary>"IP" or "Steam" while a transport is up, null otherwise.</summary>
    public static string? Kind => Transport?.Kind;

    /// <summary>Client: the round trip to the host. Host: the worst one among the clients (-1 = unknown / nobody).</summary>
    public static int PingMs
    {
        get
        {
            var t = Transport;
            if (t == null) return -1;
            int worst = -1;
            foreach (var s in t.PeerSlots) worst = Math.Max(worst, t.PingMs(s));
            return worst;
        }
    }

    public static int PingOf(byte slot) => Transport?.PingMs(slot) ?? -1;

    /// <summary>Phase 8: seconds since anything arrived (host: from the quietest client; 0 without anyone).</summary>
    public static float SilenceSeconds
    {
        get
        {
            var t = Transport;
            if (t == null) return 0f;
            float worst = 0f;
            foreach (var s in t.PeerSlots) worst = Math.Max(worst, t.SilenceSeconds(s));
            return worst;
        }
    }

    public static float SilenceOf(byte slot) => Transport?.SilenceSeconds(slot) ?? 0f;

    /// <summary>Phase 8: past this much silence the panel and the banner say "conexão instável" (both transports
    /// send at least a ping per second; the drop itself comes much later: 10 s on IP, 15 s on Steam).</summary>
    public const float UnstableAfterSeconds = 2f;

    public static bool Unstable => HasPeer && SilenceSeconds >= UnstableAfterSeconds;

    public static bool IsUnstable(byte slot) => SilenceOf(slot) >= UnstableAfterSeconds;

    /// <summary>A peer finished the handshake (host: a client, with its slot; client: the host, slot 0). On the host
    /// the Welcome has already gone out to it.</summary>
    public static event System.Action<byte>? PeerConnected;

    /// <summary>A peer went away (remote disconnect, timeout, or our own Stop()). On a client this is the host:
    /// everyone else is gone with it (Players.Left fires for each right after).</summary>
    public static event System.Action<byte>? PeerDisconnected;

    // What Status/LastError show once the transport is gone (a refusal must stay on the panel).
    private static NetStatus _idleStatus = NetStatus.Idle;
    private static string? _idleError;

    private static TransportCallbacks Callbacks() => new()
    {
        Connected = OnTransportConnected,
        Disconnected = OnTransportDisconnected,
        Data = (reader, slot) => PacketRouter.Handle(reader, slot),
    };

    public static void StartHost()
    {
        CancelReconnect();
        Stop();
        _idleError = null;
        var t = LiteNetTransport.Host(Callbacks(), out var error);
        if (t == null)
        {
            _idleError = error;
            return;
        }
        Begin(t, NetRole.Host);
    }

    public static void StartClient(string address)
    {
        Stop();
        _idleError = null;
        TargetAddress = address;
        _lastTarget = (address, 0UL, 0UL);
        Begin(LiteNetTransport.Client(Callbacks(), address), NetRole.Client);
    }

    /// <summary>Steam: host the players who join our lobby.</summary>
    public static void StartSteamHost(ulong lobbyId)
    {
        CancelReconnect();
        Stop();
        _idleError = null;
        Begin(new SteamTransport(Callbacks(), lobbyId, hostId: 0), NetRole.Host);
    }

    /// <summary>Steam: connect to the owner of the lobby we just entered.</summary>
    public static void StartSteamClient(ulong lobbyId, ulong hostId)
    {
        Stop();
        _idleError = null;
        _lastTarget = (null, lobbyId, hostId);
        Begin(new SteamTransport(Callbacks(), lobbyId, hostId), NetRole.Client);
    }

    private static void Begin(ITransport t, NetRole role)
    {
        Transport = t;
        Role = role;
        Players.SetMySlot(role == NetRole.Client ? Players.NoSlot : Players.HostSlot); // a client learns its slot from the Welcome
    }

    /// <summary>Leave co-op: closes the transport and runs every module's disconnect cleanup for whoever was there.</summary>
    public static void Stop()
    {
        _clientWasConnected = false; // (a pending reconnect is cancelled only by CancelReconnect: user actions, hosting)
        var t = Transport;
        if (t == null)
        {
            Role = NetRole.None;
            Players.ClearAll();
            return;
        }
        var hadPeers = new List<byte>(t.PeerSlots);
        var role = Role;
        _idleStatus = NetStatus.Idle;
        _idleError = t.LastError;
        try { t.Stop(); }
        catch (System.Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Transport stop failed: {ex.Message}"); }
        Transport = null;
        // The role stays what it was while the modules clean up: the host-side ones (proxies, their loot team swap)
        // only act when CoopRole.IsHostSide, and a host leaving mid-run must still recycle every proxy.
        if (hadPeers.Count > 0)
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Stopped with {hadPeers.Count} peer(s) connected - running disconnect cleanup.");
            foreach (var s in hadPeers) RaiseDisconnected(s, role);
        }
        Players.ClearAll();
        Role = NetRole.None;
    }

    // ---------------- connection events ----------------

    private static void OnTransportConnected(byte slot)
    {
        if (Role == NetRole.Host)
        {
            SendWelcome(slot); // first thing on the ordered channel: the client's slot and who else is here
            Players.MarkJoined(slot);
        }
        else
        {
            Players.MarkJoined(Players.HostSlot);
            if (_reconnects > 0) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][net] Reconnected to the host (attempt {_reconnects}).");
            _clientWasConnected = true;
            _reconnects = 0;
        }
        try { PeerConnected?.Invoke(slot); }
        catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] PeerConnected({slot}) handler: {ex}"); }
    }

    private static void OnTransportDisconnected(byte slot)
    {
        if (Role == NetRole.Host)
        {
            // The others drop that player before its slot can be handed out again (same ordered channel).
            var w = new NetDataWriter();
            w.Put((byte)PacketType.PlayerLeft);
            w.Put(slot);
            Send(w, DeliveryMethod.ReliableOrdered);
        }
        RaiseDisconnected(slot, Role);
    }

    private static void RaiseDisconnected(byte slot, NetRole role)
    {
        try { PeerDisconnected?.Invoke(slot); }
        catch (System.Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] PeerDisconnected({slot}) handler: {ex}"); }
        if (role == NetRole.Client) Players.ClearAll(); // no host, no session: everybody is gone
        else Players.MarkLeft(slot);
    }

    /// <summary>Layout: type u8 | yourSlot u8 | maxPlayers u8 | n u8 | n * slot u8 (the other clients already here).</summary>
    private static void SendWelcome(byte slot)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.Welcome);
        w.Put(slot);
        w.Put((byte)MaxPlayers);
        var others = new List<byte>();
        foreach (var s in PeerSlots) if (s != slot) others.Add(s);
        w.Put((byte)others.Count);
        foreach (var s in others) w.Put(s);
        SendTo(slot, w, DeliveryMethod.ReliableOrdered);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][players] Welcome sent to slot {slot} (others: {string.Join(",", others)}).");
    }

    internal static void OnWelcome(NetDataReader r)
    {
        byte mine = r.GetByte();
        byte max = r.GetByte();
        int n = r.GetByte();
        var others = new List<byte>();
        for (int i = 0; i < n; i++) others.Add(r.GetByte());
        if (Role != NetRole.Client) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][players] Welcome: I'm slot {mine} of up to {max} (others: {string.Join(",", others)}).");
        Players.SetMySlot(mine);
        Players.SetSessionMax(max);
        foreach (var s in others) Players.MarkJoined(s);
    }

    internal static void OnPlayerLeft(NetDataReader r)
    {
        byte slot = r.GetByte();
        if (Role != NetRole.Client || slot == Players.HostSlot) return;
        Players.MarkLeft(slot);
    }

    // Call once per frame (CoopBehaviour.Update).
    public static void PollEvents()
    {
        var t = Transport;
        if (t == null) { ReconnectTick(); return; }
        t.Poll();
        if (t.WantsStop && Transport == t)
        {
            // Deferred: stopping from inside a transport callback is not safe.
            var status = t.Status;
            bool lost = Role == NetRole.Client && _clientWasConnected && status != NetStatus.Rejected;
            float keepUntil = _reconnectUntil;
            int keepN = _reconnects;
            Stop();
            if (status == NetStatus.Rejected) _idleStatus = NetStatus.Rejected;
            if (lost) ArmReconnect();
            else if (keepUntil > 0f && status != NetStatus.Rejected) { _reconnectUntil = keepUntil; _reconnects = keepN; } // a failed attempt: keep trying
        }
        LogStability();
        LogTraffic();
    }

    // ---------------- 1.6.0: client auto-reconnect (docs/reentrada-notas.md) ----------------
    // A client whose established connection drops (not refused, not its own Disconnect) tries the same host again for
    // ReconnectWindow seconds: same IP, or the same Steam lobby and host. Back in, its Hello/LobbyState tell the host
    // where it is: still in the run (CoopLife waits instead of ending it) -> the host's usual resync rebuilds the
    // enemies, ghosts and proxy, no reload; in the menu -> the host's AllowRejoin sends the run with its build.

    private const float ReconnectWindow = 45f, ReconnectEvery = 6f;
    private static (string? Address, ulong Lobby, ulong Host) _lastTarget;
    private static bool _clientWasConnected;
    private static float _reconnectUntil, _nextReconnectAt;
    private static int _reconnects;

    /// <summary>A dropped client is trying to get back to its host.</summary>
    public static bool Reconnecting => _reconnectUntil > 0f && UnityEngine.Time.unscaledTime < _reconnectUntil && !HasPeer;

    public static float ReconnectSecondsLeft => Reconnecting ? _reconnectUntil - UnityEngine.Time.unscaledTime : 0f;

    /// <summary>Debug (F1 in a client window): drop the connection as if the network failed, to test the reconnect.</summary>
    public static void DebugDrop()
    {
        if (Role != NetRole.Client || !HasPeer) { Plugin.StaticLog.LogWarning("[SoulstoneCoop][debug] F1 (drop the connection) only works in a connected client window."); return; }
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][debug] F1: dropping the connection to the host (simulated network failure).");
        bool was = _clientWasConnected;
        Stop();
        if (was) ArmReconnect();
    }

    /// <summary>The player chose something else (Disconnect, Join, Host, Invite): no automatic reconnect.</summary>
    public static void CancelReconnect()
    {
        if (_reconnectUntil > 0f) Plugin.StaticLog.LogInfo("[SoulstoneCoop][net] Reconnect cancelled.");
        _reconnectUntil = 0f;
        _reconnects = 0;
    }

    private static void ArmReconnect()
    {
        if (_lastTarget.Address == null && _lastTarget.Host == 0) return;
        float now = UnityEngine.Time.unscaledTime;
        _reconnectUntil = now + ReconnectWindow;
        _nextReconnectAt = now + 1f;
        _reconnects = 0;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][net] Connection to the host lost: reconnecting for up to {ReconnectWindow:F0} s.");
    }

    private static void ReconnectTick()
    {
        if (_reconnectUntil <= 0f) return;
        float now = UnityEngine.Time.unscaledTime;
        if (now >= _reconnectUntil)
        {
            _reconnectUntil = 0f;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][net] Could not reconnect to the host in {ReconnectWindow:F0} s ({_reconnects} attempts).");
            return;
        }
        if (now < _nextReconnectAt) return;
        _nextReconnectAt = now + ReconnectEvery;
        _reconnects++;
        float until = _reconnectUntil;
        int n = _reconnects;
        var target = _lastTarget;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][net] Reconnect attempt {n} ({(target.Address != null ? target.Address : "Steam")}).");
        try
        {
            if (target.Address != null) StartClient(target.Address);
            else Steam.SteamLobby.ReconnectTo(target.Lobby, target.Host); // back into the lobby first (the host only takes members)
        }
        catch (System.Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][net] Reconnect attempt {n}: {ex.Message}"); }
        _reconnectUntil = until;
        _reconnects = n;
    }

    private static readonly bool[] _wasUnstable = new bool[Players.MaxSlots];
    private static readonly float[] _worstSilence = new float[Players.MaxSlots];

    /// <summary>Phase 8: one log line when a link goes quiet and one when it comes back (with how long).</summary>
    private static void LogStability()
    {
        var t = Transport;
        for (byte s = 0; s < Players.MaxSlots; s++)
        {
            bool connected = false;
            if (t != null) foreach (var p in t.PeerSlots) if (p == s) { connected = true; break; }
            float silence = connected ? t!.SilenceSeconds(s) : 0f;
            bool unstable = connected && silence >= UnstableAfterSeconds;
            if (unstable && silence > _worstSilence[s]) _worstSilence[s] = silence;
            if (unstable == _wasUnstable[s]) continue;
            _wasUnstable[s] = unstable;
            if (unstable) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][net] Link unstable: nothing from slot {s} for {silence:F1}s ({Kind}).");
            else
            {
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][net] Link to slot {s} back after {_worstSilence[s]:F1}s of silence (connected={connected}).");
                _worstSilence[s] = 0f;
            }
        }
    }

    // ---------------- sending ----------------

    private static int _sendErrors;

    /// <summary>To everyone: the host sends to every client, a client to the host. Phase 8: never throws. Senders run
    /// inside our Update (and some inside game patches); one exception here used to skip the rest of the mod's frame.</summary>
    public static void Send(NetDataWriter writer, DeliveryMethod method) => SendExcept(Players.NoSlot, writer, method);

    /// <summary>To one peer only (host: one client).</summary>
    public static void SendTo(byte slot, NetDataWriter writer, DeliveryMethod method)
    {
        var t = Transport;
        if (t == null) return;
        SendOne(t, slot, writer.Data, writer.Length, method);
    }

    /// <summary>To every peer but one (host: relays and "everyone else" notices).</summary>
    public static void SendExcept(byte skip, NetDataWriter writer, DeliveryMethod method) =>
        SendBytesExcept(skip, writer.Data, writer.Length, method);

    private static readonly List<byte> SlotScratch = new();

    private static void SendBytesExcept(byte skip, byte[] data, int length, DeliveryMethod method)
    {
        var t = Transport;
        if (t == null) return;
        SlotScratch.Clear();
        SlotScratch.AddRange(t.PeerSlots); // a send can't change the list, but a copy keeps that a non-issue
        foreach (var s in SlotScratch)
            if (s != skip) SendOne(t, s, data, length, method);
    }

    private static void SendOne(ITransport t, byte slot, byte[] data, int length, DeliveryMethod method)
    {
        if (slot < Players.MaxSlots) { _upBytes[slot] += length; _upPackets[slot]++; }
        try { t.Send(slot, data, length, method); }
        catch (System.Exception ex)
        {
            if (_sendErrors++ < 10)
                Plugin.StaticLog.LogError($"[SoulstoneCoop] Send of packet {(length > 0 ? data[0] : -1)} ({length} B, {method}) to slot {slot} failed: {ex.Message}");
        }
    }

    private static byte[] _relayBuffer = new byte[2048];

    /// <summary>Host: forward a client's packet (type byte included) to the other clients, wrapped in the Relayed
    /// envelope with its origin, so they know whose ghost/caster/health it is.</summary>
    internal static void Relay(byte origin, byte[] data, int offset, int length, DeliveryMethod method)
    {
        if (Role != NetRole.Host || length <= 0) return;
        int total = length + 2;
        if (_relayBuffer.Length < total) _relayBuffer = new byte[Math.Max(total, _relayBuffer.Length * 2)];
        _relayBuffer[0] = (byte)PacketType.Relayed;
        _relayBuffer[1] = origin;
        Buffer.BlockCopy(data, offset, _relayBuffer, 2, length);
        SendBytesExcept(origin, _relayBuffer, total, method);
        RelayedPackets++;
        RelayedBytes += total;
    }

    /// <summary>Host: send one client a packet "from" another player (the envelope a relay would have), e.g. the
    /// Hello of everyone already here when it joins.</summary>
    internal static void SendRelayedTo(byte to, byte origin, NetDataWriter inner, DeliveryMethod method)
    {
        var t = Transport;
        if (Role != NetRole.Host || t == null || inner.Length <= 0) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.Relayed);
        w.Put(origin);
        w.Put(inner.Data, 0, inner.Length);
        SendOne(t, to, w.Data, w.Length, method);
    }

    internal static int RelayedPackets, RelayedBytes;

    // Phase 9: traffic per peer slot (payload bytes, without the transport headers), logged every 5 s.
    private static readonly long[] _upBytes = new long[Players.MaxSlots], _downBytes = new long[Players.MaxSlots];
    private static readonly int[] _upPackets = new int[Players.MaxSlots], _downPackets = new int[Players.MaxSlots];
    private static float _trafficTimer;

    internal static void CountReceived(byte slot, int length)
    {
        if (slot >= Players.MaxSlots) return;
        _downBytes[slot] += length;
        _downPackets[slot]++;
    }

    private static void LogTraffic()
    {
        _trafficTimer += UnityEngine.Time.unscaledDeltaTime;
        if (_trafficTimer < 5f) return;
        float secs = _trafficTimer;
        _trafficTimer = 0f;
        if (HasPeer)
        {
            var parts = new List<string>();
            long up = 0, down = 0;
            foreach (var s in PeerSlots)
            {
                parts.Add($"{s}: up {_upBytes[s] / secs / 1024f:F1} KB/s ({_upPackets[s] / secs:F0} pkt/s) down {_downBytes[s] / secs / 1024f:F1} KB/s ({_downPackets[s] / secs:F0} pkt/s)");
                up += _upBytes[s];
                down += _downBytes[s];
            }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][net] {Role} total up {up / secs / 1024f:F1} KB/s down {down / secs / 1024f:F1} KB/s | {string.Join(" | ", parts)}");
        }
        Array.Clear(_upBytes); Array.Clear(_downBytes); Array.Clear(_upPackets); Array.Clear(_downPackets);
    }
}
