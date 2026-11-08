using LiteNetLib;
using LiteNetLib.Utils;

// Phase 9 bot swarm: fake co-op clients for load-testing a real host (by IP) without one game per player.
// Each bot is its own LiteNetLib connection that speaks just enough of the mod's protocol to be a player:
//   handshake (key + version), Welcome (its slot), Hello (the host's own character, so its ghost builds),
//   LobbyState (follows the host: menu / loading / in the run), PlayerState (circles the host at 20 Hz while in the
//   run), ClientState (alive, every 0.5 s: the host makes its proxy), PartnerHealth (1 s) and PauseState (none).
// It never takes damage or casts. Every 5 s it prints what the host sends it (packets and KB/s): the host's upload per client.
//
// Usage: dotnet run -- [--host 127.0.0.1] [--bots 3] [--version 0.8.0]
// The version must be the mod's PluginVersion (the handshake refuses anything else). The host needs
// [Coop] MaxPlayers >= bots + the real clients + 1.

const string ConnectionKey = "soulstonecoop-v2"; // must match LiteNetTransport.ConnectionKey
const int Port = 7777;

string host = "127.0.0.1";
int botCount = 3;
string version = "0.8.0";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--host") host = args[i + 1];
    else if (args[i] == "--bots" && int.TryParse(args[i + 1], out var n)) botCount = Math.Clamp(n, 1, 7);
    else if (args[i] == "--version") version = args[i + 1];
}

Console.WriteLine($"[bots] {botCount} bot(s) -> {host}:{Port}, mod version {version}. Ctrl+C to stop.");
var bots = new List<Bot>();
for (int i = 0; i < botCount; i++) bots.Add(new Bot(i + 1, host, Port, ConnectionKey, version));

var sw = System.Diagnostics.Stopwatch.StartNew();
double lastReport = 0;
while (true)
{
    double t = sw.Elapsed.TotalSeconds;
    foreach (var b in bots) b.Tick(t);
    if (t - lastReport >= 5)
    {
        double span = t - lastReport;
        lastReport = t;
        foreach (var b in bots) b.Report(span);
    }
    Thread.Sleep(10);
}

sealed class Bot
{
    // PacketType values the bot reads or sends (Net/PacketRouter.cs).
    const byte PlayerState = 2, PauseState = 6, ClientState = 13, Hello = 30, LobbyState = 31, RunStart = 32;
    const byte PartnerHealth = 50, Relayed = 100, PlayersState = 101, Welcome = 102;
    const byte Menu = 0, Loading = 1, InRun = 2;

    readonly int _index;
    readonly string _host, _key, _version;
    readonly int _port;
    readonly EventBasedNetListener _listener = new();
    readonly NetManager _manager;
    NetPeer? _peer;

    byte _slot = 255;
    byte _hostPlace = Menu, _place = Menu, _sentPlace = 255;
    int _hostRunId, _hostStage; // the host's match (its LobbyState): ours says the same, the bot always follows it
    double _inRunAt = -1;
    float[]? _hostPos;
    // The host's Hello, reused (with our own name) so the ghost builds as a real character.
    byte _charId;
    string _charName = "", _weapon = "", _skin = "", _weaponSkin = "";
    bool _helloSent, _pauseSent;
    double _lastPos, _lastState, _lastHealth;
    long _bytes, _packets, _relayed;

    public Bot(int index, string host, int port, string key, string version)
    {
        _index = index; _host = host; _port = port; _key = key; _version = version;
        _manager = new NetManager(_listener) { DisconnectTimeout = 10000 };
        _listener.PeerConnectedEvent += p => { _peer = p; Console.WriteLine($"[bot{_index}] connected"); };
        _listener.PeerDisconnectedEvent += (p, info) =>
        {
            string why = info.Reason.ToString();
            try { if (info.AdditionalData != null && info.AdditionalData.TryGetString(out var s) && s.Length > 0) why += $" ({s})"; } catch { }
            Console.WriteLine($"[bot{_index}] disconnected: {why}");
            _peer = null;
            _slot = 255;
            _helloSent = _pauseSent = false;
            _sentPlace = 255;
        };
        _listener.NetworkReceiveEvent += OnReceive;
        _manager.Start();
        Connect();
    }

    void Connect()
    {
        var w = new NetDataWriter();
        w.Put(_key);
        w.Put(_version);
        _manager.Connect(_host, _port, w);
    }

