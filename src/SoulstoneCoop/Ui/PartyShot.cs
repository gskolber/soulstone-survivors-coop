using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Ui;

/// <summary>Debug only (--coop-party-shot &lt;folder&gt;, issue #7): two local instances play a scripted run so the
/// party panel can be seen without a person at the keyboard.
/// - Host (--coop-host): gets past the title screen, waits for a partner in the menu, starts a Desert run (what the
///   client does on RunStart), shoots the panel with the real partner (alive, then down and being revived, then
///   disconnected), then with 7 fake rows in every state, dumps the run's UI hierarchy and quits.
/// - Client (--coop-connect): follows the run, takes a lethal hit (F9's) at 12 s, shoots its own view and quits at 22 s.
/// The clock only runs in the run, unpaused. --coop-ui-shot-lang &lt;code&gt; switches the game's language first.
/// --coop-party-shot-map &lt;scene&gt; picks another map (default SurvivorsDesert).</summary>
internal static class PartyShot
{
    private static string? _dir;
    private static string _map = "SurvivorsDesert";
    private static bool _release;
    private static int _step;
    private static float _wait, _clock, _runClock, _splashTimer;
    private static bool _done, _runStarted;
    private const float Deadline = 360f;

    internal static void ParseArgs(string[] args)
    {
        foreach (var a in args) if (a == "--coop-party-shot-release") _release = true;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--coop-party-shot") _dir = args[i + 1];
            else if (args[i] == "--coop-party-shot-map") _map = args[i + 1];
        }
        if (_dir == null) return;
        try { Directory.CreateDirectory(_dir); } catch { }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Party panel screenshot mode: {_dir} (map {_map}).");
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
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][shot] Party shot deadline at step {_step}: quitting.");
                Finish();
                return;
            }
            // Unpaused only (the loading barrier holds the clock at 0), except at the end: alone again, the host's level-up
            // pauses its game (solo rule) and the clock must still reach the last shots.
            if (CoopSession.LocalInRun && GameState.LocalPlayerEntity != null && (Time.timeScale > 0.01f || IsHost && _step >= 10)) _runClock += dt;
            KeepAlive();
            if (_wait > 0f) { _wait -= dt; return; }
            if (IsHost) HostStep(); else ClientStep();
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][shot] party step {_step}: {ex}");
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

    /// <summary>Waits for the run clock to reach t, then runs the action once and moves on.</summary>
    private static void At(float t, Action action)
    {
        if (_runClock < t) return;
        action();
        _step++;
        _stepAt = _runClock;
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
                if (!ready || _runStarted) return;
                _runStarted = true;
                Global.SelectedGameMode = GameModes.Survivor;
                MainUIManager.Instance.ChangeScene(_map, 0f, LoadingScreenManager.LoadingScreenMode.Full);
                RunSync.AfterHostFight(_map);
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] Host: run started ({_map}).");
                _step = 2;
                return;
            }
            // --coop-party-shot-release: three partners (the real one is the first row), one being revived, a few frames.
            case 2 when _release: At(5f, () => PartyHud.DebugRows = ReleaseRows()); return;
            case 3 when _release: After(1.5f, () => Shot("release-1")); return;
            case 4 when _release: After(2.5f, () => Shot("release-2")); return;
            case 5 when _release: After(2.5f, () => Shot("release-3")); return;
            case 6 when _release: After(0.5f, Finish); return;
            case 2: At(6f, () => Shot("01-host-2p")); return;
            case 3: After(0.5f, () => PartyHud.DebugRows = FakeRows()); return;
            case 4: After(0.6f, () => Shot("02-host-8p-fake")); return;
            case 5: After(0.1f, () => PartyHud.DebugRows = null); return;
            // The client hits itself from 10 s until it's down (its death guards go first): shoot when we see it.
            case 6: When(PartnerDown(), 25f, () => Shot("03-host-partner-down")); return;
            case 7: After(0.9f, () => Shot("04-host-partner-reviving")); return;
            case 8: After(0.9f, () => Shot("05-host-partner-reviving")); return;
            case 9: When(!PartnerDown() || Players.OtherCount == 0, 40f, () => Shot("06-host-after-down")); return;
            case 10: When(Players.OtherCount == 0, 45f, () => { }); return; // the client quits after its shots
            case 11: After(1.5f, () => Shot("07-host-partner-gone")); return;
            case 12: After(0.5f, Finish); return;
        }
    }

    /// <summary>Standing still among the Desert's first waves, a player goes down within ~20 s and two downs end the run:
    /// the script keeps its players invulnerable (the client only until it starts hitting itself).</summary>
    private static void KeepAlive()
    {
        if (!IsHost && _step >= 6) return;
        var h = GameState.LocalPlayerEntity != null ? GameState.LocalPlayerEntity._health : null;
        if (h != null && CoopSession.LocalInRun && h.InvulnerabilityTime < 1f) h.InvulnerabilityTime = 1f;
    }

    private static bool PartnerDown()
    {
        foreach (var r in CoopSession.Remotes) if (Life.PartnerHealth.Of(r.Slot).Down) return true;
        return false;
    }

    private static float _stepAt;

    /// <summary>Once the condition holds (or the run clock passes the limit), runs the action and moves on.</summary>
    private static void When(bool condition, float limit, Action action)
    {
        if (!condition && _runClock < limit) return;
        if (!condition) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][shot] step {_step}: condition not met by {limit}s.");
        action();
        _step++;
        _stepAt = _runClock;
    }

    /// <summary>That long (run clock) after the previous step.</summary>
    private static void After(float seconds, Action action) => When(_runClock - _stepAt >= seconds, float.MaxValue, action);

    private static float _hitTimer;

    private static void ClientStep()
    {
        switch (_step)
        {
            case 0: if (CoopSession.LocalInRun) _step = 2; else Menu(); return; // the RunStart may come before the menu shows
            case 1: if (CoopSession.LocalInRun) _step = 2; return;
            case 2 when _release: At(25f, Finish); return; // the host does the shooting
            case 2: At(6f, () => Shot("c01-client-2p")); return;
            case 3: After(1f, () => PartyHud.DebugRows = FakeRows()); return;
            case 4: After(0.6f, () => Shot("c02-client-8p-fake")); return;
            case 5: After(0.1f, () => PartyHud.DebugRows = null); return;
            case 6:
                if (_runClock < 10f) return;
                if (!Life.CoopLife.LocalDowned)
                {
                    _hitTimer -= Time.unscaledDeltaTime;
                    if (_hitTimer <= 0f) { _hitTimer = 0.4f; Life.CoopLife.DebugLethalHit(); }
                    if (_runClock < 20f) return;
                }
                _step++;
                _stepAt = _runClock;
                return;
            case 7: After(0.5f, () => Shot("c03-client-self-down")); return;
            case 8: When(!Life.CoopLife.LocalDowned, 30f, () => { }); return;
            case 9: After(1.5f, () => Shot("c04-client-after-down")); return;
            case 10: After(1f, Finish); return;
        }
    }

    /// <summary>For the release notes: three partners mid-run, one of them being revived. Slot 1 matches the real client
    /// (launch it with --coop-name Mira) so its ghost's name tag agrees with the panel.</summary>
    private static List<PartyHud.RowData> ReleaseRows()
    {
        string mira = CoopSession.Get(1)?.Info.CharacterName is { Length: > 0 } c ? c : "MonkeyKing";
        return new List<PartyHud.RowData>
        {
            new() { Slot = 1, Name = "Mira", CharName = mira, State = PartyHud.State.Alive, HpKnown = true, Hp = 168, MaxHp = 205,
                    StatusKnown = true, Level = 14, Xp = 0.55f, Revives = 1 },
            new() { Slot = 2, Name = "Tobias", CharName = "Paladin", State = PartyHud.State.Down, HpKnown = true, Hp = 1, MaxHp = 240,
                    StatusKnown = true, Level = 13, Xp = 0.3f, Revives = 0, Progress = 0.6f },
            new() { Slot = 3, Name = "Kael", CharName = "Necromancer", State = PartyHud.State.Alive, HpKnown = true, Hp = 74, MaxHp = 160,
                    StatusKnown = true, Level = 14, Xp = 0.8f, Revives = 2 },
        };
    }

    /// <summary>Seven partners in every state the panel knows.</summary>
    private static List<PartyHud.RowData> FakeRows()
    {
        PartyHud.RowData R(byte slot, string name, string ch, PartyHud.State st, float hp, float max, int lv, float xp, int rev, float prog = 0f) => new()
        {
            Slot = slot, Name = name, CharName = ch, State = st, HpKnown = true, Hp = hp, MaxHp = max,
            StatusKnown = true, Level = lv, Xp = xp, Revives = rev, Progress = prog,
        };
        var rows = new List<PartyHud.RowData>
        {
            R(1, "Amigo", "Barbarian", PartyHud.State.Alive, 182, 210, 23, 0.62f, 1),
            R(2, "Outra pessoa com nome longo", "Pyromancer", PartyHud.State.Alive, 64, 150, 22, 0.15f, 0),
            R(3, "Marina", "Paladin", PartyHud.State.Down, 1, 240, 23, 0.4f, 0, 0.45f),
            R(4, "Longe", "Necromancer", PartyHud.State.Down, 1, 130, 21, 0.9f, 0),
            R(5, "Zé", "Rogue", PartyHud.State.Dead, 0, 160, 20, 0.3f, 0),
            new() { Slot = 6, Name = "Caiu a net", CharName = "Samurai", State = PartyHud.State.Gone },
            R(7, "Pirata", "Pirate", PartyHud.State.Alive, 25, 190, 24, 0.05f, 2),
        };
        rows[1].Unstable = true;
        return rows;
    }

    private static void Shot(string name)
    {
        string path = Path.Combine(_dir!, $"{name}.png");
        ScreenCapture.CaptureScreenshot(path);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][shot] {path} ({Screen.width}x{Screen.height}) runClock={_runClock:F1} panel={PartyHud.Visible}.");
    }

    private static void Finish()
    {
        _done = true;
        if (IsHost)
        {
            try { UiShot.DumpUi(Path.Combine(_dir!, "run-ui-dump.txt")); }
            catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][shot] dump: {ex}"); }
        }
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][shot] Party shot done: quitting.");
        Application.Quit();
    }
}
