using ECM2.Characters;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop.Life;

/// <summary>
/// Phase 4A: downed / revive / shared end of run. See docs/fase4a-investigacao.md.
///
/// Downed = the game's own ReviveOnDeath state: a lethal ChangeEntityHealth on an entity with
/// HealthComponent.ReviveOnDeath leaves it at 1 HP, unregisters it from EntityRegistry (no enemy finds it) and
/// never sets HasBeenKilled (no death sequence, no game over). The flag is only on DURING ChangeEntityHealth calls
/// on the local player (HealthComponent.UpdateAll would otherwise animate "Cowering" every frame). On top of it:
/// huge invulnerability, ragdoll, autocast off, input off, camera on an anchor that follows the partner's ghost.
///
/// The host is the authority: it runs the proximity revive for everyone, and decides the defeat (everyone down).
/// Phase 9: any living ally next to a downed player revives it; a client that surrenders leaves alone.
/// Victory, the Home portal and surrender are mirrored to the other side.
/// </summary>
public static class CoopLife
{
    public enum EndKind : byte { None = 0, Defeat = 1, Victory = 2, Surrender = 3, PortalHome = 4 }

    private const float ReviveRadius = 2.5f;
    private const float ReviveTime = 3f;
    private const float DownedInvulnerability = 1e6f;
    private const float ReviveInvulnerability = 2f;
    private const float ReviveHealthFraction = 0.5f;
    private const float DefeatGrace = 0.5f;
    private const float LifePacketInterval = 0.2f;

    // ---- local state ----
    internal static bool LocalDowned;
    private static bool _enterPending;      // downed inside ChangeEntityHealth; the heavy part runs in Tick
    internal static bool RunEnding;         // defeat/surrender in progress: no more downing
    /// <summary>Phase 9, client: we surrendered alone; the host's later victory / portals are not ours any more.</summary>
    internal static bool LeftAlone;
    private static bool _matchStarted;
    private static bool _hooked;

    // ---- the others, by slot (phase 9) ----
    // Host: revive progress 0..1 of each downed player (itself on slot 0), from its own proximity check.
    // Client: down flags and progress of everyone, from the host's CoopLife packets (its own slot included).
    private static readonly bool[] _downFromHost = new bool[Players.MaxSlots];
    private static readonly float[] _progress = new float[Players.MaxSlots];
    private static readonly float[] _revivePendingTimer = new float[Players.MaxSlots]; // host: Revive sent, waiting for its ClientState
    private static float _allDownTimer;
    private static float _lifeTimer;
    private static int _lastSentDownMask = -1;

    // ---- camera ----
    private static GameObject? _anchor;
    private static Transform? _savedTarget;
    private static bool _cameraSwapped;
    private static int _camRetargets;

    // ---- telemetry ----
    private static float _telemetryTimer;
    private static int _downs, _revives, _armed, _finalDeaths, _portalQueued;

