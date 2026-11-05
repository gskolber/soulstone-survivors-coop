using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Enemies;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Skills;

/// <summary>One local cast, enough to replay it on the remote caster. Layout (after the type byte):
/// key string | skillType i32 | level u8 | countA u8 | countB u8 | scale f32 | yaw f32 | flags u8 |
/// [source v3] [ground v3] [front v3] [direction v3] | targetKind u8 | [targetNetId u16 | targetSlot u8]</summary>
public sealed class CastMessage
{
    public const byte FSource = 1, FGround = 2, FFront = 4, FDirection = 8, FChannel = 16, FFrontFunc = 32, FGroundFunc = 64;
    public const byte TargetNone = 0, TargetSelf = 1, TargetEnemy = 2, TargetPlayer = 3; // phase 9: TargetPlayer carries the slot

    public string Key = "";
    public int SkillType;
    public byte Level, CountA, CountB;
    public float Scale, Yaw;
    public byte Flags;
    public Vector3 Source, Ground, Front, Direction;
    public byte TargetKind;
    public ushort TargetNetId;
    public byte TargetSlot;

    public bool Has(byte flag) => (Flags & flag) != 0;

    public void Write(NetDataWriter w)
    {
        w.Put(Key);
        w.Put(SkillType);
        w.Put(Level); w.Put(CountA); w.Put(CountB);
        w.Put(Scale); w.Put(Yaw);
        w.Put(Flags);
        if (Has(FSource)) PutV3(w, Source);
        if (Has(FGround)) PutV3(w, Ground);
        if (Has(FFront)) PutV3(w, Front);
        if (Has(FDirection)) PutV3(w, Direction);
        w.Put(TargetKind);
        if (TargetKind == TargetEnemy) w.Put(TargetNetId);
        else if (TargetKind == TargetPlayer) w.Put(TargetSlot);
    }

    public static CastMessage Read(NetDataReader r)
    {
        var m = new CastMessage
        {
            Key = r.GetString(),
            SkillType = r.GetInt(),
            Level = r.GetByte(), CountA = r.GetByte(), CountB = r.GetByte(),
            Scale = r.GetFloat(), Yaw = r.GetFloat(),
            Flags = r.GetByte(),
        };
        if (m.Has(FSource)) m.Source = GetV3(r);
        if (m.Has(FGround)) m.Ground = GetV3(r);
        if (m.Has(FFront)) m.Front = GetV3(r);
        if (m.Has(FDirection)) m.Direction = GetV3(r);
        m.TargetKind = r.GetByte();
        if (m.TargetKind == TargetEnemy) m.TargetNetId = r.GetUShort();
        else if (m.TargetKind == TargetPlayer) m.TargetSlot = r.GetByte();
        return m;
    }

    private static void PutV3(NetDataWriter w, Vector3 v) { w.Put(v.x); w.Put(v.y); w.Put(v.z); }
    private static Vector3 GetV3(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat());
}

/// <summary>
/// Phase 4C, both directions: every skill the LOCAL player casts (the SkillCasterComponent.CastSkill funnel:
/// auto-cast, CastSkillAtTarget, input, procs, chain casts, multicasts) is sent as SkillCast and replayed on
/// the other side's RemoteCaster. Visual only - the damage keeps flowing through phase 3 (ClientDamage /
/// DamageToClient). Channeled skills also get a SkillChannelEnd when the local channel stops.
/// SkillCast and SkillChannelEnd share ReliableOrdered: an end must never overtake its start.
/// While a channel is open, its aim (what the skill's GetTargetFrontPosition / GetTargetFrontPositionGround funcs
/// return, re-read by ChannelingSkill.OnUpdate 0x516B00 every frame) goes out ~10 times a second as SkillChannelAim
/// (Sequenced), so the partner's beam follows the cursor instead of staying where the cast started.
/// </summary>
public static class CastSync
{
    private const float TelemetryInterval = 5f;

    private static IntPtr _localCasterPtr;
    private static IntPtr _localPlayerPtr;
    private static readonly HashSet<string> LoggedKeys = new();

    private sealed class OpenChannel
    {
        public string Key = "";
        public Il2CppSystem.Func<bool> Func = null!;
        public ChannelingSkill? Skill;
        public bool SeenTrue;
        public float Age;
        // Aim: the cast's own funcs, invoked with the skill that was cast.
        public Skill? Owner;
        public Il2CppSystem.Func<Skill, Vector3>? FrontFunc, GroundFunc;
        public float AimTimer;
    }

