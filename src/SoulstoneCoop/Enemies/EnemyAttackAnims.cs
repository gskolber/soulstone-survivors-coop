using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Regular enemies' attack animations on the guests (docs/inimigos-ataque-notas.md, proximos-investigacao.md section 3).
///
/// Bosses, elites and specials already replay their whole cast on the guest (EnemyCasts). Regular enemies didn't: the
/// guest saw them walk up and hit without swinging. Replaying every regular cast would be expensive, but the animation
/// alone is tiny: every skill type's DoActivate (MeleeSkill 0x5371A0, ProjectileSkill, AreaSkill...) plays it through
/// SkillAnimationTrait.Activate(AnimatorComponent) (0x55E710), which is just AnimatorComponent.CrossFade(AnimationName,
/// Fade, AnimationLayer) (0x5DCBF0): Animator.StringToHash(name), then Animator.CrossFade(hash, fade, layer) on each
/// animator of the component.
///
/// Host: a postfix on SkillAnimationTrait.Activate. When the animator belongs to a tracked enemy without ReplayCasts,
/// queue (netId, state hash, fade, layer); at most one per enemy every MinIntervalPerEnemy, and only for enemies within
/// [Coop] ReplicaCastRadius of some guest. The queue goes out once per frame as EnemyAttackAnim (Unreliable: a lost
/// swing is not worth a resend).
/// Guest: the puppet's animators CrossFade to that state (only if the controller has it: Animator.HasState), and the
/// controller's own transitions take it back to locomotion, as on the host.
/// Layout (after the type byte): count u8 | count * (netId u16 | hash i32 | fade f32 | layer u8).
/// </summary>
public static class EnemyAttackAnims
{
    private const float MinIntervalPerEnemy = 0.15f;
    private const int MaxPerPacket = 100;
    private const float TelemetryInterval = 10f;

    private struct Pending { public ushort NetId; public int Hash; public float Fade; public byte Layer; }

    private static readonly List<Pending> Outbox = new();
    private static readonly Dictionary<IntPtr, IntPtr> EntityByAnimator = new(); // AnimatorComponent -> its Entity (Zero: none)
    private static readonly Dictionary<IntPtr, int> HashByTrait = new();
    private static readonly Dictionary<ushort, float> LastSent = new();
    private static readonly List<Vector3> GuestPositions = new();

    private static float _telemetryTimer;
    private static int _captured, _throttled, _culled, _skippedReplay, _sentEntries, _sentPackets, _captureErrors;
    private static int _recv, _played, _noPuppet, _noState, _playErrors;

    // ---------------- host ----------------

    internal static void OnAnimationActivated(SkillAnimationTrait trait, AnimatorComponent animator)
    {
        if (!CoopRole.IsHostSide || !NetworkManager.HasPeer || trait == null || animator == null) return;

        var animPtr = animator.Pointer;
        if (!EntityByAnimator.TryGetValue(animPtr, out var entityPtr))
        {
            var owner = animator.GetComponentInParent<Entity>();
            entityPtr = owner != null ? owner.Pointer : IntPtr.Zero;
            EntityByAnimator[animPtr] = entityPtr; // pooled objects keep their components: stable
        }
        if (entityPtr == IntPtr.Zero || !HostEnemySync.TryGetTracked(entityPtr, out var t) || t.DiedSent) return;
        if (t.ReplayCasts) { _skippedReplay++; return; } // the whole cast goes through EnemyCasts already

        float now = Time.unscaledTime;
        if (LastSent.TryGetValue(t.NetId, out float last) && now - last < MinIntervalPerEnemy) { _throttled++; return; }

        if (GuestPositions.Count > 0)
        {
            float radius = Plugin.ReplicaCastRadius?.Value ?? 0f;
            if (radius > 0f)
            {
                var p = t.Entity.transform.position;
                bool near = false;
                foreach (var g in GuestPositions)
                    if ((g - p).sqrMagnitude <= radius * radius) { near = true; break; }
                if (!near) { _culled++; return; }
            }
        }

        var tp = trait.Pointer;
        if (!HashByTrait.TryGetValue(tp, out int hash))
        {
            string name = trait.AnimationName ?? "";
            if (name.Length == 0) { HashByTrait[tp] = 0; return; }
            hash = Animator.StringToHash(name);
            HashByTrait[tp] = hash;
        }
        if (hash == 0) return;

        LastSent[t.NetId] = now;
        Outbox.Add(new Pending { NetId = t.NetId, Hash = hash, Fade = trait.Fade, Layer = (byte)Mathf.Clamp(trait.AnimationLayer, 0, 255) });
        _captured++;
    }

    internal static void CountCaptureError() => _captureErrors++;