    // ---- re-entrancy for mirrored actions ----
    internal static bool FromNetwork;
    private static float _pendingPortalTimer;

    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        NetworkManager.PeerDisconnected += _ => OnPeerDisconnected();
        Players.Left += ForgetSlot;
    }

    private static void ForgetSlot(byte slot)
    {
        if (slot >= Players.MaxSlots) return;
        _downFromHost[slot] = false;
        _progress[slot] = 0f;
        _revivePendingTimer[slot] = 0f;
        _wasRemoteDown[slot] = false;
    }

    public static void OnMatchStarted()
    {
        Reset("match started");
        _matchStarted = true;
    }

    private static void Reset(string why)
    {
        if (LocalDowned || RunEnding || _cameraSwapped)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Reset ({why}).");
        LocalDowned = false;
        _enterPending = false;
        RunEnding = false;
        LeftAlone = false;
        Array.Clear(_downFromHost);
        Array.Clear(_progress);
        Array.Clear(_revivePendingTimer);
        Array.Clear(_wasRemoteDown);
        _allDownTimer = 0f;
        _lastSentDownMask = -1;
        _pendingPortalTimer = 0f;
        _hostLost = false;
        _armDepth = 0;
        _cameraSwapped = false;
        _savedTarget = null;
        if (_anchor != null) { UnityEngine.Object.Destroy(_anchor); _anchor = null; }
    }

    private static bool Connected => NetworkManager.HasPeer;

    private static bool InMatch =>
        _matchStarted && GameManagerUtil.HasGameManager && GameState.LocalPlayerEntity != null;

    /// <summary>That other player is down (or dead). Host: from its ClientState; client: from the host's CoopLife packets.</summary>
    internal static bool IsRemoteDown(byte slot) =>
        slot < Players.MaxSlots && (CoopRole.IsHostSide ? Enemies.RemoteProxy.IsDownOrDead(slot) : _downFromHost[slot]);

    /// <summary>Revive progress shown over that player (0..1): the host's own count, or what the host sent.</summary>
    internal static float ProgressOf(byte slot) => slot < Players.MaxSlots ? _progress[slot] : 0f;

    /// <summary>Local player down or dead for real.</summary>
    internal static bool LocalOut
    {
        get
        {
            var h = GameState.LocalPlayerEntity != null ? GameState.LocalPlayerEntity._health : null;
            return LocalDowned || (h != null && h.HasBeenKilled);
        }
    }

    // =====================================================================================
    // ChangeEntityHealth: arm ReviveOnDeath for the duration of a call on the local player
    // =====================================================================================

    private static int _armDepth;
    private static bool _origReviveOnDeath;
    private static float _origReviveDelay;

    internal static bool TryArm(Entity target)
    {
        if (LocalDowned || RunEnding || !Connected || !InMatch) return false;
        var local = GameState.LocalPlayerEntity;
        if (local == null || target.Pointer != local.Pointer) return false;
        var h = local._health;
        if (h == null || h.HasBeenKilled) return false;
        if (_armDepth == 0)
        {
            _origReviveOnDeath = h.ReviveOnDeath;
            _origReviveDelay = h.ReviveDelay;
            h.ReviveOnDeath = true;
            h.ReviveDelay = DownedInvulnerability;
        }
        _armDepth++;
        return true;
    }

    internal static void Disarm()
    {
        if (_armDepth <= 0) { _armDepth = 0; return; }
        if (--_armDepth > 0) return;
        var local = GameState.LocalPlayerEntity;
        var h = local != null ? local._health : null;
        if (h == null) return;
        bool downedNow = !LocalDowned && h.DelayToRevive > 0f && !h.HasBeenKilled;
        h.ReviveOnDeath = _origReviveOnDeath;
        h.ReviveDelay = _origReviveDelay;
        _armed++;
        if (!downedNow) return;
        // Minimal, can't-fail part right here (we're inside the game's damage code): flags + invulnerability.
        LocalDowned = true;
        _enterPending = true;
        h.InvulnerabilityTime = DownedInvulnerability;
        _downs++;
    }

    // =====================================================================================
    // Tick
    // =====================================================================================

    public static void Tick(float dt)
    {
        try
        {
            if (!GameManagerUtil.HasGameManager)
            {
                if (_matchStarted) Reset("no game manager");
                _matchStarted = false;
            }
            if (_enterPending) { _enterPending = false; EnterDownedVisuals(); }
            if (LocalDowned) UpdateSpectator();
            if (CoopRole.IsHostSide && Connected && InMatch) HostTick(dt);
            if (_pendingPortalTimer > 0f) RetryPendingPortal(dt);
            HostLostTick(dt);
            FeedbackTick(dt);
            HandleHotkeys();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][life] Tick: {ex}");
        }

        _telemetryTimer += dt;
        if (_telemetryTimer >= 5f)
        {
            _telemetryTimer = 0f;
            if (Connected && InMatch)
            {
                var h = GameState.LocalPlayerEntity!._health;
                Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][life] localDowned={LocalDowned} down=[{DescribeDown()}] " +
                    $"hp={(h != null ? h.CurrentHealth : -1):F0} killed={(h != null && h.HasBeenKilled)} delayToRevive={(h != null ? h.DelayToRevive : -1):F0} " +
                    $"downs={_downs} revives={_revives} texts={_texts} textErrors={_feedbackErrors} vfx={_vfx} camRetargets={_camRetargets} armedCalls={_armed} finalDeaths={_finalDeaths} runEnding={RunEnding} portalQueued={_portalQueued}");
            }
            _armed = 0; _texts = 0; _vfx = 0; _camRetargets = 0;
        }
    }

    // ---------------- entering / leaving the downed state ----------------

    private static void EnterDownedVisuals()
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        bool ragdolled = false;
        try
        {
            var rag = local._ragdoll;
            if (rag != null && !rag.ImmuneToRagdollEffect)
            {
                rag.SetToRagdoll(-1f, true);
                ragdolled = rag.Ragdolled;
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] SetToRagdoll: {ex.Message}"); }
        try { if (local._character != null) local._character.SetMovementMode(MovementMode.None); }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] SetMovementMode(None): {ex.Message}"); }
        try { if (local._playerSkillAutoCast != null) local._playerSkillAutoCast.ForceStopAutoCasting = true; }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] ForceStopAutoCasting: {ex.Message}"); }
        if (CoopRole.IsHostSide) DropEnemiesTargeting(local);
        bool registered = EntityRegistry.TryGetEntity(local.Id, out var reg) && reg != null && reg.Pointer == local.Pointer;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Local player DOWNED (hp={local._health.CurrentHealth:F0}, ragdoll={ragdolled}, stillRegistered={registered}).");
        if (CoopRole.IsHostSide) SendLife(force: true);
    }

    /// <summary>Enemies already chasing us keep their target until RefreshTarget; we're unregistered, so drop it now.</summary>
    private static void DropEnemiesTargeting(Entity who)
    {
        var all = EntityRegistry.AllEntities;
        if (all == null) return;
        int dropped = 0;
        for (int i = 0; i < all.Count; i++)
        {
            var ai = all[i] != null ? all[i]._basicAI : null;
            if (ai == null) continue;
            var t = ai._entityToFollow;
            if (t == null || t.Pointer != who.Pointer) continue;
            ai.RemoveTarget();
            dropped++;
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] {dropped} enemies dropped the downed host as target.");
    }

    /// <summary>Back on our feet: undo the downed state (and what the native ReviveOnDeath branch did).</summary>
    internal static void ReviveLocal(string why)
    {
        if (!LocalDowned) return;
        var local = GameState.LocalPlayerEntity;
        LocalDowned = false;
        _enterPending = false;
        if (local == null) return;
        var h = local._health;
        try
        {
            if (h != null)
            {
                h.DelayToRevive = 0f;
                h.TriggeredRevive = false;
                float max = local._stats != null ? local._stats.GetMaxHealth() : h.CurrentHealth;
                h.CurrentHealth = Mathf.Max(1f, Mathf.Round(max * ReviveHealthFraction));
                h.InvulnerabilityTime = ReviveInvulnerability;
                if (h.AttackerList != null) h.AttackerList.Clear();
            }
            bool registered = EntityRegistry.TryGetEntity(local.Id, out var reg) && reg != null && reg.Pointer == local.Pointer;
            if (!registered) EntityRegistry.RegisterEntity(local);
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][life] Revive (health/registry): {ex}"); }

        try
        {
            var rag = local._ragdoll;
            if (rag != null && rag.Ragdolled) rag.SetToAnimator(); // get-up animation, then Walking + Ragdolled=false
            else if (local._character != null) local._character.SetMovementMode(MovementMode.Walking);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][life] Revive (ragdoll): {ex}");
            try { if (local._character != null) local._character.SetMovementMode(MovementMode.Walking); } catch { /* best effort */ }
        }
        try { if (local._playerSkillAutoCast != null) local._playerSkillAutoCast.ForceStopAutoCasting = false; }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] ForceStopAutoCasting off: {ex.Message}"); }
        RestoreCamera();
        if (why != "victory" && why != "portal") OnLocalRevivedEffect();
        _revives++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Local player REVIVED ({why}): hp={(h != null ? h.CurrentHealth : -1):F0}.");
        if (CoopRole.IsHostSide) SendLife(force: true);
    }

    /// <summary>Real death through the game's own path (ragdoll is already on), so its UpdateAll sees "all dead".</summary>
    internal static void FinalDeathLocal(string why)
    {
        RunEnding = true;
        var local = GameState.LocalPlayerEntity;
        var h = local != null ? local._health : null;
        bool wasDowned = LocalDowned;
        LocalDowned = false;
        _enterPending = false;
        RestoreCamera();
        if (local == null || h == null || h.HasBeenKilled) return;
        try
        {
            h.InvulnerabilityTime = 0f;
            h.DelayToRevive = 0f;
            h.ReviveOnDeath = false;
            UnitUtil.ChangeEntityHealth(null!, local, -(h.CurrentHealth + 1e9f), false, out _, out _, out _);
            if (!h.HasBeenKilled)
            {
                // Death guard absorbed it (charge gained while down): end it like GameManagerUtil.Surrender does.
                h.CurrentHealth = 0f;
                h.HasBeenKilled = true;
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][life] FinalDeath: {ex}");
            h.HasBeenKilled = true;
        }
        _finalDeaths++;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Local player DEAD for real ({why}, wasDowned={wasDowned}).");
    }

    // ---------------- spectator camera ----------------

    private static byte _spectated = Players.NoSlot;
    private static float _spectateCheck;

    /// <summary>Phase 9: the downed player watches the nearest living ally (checked every second; another one once
    /// that ally goes down too). Nobody alive: whoever has a ghost.</summary>
    private static GameObject? SpectatedGhost()
    {
        var cur = Lobby.CoopSession.Get(_spectated);
        _spectateCheck -= Time.unscaledDeltaTime;
        if (cur != null && cur.Ghost != null && _spectateCheck > 0f) return cur.Ghost;
        _spectateCheck = 1f;
        var local = GameState.LocalPlayerEntity;
        Vector3 from = _anchor != null ? _anchor.transform.position : local != null ? local.transform.position : Vector3.zero;
        Lobby.RemotePlayer? best = null, anyone = null;
        float bestD = float.MaxValue;
        foreach (var r in Lobby.CoopSession.Remotes)
        {
            if (r.Ghost == null) continue;
            anyone ??= r;
            if (IsRemoteDown(r.Slot)) continue;
            float d = (r.Ghost.transform.position - from).sqrMagnitude;
            if (d < bestD) { bestD = d; best = r; }
        }
        best ??= anyone;
        byte now = best != null ? best.Slot : Players.NoSlot;
        if (now != _spectated && best != null)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Spectating slot {now} ({best.Name}).");
        _spectated = now;
        return best?.Ghost;
    }

    private static void UpdateSpectator()
    {
        var cam = GameManagerUtil.PlayerCamera;
        var ghost = SpectatedGhost();
        if (cam == null) return;
        if (_anchor == null)
        {
            _anchor = new GameObject("SoulstoneCoop_SpectatorAnchor");
            var local = GameState.LocalPlayerEntity;
            if (local != null) _anchor.transform.position = local.transform.position;
        }
        // Only follow a live ghost; the anchor itself is ours and never destroyed while the camera points at it.
        if (ghost != null && Connected) _anchor.transform.position = ghost.transform.position;
        if (!_cameraSwapped)
        {
            _savedTarget = cam.Target;
            _cameraSwapped = true;
        }
        if (cam.Target == null || cam.Target.Pointer != _anchor.transform.Pointer) { cam.Target = _anchor.transform; _camRetargets++; }
    }

    private static void RestoreCamera()
    {
        try
        {
            if (_cameraSwapped)
            {
                var cam = GameManagerUtil.PlayerCamera;
                var local = GameState.LocalPlayerEntity;
                Transform? back = _savedTarget != null ? _savedTarget : (local != null ? local.transform : null);
                if (cam != null && back != null) cam.Target = back;
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] RestoreCamera: {ex.Message}"); }
        _cameraSwapped = false;
        _savedTarget = null;
        if (_anchor != null) { UnityEngine.Object.Destroy(_anchor); _anchor = null; }
    }

    // =====================================================================================
    // Host authority: revive by proximity, defeat when everyone is down
    // =====================================================================================

    /// <summary>One player of this run as the host sees it.</summary>
    private struct Participant
    {
        public byte Slot;
        public bool Down;       // down or dead (dead: can't be revived)
        public bool Revivable;  // down, not dead for real
        public bool HasPos;
        public Vector3 Pos;
    }

    private static readonly List<Participant> Run = new();

    /// <summary>The host plus every client whose ClientState came this run (one that left the run has no proxy state).</summary>
    private static void CollectParticipants(float dt)
    {
        Run.Clear();
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        bool hostDown = LocalOut;
        Run.Add(new Participant
        {
            Slot = Players.HostSlot, Down = hostDown,
            Revivable = hostDown && LocalDowned && !LocalPlayerKilled(), // host really dead can't be revived
            HasPos = true, Pos = local.transform.position,
        });
        foreach (var slot in Enemies.RemoteProxy.KnownSlots)
        {
            // A ClientState from before it left the run (its proxy may never have been made) must not keep it "alive".
            if (Lobby.CoopSession.Get(slot)?.InRun != true) continue;
            bool down = Enemies.RemoteProxy.IsDownOrDead(slot);
            if (_revivePendingTimer[slot] > 0f)
            {
                _revivePendingTimer[slot] -= dt;
                if (!down) _revivePendingTimer[slot] = 0f;
                else down = false; // Revive sent, waiting for its ClientState: don't count it as down meanwhile
            }
            bool hasPos = Enemies.RemoteProxy.TryGetPosition(slot, out var pos, out _);
            Run.Add(new Participant
            {
                Slot = slot, Down = down, Revivable = down && !Enemies.RemoteProxy.IsKilled(slot),
                HasPos = hasPos, Pos = pos,
            });
        }
        // In the run but no ClientState yet (its first one comes within 0.5 s of its StartMatch): alive, as before
        // phase 9, so a host that goes down in the first second isn't taken for the last one standing.
        foreach (var r in Lobby.CoopSession.Remotes)
        {
            if (!r.InRun || Enemies.RemoteProxy.HasState(r.Slot)) continue;
            bool hasPos = r.TryGetPosition(out var pos, out _);
            Run.Add(new Participant { Slot = r.Slot, HasPos = hasPos, Pos = pos });
        }
    }

    private static void HostTick(float dt)
    {
        if (Time.timeScale <= 0f) { SendLife(force: false); return; } // paused: nothing progresses
        CollectParticipants(dt);
        if (RunEnding) { SendLife(force: false); return; }

        bool allDown = true;
        foreach (var p in Run) if (!p.Down) { allDown = false; break; }
        if (allDown)
        {
            Array.Clear(_progress);
            _allDownTimer += dt;
            if (_allDownTimer >= DefeatGrace) EndRun(EndKind.Defeat);
            SendLife(force: false);
            return;
        }
        _allDownTimer = 0f;

        // Each downed player fills its bar while any living ally stands within the radius; away from all, it drains twice as fast.
        var downedNow = new bool[Players.MaxSlots];
        foreach (var d in Run)
        {
            if (!d.Down) continue;
            downedNow[d.Slot] = true;
            if (!d.Revivable || !d.HasPos) { _progress[d.Slot] = 0f; continue; }
            bool near = false;
            foreach (var a in Run)
            {
                if (a.Down || !a.HasPos) continue;
                Vector3 x = a.Pos, y = d.Pos;
                x.y = y.y = 0f;
                if ((x - y).sqrMagnitude <= ReviveRadius * ReviveRadius) { near = true; break; }
            }
            _progress[d.Slot] = Mathf.Clamp01(_progress[d.Slot] + (near ? dt : -2f * dt) / ReviveTime);
            if (_progress[d.Slot] < 1f) continue;
            _progress[d.Slot] = 0f;
            if (d.Slot == Players.HostSlot) ReviveLocal("an ally stood by");
            else SendRevive(d.Slot);
        }
        for (int s = 0; s < Players.MaxSlots; s++) if (!downedNow[s]) _progress[s] = 0f;
        SendLife(force: false);
    }

    private static bool LocalPlayerKilled()
    {
        var h = GameState.LocalPlayerEntity != null ? GameState.LocalPlayerEntity._health : null;
        return h != null && h.HasBeenKilled;
    }

    /// <summary>Revive packet: target slot u8. Everyone gets it (the target gets up; the others see it happen).</summary>
    private static void SendRevive(byte slot)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.Revive);
        w.Put(slot);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _revivePendingTimer[slot] = 3f;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Host: slot {slot} revived (Revive sent).");
    }

    private static void ReviveDownedClients()
    {
        foreach (var slot in Players.Others)
            if (Enemies.RemoteProxy.IsDownOrDead(slot) && !Enemies.RemoteProxy.IsKilled(slot)) SendRevive(slot);
    }

    /// <summary>CoopLife packet, host -> clients: count u8 | count * (slot u8, down u8, progress f32), with the host and
    /// every client of the run. On change, and at 5 Hz while someone is down.</summary>
    private static void SendLife(bool force)
    {
        if (!Connected) return;
        if (Run.Count == 0) CollectParticipants(0f);
        int mask = 0;
        foreach (var p in Run) if (p.Down) mask |= 1 << p.Slot;
        _lifeTimer += Time.unscaledDeltaTime;
        bool changed = mask != _lastSentDownMask;
        if (!force && !changed && (mask == 0 || _lifeTimer < LifePacketInterval)) return;
        _lifeTimer = 0f;
        _lastSentDownMask = mask;
        var w = new NetDataWriter();
        w.Put((byte)PacketType.CoopLife);
        w.Put((byte)Run.Count);
        foreach (var p in Run)
        {
            w.Put(p.Slot);
            w.Put(p.Down);
            w.Put(_progress[p.Slot]);
        }
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static string DescribeDown()
    {
        var parts = new List<string>();
        if (LocalDowned) parts.Add($"me({Players.MySlot})");
        foreach (var r in Lobby.CoopSession.Remotes)
            if (IsRemoteDown(r.Slot)) parts.Add($"{r.Slot}:{ProgressOf(r.Slot):F2}");
        return string.Join(" ", parts);
    }

    // =====================================================================================
    // End of run
    // =====================================================================================

    /// <summary>Host: decide/mirror an end, then apply it locally.</summary>
    internal static void EndRun(EndKind kind)
    {
        if (CoopRole.IsHostSide)
        {
            var w = new NetDataWriter();
            w.Put((byte)PacketType.RunEnd);
            w.Put((byte)kind);
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Run end: {kind} ({(CoopRole.IsHostSide ? "host" : "client")}).");
        ApplyEnd(kind);
    }

    private static void ApplyEnd(EndKind kind)
    {
        switch (kind)
        {
            case EndKind.Defeat:
                FinalDeathLocal("all players down");
                break;
            case EndKind.Surrender:
                RunEnding = true;
                if (LocalDowned) { LocalDowned = false; RestoreCamera(); }
                RunMirrored(() => GameManagerUtil.Surrender());
                break;
            case EndKind.Victory:
                ReviveLocal("victory");
                if (CoopRole.IsClientSide) ForceLocalVictory();
                break;
            case EndKind.PortalHome:
                ReviveLocal("portal");
                TryEnterPortalLocally();
                break;
        }
    }

    private static void RunMirrored(System.Action a)
    {
        try { FromNetwork = true; a(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][life] Mirrored action: {ex}"); }
        finally { FromNetwork = false; }
    }

    /// <summary>The local match is up (StartMatch seen, player alive in the registry or downed).</summary>
    internal static bool InMatchNow => InMatch;

    /// <summary>Phase 9, client: our own Surrender is about to run (the game's, unmirrored): no more downing, and back
    /// from the spectator camera if we were down.</summary>
    internal static void PrepareToLeaveAlone()
    {
        RunEnding = true;
        LeftAlone = true;
        if (LocalDowned) { LocalDowned = false; _enterPending = false; RestoreCamera(); }
    }

    /// <summary>Endless/BossRush portal about to be taken (Lobby.PortalSync), before the game's own portal code (which
    /// stops the player): whoever is down gets up first, as for the Home portal, and nobody goes down again in the
    /// old scene's last seconds. Reset at the next StartMatch.</summary>
    internal static void PrepareForStageTransition()
    {
        ReviveLocal("portal");
        RunEnding = true;
    }

    /// <summary>Client: complete the boss objective; the game's own UpdateAll then runs its victory branch.</summary>
    private static void ForceLocalVictory()
    {
        var data = GameManagerUtil.GameData;
        var sgm = data != null ? data.SurvivorsGameManager : null;
        var obj = sgm != null ? sgm.MainObjective : null;
        if (obj == null)
        {
            if (sgm == null && CompleteBossRoomProgress(data)) return;
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][life] Victory: no MainObjective on this side.");
            return;
        }
        obj.Current = obj.Total;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Victory: MainObjective {obj.Current}/{obj.Total}.");
    }

    /// <summary>The BossRush map (SurvivorsBossRoomGameManager, no SurvivorsGameManager): its UpdateAll wins when
    /// GameManagerUtil.GetMapProgress() = (Completed + Current) / Total reaches 1. See docs/portais-notas.md.</summary>
    private static bool CompleteBossRoomProgress(GameData? data)
    {
        var prog = data != null ? data.Progression : null;
        if (prog == null || prog.TotalObjectiveProgression <= 0f) return false;
        prog.CompletedObjectiveProgression = prog.TotalObjectiveProgression;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Victory (BossRush room): progression {prog.CompletedObjectiveProgression}/{prog.TotalObjectiveProgression}.");
        return true;
    }

    private static void TryEnterPortalLocally()
    {
        var data = GameManagerUtil.GameData;
        if (data == null) return;
        var obj = data.EnterPortalObjective;
        if (obj == null)
        {
            // The portal isn't open on this side yet (victory still in flight): retry for a while.
            if (_pendingPortalTimer <= 0f) _portalQueued++;
            _pendingPortalTimer = 30f;
            return;
        }
        _pendingPortalTimer = 0f;
        if (obj.Completed) return;
        // SurvivorsGameManager, or the BossRush map's SurvivorsBossRoomGameManager.
        RunMirrored(() => Lobby.PortalSync.EnterHomePortal());
    }

    private static void RetryPendingPortal(float dt)
    {
        _pendingPortalTimer -= dt;
        var data = GameManagerUtil.GameData;
        if (data != null && data.EnterPortalObjective != null) TryEnterPortalLocally();
    }

    /// <summary>A local Home-portal entry (from the HomePortal trigger): tell the other side.</summary>
    internal static void OnLocalPortalEntered()
    {
        if (!Connected) return;
        if (CoopRole.IsHostSide)
        {
            var w = new NetDataWriter();
            w.Put((byte)PacketType.RunEnd);
            w.Put((byte)EndKind.PortalHome);
            NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        }
        else SendRequest(EndKind.PortalHome);
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][life] Local player entered the Home portal: mirroring.");
    }

    internal static void SendRequest(EndKind kind)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.RunEndRequest);
        w.Put((byte)kind);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Host, local victory (OpenPortalAndObjective started by the game's own UpdateAll).</summary>
    internal static void OnHostVictory()
    {
        if (!Connected || !InMatch || RunEnding) return;
        ReviveDownedClients();
        EndRun(EndKind.Victory);
    }

    // =====================================================================================
    // Packets
    // =====================================================================================

    public static void OnRevive(NetDataReader r)
    {
        byte target = r.GetByte();
        if (!CoopRole.IsClientSide || target != Players.MySlot) return;
        ReviveLocal("host says so");
    }

    public static void OnLife(NetDataReader r)
    {
        int n = r.GetByte();
        var seen = new bool[Players.MaxSlots];
        for (int i = 0; i < n; i++)
        {
            byte slot = r.GetByte();
            bool down = r.GetBool();
            float progress = r.GetFloat();
            if (slot >= Players.MaxSlots || !CoopRole.IsClientSide) continue;
            seen[slot] = true;
            _downFromHost[slot] = down;
            _progress[slot] = progress;
        }
        if (!CoopRole.IsClientSide) return;
        for (int s = 0; s < Players.MaxSlots; s++)
        {
            if (seen[s]) continue;
            _downFromHost[s] = false; // not in the run any more
            _progress[s] = 0f;
        }
    }

    public static void OnRunEnd(NetDataReader r)
    {
        var kind = (EndKind)r.GetByte();
        if (!CoopRole.IsClientSide) return;
        if (!InMatch) { Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] RunEnd {kind} ignored: not in a match (phase 6: late packet after the run)."); return; }
        if (LeftAlone || LocalPlayerKilled()) { Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] RunEnd {kind} ignored: we already left this run (surrendered / dead)."); return; }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] RunEnd from host: {kind}.");
        try { ApplyEnd(kind); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][life] ApplyEnd({kind}): {ex}"); }
    }

    public static void OnRunEndRequest(NetDataReader r)
    {
        var kind = (EndKind)r.GetByte();
        if (!CoopRole.IsHostSide) return;
        byte from = PacketRouter.From;
        if (!InMatch) { Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] RunEndRequest {kind} from slot {from} ignored: not in a match."); return; }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] RunEndRequest from slot {from}: {kind}.");
        try
        {
            // Phase 9: a client that surrenders leaves alone (it runs the game's Surrender itself); old builds asked.
            if (kind == EndKind.Surrender) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Slot {from} surrendered: it leaves the run, the others go on.");
            else if (kind == EndKind.PortalHome)
            {
                // The other clients go through too (the host's own entry below runs mirrored, so it isn't announced again).
                var w = new NetDataWriter();
                w.Put((byte)PacketType.RunEnd);
                w.Put((byte)EndKind.PortalHome);
                NetworkManager.SendExcept(from, w, DeliveryMethod.ReliableOrdered);
                ReviveLocal("an ally took the portal");
                TryEnterPortalLocally();
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][life] Request {kind}: {ex}"); }
    }

    private static void OnPeerDisconnected()
    {
        try
        {
            // Alone now: solo rules. A downed player has nobody to revive it (the host keeps going while a client is left).
            if (LocalDowned && InMatch && !NetworkManager.HasPeer) FinalDeathLocal("everyone else disconnected");
            // Phase 8: the client's run can't go on without the host (no enemies, no spawns): end it cleanly.
            if (CoopRole.IsClientSide && InMatch && !RunEnding) BeginHostLost();
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][life] OnPeerDisconnected: {ex}"); }
    }

    // =====================================================================================
    // Phase 8: the host left in the middle of the client's run
    // =====================================================================================

    private const float HostLostDelay = 4f; // s: long enough to read the notice before the end screen

    private static bool _hostLost;
    private static float _hostLostTimer;
    private static bool _hostLostWaitingLogged;

    private static void BeginHostLost()
    {
        _hostLost = true;
        _hostLostTimer = 0f;
        _hostLostWaitingLogged = false;
        bool won = VictoryReached();
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] Host lost mid-run (victory reached={won}, auto end={Plugin.EndRunWhenHostLeaves.Value}).");
        if (won) Lobby.CoopSession.Notice(Ui.Loc.Msg("nt.host_left_portal"), 10f);
        else if (Plugin.EndRunWhenHostLeaves.Value) Lobby.CoopSession.Notice(Ui.Loc.Msg("nt.host_left_ending"), 10f);
        else Lobby.CoopSession.Notice(Ui.Loc.Msg("nt.host_left_surrender"), 10f);
        if (won || !Plugin.EndRunWhenHostLeaves.Value) _hostLost = false; // nothing to do automatically
    }

    /// <summary>The run was already won here (the boss objective is done and the Home portal is open): the
    /// player keeps that and walks into the portal; surrendering would throw the victory away.</summary>
    internal static bool VictoryReached()
    {
        try
        {
            var data = GameManagerUtil.GameData;
            if (data == null) return false;
            if (data.EnterPortalObjective != null) return true;
            // RunEnd(Victory) applied, the game's victory branch (which opens the portal) still on its way.
            var obj = data.SurvivorsGameManager != null ? data.SurvivorsGameManager.MainObjective : null;
            if (obj == null && data.SurvivorsGameManager == null)
            {
                // BossRush map (SurvivorsBossRoomGameManager): its victory is GetMapProgress() >= 1.
                var prog = data.Progression;
                return prog != null && prog.TotalObjectiveProgression > 0f
                       && prog.CompletedObjectiveProgression + prog.CurrentObjectiveProgression >= prog.TotalObjectiveProgression;
            }
            return obj != null && obj.Total > 0 && obj.Current >= obj.Total;
        }
        catch { return false; }
    }

    /// <summary>Surrender through the game's own path (GameManagerUtil.Surrender: LossBySurrender, every player
    /// killed, the normal end screen and the profile save) once nothing is in the way: not in the level-up
    /// screen or the pause menu (the player can also press Desistir there themselves).</summary>
    private static void HostLostTick(float dt)
    {
        if (!_hostLost) return;
        if (!InMatch || RunEnding || NetworkManager.HasPeer || LocalPlayerKilled()) { _hostLost = false; return; }
        if (VictoryReached())
        {
            _hostLost = false;
            Lobby.CoopSession.Notice(Ui.Loc.Msg("nt.host_left_portal"), 10f);
            return;
        }
        _hostLostTimer += dt;
        if (_hostLostTimer < HostLostDelay) return;
        if (PauseSync.LocalReason != PauseReason.None)
        {
            if (!_hostLostWaitingLogged)
            {
                _hostLostWaitingLogged = true;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Host lost: waiting for the local {PauseSync.LocalReason} screen to close before ending.");
                Lobby.CoopSession.Notice(Ui.Loc.Msg(PauseSync.LocalReason is PauseReason.LevelUp or PauseReason.Choosing
                    ? "nt.host_left_pick" : "nt.host_left_pause"), 10f);
            }
            return;
        }
        _hostLost = false;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][life] Host lost: ending the client's run (Surrender).");
        RunEnding = true;
        if (LocalDowned) { LocalDowned = false; RestoreCamera(); }
        RunMirrored(() => GameManagerUtil.Surrender());
    }

    // =====================================================================================
    // Debug hotkeys: F9 lethal hit on the local player, F10 (host) victory, F11 (host) revive everyone
    // =====================================================================================

    private static bool _prevF9, _prevF10, _prevF11;

    private static void HandleHotkeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        bool f9 = kb.f9Key.isPressed, f10 = kb.f10Key.isPressed, f11 = kb.f11Key.isPressed;
        if (f9 && !_prevF9) DebugLethalHit();
        if (f10 && !_prevF10 && CoopRole.IsHostSide) DebugVictory();
        if (f11 && !_prevF11 && CoopRole.IsHostSide) DebugReviveAll();
        _prevF9 = f9; _prevF10 = f10; _prevF11 = f11;
    }

    internal static void DebugLethalHit()
    {
        var local = GameState.LocalPlayerEntity;
        var h = local != null ? local._health : null;
        if (local == null || h == null || !InMatch) return;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][life] F9: lethal hit on the local player.");
        h.InvulnerabilityTime = 0f;
        UnitUtil.ChangeEntityHealth(null!, local, -(h.CurrentHealth + 1e9f), false, out _, out _, out _);
    }

    private static void DebugVictory()
    {
        var data = GameManagerUtil.GameData;
        var obj = data != null && data.SurvivorsGameManager != null ? data.SurvivorsGameManager.MainObjective : null;
        if (obj == null && data != null && data.SurvivorsGameManager == null && CompleteBossRoomProgress(data)) return;
        if (obj == null) { Plugin.StaticLog.LogWarning("[SoulstoneCoop][life] F10: no MainObjective yet."); return; }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] F10: forcing the boss objective ({obj.Current}/{obj.Total}).");
        obj.Current = obj.Total;
    }

    private static void DebugReviveAll()
    {
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][life] F11: revive everyone.");
        ReviveLocal("F11");
        if (Connected) ReviveDownedClients();
    }

    // =====================================================================================
    // Feedback in both windows, with the game's own systems (IMGUI is partly stripped in this build:
    // GUI calls throw "Method unstripping failed"). Floating text = the damage-number system
    // (OverheadCombatTextPanelManager.RequestCombatTextWithString); revive = the death guard VFX.
    // =====================================================================================

    private const float MarkerInterval = 0.9f;
    private const float ProgressInterval = 0.45f;
    private static float _markerTimer, _progressTimer;
    private static readonly bool[] _wasRemoteDown = new bool[Players.MaxSlots];
    private static int _texts, _vfx, _feedbackErrors;

    /// <summary>"CAIDO" over every downed body (ours and the ghosts), "REVIVE xx%" while one is being revived, and
    /// "REVIVEU!" with the death guard VFX when a ghost's player gets up.</summary>
    private static void FeedbackTick(float dt)
    {
        if (!Connected || !InMatch || RunEnding) { Array.Clear(_wasRemoteDown); return; }
        bool anyDown = LocalDowned;
        foreach (var r in Lobby.CoopSession.Remotes)
        {
            bool down = IsRemoteDown(r.Slot);
            if (_wasRemoteDown[r.Slot] && !down) OnAllyRevivedEffect(r);
            _wasRemoteDown[r.Slot] = down;
            anyDown |= down && r.Ghost != null;
        }
        if (!anyDown) { _markerTimer = _progressTimer = 0f; return; }

        var local = GameState.LocalPlayerEntity;
        _markerTimer -= dt;
        bool marker = _markerTimer <= 0f;
        if (marker) _markerTimer = MarkerInterval;
        _progressTimer -= dt;
        bool progressText = _progressTimer <= 0f;
        if (progressText) _progressTimer = ProgressInterval;

        // Short on purpose: the GPU combat text has a fixed per-text buffer (MaxCharacterCountPerText).
        if (LocalDowned && local != null)
        {
            if (marker) FloatText(CombatTextType.StatusEffect, local.transform, "float.down");
            float mine = ProgressOf(Players.MySlot);
            if (progressText && mine > 0.01f) FloatText(CombatTextType.Healing, local.transform, "float.revive", Mathf.RoundToInt(mine * 100f));
        }
        foreach (var r in Lobby.CoopSession.Remotes)
        {
            if (r.Ghost == null || !IsRemoteDown(r.Slot)) continue;
            if (marker) FloatText(CombatTextType.StatusEffect, r.Ghost.transform, "float.down");
            float p = ProgressOf(r.Slot);
            if (progressText && p > 0.01f) FloatText(CombatTextType.Healing, r.Ghost.transform, "float.revive", Mathf.RoundToInt(p * 100f));
        }
    }

    /// <summary>A floating text from Ui.Loc ("float.*" keys, {pct} = <paramref name="pct"/>): in the local language
    /// when it fits the buffer and the combat text's font has the glyphs, else without accents, else English.</summary>
    private static void FloatText(CombatTextType type, Transform t, string key, int pct = 0)
    {
        try
        {
            int max = MaxCombatTextChars();
            if (max <= 0 || max > MaxCombatTextFallback) max = MaxCombatTextFallback;
            string text = Ui.Loc.Float(key, max, ("pct", pct));
            if (text.Length > max) text = text.Substring(0, max);
            OverheadCombatTextPanelManager.RequestCombatTextWithString(type, t, text, false);
            _texts++;
        }
        catch (Exception ex)
        {
            if (_feedbackErrors++ < 3) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] Floating text: {ex.Message}");
        }
    }

    /// <summary>GPUIndirectInstancedCombatTextManager.CreateCombatTextRequest (RVA 0x601550) only logs "Combat Text is
    /// bigger than the buffer capacity" and then writes past its per-text buffer (IndexOutOfRange).</summary>
    private const int MaxCombatTextFallback = 16; // observed: "buffer capacity 16"

    private static int MaxCombatTextChars()
    {
        try
        {
            var ui = GameManagerUtil.GameplayUIManager;
            var gpu = ui != null ? ui.GPUIndirectWorldSpaceOverheadCombatTextPanel : null;
            return gpu != null ? gpu.MaxCharacterCountPerText : 0;
        }
        catch { return 0; }
    }

    /// <summary>The local player's InventoryComponent.DeathGuardVfx (the "second chance" effect), at a position.</summary>
    private static void PlayReviveVfx(Transform where, bool onLocalPlayer)
    {
        try
        {
            var local = GameState.LocalPlayerEntity;
            var trait = local != null && local._inventory != null ? local._inventory.DeathGuardVfx : null;
            var runner = JobRunner.Instance;
            if (trait == null || runner == null) return;
            var e = onLocalPlayer
                ? SkillUtil.RunVfx(trait, 1f, new SkillTimedVfxTraitData())
                : SkillUtil.RunVfx(trait, where.position, where.rotation, 1f, null!, new SkillTimedVfxTraitData());
            if (e != null) { runner.StartCoroutine(e); _vfx++; }
        }
        catch (Exception ex)
        {
            if (_feedbackErrors++ < 3) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] Revive VFX: {ex.Message}");
        }
    }

    private static void OnLocalRevivedEffect()
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        PlayReviveVfx(local.transform, onLocalPlayer: true);
        FloatText(CombatTextType.Healing, local.transform, "float.revived");
    }

    private static void OnAllyRevivedEffect(Lobby.RemotePlayer r)
    {
        var ghost = r.Ghost;
        if (ghost == null) return;
        PlayReviveVfx(ghost.transform, onLocalPlayer: false);
        FloatText(CombatTextType.Healing, ghost.transform, "float.revived");
    }
}

