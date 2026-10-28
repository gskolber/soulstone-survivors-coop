using HarmonyLib;
using SoulstoneCoop.Enemies;
using SoulstoneCoop.Lobby;
using UnityEngine;

namespace SoulstoneCoop.Ui;

/// <summary>Debug only (--coop-mob-shot &lt;folder&gt;, issues #6 and #3): two local instances play a short scripted run
/// so the client's enemies, affixes and objectives can be compared with the host's without a person at the keyboard.
/// - Host (--coop-host): gets past the title screen, waits until the partner has been in the menu for 15 s (on the
///   title screen it already says "menu", before its profile loads), starts a run on --coop-mob-shot-map &lt;scene&gt;
///   (default SurvivorsDungeon).
/// - Client (--coop-connect): follows the run.
/// Both shoot at 15 and 30 s (and 10 s before the end), log their tier lists, run affixes and objectives, and quit after
/// --coop-mob-shot-length &lt;s&gt; (default 40; the client 2 s earlier). Both stay invulnerable. Extra switches:
///   --coop-profile-dir &lt;folder&gt;: this instance reads and writes its save files there (StandalonePlayerDataProvider),
///     so two local instances can have different profiles; seed it with a copy of the real save folder;
///   --coop-mob-shot-set-affixes &lt;a,b,c|none&gt;: once the profile is loaded, this instance's selection for the map
///     (only in memory, and saved only into its own --coop-profile-dir);
///   --coop-mob-shot-tier &lt;n&gt; (host): GameData.Tier forced to n after 5 s, so the regular spawner uses another tier list;
///   --coop-mob-shot-forget &lt;address&gt; (client): drop that address from our own tier lists at the start of the run, as if
///     we had rolled variants without it (the issue #6 case);
///   --coop-mob-shot-nofix (client): skip the issue #6 fallback, to see the old behaviour.</summary>
internal static class MobShot
{
    private static string? _dir;
    private static string _map = "SurvivorsDungeon";
    private static float _length = 40f;
    private static int _forceTier = -1;
    private static string? _forget;
    private static List<string>? _setAffixes;
    internal static string? ProfileDir;
    private static int _step;
    private static float _wait, _clock, _runClock, _splashTimer, _partnerInMenu, _lastLog;
    private static bool _done, _runStarted, _affixesSet, _shotEnd;
    private static string? _profileBefore;