    public static void Tick(float unscaledDeltaTime)
    {
        try
        {
            if (CoopRole.IsHostSide) HostTick();
            else { Outbox.Clear(); EntityByAnimator.Clear(); }
        }
        catch (Exception ex)
        {
            if (_captureErrors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][enemyanims] Tick: {ex}");
        }

        _telemetryTimer += unscaledDeltaTime;
        if (_telemetryTimer < TelemetryInterval) return;
        _telemetryTimer = 0f;
        if (_captured + _sentEntries + _recv + _captureErrors + _playErrors == 0) return;
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][enemyanims] {NetworkManager.Role}: captured={_captured} sent={_sentEntries}/{_sentPackets}p throttled={_throttled} " +
            $"culled={_culled} skippedReplay={_skippedReplay} captureErrors={_captureErrors} | recv={_recv} played={_played} " +
            $"noPuppet={_noPuppet} noState={_noState} playErrors={_playErrors}");
        _captured = _throttled = _culled = _skippedReplay = _sentEntries = _sentPackets = 0;
        _recv = _played = _noPuppet = _noState = 0;
    }

    private static void HostTick()
    {
        // Where the guests are, for the next frame's culling (cheap: up to 7 positions).
        GuestPositions.Clear();
        foreach (byte slot in Players.Others)
            if (CoopSession.InRun(slot) && RemoteProxy.TryGetPosition(slot, out var pos, out _)) GuestPositions.Add(pos);

        if (!GameManagerUtil.HasGameManager) { Outbox.Clear(); LastSent.Clear(); EntityByAnimator.Clear(); HashByTrait.Clear(); return; }
        if (Outbox.Count == 0) return;
        if (!NetworkManager.HasPeer) { Outbox.Clear(); return; }

        for (int start = 0; start < Outbox.Count; start += MaxPerPacket)
        {
            int n = Math.Min(MaxPerPacket, Outbox.Count - start);
            var w = new NetDataWriter();
            w.Put((byte)PacketType.EnemyAttackAnim);
            w.Put((byte)n);
            for (int i = start; i < start + n; i++)
            {
                var q = Outbox[i];
                w.Put(q.NetId);
                w.Put(q.Hash);
                w.Put(q.Fade);
                w.Put(q.Layer);
            }
            NetworkManager.Send(w, DeliveryMethod.Unreliable);
            _sentPackets++;
            _sentEntries += n;
        }
        Outbox.Clear();
    }

    // ---------------- guest ----------------

    public static void OnPacket(NetDataReader r)
    {
        int n = r.GetByte();
        bool forUs = NetworkManager.Role == NetRole.Client && PacketRouter.From == Players.HostSlot;
        for (int i = 0; i < n; i++)
        {
            ushort netId = r.GetUShort();
            int hash = r.GetInt();
            float fade = r.GetFloat();
            int layer = r.GetByte();
            _recv++;
            if (!forUs) continue;
            try { Play(netId, hash, fade, layer); }
            catch (Exception ex)
            {
                if (_playErrors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][enemyanims] Playing on #{netId}: {ex.Message}");
            }
        }
    }

    private static void Play(ushort netId, int hash, float fade, int layer)
    {
        var p = ClientPuppets.TryGetLive(netId);
        var comp = p != null && p.Entity != null ? p.Entity._animator : null;
        var list = comp != null ? comp.Animators : null;
        if (list == null) { _noPuppet++; return; }
        bool any = false;
        foreach (var a in list)
        {
            if (a == null || !a.isActiveAndEnabled || layer >= a.layerCount || !a.HasState(layer, hash)) continue;
            a.CrossFade(hash, fade, layer);
            any = true;
        }
        if (any) _played++; else _noState++;
    }

    // ---------------- patch (by hand: a failure only turns this off) ----------------

    internal static void ApplyPatch(Harmony harmony)
    {
        try
        {
            MethodInfo? target = null;
            foreach (var mi in typeof(SkillAnimationTrait).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (mi.Name == nameof(SkillAnimationTrait.Activate) && mi.GetParameters().Length == 1) { target = mi; break; }
            if (target == null) throw new MissingMethodException(nameof(SkillAnimationTrait), nameof(SkillAnimationTrait.Activate));
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(EnemyAttackAnimPatch), nameof(EnemyAttackAnimPatch.Postfix)));
            Plugin.StaticLog.LogInfo("[SoulstoneCoop][enemyanims] SkillAnimationTrait.Activate patched.");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][enemyanims] Could not patch SkillAnimationTrait.Activate (regular enemies won't animate on guests): {ex.Message}");
        }
    }
}

/// <summary>Host: every skill animation of every entity goes through here; the filter is a few dictionary lookups.
/// Signature: two references, trampoline-safe.</summary>
public static class EnemyAttackAnimPatch
{
    public static void Postfix(SkillAnimationTrait __instance, AnimatorComponent animator)
    {
        try { EnemyAttackAnims.OnAnimationActivated(__instance, animator); }
        catch (Exception ex)
        {
            EnemyAttackAnims.CountCaptureError();
            Plugin.StaticLog.LogError($"[SoulstoneCoop] EnemyAttackAnimPatch: {ex}");
        }
    }
}
