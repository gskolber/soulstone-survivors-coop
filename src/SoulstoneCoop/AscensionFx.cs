using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop;

/// <summary>
/// The partner's ascension effects: the flash, the sound and the world light change (docs/ascensao-notas.md).
///
/// Every character mechanic (SiegeExpertComponent, DemonicBindingComponent, ArcanePower, DivineInspiration,
/// Concentration, Mechatron, MyrmidonFavor, NaturesGuardian, SoulHarvest, CursedShrine, GhostShip, MachineSpirit,
/// ElementalOverload, AncestralRitual, Mana, Rage, Reanimation, ChaosEnergy, CallOfTheWild) plays its effects from its
/// activation, its end (FinishOrClearAscension) or its static UpdateAll, through a few shared helpers:
/// SkillUtil.RunOneShotVfx (by VfxReferences or by path), SkillUtil.RunFireAndForgetVfxReference,
/// AudioUtil.PostEventFireAndForget (by position or object) and WorldLightProcessor.ChangeLight.
///
/// Sender: those mechanic methods are wrapped (prefix + finalizer, a depth counter); while one runs, every call to those
/// helpers is sent as AscensionFx: what (VFX name or path, sound event, light values), where relative to our player
/// and whether it follows the player. They only run for the local player's own mechanic, so everything inside is ours.
/// At most MaxPerSecond.
/// Receiver: the same helper runs at the partner's ghost (VFX by its loaded prefab of that name, or the path), the
/// sound on the ghost, the light through a WorldLightProcessor of its own per partner (built from the values sent).
/// Effects of a partner farther than MaxDistance are skipped, the light never is.
/// Layout (after the type byte): kind u8 | name str | a f32 | b f32 | c f32 | rel 3f | rot 4f | scale f32 | flags u8
///   [kind Light: light color 4f | fog color 4f | temperature f32 | intensity f32 | environment f32 | duration f32].
/// </summary>
public static class AscensionFx
{
    private const byte KindVfx = 1, KindVfxPath = 2, KindFireAndForget = 3, KindSound = 4, KindLight = 5;
    private const byte FlagFollow = 1, FlagDisable = 2, FlagToOriginal = 4;
    private const int MaxPerSecond = 30;
    private const float MaxDistance = 45f;

    private static readonly Type[] MechanicTypes =
    {
        typeof(SiegeExpertComponent), typeof(DemonicBindingComponent), typeof(ArcanePowerComponent), typeof(DivineInspirationComponent),
        typeof(ConcentrationComponent), typeof(MechatronComponent), typeof(MyrmidonFavorComponent), typeof(NaturesGuardianComponent),
        typeof(SoulHarvestComponent), typeof(CursedShrineComponent), typeof(GhostShipComponent), typeof(MachineSpiritComponent),
        typeof(ElementalOverloadComponent), typeof(AncestralRitualComponent), typeof(ManaComponent), typeof(RageComponent),
        typeof(ReanimationComponent), typeof(ChaosEnergyComponent), typeof(CallOfTheWildComponent),
    };
    private static readonly HashSet<string> MechanicMethods = new()
    {
        "Activate", "ActivateAscension", "FinishOrClearAscension", "FinishAscension", "Ride", "Unride", "ForceAscensionToActivate", "UpdateAll",
    };

    [ThreadStatic] private static int _depth;
    private static bool _replaying;
    private static float _windowStart;
    private static int _sentThisWindow;
    private static int _sent, _recv, _played, _missing, _errors;
    private static float _telemetryTimer;

    internal static void Enter() => _depth++;
    internal static void Exit() { if (_depth > 0) _depth--; }
    private static bool Capturing => _depth > 0 && !_replaying && NetworkManager.HasPeer;

    // =====================================================================================
    // Sender
    // =====================================================================================

    private static bool Budget()
    {
        float now = Time.unscaledTime;
        if (now - _windowStart >= 1f) { _windowStart = now; _sentThisWindow = 0; }
        if (_sentThisWindow >= MaxPerSecond) return false;
        _sentThisWindow++;
        return true;
    }

    private static bool Relative(Vector3 pos, Transform? parent, out Vector3 rel, out byte flags)
    {
        rel = default;
        flags = 0;
        var local = GameState.LocalPlayerEntity;
        if (local == null) return false;
        var lt = local.transform;
        rel = pos - lt.position;
        if (parent != null && (parent.Pointer == lt.Pointer || parent.IsChildOf(lt))) flags |= FlagFollow;
        return rel.sqrMagnitude < 60f * 60f || (flags & FlagFollow) != 0;
    }

