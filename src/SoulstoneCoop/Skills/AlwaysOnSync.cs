using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Skills;

/// <summary>
/// The partner's always-on skills (docs/sempre-ligadas-notas.md, proximos-investigacao.md section 4).
///
/// RotatingShieldSkill never goes through SkillCasterComponent.CastSkill (no DoActivate): its OnUpdate (0x55D830),
/// called every frame by the owner's SkillCasterComponent.UpdateAll, spawns and spins the projectiles by itself. So
/// CastSync, which replays casts, never saw it and the partner's shields were invisible.
///
/// Sender: every 0.5 s, the local player's RotatingShieldSkills (SkillCasterComponent.Skills) with what shapes them
/// (level, ExtraProjectiles, RotationSpeed, ProjectileCooldown, adjusted scale). Sent as SkillAlwaysOn when it changes
/// and every 3 s (ReliableOrdered, relayed by the host to the other clients); an empty list when they're gone.
/// Receiver: RemoteCaster.SetAlwaysOn keeps the list per player; RemoteCaster.Tick runs a copy of each skill on that
/// player's caster and calls its OnUpdate every frame (the copies are not AddSkill'ed, so nothing else runs them), and
/// destroys it (Recycle ends the projectiles) when it leaves the list or the player is down. The caster's hits are
/// blocked like every replica (RemoteCasterDamageBlockPatch): visual only.
/// Layout (after the type byte): count u8 | count * (key str | skillType i32 | level u8 | extra i16 | speed f32 |
/// cooldown f32 | scale f32).
/// </summary>
public static class AlwaysOnSync
{
    private const float ScanInterval = 0.5f;
    private const float RefreshInterval = 3f;

    private static float _scanTimer, _refreshTimer;
    private static string _lastSignature = "";
    private static int _sent, _recv, _errors;
    private static readonly List<(string Key, RemoteCaster.AlwaysOnSpec Spec)> Scratch = new();

    public static void Tick(float dt)
    {
        if (NetworkManager.Role == NetRole.None) return;
        _scanTimer += dt;
        _refreshTimer += dt;
        if (_scanTimer < ScanInterval) return;
        _scanTimer = 0f;
        try
        {
            Collect(Scratch);
            string sig = Signature(Scratch);
            if (!NetworkManager.HasPeer) { _lastSignature = ""; return; }
            bool changed = sig != _lastSignature;
            if (!changed && (sig.Length == 0 || _refreshTimer < RefreshInterval)) return;
            Send(Scratch);
            if (changed)
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Own always-on skills: {(sig.Length > 0 ? sig : "none")} (sent {_sent}, recv {_recv}, errors {_errors}).");
            _lastSignature = sig;
            _refreshTimer = 0f;
        }
        catch (Exception ex)
        {
            if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] AlwaysOnSync.Tick: {ex}");
        }
    }

    private static void Collect(List<(string Key, RemoteCaster.AlwaysOnSpec Spec)> into)
    {
        into.Clear();
        var local = GameState.LocalPlayerEntity;
        if (local == null || !GameManagerUtil.HasGameManager || Life.CoopLife.LocalOut) return;
        var skills = local._skillCaster != null ? local._skillCaster.Skills : null;
        if (skills == null) return;
        var stats = local._stats;
        foreach (var s in skills)
        {
            var rs = s != null ? s.TryCast<RotatingShieldSkill>() : null;
            if (rs == null) continue;
            float scale = stats != null ? UnitUtil.GetAdjustedScale(s!.Modifiers, stats) : 0f;
            into.Add((CastSync.KeyOf(s!), new RemoteCaster.AlwaysOnSpec(
                (int)s!.SkillType, (byte)Mathf.Clamp(s.SkillLevel, 0, 255), rs.ExtraProjectiles, rs.RotationSpeed, rs.ProjectileCooldown, scale)));
        }
    }

    private static string Signature(List<(string Key, RemoteCaster.AlwaysOnSpec Spec)> list)
    {
        if (list.Count == 0) return "";
        var parts = new List<string>(list.Count);
        foreach (var (key, s) in list)
            parts.Add($"{key}(lv{s.Level} +{s.Extra} spd{s.Speed:F0} cd{s.Cooldown:F2} x{s.Scale:F2})");
        parts.Sort(StringComparer.Ordinal);
        return string.Join(", ", parts);
    }

    private static void Send(List<(string Key, RemoteCaster.AlwaysOnSpec Spec)> list)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.SkillAlwaysOn);
        w.Put((byte)Math.Min(list.Count, 255));
        int n = 0;
        foreach (var (key, s) in list)
        {
            if (n++ >= 255) break;
            w.Put(key);
            w.Put(s.Type);
            w.Put(s.Level);
            w.Put((short)Mathf.Clamp(s.Extra, short.MinValue, short.MaxValue));
            w.Put(s.Speed);
            w.Put(s.Cooldown);
            w.Put(s.Scale);
        }
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _sent++;
    }

    public static void OnPacket(NetDataReader r)
    {
        int n = r.GetByte();
        var list = new List<(string Key, RemoteCaster.AlwaysOnSpec Spec)>(n);
        for (int i = 0; i < n; i++)
        {
            string key = r.GetString();
            int type = r.GetInt();
            byte level = r.GetByte();
            int extra = r.GetShort();
            float speed = r.GetFloat(), cooldown = r.GetFloat(), scale = r.GetFloat();
            list.Add((key, new RemoteCaster.AlwaysOnSpec(type, level, extra, speed, cooldown, scale)));
        }
        _recv++;
        RemoteCaster.SetAlwaysOn(PacketRouter.From, list);
    }
}