    internal static void ParseArgs(string[] args)
    {
        foreach (var a in args) if (a == "--coop-mob-shot-nofix") EnemyPrefabKeys.DebugNoOutsideLists = true;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--coop-mob-shot") _dir = args[i + 1];
            else if (args[i] == "--coop-mob-shot-map") _map = args[i + 1];
            else if (args[i] == "--coop-mob-shot-length" && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var l)) _length = l;
            else if (args[i] == "--coop-mob-shot-set-affixes")
                _setAffixes = args[i + 1] == "none" ? new List<string>() : args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            else if (args[i] == "--coop-profile-dir") ProfileDir = args[i + 1];
            else if (args[i] == "--coop-mob-shot-forget") _forget = args[i + 1];
            else if (args[i] == "--coop-mob-shot-tier" && int.TryParse(args[i + 1], out var t)) _forceTier = t;
        }
        if (_dir == null) return;
        try { Directory.CreateDirectory(_dir); } catch { }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Mob screenshot mode: {_dir} (map {_map}, length {_length}s, nofix={EnemyPrefabKeys.DebugNoOutsideLists}, " +
                                 $"setAffixes={(_setAffixes == null ? "-" : string.Join(",", _setAffixes))}, profileDir={ProfileDir ?? "-"}).");
    }

    private static bool IsHost => Plugin.HostArg;

    internal static void Tick()
    {
        if (_dir == null || _done) return;
        try
        {
            float dt = Time.unscaledDeltaTime;
            _clock += dt;
            if (_clock > _length + 260f)
            {
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][shot] Mob shot deadline at step {_step}: quitting.");
                Finish();
                return;
            }
            if (CoopSession.LocalInRun && GameState.LocalPlayerEntity != null && Time.timeScale > 0.01f) _runClock += dt;
            KeepAlive();
            if (IsHost && _forceTier >= 0 && _runClock > 5f && GameManagerUtil.GameData is { } gd && gd.Tier < _forceTier)
            {
                gd.Tier = _forceTier; // --coop-mob-shot-tier: the spawner picks from that tier list (issue #6 path)
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Host: tier forced to {_forceTier}.");
            }
            if (_runClock - _lastLog >= 30f) { _lastLog = _runClock; LogRun(); }
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

    /// <summary>In the menu, profile loaded: this instance's own selection for the map (--coop-mob-shot-set-affixes).</summary>
    private static void InMenu()
    {
        if (_setAffixes != null && !_affixesSet && TryGetProgression(out var prog))
        {
            _affixesSet = true;
            prog.AffixesDisabled = false;
            prog.SelectedAffixes ??= new Il2CppSystem.Collections.Generic.List<string>();
            prog.SelectedAffixes.Clear();
            foreach (var a in _setAffixes) prog.SelectedAffixes.Add(a);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Own affixes for {_map} set to [{string.Join(",", _setAffixes)}].");
        }
        if (CoopSession.LocalLoading) return;
        string now = ProfileAffixes();
        if (now != _profileBefore) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Profile affixes for {_map}: [{now}].");
        _profileBefore = now;
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
                InMenu();
                bool ready = false;
                foreach (var r in CoopSession.Remotes) ready |= r.Info.Known && r.Place == LobbyPlace.Menu;
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
            default: RunSteps("host", _length); return;
        }
    }

    private static void ClientStep()
    {
        switch (_step)
        {
            case 0: if (CoopSession.LocalInRun) _step = 2; else Menu(); return;
            case 1:
                InMenu();
                if (CoopSession.LocalInRun) _step = 2;
                return;
            default: RunSteps("client", _length - 2f); return;
        }
    }

    private static void RunSteps(string side, float end)
    {
        switch (_step)
        {
            case 2 when _forget != null && !IsHost && !_forgotten: _forgotten = true; Forget(_forget); return;
            case 2:
                At(2f, () => Plugin.StaticLog.LogInfo(
                    $"[SoulstoneCoop][shot] {side}: profile affixes for {_map} before the run [{_profileBefore}], after StartMatch [{ProfileAffixes()}] " +
                    $"-> {(_profileBefore == ProfileAffixes() ? "unchanged" : "CHANGED")}."));
                return;
            case 3: At(15f, () => { LogLists(); LogRun(); Shot($"{side}-15s"); }); return;
            case 4: At(30f, () => { LogRun(); Shot($"{side}-30s"); }); return;
            case 5:
                if (end > 45f && !_shotEnd) { if (_runClock < end - 10f) return; _shotEnd = true; LogLists(); LogRun(); Shot($"{side}-{end - 10f:F0}s"); }
                At(end, Finish);
                return;
        }
    }

    private static bool _forgotten;

    private static void Forget(string address)
    {
        int n = 0;
        foreach (var l in new[] { EnemyPrefabsUtil.Tier1, EnemyPrefabsUtil.Tier2, EnemyPrefabsUtil.Tier3, EnemyPrefabsUtil.Tier4, EnemyPrefabsUtil.Tier5,
                                  EnemyPrefabsUtil.Tier1Elite, EnemyPrefabsUtil.Tier2Elite, EnemyPrefabsUtil.Tier3Elite, EnemyPrefabsUtil.Tier4Elite, EnemyPrefabsUtil.Tier5Elite })
        {
            if (l == null) continue;
            for (int i = l.Count - 1; i >= 0; i--) if (l[i].PrefabAddress == address) { l.RemoveAt(i); n++; }
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Client: dropped '{address}' from our tier lists ({n} entries).");
    }

    private static bool TryGetProgression(out PlayerProfileMapProgressionData prog)
    {
        prog = null!;
        var mp = Global.PlayerProfile?.MapProgression;
        return mp != null && mp.TryGetMapProgressionById(_map, out prog) && prog != null;
    }

    private static string ProfileAffixes()
    {
        try
        {
            if (!TryGetProgression(out var prog)) return "no progression";
            var list = prog.SelectedAffixes;
            var names = new List<string>();
            if (list != null) foreach (var a in list) names.Add(a);
            return $"disabled={prog.AffixesDisabled} {(list == null ? "null" : string.Join(",", names))}";
        }
        catch (Exception ex) { return "error " + ex.Message; }
    }

    /// <summary>What this run actually got: affixes, curse level, tier and the objectives (issue #3: boss counters).</summary>
    private static void LogRun()
    {
        if (!CoopSession.LocalInRun) return;
        try
        {
            var data = GameManagerUtil.AffixGameplayData;
            var names = new List<string>();
            if (data?.SelectedAffixes != null) foreach (var a in data.SelectedAffixes) names.Add(a);
            var gd = GameManagerUtil.GameData;
            var sgm = gd?.SurvivorsGameManager;
            var main = sgm?.MainObjective;
            var progs = sgm?.ProgressObjectives;
            string prog = progs != null && progs.Count > 0 && progs[0] != null ? $"{progs[0].Current}/{progs[0].Total}" : "-";
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][shot] Run t={_runClock:F0}s: affixes [{string.Join(",", names)}] curse={data?.CurseLevel ?? -1} " +
                $"extraBosses={data?.AdditionalBossQuantity ?? -1} simultaneousBosses={data?.AdditionalSimultaneousBossesAmount ?? -1} tier={gd?.Tier ?? -1} " +
                $"main={(main != null ? $"{main.Current}/{main.Total}" : "-")} progress={prog} pendingBosses={gd?.BossSpawnDetails?._pendingBosses ?? -1}.");
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][shot] LogRun: {ex.Message}"); }
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

/// <summary>--coop-profile-dir: this instance's save files live in its own folder (debug, MobShot).</summary>
[HarmonyPatch(typeof(StandalonePlayerDataProvider), nameof(StandalonePlayerDataProvider.Init))]
public static class DebugProfileDirPatch
{
    [HarmonyPostfix]
    public static void Postfix(StandalonePlayerDataProvider __instance)
    {
        try
        {
            if (MobShot.ProfileDir == null || __instance == null) return;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Save folder {__instance.PersistentDataPath} -> {MobShot.ProfileDir}");
            __instance.PersistentDataPath = MobShot.ProfileDir;
        }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] DebugProfileDirPatch: {ex}"); }
    }
}