    private static void Send(byte kind, string name, float a, float b, float c, Vector3 rel, Quaternion rot, float scale, byte flags, WorldLightFeedback? light = null)
    {
        if (!Budget()) return;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.AscensionFx);
        w.Put(kind); w.Put(name ?? "");
        w.Put(a); w.Put(b); w.Put(c);
        w.Put(rel.x); w.Put(rel.y); w.Put(rel.z);
        w.Put(rot.x); w.Put(rot.y); w.Put(rot.z); w.Put(rot.w);
        w.Put(scale); w.Put(flags);
        if (kind == KindLight)
        {
            var lc = light != null ? light.LightColorWhileFavorActive : Color.white;
            var fc = light != null ? light.VolumetricFogColorWhileFavorActive : Color.white;
            w.Put(lc.r); w.Put(lc.g); w.Put(lc.b); w.Put(lc.a);
            w.Put(fc.r); w.Put(fc.g); w.Put(fc.b); w.Put(fc.a);
            w.Put(light != null ? light.LightTemperatureWhileFavorActive : 6500f);
            w.Put(light != null ? light.LightIntensityWhileFavorActive : 1f);
            w.Put(light != null ? light.EnvironmentalLightIntensity : 1f);
            w.Put(light != null ? light.TransitionDuration : 1f);
        }
        NetworkManager.Send(w, DeliveryMethod.ReliableUnordered);
        _sent++;
    }

    internal static void OnVfx(VfxReferences? vfx, float delayToStore, Vector3 pos, Quaternion rot, Transform? parent)
    {
        if (!Capturing || vfx == null || !Relative(pos, parent, out var rel, out var flags)) return;
        Send(KindVfx, Enemies.EnemyPrefabKeys.StripClone(vfx.gameObject.name), delayToStore, 0f, 0f, rel, rot, 1f, flags);
    }

    internal static void OnVfxPath(string? path, float delayToStore, Vector3 pos, Quaternion rot, Transform? parent)
    {
        if (!Capturing || string.IsNullOrEmpty(path) || !Relative(pos, parent, out var rel, out var flags)) return;
        Send(KindVfxPath, path!, delayToStore, 0f, 0f, rel, rot, 1f, flags);
    }

    internal static void OnFireAndForget(VfxReferences? vfx, float delayToEnable, float duration, float delayToStore, Vector3 pos, Quaternion rot,
        float scale, Transform? parent, bool disable)
    {
        if (!Capturing || vfx == null || !Relative(pos, parent, out var rel, out var flags)) return;
        if (disable) flags |= FlagDisable;
        Send(KindFireAndForget, Enemies.EnemyPrefabKeys.StripClone(vfx.gameObject.name), delayToEnable, duration, delayToStore, rel, rot, scale, flags);
    }

    internal static void OnSound(string? eventName)
    {
        if (!Capturing || string.IsNullOrEmpty(eventName)) return;
        Send(KindSound, eventName!, 0f, 0f, 0f, Vector3.zero, Quaternion.identity, 1f, FlagFollow);
    }

    internal static void OnLight(WorldLightProcessor? processor, bool toOriginal)
    {
        if (!Capturing || processor == null) return;
        Send(KindLight, "", 0f, 0f, 0f, Vector3.zero, Quaternion.identity, 1f, toOriginal ? FlagToOriginal : (byte)0, processor.WorldLightFeedback);
    }

    // =====================================================================================
    // Receiver
    // =====================================================================================

    private static readonly Dictionary<string, VfxReferences?> VfxCache = new();
    private static readonly HashSet<string> LoggedMissing = new();
    private static readonly Dictionary<byte, WorldLightProcessor> Lights = new();

    public static void OnPacket(NetDataReader r)
    {
        byte kind = r.GetByte();
        string name = r.GetString();
        float a = r.GetFloat(), b = r.GetFloat(), c = r.GetFloat();
        var rel = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        var rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
        float scale = r.GetFloat();
        byte flags = r.GetByte();
        Color lc = default, fc = default;
        float temp = 0f, intensity = 0f, env = 0f, duration = 0f;
        if (kind == KindLight)
        {
            lc = new Color(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
            fc = new Color(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
            temp = r.GetFloat(); intensity = r.GetFloat(); env = r.GetFloat(); duration = r.GetFloat();
        }
        _recv++;
        byte from = PacketRouter.From;
        var who = CoopSession.Get(from);
        var ghost = who?.Ghost;
        if (ghost == null || !CoopSession.LocalInRun || !GameManagerUtil.HasGameManager) return;
        try
        {
            _replaying = true;
            if (kind == KindLight) { PlayLight(from, (flags & FlagToOriginal) != 0, lc, fc, temp, intensity, env, duration); return; }
            var local = GameState.LocalPlayerEntity;
            if (local != null && (ghost.transform.position - local.transform.position).sqrMagnitude > MaxDistance * MaxDistance) return;
            var gt = ghost.transform;
            bool follow = (flags & FlagFollow) != 0;
            var pos = gt.position + rel;
            var parent = follow ? gt : null;
            var runner = JobRunner.Instance;
            if (runner == null) return;
            switch (kind)
            {
                case KindVfx:
                {
                    var vfx = ResolveVfx(name);
                    if (vfx == null) return;
                    var e = SkillUtil.RunOneShotVfx(vfx, a, pos, rot, parent!);
                    if (e != null) { runner.StartCoroutine(e); _played++; }
                    break;
                }
                case KindVfxPath:
                {
                    var e = SkillUtil.RunOneShotVfx(name, a, pos, rot, parent!);
                    if (e != null) { runner.StartCoroutine(e); _played++; }
                    break;
                }
                case KindFireAndForget:
                {
                    var vfx = ResolveVfx(name);
                    if (vfx == null) return;
                    var e = SkillUtil.RunFireAndForgetVfxReference(vfx, a, b, c, pos, rot, scale, parent!, (flags & FlagDisable) != 0);
                    if (e != null) { runner.StartCoroutine(e); _played++; }
                    break;
                }
                case KindSound:
                    AudioUtil.PostEventFireAndForget(name, ghost);
                    _played++;
                    break;
            }
        }
        catch (Exception ex)
        {
            if (_errors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ascension] Effect '{name}' (kind {kind}) of slot {from}: {ex.Message}");
        }
        finally { _replaying = false; }
    }

    private static VfxReferences? ResolveVfx(string name)
    {
        if (VfxCache.TryGetValue(name, out var cached)) return cached;
        VfxReferences? found = null;
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<VfxReferences>());
            for (int i = 0; i < all.Length && found == null; i++)
            {
                var o = all[i];
                if (o == null) continue;
                var v = new VfxReferences(o.Pointer);
                if (!v.gameObject.scene.IsValid() && v.gameObject.name == name) found = v;
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ascension] Resolving effect '{name}': {ex.Message}"); }
        VfxCache[name] = found;
        if (found == null)
        {
            _missing++;
            if (LoggedMissing.Add(name)) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] Effect '{name}' isn't loaded here: not shown.");
        }
        return found;
    }

    /// <summary>The world light while that partner's mechanic is on: a processor of our own per partner, so its
    /// "back to original" returns what that same processor changed.</summary>
    private static void PlayLight(byte slot, bool toOriginal, Color lc, Color fc, float temp, float intensity, float env, float duration)
    {
        if (toOriginal)
        {
            if (Lights.TryGetValue(slot, out var p)) { p.ChangeLight(true); _played++; }
            return;
        }
        var fb = new WorldLightFeedback
        {
            LightColorWhileFavorActive = lc,
            VolumetricFogColorWhileFavorActive = fc,
            LightTemperatureWhileFavorActive = temp,
            LightIntensityWhileFavorActive = intensity,
            EnvironmentalLightIntensity = env,
            TransitionDuration = duration,
            TransitionCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f),
        };
        var proc = new WorldLightProcessor(fb);
        Lights[slot] = proc;
        proc.ChangeLight(false);
        _played++;
    }

    /// <summary>Our scene changed or the partner left: its light goes back (the next scene starts with its own).</summary>
    internal static void ResetLights(bool restore)
    {
        if (restore) foreach (var p in Lights.Values) { try { p.ChangeLight(true); } catch { } }
        Lights.Clear();
    }

    public static void Tick(float dt)
    {
        if (Lights.Count > 0 && (!CoopSession.LocalInRun || !GameManagerUtil.HasGameManager)) ResetLights(false);
        _telemetryTimer += dt;
        if (_telemetryTimer < 10f) return;
        _telemetryTimer = 0f;
        if (_sent + _recv + _errors == 0) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] effects sent={_sent} recv={_recv} played={_played} missing={_missing} errors={_errors}");
        _sent = _recv = _played = 0;
    }

    // =====================================================================================
    // Patches (by hand, each alone)
    // =====================================================================================

    internal static void ApplyPatches(Harmony harmony)
    {
        Players.Left += slot =>
        {
            if (Lights.TryGetValue(slot, out var p)) { try { p.ChangeLight(true); } catch { } Lights.Remove(slot); }
        };
        int windows = 0, hooks = 0, failed = 0;
        foreach (var type in MechanicTypes)
        {
            string typeName = type.Name;
            foreach (var mi in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!MechanicMethods.Contains(mi.Name) || mi.GetParameters().Length != 0) continue;
                try
                {
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(AscensionFxPatches), nameof(AscensionFxPatches.WindowPrefix)),
                        finalizer: new HarmonyMethod(typeof(AscensionFxPatches), nameof(AscensionFxPatches.WindowFinalizer)));
                    windows++;
                }
                catch (Exception ex) { failed++; Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ascension] Window on {typeName}.{mi.Name}: {ex.Message}"); }
            }
        }

        void Hook(Type type, string method, Func<ParameterInfo[], bool> match, string prefix)
        {
            try
            {
                MethodInfo? target = null;
                foreach (var mi in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (mi.Name == method && match(mi.GetParameters())) { target = mi; break; }
                if (target == null) throw new MissingMethodException(type.Name, method);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(AscensionFxPatches), prefix));
                hooks++;
            }
            catch (Exception ex) { failed++; Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ascension] Hook {type.Name}.{method}: {ex.Message}"); }
        }
        Hook(typeof(SkillUtil), nameof(SkillUtil.RunOneShotVfx), p => p.Length == 5 && p[0].ParameterType == typeof(VfxReferences), nameof(AscensionFxPatches.VfxPrefix));
        Hook(typeof(SkillUtil), nameof(SkillUtil.RunOneShotVfx), p => p.Length == 5 && p[0].ParameterType == typeof(string), nameof(AscensionFxPatches.VfxPathPrefix));
        Hook(typeof(SkillUtil), nameof(SkillUtil.RunFireAndForgetVfxReference), p => p.Length == 9, nameof(AscensionFxPatches.FireAndForgetPrefix));
        Hook(typeof(AudioUtil), nameof(AudioUtil.PostEventFireAndForget), p => p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(Vector3), nameof(AscensionFxPatches.SoundAtPrefix));
        Hook(typeof(AudioUtil), nameof(AudioUtil.PostEventFireAndForget), p => p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(GameObject), nameof(AscensionFxPatches.SoundOnPrefix));
        Hook(typeof(WorldLightProcessor), nameof(WorldLightProcessor.ChangeLight), p => p.Length == 1, nameof(AscensionFxPatches.LightPrefix));
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ascension] Effect capture: {windows} mechanic windows, {hooks}/6 effect hooks, {failed} failed.");
    }
}