    private const float AimInterval = 0.1f;
    private static int _aimSent, _aimRecv, _aimErrors;

    private static readonly Dictionary<string, OpenChannel> LocalChannels = new();

    // Telemetry window
    private static float _telemetryTimer;
    private static int _sent, _recv, _played, _noCaster, _noSkill, _rejected, _errors, _skippedSummon, _captureErrors;
    private static int _channelEndsSent, _channelEndsRecv, _blockedEffects, _blockedDamage, _bytesSent, _culledFar;

    internal static void CountBlockedEffects() => _blockedEffects++;
    internal static void CountBlockedDamage() => _blockedDamage++;

    /// <summary>Cheap filter for the CastSkill postfix, which runs for every caster in the game.</summary>
    internal static bool IsLocalCaster(SkillCasterComponent c) => _localCasterPtr != IntPtr.Zero && c.Pointer == _localCasterPtr;

    public static void Tick(float unscaledDeltaTime)
    {
        if (NetworkManager.Role == NetRole.None) return;
        try
        {
            var local = GameState.LocalPlayerEntity;
            var caster = local != null ? local._skillCaster : null;
            _localCasterPtr = caster != null ? caster.Pointer : IntPtr.Zero;
            _localPlayerPtr = local != null ? local.Pointer : IntPtr.Zero;
            PollChannels(unscaledDeltaTime);
            Watchdog();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][casts] CastSync.Tick: {ex}");
        }
        RemoteCaster.Tick(unscaledDeltaTime);

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer < TelemetryInterval) return;
        _telemetryTimer = 0f;
        int failed = _noCaster + _noSkill + _rejected + _errors;
        int numbersHidden = ReplicaCombatText.TakeHidden();
        if (_sent + _recv + failed + _blockedEffects + _blockedDamage + _captureErrors + numbersHidden > 0)
        {
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][casts] {NetworkManager.Role}: sent={_sent} ({_bytesSent} B) recv={_recv} played={_played} failed={failed} " +
                $"(noCaster={_noCaster} noSkill={_noSkill} rejected={_rejected} error={_errors}) skippedSummon={_skippedSummon} " +
                $"captureErrors={_captureErrors} channelEnds={_channelEndsSent}/{_channelEndsRecv} openChannels={RemoteCaster.OpenChannels}/{LocalChannels.Count} " +
                $"aim={_aimSent}/{_aimRecv}{(_aimErrors > 0 ? $" aimErrors={_aimErrors}" : "")} " +
                $"blockedEffects={_blockedEffects} blockedDamage={_blockedDamage} numbersHidden={numbersHidden} culledFar={_culledFar} caster={RemoteCaster.Active} copies={RemoteCaster.CopyCount} " +
                $"ghostAnim={RemoteCaster.GhostAnimPlayed}/{RemoteCaster.GhostAnimPlayed + RemoteCaster.GhostAnimMissing}");
            RemoteCaster.GhostAnimPlayed = RemoteCaster.GhostAnimMissing = 0;
        }
        _sent = _recv = _played = _noCaster = _noSkill = _rejected = _errors = _skippedSummon = _captureErrors = 0;
        _channelEndsSent = _channelEndsRecv = _blockedEffects = _blockedDamage = _bytesSent = _culledFar = 0;
        _aimSent = _aimRecv = 0;
    }

    // ---------------- sender ----------------

    /// <summary>"ArcaneMissiles-Skill(Clone)" -> "ArcaneMissiles-Skill", the name under Resources/Skill-Prefabs.</summary>
    internal static string KeyOf(Skill skill)
    {
        string name = skill.gameObject.name;
        int i = name.IndexOf("(Clone)", StringComparison.Ordinal);
        return (i >= 0 ? name.Substring(0, i) : name).Trim();
    }

    internal static void OnLocalCast(Skill skill, SkillActivationParameters p)
    {
        if (!NetworkManager.HasPeer || skill == null || p == null) return;
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;

        string key = KeyOf(skill);
        var type = skill.SkillType;
        // A summon would be a real Entity on the other side, dealing its own damage: not replicated. Not only the
        // Summon-tagged skills: the ascensions' summons (Legionnaire's centurions, Demon Hunter's demons, Apocalypse
        // knights) are set up from power-up data without that tag, and replaying them broke the others' casting.
        if (Summons(skill))
        {
            _skippedSummon++;
            if (LoggedSummonKeys.Add(key)) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] '{key}' summons units (type={type}): not sent, the partners see the units through SummonSync.");
            return;
        }

        var m = new CastMessage { Key = key, SkillType = (int)type, Yaw = local.transform.eulerAngles.y };
        m.Level = (byte)Mathf.Clamp(skill.SkillLevel, 0, 255);
        var proj = skill.TryCast<ProjectileSkill>();
        if (proj != null) m.CountA = ClampByte(proj.ProjectilesToFire);
        var area = skill.TryCast<AreaSkill>();
        if (area != null) m.CountA = ClampByte(area.AreasToSpawn);
        var multi = skill.TryCast<MultiProjectileSkill>();
        if (multi != null) { m.CountA = ClampByte(multi.MinProjectilesQuantity); m.CountB = ClampByte(multi.MaxProjectilesQuantity); }
        var stats = local._stats;
        if (stats != null) m.Scale = UnitUtil.GetAdjustedScale(skill.Modifiers, stats);

        // Raw reads: the interop getters for these Nullable<Vector3> fields throw (see ActivationParamsRaw).
        if (ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.SourcePositionOverride, out m.Source)) m.Flags |= CastMessage.FSource;
        if (ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetGroundPosition, out m.Ground)) m.Flags |= CastMessage.FGround;
        if (ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetFrontPosition, out m.Front)) m.Flags |= CastMessage.FFront;
        if (ActivationParamsRaw.TryGetVector(p, ActivationParamsRaw.TargetDirection, out m.Direction)) m.Flags |= CastMessage.FDirection;
        var channel = ActivationParamsRaw.HasReference(p, ActivationParamsRaw.IsBeingChanneled) ? p.IsBeingChanneled : null;
        if (channel != null) m.Flags |= CastMessage.FChannel;
        if (ActivationParamsRaw.HasReference(p, ActivationParamsRaw.GetTargetFrontPosition)) m.Flags |= CastMessage.FFrontFunc;
        if (ActivationParamsRaw.HasReference(p, ActivationParamsRaw.GetTargetFrontPositionGround)) m.Flags |= CastMessage.FGroundFunc;

        var target = p.TargetEntity;
        if (target != null)
        {
            if (target.Pointer == _localPlayerPtr) m.TargetKind = CastMessage.TargetSelf;
            else if (CoopRole.IsHostSide && RemoteProxy.TryGetProxySlot(target, out m.TargetSlot)) m.TargetKind = CastMessage.TargetPlayer;
            else if (CoopRole.IsHostSide ? HostEnemySync.TryGetNetId(target, out m.TargetNetId) : ClientPuppets.TryGetNetId(target, out m.TargetNetId))
                m.TargetKind = CastMessage.TargetEnemy;
        }

        var w = new NetDataWriter();
        w.Put((byte)PacketType.SkillCast);
        m.Write(w);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _sent++;
        _bytesSent += w.Length;

        if (channel != null && !LocalChannels.ContainsKey(key))
        {
            Il2CppSystem.Func<Skill, Vector3>? front = null, ground = null;
            try
            {
                if (m.Has(CastMessage.FFrontFunc)) front = p.GetTargetFrontPosition;
                if (m.Has(CastMessage.FGroundFunc)) ground = p.GetTargetFrontPositionGround;
            }
            catch (Exception ex) { _aimErrors++; Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Aim funcs of '{key}': {ex.Message}"); }
            LocalChannels[key] = new OpenChannel
            {
                Key = key, Func = channel, Skill = skill.TryCast<ChannelingSkill>(),
                Owner = skill, FrontFunc = front, GroundFunc = ground,
            };
        }

        if (LoggedKeys.Add(key))
        {
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][casts] First local cast of '{key}' ({skill.GetIl2CppType().Name}, type={type}, level={m.Level}, " +
                $"counts={m.CountA}/{m.CountB}, scale={m.Scale:F2}, flags={m.Flags}, target={m.TargetKind}).");
        }
    }

    /// <summary>A channel ends when the func the skill polls says so (the auto-cast's IsChanneling), or when the
    /// skill itself stopped channeling (max duration) after having started.</summary>
    private static void PollChannels(float dt)
    {
        if (LocalChannels.Count == 0) return;
        List<string>? ended = null;
        foreach (var ch in LocalChannels.Values)
        {
            ch.Age += dt;
            bool on;
            try
            {
                on = ch.Func.Invoke();
                if (ch.Skill != null)
                {
                    if (ch.Skill.LastBeingChanneled) ch.SeenTrue = true;
                    else if (ch.SeenTrue || ch.Age > 1f) on = false;
                }
            }
            catch { on = false; }
            if (on) { SendAim(ch, dt); continue; }
            (ended ??= new List<string>()).Add(ch.Key);
        }
        if (ended == null) return;
        foreach (var key in ended)
        {
            LocalChannels.Remove(key);
            if (!NetworkManager.HasPeer) continue;
            var w = new NetDataWriter();
            w.Put((byte)PacketType.SkillChannelEnd);
            w.Put(key);
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
            _channelEndsSent++;
        }
    }

    /// <summary>An open channel's current aim, ~10 times a second. The funcs are the ones the local skill polls itself
    /// (the auto-aim / cursor position), so this is exactly where our own beam points right now.</summary>
    private static void SendAim(OpenChannel ch, float dt)
    {
        if (ch.Owner == null || (ch.FrontFunc == null && ch.GroundFunc == null)) return;
        ch.AimTimer += dt;
        if (ch.AimTimer < AimInterval || !NetworkManager.HasPeer) return;
        ch.AimTimer = 0f;
        byte flags = 0;
        Vector3 front = default, ground = default;
        try
        {
            if (ch.FrontFunc != null) { front = ch.FrontFunc.Invoke(ch.Owner); flags |= 1; }
            if (ch.GroundFunc != null) { ground = ch.GroundFunc.Invoke(ch.Owner); flags |= 2; }
        }
        catch (Exception ex)
        {
            // A func that throws once will again: stop sending this channel's aim (the cast's start aim stays).
            ch.FrontFunc = ch.GroundFunc = null;
            if (_aimErrors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Aim of '{ch.Key}': {ex.Message}");
            return;
        }
        var w = new NetDataWriter();
        w.Put((byte)PacketType.SkillChannelAim);
        w.Put(ch.Key);
        w.Put(flags);
        if ((flags & 1) != 0) { w.Put(front.x); w.Put(front.y); w.Put(front.z); }
        if ((flags & 2) != 0) { w.Put(ground.x); w.Put(ground.y); w.Put(ground.z); }
        NetworkManager.Send(w, DeliveryMethod.Sequenced);
        _aimSent++;
        _bytesSent += w.Length;
    }

    private static byte ClampByte(int v) => (byte)Mathf.Clamp(v, 0, 255);

    // ---------------- summons: never replayed ----------------

    private static readonly HashSet<string> LoggedSummonKeys = new();
    private static readonly Dictionary<IntPtr, bool> SummonCache = new();

    /// <summary>A new match (RemoteCaster.OnMatchStarted): pointers of the old scene's skills may be reused.</summary>
    internal static void ClearSummonCache() => SummonCache.Clear();

    /// <summary>Does this skill (an instance or a prefab) bring units into the world: the Summon tag, or any
    /// SummonEntity*Effect under it. Cached per object (skill instances and prefabs live for the whole match).</summary>
    internal static bool Summons(Skill? skill)
    {
        if (skill == null) return false;
        if ((skill.SkillType & SkillType.Summon) != 0) return true;
        var ptr = skill.Pointer;
        if (SummonCache.TryGetValue(ptr, out bool cached)) return cached;
        bool found = false;
        try
        {
            foreach (var c in skill.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                if (global::SoulstoneCoop.Summons.SummonSync.SummonPrefabsOf(c) != null) { found = true; break; }
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Checking '{skill.gameObject.name}' for summons: {ex.Message}"); }
        SummonCache[ptr] = found;
        return found;
    }

    // ---------------- watchdog: the local player stopped casting ----------------

    private const float StallSeconds = 12f;
    private const float StallReportInterval = 60f;
    private static float _lastLocalCast = -1f, _stallReportAt, _watchdogTimer;
    private static int _stalls;

    internal static void NoteLocalCast() => _lastLocalCast = Time.time;

    /// <summary>In a co-op run, alive, not paused, auto-cast on and enemies around, but no cast of ours for
    /// StallSeconds of game time: the "can't attack any more" state (the ascension summons report). Logs what
    /// could be in the way, and turns off any other player auto-cast that was never set up (its UpdateAll would throw
    /// for every frame, which stops the loop that also runs ours).</summary>
    private static void Watchdog()
    {
        // Perf: once a second is plenty (the cast time itself comes from the capture patch).
        _watchdogTimer += Time.unscaledDeltaTime;
        if (_watchdogTimer < 1f) return;
        _watchdogTimer = 0f;
        if (!NetworkManager.HasPeer || !GameManagerUtil.HasGameManager || !Lobby.CoopSession.LocalInRun || Time.timeScale <= 0f) { _lastLocalCast = -1f; return; }
        var local = GameState.LocalPlayerEntity;
        var auto = local != null ? local._playerSkillAutoCast : null;
        if (local == null || auto == null || auto.ForceStopAutoCasting || Life.CoopLife.LocalOut) { _lastLocalCast = Time.time; return; }
        if (_lastLocalCast < 0f || Time.time < _lastLocalCast) { _lastLocalCast = Time.time; return; }
        float idle = Time.time - _lastLocalCast;
        if (idle < StallSeconds || Time.unscaledTime < _stallReportAt) return;

        int near = 0;
        var all = EntityRegistry.AllEntities;
        var at = local.transform.position;
        if (all != null)
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (e != null && ClientSpawnBlock.IsEnemyTeam(e) && (e.transform.position - at).sqrMagnitude < 15f * 15f) near++;
            }
        if (near == 0) return; // nothing to hit: not casting is normal
        _stallReportAt = Time.unscaledTime + StallReportInterval;
        _stalls++;

        var autos = new List<string>();
        int healed = 0;
        try
        {
            foreach (var a in UnityEngine.Object.FindObjectsOfType<PlayerSkillAutoCastComponent>())
            {
                if (a == null) continue;
                var owner = a._casterEntity;
                bool mine = a.Pointer == auto.Pointer;
                autos.Add($"{a.gameObject.name}{(mine ? "(ours)" : owner == null ? "(NOT SET UP)" : $"(of {owner.gameObject.name})")}");
                if (!mine && owner == null && a.enabled) { a.enabled = false; healed++; }
            }
        }
        catch (Exception ex) { autos.Add("scan failed: " + ex.Message); }
        Plugin.StaticLog.LogWarning(
            $"[SoulstoneCoop][casts] Local player has not cast for {idle:F0} s with {near} enemies within 15 u (stall #{_stalls}). " +
            $"autoCasts=[{string.Join(", ", autos)}]{(healed > 0 ? $" - {healed} never-set-up auto-cast(s) turned off" : "")}; " +
            $"remote casters={RemoteCaster.Active} copies=[{RemoteCaster.DescribeCopies()}] replicaSummonsRemoved={RemoteCaster.ReplicaSummonsRemoved}. " +
            "Please send this log.");
    }

    // ---------------- receiver ----------------

    public static void OnSkillCast(NetDataReader reader)
    {
        var m = CastMessage.Read(reader);
        _recv++;
        switch (RemoteCaster.Play(PacketRouter.From, m))
        {
            case RemoteCaster.PlayResult.Played: _played++; break;
            case RemoteCaster.PlayResult.NoCaster: _noCaster++; break;
            case RemoteCaster.PlayResult.NoSkill: _noSkill++; break;
            case RemoteCaster.PlayResult.Rejected: _rejected++; break;
            case RemoteCaster.PlayResult.Culled: _culledFar++; break;
            default: _errors++; break;
        }
    }

    public static void OnSkillChannelAim(NetDataReader reader)
    {
        string key = reader.GetString();
        byte flags = reader.GetByte();
        Vector3 front = default, ground = default;
        if ((flags & 1) != 0) front = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        if ((flags & 2) != 0) ground = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        _aimRecv++;
        RemoteCaster.OnChannelAim(PacketRouter.From, key, flags, front, ground);
    }

    public static void OnSkillChannelEnd(NetDataReader reader)
    {
        string key = reader.GetString();
        _channelEndsRecv++;
        RemoteCaster.OnChannelEnd(PacketRouter.From, key);
    }

    internal static void CountCaptureError() => _captureErrors++;
}

/// <summary>Capture point: the funnel every player cast goes through, with the parameters already complete
/// (CastSkillAtTarget fills them in before calling it). Runs for every caster, so the filter is a pointer compare.</summary>
[HarmonyPatch(typeof(SkillCasterComponent), nameof(SkillCasterComponent.CastSkill))]
public static class LocalCastCapturePatch
{
    [HarmonyPostfix]
    public static void Postfix(SkillCasterComponent __instance, Skill skill, SkillActivationParameters parameters, bool __result)
    {
        try
        {
            if (!__result || __instance == null || !CastSync.IsLocalCaster(__instance)) return;
            CastSync.NoteLocalCast();
            CastSync.OnLocalCast(skill, parameters);
        }
        catch (Exception ex)
        {
            CastSync.CountCaptureError();
            Plugin.StaticLog.LogError($"[SoulstoneCoop] LocalCastCapturePatch: {ex}");
        }
    }
}