// =====================================================================================
// Harmony patches (every body wrapped: several run inside the game's UpdateAll systems)
// =====================================================================================

/// <summary>Arms ReviveOnDeath only for this call; the finalizer always disarms (and detects the down).</summary>
[HarmonyPatch(typeof(UnitUtil), nameof(UnitUtil.ChangeEntityHealth))]
public static class CoopLifeDownPatch
{
    [HarmonyPrefix]
    public static void Prefix(Entity targetEntity, float healthChange, out bool __state)
    {
        __state = false;
        try
        {
            if (targetEntity == null || healthChange >= 0f) return;
            __state = CoopLife.TryArm(targetEntity);
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopLifeDownPatch.Prefix: {ex}"); }
    }

    [HarmonyFinalizer]
    public static void Finalizer(bool __state)
    {
        if (!__state) return;
        try { CoopLife.Disarm(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopLifeDownPatch.Finalizer: {ex}"); }
    }
}

/// <summary>No movement/dash/click-to-move input while downed. Only the local player is in this list.</summary>
[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.UpdateAll))]
public static class CoopLifeInputBlockPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try { return !CoopLife.LocalDowned; }
        catch { return true; }
    }
}

/// <summary>Host: the game's own victory branch starts OpenPortalAndObjective; mirror it to the client.</summary>
[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.OpenPortalAndObjective))]
public static class CoopLifeVictoryPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try { if (CoopRole.IsHostSide) CoopLife.OnHostVictory(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopLifeVictoryPatch: {ex}"); }
    }
}