/// <summary>Signatures: references, floats, bools, Vector3 (12 bytes) and Quaternion (16 bytes, both passed by
/// pointer): trampoline-safe. Every body in try/catch.</summary>
public static class AscensionFxPatches
{
    public static void WindowPrefix() => AscensionFx.Enter();

    public static Exception? WindowFinalizer(Exception? __exception)
    {
        AscensionFx.Exit();
        return __exception;
    }

    public static void VfxPrefix(VfxReferences vfx, float delayToStore, Vector3 position, Quaternion rotation, Transform parent)
    {
        try { AscensionFx.OnVfx(vfx, delayToStore, position, rotation, parent); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] AscensionFx.VfxPrefix: {ex.Message}"); }
    }

    public static void VfxPathPrefix(string vfxPath, float delayToStore, Vector3 position, Quaternion rotation, Transform parent)
    {
        try { AscensionFx.OnVfxPath(vfxPath, delayToStore, position, rotation, parent); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] AscensionFx.VfxPathPrefix: {ex.Message}"); }
    }

    public static void FireAndForgetPrefix(VfxReferences vfxReference, float delayToEnable, float duration, float delayToStore, Vector3 vfxPosition,
        Quaternion vfxRotation, float adjustedScale, Transform vfxParent, bool disableObject)
    {
        try { AscensionFx.OnFireAndForget(vfxReference, delayToEnable, duration, delayToStore, vfxPosition, vfxRotation, adjustedScale, vfxParent, disableObject); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] AscensionFx.FireAndForgetPrefix: {ex.Message}"); }
    }

    public static void SoundAtPrefix(string eventName)
    {
        try { AscensionFx.OnSound(eventName); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] AscensionFx.SoundAtPrefix: {ex.Message}"); }
    }

    public static void SoundOnPrefix(string eventName)
    {
        try { AscensionFx.OnSound(eventName); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] AscensionFx.SoundOnPrefix: {ex.Message}"); }
    }

    public static void LightPrefix(WorldLightProcessor __instance, bool changeToOriginal)
    {
        try { AscensionFx.OnLight(__instance, changeToOriginal); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] AscensionFx.LightPrefix: {ex.Message}"); }
    }
}