    void OnReceive(NetPeer peer, NetPacketReader r, byte channel, DeliveryMethod method)
    {
        try
        {
            _bytes += r.AvailableBytes + 4; // + a rough UDP/LiteNetLib header share
            _packets++;
            byte type = r.GetByte();
            switch (type)
            {
                case Welcome:
                    _slot = r.GetByte();
                    Console.WriteLine($"[bot{_index}] Welcome: slot {_slot} of up to {r.GetByte()}");
                    break;
                case Hello:
                    r.GetString(); // host name
                    _charId = r.GetByte();
                    _charName = r.GetString();
                    _weapon = r.GetString();
                    _skin = r.GetString();
                    _weaponSkin = r.GetString();
                    _helloSent = false; // resend ours with the host's character
                    break;
                case LobbyState:
                    _hostPlace = r.GetByte();
                    _hostRunId = r.GetInt();
                    _hostStage = r.GetInt();
                    break;
                case RunStart:
                    _place = Loading;
                    break;
                case PlayersState:
                {
                    int n = r.GetByte();
                    for (int i = 0; i < n; i++)
                    {
                        byte s = r.GetByte();
                        float x = r.GetFloat(), y = r.GetFloat(), z = r.GetFloat();
                        r.GetFloat(); r.GetUInt();
                        if (s == 0) _hostPos = new[] { x, y, z };
                    }
                    break;
                }
                case Relayed:
                    _relayed++;
                    break;
            }
        }
        catch { /* a format the bot doesn't know: ignore */ }
        finally { r.Recycle(); }
    }

    public void Tick(double t)
    {
        _manager.PollEvents();
        if (_peer == null || _slot == 255) return;

        // Follow the host: it loads, we load; it's in the run, we are a second later; it's back in the menu, so are we.
        if (_hostPlace == Menu) { _place = Menu; _inRunAt = -1; }
        else if (_hostPlace == Loading && _place == Menu) _place = Loading;
        else if (_hostPlace == InRun && _place != InRun)
        {
            if (_inRunAt < 0) _inRunAt = t + 1.0;
            else if (t >= _inRunAt) { _place = InRun; _inRunAt = -1; }
        }

        if (!_helloSent)
        {
            var w = new NetDataWriter();
            w.Put(Hello);
            w.Put($"Bot {_index}");
            w.Put(_charId);
            w.Put(_charName);
            w.Put(_weapon);
            w.Put(_skin);
            w.Put(_weaponSkin);
            w.Put(_version);
            _peer.Send(w, DeliveryMethod.ReliableOrdered);
            _helloSent = true;
        }
        if (_place != _sentPlace)
        {
            var w = new NetDataWriter();
            w.Put(LobbyState);
            w.Put(_place);
            w.Put(_place == Menu ? 0 : _hostRunId);
            w.Put(_place == Menu ? 0 : _hostStage);
            _peer.Send(w, DeliveryMethod.ReliableOrdered);
            _sentPlace = _place;
            Console.WriteLine($"[bot{_index}] place {_place}");
        }
        if (!_pauseSent)
        {
            var w = new NetDataWriter();
            w.Put(PauseState);
            w.Put((byte)0);
            _peer.Send(w, DeliveryMethod.ReliableOrdered);
            _pauseSent = true;
        }
        if (_place != InRun || _hostPos == null) return;

        if (t - _lastPos >= 0.05)
        {
            _lastPos = t;
            float radius = 2.5f + _index;
            float angle = (float)t * (0.6f + 0.1f * _index) + _index;
            var w = new NetDataWriter();
            w.Put(PlayerState);
            w.Put(_hostPos[0] + MathF.Cos(angle) * radius);
            w.Put(_hostPos[1]);
            w.Put(_hostPos[2] + MathF.Sin(angle) * radius);
            w.Put(angle * 57.29578f + 90f);
            w.Put((uint)(t * 1000));
            _peer.Send(w, DeliveryMethod.Sequenced);
        }
        if (t - _lastState >= 0.5)
        {
            _lastState = t;
            var w = new NetDataWriter();
            w.Put(ClientState);
            w.Put((byte)0);    // alive
            w.Put(500f); w.Put(500f); // hp, max hp
            w.Put(0f); w.Put(0f); w.Put(0f); // armor, damage reduction, block
            w.Put(0f); w.Put(0f); // status dampening
            _peer.Send(w, DeliveryMethod.ReliableOrdered);
        }
        if (t - _lastHealth >= 1.0)
        {
            _lastHealth = t;
            var w = new NetDataWriter();
            w.Put(PartnerHealth);
            w.Put(500f); w.Put(500f); w.Put((byte)0);
            _peer.Send(w, DeliveryMethod.ReliableOrdered);
        }
    }

    public void Report(double span)
    {
        string ping = _peer != null ? $"{_peer.Ping * 2} ms" : "-";
        Console.WriteLine($"[bot{_index}] slot={(_slot == 255 ? "-" : _slot.ToString())} place={_place} host={_hostPlace} ping={ping} " +
                          $"from host: {_packets / span:F0} pkt/s, {_bytes / span / 1024:F1} KB/s (relayed {_relayed / span:F0}/s)");
        _bytes = _packets = _relayed = 0;
        if (_peer == null && _manager.ConnectedPeersCount == 0) Connect();
    }
}