/// <summary>Home portal (UnityEvent from HomePortal.OnTriggerStay): whoever enters takes the other one along.</summary>
[HarmonyPatch(typeof(SurvivorsGameManager), nameof(SurvivorsGameManager.OnPlayerEnterPortal))]
public static class CoopLifeHomePortalPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try { if (!CoopLife.FromNetwork) CoopLife.OnLocalPortalEntered(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopLifeHomePortalPatch: {ex}"); }
    }
}

/// <summary>Same Home portal on the BossRush map, whose scene has SurvivorsBossRoomGameManager instead.</summary>
[HarmonyPatch(typeof(SurvivorsBossRoomGameManager), nameof(SurvivorsBossRoomGameManager.OnPlayerEnterPortal))]
public static class CoopLifeBossRoomHomePortalPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try { if (!CoopLife.FromNetwork) CoopLife.OnLocalPortalEntered(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopLifeBossRoomHomePortalPatch: {ex}"); }
    }
}

// The Endless / BossRush portals (blocked here in phase 4A) are synchronized by Lobby/PortalSync.cs.

/// <summary>Surrender: the host's ends the run for everyone (mirrored); a client's takes only that client out (phase 9).</summary>
[HarmonyPatch(typeof(GameManagerUtil), nameof(GameManagerUtil.Surrender))]
public static class CoopLifeSurrenderPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try
        {
            if (CoopLife.FromNetwork || !NetworkManager.HasPeer) return true;
            if (CoopRole.IsClientSide)
            {
                // Phase 9: a client that gives up leaves alone (like one that drops, phase 8); the others go on.
                CoopLife.PrepareToLeaveAlone();
                Plugin.StaticLog.LogInfo("[SoulstoneCoop][life] Surrender pressed: leaving the run alone (the host and the others go on).");
                CoopLife.SendRequest(CoopLife.EndKind.Surrender); // just so the host logs it
                return true;
            }
            if (CoopLife.RunEnding) return false;
            CoopLife.EndRun(CoopLife.EndKind.Surrender); // runs the original through RunMirrored
            return false;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopLifeSurrenderPatch: {ex}");
            return true;
        }
    }
}
