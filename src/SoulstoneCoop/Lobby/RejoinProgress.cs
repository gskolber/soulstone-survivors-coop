using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Lobby;

/// <summary>
/// The host's [Coop] AllowRejoin (sent in RunStart): a guest who drops out of a run and comes back keeps its level, XP, upgrades, runes and items
/// (docs/reentrada-notas.md, docs/proximos-investigacao.md section 5).
///
/// The game already carries a player's build from one scene to the next: between Endless stages it fills a
/// GameModeBaseData from the player (SetDataBasedOnPlayerEntity: level, XP, collected power-ups, runes, banished and
/// locked slots, inventory, summed stats, match time), parks it in GameManagerUtil.InitialEndlessData, and the next
/// scene's Start hands it back to the new player (GetAndRemoveInitialData, SetupPlayerForMatch).
///
/// Guest, while in a co-op run: every SnapshotInterval that same GameModeBaseData is filled from our player and kept
/// in memory, tagged with the run's id (RunStart carries it). When the host's RunStart for that SAME run comes back
/// (the host resends it to a partner who reconnects mid-run), the snapshot goes into InitialEndlessData before the
/// scene loads, with the host's affixes, so the run starts with our build instead of a fresh character. Any other run
/// (a new one, a restart) drops it. Only memory: closing the game loses it.
/// </summary>
public static class RejoinProgress
{
    private const float SnapshotInterval = 5f;

    internal static int CurrentRunId;        // client: the run we're in (from its RunStart)
    internal static bool HostAllowsRejoin;   // client: that run's host has [Coop] AllowRejoin on
    private static GameModeBaseData? _saved;
    private static int _savedRunId;
    private static int _savedLevel;
    private static float _savedAt;
    private static float _timer;
    private static int _snapshots, _errors;

    public static void Tick(float dt)
    {
        if (!HostAllowsRejoin || NetworkManager.Role != NetRole.Client) return;
        _timer += dt;
        if (_timer < SnapshotInterval) return;
        _timer = 0f;
        if (CurrentRunId == 0 || !CoopSession.Connected || !CoopSession.LocalInRun || CoopSession.StageTransition) return;
        if (!GameManagerUtil.HasGameManager || !Enemies.ClientPuppets.MatchStarted) return;
        if (Life.CoopLife.LocalOut || Life.CoopLife.RunEnding) return; // keep the last build from before that
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        try
        {
            int endless = 0;
            try { var current = GameManagerUtil.BaseGameData; if (current != null) endless = current.GetEndlessLevel(); } catch { }
            var data = new GameModeBaseData();
            data.SetDataBasedOnPlayerEntity(local, endless);
            bool first = _saved == null || _savedRunId != CurrentRunId;
            _saved = data;
            _savedRunId = CurrentRunId;
            _savedLevel = data.CurrentLevel;
            _savedAt = Time.unscaledTime;
            _snapshots++;
            if (first) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][rejoin] Client: keeping this run's build for a rejoin (run {CurrentRunId}, level {_savedLevel}, every {SnapshotInterval:F0} s).");
        }
        catch (Exception ex)
        {
            if (_errors++ < 3) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][rejoin] Client: snapshot of our build: {ex.Message}");
        }
    }

    /// <summary>Client, the host's RunStart is about to load its scene: our build for that run, if we have one.</summary>
    internal static void BeforeClientRunLoad(int runId, bool hostAllowsRejoin, List<string>? hostAffixes)
    {
        CurrentRunId = runId;
        HostAllowsRejoin = hostAllowsRejoin;
        if (_saved == null) return;
        if (!hostAllowsRejoin || runId == 0 || runId != _savedRunId)
        {
            _saved = null;
            return;
        }
        try
        {
            if (hostAffixes != null)
            {
                var list = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (var a in hostAffixes) list.Add(a);
                _saved.SelectedAffixes = list;
            }
            GameManagerUtil.InitialEndlessData = _saved;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][rejoin] Client: rejoining run {runId} with our build (level {_savedLevel}, kept {Time.unscaledTime - _savedAt:F0} s ago).");
            CoopSession.Notice(Ui.Loc.Msg("nt.rejoin_build", ("level", _savedLevel.ToString())), 6f);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][rejoin] Client: handing our build to the game: {ex.Message} (fresh character instead)");
        }
        _saved = null; // handed over once; the new run's snapshots replace it
    }

}
