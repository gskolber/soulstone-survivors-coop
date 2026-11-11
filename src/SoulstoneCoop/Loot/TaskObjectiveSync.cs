using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Loot;

/// <summary>
/// A started map task's timer or kill counter on the guests' objectives panel (docs/tarefas-mapa-notas.md).
///
/// On the host, the task's DoOnTrigger builds the objective and hands it to the panel
/// (GameplayUIManager.GameplayPanel.ObjectivesPanel.TrackObjective):
/// - MapSurviveForSecondsTask (and ...AgainstEnemies, which derives from it): a TimerObjective(SurviveObjectiveId, TimeToSurvive, CountDown) in _timerObjective,
///   advanced by the task's DoOnUpdate;
/// - MapKillBossesInAreaTask / MapKillElitesInAreaTask: an EliminateSpecificEntitiesObjective(EliminationObjectiveId,
///   total) in _eliminationObjective, counted by ObjectivesPanelReferences.RunEliminateSpecificEntitiesObjective.
/// The guest has no MapTask at all, so its panel never had them.
///
/// Host, every 0.5 s while a task runs (ChestSync knows the MapTasks): TaskObjective with each running task's objective
/// (id, kind, the objective's own Id string, total, current, countdown, done), and "done" once when it stops
/// (completed, failed or gone).
/// Guest: the same objective type built with the same id (so the panel shows the game's own text), tracked on its own
/// panel; the timer and the kill count are written as they come; "done" (or 5 s without news)
/// completes it so the panel fades it out as usual. These objects only feed the panel: completing one does nothing else.
/// Layout (after the type byte): count u8 | count * (id u16 | kind u8 | objectiveId str | total f32 | current f32 | flags u8).
/// </summary>
public static class TaskObjectiveSync
{
    private const byte KindTimer = 1, KindKill = 2;
    private const byte FlagCountDown = 1, FlagDone = 2, FlagFailed = 4; // 1.9.0: failed = the task went back to its marker
    private const float SendInterval = 0.5f;
    private const float StaleAfter = 5f;

    private struct Entry
    {
        public ushort Id;
        public byte Kind, Flags;
        public string ObjectiveId;
        public float Total, Current;
    }

    // ---------------- host ----------------

    private sealed class HostTask { public ushort Id; public bool Running; public MapTask? Task; }

    private static readonly Dictionary<IntPtr, HostTask> HostIds = new();
    private static readonly List<Entry> Out = new();
    private static readonly List<IntPtr> Dead = new();
    private static ushort _nextId = 1;
    private static float _sendTimer;
    private static int _sent, _recv, _tracked, _errors;
    private static float _telemetryTimer;

    public static void Tick(float dt)
    {
        try
        {
            if (CoopRole.IsHostSide) HostTick(dt);
            else if (CoopRole.IsClientSide) ClientTick(dt);
        }
        catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogError($"[SoulstoneCoop][taskobj] Tick: {ex}"); }

