using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Life;

/// <summary>
/// Issue #7: what the party panel (Ui/PartyHud) shows of each other player besides its health (PartnerHealth) and
/// its down/revive state (PartnerHealth flags, CoopLife progress): its level, its progress to the next level and its
/// revives left (the game's death guard charges, InventoryData.DeathGuards). Levels are per player: the XP is shared
/// (team XP) but each side applies its own XP stat, so levels can drift apart.
/// Layout: type u8 | level u16 | xp progress u8 (0..255 = 0..1 of the way to the next level) | death guards u8.
/// Sent on change at most every 0.25 s (4 Hz) and every 2 s as a refresher; the host relays each client's.
/// </summary>
public static class PartyStatus
{
    private const float MinInterval = 0.25f;
    private const float RefreshInterval = 2f;

    /// <summary>What we know of one other player's run.</summary>
    public sealed class View
    {
        public bool Known;
        public int Level;
        public float Xp;      // 0..1 to the next level
        public int Revives;   // death guards left
    }

    private static readonly View Unknown = new();
    private static readonly Dictionary<byte, View> Views = new();

    /// <summary>That player's last status (an empty, unknown view if nothing came yet).</summary>
    public static View Of(byte slot) => Views.TryGetValue(slot, out var v) ? v : Unknown;

    private static float _sinceSend;
    private static int _lastLevel = -1, _lastXp = -1, _lastRevives = -1;
    private static bool _hooked;
    private static int _sent, _recv;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerConnected += _ => ResendOwn();
        Players.Left += slot => Views.Remove(slot);
    }

    /// <summary>A new ghost of that player (a new run): unknown until its next packet, and ours goes out now.</summary>
    public static void Reset(byte slot)
    {
        Views.Remove(slot);
        ResendOwn();
    }

    private static void ResendOwn()
    {
        _lastLevel = _lastXp = _lastRevives = -1;
        _sinceSend = RefreshInterval;
    }

    /// <summary>The local player's level, progress (0..1) and death guards; false outside a run.</summary>
    internal static bool ReadLocal(out int level, out float xp, out int revives)
    {
        level = 0; xp = 0f; revives = 0;
        var local = GameState.LocalPlayerEntity;
        var e = local != null ? local._experience : null;
        if (e == null) return false;
        level = e.Level;
        float need = e._requiredExperience;
        xp = need > 0f ? Mathf.Clamp01(e._experience / need) : 0f;
        var inv = local!._inventory != null ? local._inventory.InventoryData : null;
        revives = inv != null ? Mathf.Max(0, inv.DeathGuards) : 0;
        return true;
    }

    public static void Tick(float unscaledDeltaTime)
    {
        try
        {
            if (!NetworkManager.HasPeer || !Lobby.CoopSession.LocalInRun) return;
            _sinceSend += unscaledDeltaTime;
            if (_sinceSend < MinInterval) return;
            if (!ReadLocal(out int level, out float xp, out int revives)) return;
            int xpByte = Mathf.RoundToInt(xp * 255f);
            bool changed = level != _lastLevel || revives != _lastRevives || Mathf.Abs(xpByte - _lastXp) >= 3;
            if (!changed && _sinceSend < RefreshInterval) return;

            var w = new NetDataWriter();
            w.Put((byte)PacketType.PartyStatus);
            w.Put((ushort)Mathf.Clamp(level, 0, ushort.MaxValue));
            w.Put((byte)xpByte);
            w.Put((byte)Mathf.Clamp(revives, 0, 255));
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            _sinceSend = 0f;
            _lastLevel = level;
            _lastXp = xpByte;
            _lastRevives = revives;
            _sent++;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][party] PartyStatus.Tick: {ex.Message}");
        }
    }

    public static void OnPacket(NetDataReader r)
    {
        int level = r.GetUShort();
        float xp = r.GetByte() / 255f;
        int revives = r.GetByte();
        byte slot = PacketRouter.From;
        if (!Players.IsPresent(slot)) return;
        if (!Views.TryGetValue(slot, out var v)) Views[slot] = v = new View();
        bool first = !v.Known;
        v.Known = true;
        v.Level = level;
        v.Xp = xp;
        v.Revives = revives;
        _recv++;
        if (first) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][party] First status of slot {slot}: level {level} xp {xp:P0} revives {revives}.");
    }

    /// <summary>For the 5 s telemetry line of PartyHud.</summary>
    internal static string TakeStats()
    {
        string s = $"sent={_sent} recv={_recv}";
        _sent = _recv = 0;
        return s;
    }
}
