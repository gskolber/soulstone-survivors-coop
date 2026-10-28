using SoulstoneCoop.Enemies;
using SoulstoneCoop.Lobby;
using UnityEngine;

namespace SoulstoneCoop.Ui;

/// <summary>Debug only (--coop-mob-shot &lt;folder&gt;, issues #6 and #3): two local instances play a short scripted run
/// so the client's enemies can be compared with the host's without a person at the keyboard.
/// - Host (--coop-host): gets past the title screen, waits for a partner in the menu, starts a run on
///   --coop-mob-shot-map &lt;scene&gt; (default SurvivorsDungeon), shoots at 15 and 30 s and quits at 40 s.
/// - Client (--coop-connect): follows the run, shoots at the same times and quits at 38 s.
/// Both stay invulnerable. Extra switches:
///   --coop-mob-shot-affixes &lt;a,b,c|none&gt; (host): the affix list RunStart sends instead of the profile's (never saved);
///   --coop-mob-shot-nofix (client): the client skips the issue #6 fallback, to see the old behaviour.
/// The client logs its profile's affix list for the map before the run and after StartMatch (issue #3: it must be its own).</summary>
internal static class MobShot
{
    private static string? _dir;
    private static string _map = "SurvivorsDungeon";
    private static int _step;
    private static float _wait, _clock, _runClock, _splashTimer, _partnerInMenu;
    private static bool _done, _runStarted;
    private static string? _profileBefore;
    private const float Deadline = 300f;

    internal static void ParseArgs(string[] args)
    {
        foreach (var a in args) if (a == "--coop-mob-shot-nofix") EnemyPrefabKeys.DebugNoOutsideLists = true;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--coop-mob-shot") _dir = args[i + 1];
            else if (args[i] == "--coop-mob-shot-map") _map = args[i + 1];
            else if (args[i] == "--coop-mob-shot-affixes")
                RunSync.DebugHostAffixes = args[i + 1] == "none" ? new List<string>() : args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        if (_dir == null) return;
        try { Directory.CreateDirectory(_dir); } catch { }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Mob screenshot mode: {_dir} (map {_map}, nofix={EnemyPrefabKeys.DebugNoOutsideLists}, " +
                                 $"hostAffixes={(RunSync.DebugHostAffixes == null ? "profile" : string.Join(",", RunSync.DebugHostAffixes))}).");
    }

    private static bool IsHost => Plugin.HostArg;