        _telemetryTimer += dt;
        if (_telemetryTimer < 10f) return;
        _telemetryTimer = 0f;
        if (_sent + _recv + _tracked + _errors == 0) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][taskobj] {NetworkManager.Role}: sent={_sent} recv={_recv} tracked={_tracked} active={Local.Count} errors={_errors}");
        _sent = _recv = _tracked = 0;
    }

    private static void HostTick(float dt)
    {
        _sendTimer += dt;
        if (_sendTimer < SendInterval) return;
        _sendTimer = 0f;
        if (!NetworkManager.HasPeer || !GameManagerUtil.HasGameManager) { HostIds.Clear(); return; }

        Out.Clear();
        Dead.Clear();
        foreach (var kv in HostIds) kv.Value.Running = false;
        foreach (var task in ChestSync.HostTasks)
        {
            if (task == null) continue;
            MapTaskCompletionStage stage;
            try { stage = task.Stage; } catch { continue; }
            if (stage != MapTaskCompletionStage.Triggered) continue;
            if (!TryRead(task, out var e)) continue;
            if (!HostIds.TryGetValue(task.Pointer, out var h))
            {
                h = new HostTask { Id = _nextId++, Task = task };
                if (_nextId == 0) _nextId = 1;
                HostIds[task.Pointer] = h;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][taskobj] Host: task objective #{h.Id} '{e.ObjectiveId}' ({(e.Kind == KindTimer ? "timer" : "kill")}, total {e.Total:F0}).");
            }
            h.Running = true;
            e.Id = h.Id;
            Out.Add(e);
        }
        foreach (var kv in HostIds)
        {
            if (kv.Value.Running) continue;
            byte flags = FlagDone;
            try
            {
                var t = kv.Value.Task;
                if (t != null && t.Stage != MapTaskCompletionStage.Completed) flags |= FlagFailed; // reset / cancelled
            }
            catch { }
            Out.Add(new Entry { Id = kv.Value.Id, Kind = 0, Flags = flags, ObjectiveId = "" });
            Dead.Add(kv.Key);
        }
        foreach (var p in Dead) HostIds.Remove(p);
        if (Out.Count == 0) return;

        var w = new NetDataWriter();
        w.Put((byte)PacketType.TaskObjective);
        w.Put((byte)Math.Min(Out.Count, 255));
        for (int i = 0; i < Out.Count && i < 255; i++)
        {
            var e = Out[i];
            w.Put(e.Id); w.Put(e.Kind); w.Put(e.ObjectiveId ?? ""); w.Put(e.Total); w.Put(e.Current); w.Put(e.Flags);
        }
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        _sent++;
    }

    private static bool TryRead(MapTask task, out Entry e)
    {
        e = default;
        var survive = task.TryCast<MapSurviveForSecondsTask>();
        var timer = survive != null ? survive._timerObjective : null;
        if (timer != null)
        {
            e.Kind = KindTimer;
            e.ObjectiveId = timer.Id ?? "";
            e.Total = timer.TotalTimeInSeconds;
            e.Current = timer.CurrentTimeInSeconds;
            if (timer.CountDown) e.Flags |= FlagCountDown;
            if (timer.Completed) e.Flags |= FlagDone;
            return true;
        }
        EliminateSpecificEntitiesObjective? kill = null;
        var bosses = task.TryCast<MapKillBossesInAreaTask>();
        if (bosses != null) kill = bosses._eliminationObjective;
        var elites = kill == null ? task.TryCast<MapKillElitesInAreaTask>() : null;
        if (elites != null) kill = elites._eliminationObjective;
        if (kill == null) return false;
        e.Kind = KindKill;
        e.ObjectiveId = kill.Id ?? "";
        e.Total = kill.Total;
        e.Current = kill.Current;
        if (kill.Total > 0 && kill.Current >= kill.Total) e.Flags |= FlagDone;
        return true;
    }

    // ---------------- guest ----------------

    private sealed class Shown
    {
        public byte Kind;
        public ObjectiveBase? Objective;
        public TimerObjective? Timer;
        public EliminateSpecificEntitiesObjective? Kill;
        public float LastSeen;
        public bool CountDown;
    }

    private static readonly Dictionary<ushort, Shown> Local = new();
    private static readonly List<ushort> Finished = new();
    private static IntPtr _panelPtr;

    public static void OnPacket(NetDataReader r)
    {
        int n = r.GetByte();
        bool forUs = CoopRole.IsClientSide && PacketRouter.From == Players.HostSlot;
        for (int i = 0; i < n; i++)
        {
            var e = new Entry
            {
                Id = r.GetUShort(), Kind = r.GetByte(), ObjectiveId = r.GetString(),
                Total = r.GetFloat(), Current = r.GetFloat(), Flags = r.GetByte(),
            };
            _recv++;
            if (!forUs) continue;
            try { Apply(e); }
            catch (Exception ex) { if (_errors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][taskobj] Applying #{e.Id}: {ex.Message}"); }
        }
    }

    private static ObjectivesPanelReferences? Panel()
    {
        var ui = GameManagerUtil.GameplayUIManager;
        var gp = ui != null ? ui.GameplayPanel : null;
        return gp != null ? gp.ObjectivesPanel : null;
    }

    private static void Apply(Entry e)
    {
        bool done = (e.Flags & FlagDone) != 0;
        if (!Local.TryGetValue(e.Id, out var s))
        {
            if (done || e.Kind == 0 || !GameManagerUtil.HasGameManager) return;
            var panel = Panel();
            if (panel == null) return;
            s = new Shown { Kind = e.Kind, CountDown = (e.Flags & FlagCountDown) != 0 };
            if (e.Kind == KindTimer)
            {
                s.Timer = new TimerObjective(e.ObjectiveId, e.Total, s.CountDown);
                s.Objective = s.Timer;
            }
            else
            {
                s.Kill = new EliminateSpecificEntitiesObjective(e.ObjectiveId, Mathf.RoundToInt(e.Total));
                s.Objective = s.Kill;
            }
            panel.TrackObjective(s.Objective);
            _panelPtr = panel.Pointer;
            Local[e.Id] = s;
            _tracked++;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][taskobj] Client: task objective #{e.Id} '{e.ObjectiveId}' on the panel ({(e.Kind == KindTimer ? "timer" : "kill")}, total {e.Total:F0}).");
        }
        s.LastSeen = Time.unscaledTime;
        if (s.Timer != null && e.Kind == KindTimer)
        {
            s.Timer.TotalTimeInSeconds = e.Total;
            s.Timer.CurrentTimeInSeconds = e.Current;
        }
        else if (s.Kill != null && e.Kind == KindKill)
        {
            s.Kill.Total = Mathf.RoundToInt(e.Total);
            s.Kill.Current = Mathf.RoundToInt(e.Current);
        }
        if (done) Finish(e.Id, s, (e.Flags & FlagFailed) != 0);
    }

    private static void Finish(ushort id, Shown s, bool failed = false)
    {
        try
        {
            if (failed && s.Objective != null) { s.Objective.Failed = true; Local.Remove(id); return; }
            if (s.Timer != null) { s.Timer.CurrentTimeInSeconds = s.Timer.TotalTimeInSeconds; s.Timer.Completed = true; }
            if (s.Kill != null) s.Kill.Current = s.Kill.Total;
        }
        catch { /* the panel item goes with its scene anyway */ }
        Local.Remove(id);
    }

    private static void ClientTick(float dt)
    {
        if (Local.Count == 0) return;
        var panel = GameManagerUtil.HasGameManager ? Panel() : null;
        if (panel == null || panel.Pointer != _panelPtr) { Local.Clear(); return; } // another scene: those items are gone
        float now = Time.unscaledTime;
        Finished.Clear();
        // The timer value is the host's, twice a second (counting up or down is the objective's own business).
        foreach (var kv in Local)
            if (now - kv.Value.LastSeen > StaleAfter) Finished.Add(kv.Key);
        foreach (var id in Finished) if (Local.TryGetValue(id, out var s)) Finish(id, s);
    }
}
