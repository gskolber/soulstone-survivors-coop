namespace SoulstoneCoop.Ui;

/// <summary>
/// 1.9.0: a boss cutscene is playing on this machine. The mod's own overlays (party panel, the partners' name and health
/// tags, the downed rings and the level-up popup) hide meanwhile, as the game's HUD does:
///   - GameplayUIManager.ForceHideGameplayElements (the game hiding its own HUD),
///   - a Crusade map's boss presentation (Modes.CrusadeSync.PresentationRunning),
///   - a Mastery main boss phase transition (Modes.MasterySync.BossCutsceneRunning: intro, arena, death).
/// Read many times a frame: computed once per frame.
/// </summary>
public static class Cutscene
{
    private static int _frame = -1;
    private static bool _running, _wasRunning;

    public static bool Running
    {
        get
        {
            int f = UnityEngine.Time.frameCount;
            if (f == _frame) return _running;
            _frame = f;
            _running = Compute();
            if (_running != _wasRunning)
            {
                _wasRunning = _running;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][ui] Boss cutscene {(_running ? "started: co-op overlays hidden" : "over: co-op overlays back")}.");
            }
            return _running;
        }
    }

    private static bool Compute()
    {
        if (!GameManagerUtil.HasGameManager) return false;
        try
        {
            var ui = GameManagerUtil.GameplayUIManager;
            if (ui != null && ui.ForceHideGameplayElements && !Lobby.CoopSession.StageTransition) return true;
        }
        catch { }
        return Modes.CrusadeSync.PresentationRunning || Modes.MasterySync.BossCutsceneRunning;
    }
}