    internal static void Tick()
    {
        if (_dir == null || _done) return;
        try
        {
            float dt = Time.unscaledDeltaTime;
            _clock += dt;
            if (_clock > Deadline)
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][shot] Mob shot deadline at step {_step}: quitting.");
                Finish();
                return;
            }
            if (CoopSession.LocalInRun && GameState.LocalPlayerEntity != null && Time.timeScale > 0.01f) _runClock += dt;
            KeepAlive();
            if (_wait > 0f) { _wait -= dt; return; }
            if (IsHost) HostStep(); else ClientStep();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][shot] mob step {_step}: {ex}");
            _step++;
            _wait = 1f;
        }
    }

    private static void Menu()
    {
        if (CoopUi.MenuReady)
        {
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Menu ready at {_clock:F0}s.");
            _step = 1;
            _wait = UiShot.SetGameLanguage() ? 5f : 1f;
            return;
        }
        _splashTimer -= Time.unscaledDeltaTime;
        if (_splashTimer <= 0f) { _splashTimer = 3f; UiShot.PassSplash(); }
    }

    private static void At(float t, Action action)
    {
        if (_runClock < t) return;
        action();
        _step++;
    }

    private static void HostStep()
    {
        switch (_step)
        {
            case 0: Menu(); return;
            case 1:
            {
                bool ready = false;
                foreach (var r in CoopSession.Remotes) ready |= r.Info.Known && r.Place == LobbyPlace.Menu;
                // "Menu" already holds on the title screen, before the partner's profile loads; a RunStart then leaves it
                // stuck loading. Give it a while in the menu first.
                _partnerInMenu = ready ? _partnerInMenu + Time.unscaledDeltaTime : 0f;
                if (_partnerInMenu < 15f || _runStarted) return;
                _runStarted = true;
                Global.SelectedGameMode = GameModes.Survivor;
                MainUIManager.Instance.ChangeScene(_map, 0f, LoadingScreenManager.LoadingScreenMode.Full);
                RunSync.AfterHostFight(_map);
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Host: run started ({_map}).");
                _step = 2;
                return;
            }
            case 2: At(15f, () => { LogLists(); Shot("host-15s"); }); return;
            case 3: At(30f, () => Shot("host-30s")); return;
            case 4: At(40f, Finish); return;
        }
    }

    private static void ClientStep()
    {
        switch (_step)
        {
            case 0: if (CoopSession.LocalInRun) _step = 2; else Menu(); return;
            case 1:
                if (!CoopSession.LocalLoading) _profileBefore = ProfileAffixes(); // the menu, profile loaded
                if (CoopSession.LocalInRun) _step = 2;
                return;
            case 2:
                At(2f, () => Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][shot] Client: profile affixes for {_map} before the run [{_profileBefore}], after StartMatch [{ProfileAffixes()}] " +
                    $"-> {(_profileBefore == ProfileAffixes() ? "restored" : "CHANGED")}."));
                return;
            case 3: At(15f, () => { LogLists(); Shot("client-15s"); }); return;
            case 4: At(30f, () => Shot("client-30s")); return;
            case 5: At(38f, Finish); return;
        }
    }

    private static string ProfileAffixes()
    {
        try
        {
            var mp = Global.PlayerProfile?.MapProgression;
            if (mp == null || !mp.TryGetMapProgressionById(_map, out var prog) || prog == null) return "no progression";
            var list = prog.SelectedAffixes;
            var names = new List<string>();
            if (list != null) foreach (var a in list) names.Add(a);
            return $"disabled={prog.AffixesDisabled} {(list == null ? "null" : string.Join(",", names))}";
        }
        catch (Exception ex) { return "error " + ex.Message; }
    }

    /// <summary>The tier lists this machine rolled (issue #6: each machine rolls its own).</summary>
    private static void LogLists()
    {
        var parts = new List<string>();
        void Add(string name, Il2CppSystem.Collections.Generic.List<EnemyPrefabByPowerWithData>? l)
        {
            var names = new List<string>();
            if (l != null) for (int i = 0; i < l.Count; i++) names.Add(l[i].PrefabAddress);
            parts.Add($"{name}=[{string.Join(",", names)}]");
        }
        Add("T1", EnemyPrefabsUtil.Tier1); Add("T2", EnemyPrefabsUtil.Tier2); Add("T3", EnemyPrefabsUtil.Tier3);
        Add("E1", EnemyPrefabsUtil.Tier1Elite);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Tier lists: {string.Join(" ", parts)}");
    }

    private static void KeepAlive()
    {
        var h = GameState.LocalPlayerEntity != null ? GameState.LocalPlayerEntity._health : null;
        if (h != null && CoopSession.LocalInRun && h.InvulnerabilityTime < 1f) h.InvulnerabilityTime = 1f;
    }

    private static void Shot(string name)
    {
        string path = Path.Combine(_dir!, $"{name}.png");
        ScreenCapture.CaptureScreenshot(path);
        string count = IsHost ? $"hostTracked={HostEnemySync.TrackedCount}" : $"puppetsAlive={ClientPuppets.AliveCount} outsideLists={EnemyPrefabKeys.OutsideListsResolved}";
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] {path} ({Screen.width}x{Screen.height}) runClock={_runClock:F1} {count}.");
    }

    private static void Finish()
    {
        _done = true;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][shot] Mob shot done: quitting.");
        Application.Quit();
    }
}
