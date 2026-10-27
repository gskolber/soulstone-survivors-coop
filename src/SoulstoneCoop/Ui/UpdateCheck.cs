using System.Net.Http;
using System.Text.Json;
using BepInEx.Configuration;

namespace SoulstoneCoop.Ui;

/// <summary>
/// 1.0: tells the player when a newer release is out (it doesn't download anything). At startup, on a worker thread,
/// one GET of GitHub's "latest release" (drafts and prereleases are never "latest"), reading only tag_name and
/// html_url. Any failure (offline, rate limit, non-200, bad JSON) is a log line and nothing else. The result is
/// handed to the main thread through a volatile field; CoopUi shows the pop-up when the main menu is idle.
/// [Updates] CheckForUpdates = false makes no request at all. Debug: --coop-fake-latest 1.0.1 (no network),
/// --coop-update-url &lt;url&gt; (another endpoint, to test failures).
/// </summary>
public static class UpdateCheck
{
    public const string RepoUrl = "https://github.com/gskolber/soulstone-survivors-coop/";
    private const string ApiUrl = "https://api.github.com/repos/gskolber/soulstone-survivors-coop/releases/latest";

    public sealed class Release
    {
        public string Tag = "";
        public string Version = ""; // tag without the leading "v"
        public string Url = "";
    }

    private static ConfigEntry<bool>? _enabled;
    private static ConfigEntry<string>? _skipped;
    private static string? _fakeLatest, _urlOverride;

    // Written by the worker, read by the main thread.
    private static volatile Release? _found;
    private static volatile string? _logLine;
    private static bool _taken;

    public static void Bind(ConfigFile config)
    {
        _enabled = config.Bind("Updates", "CheckForUpdates", true,
            "Look for a newer release of the mod on GitHub at startup and say so in the main menu (it never downloads anything). false = no request at all.");
        _skipped = config.Bind("Updates", "SkippedVersion", "",
            "Release you chose not to be reminded about (\"Don't remind me for this version\"). Empty = remind about any newer one.");
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--coop-fake-latest=", StringComparison.Ordinal)) _fakeLatest = a.Substring("--coop-fake-latest=".Length);
            else if (a == "--coop-fake-latest" && i + 1 < args.Length) _fakeLatest = args[i + 1];
            else if (a == "--coop-update-url" && i + 1 < args.Length) _urlOverride = args[i + 1];
        }
    }

    /// <summary>Starts the check (Plugin.Load). Never throws, never blocks.</summary>
    public static void Start()
    {
        try
        {
            if (_fakeLatest != null)
            {
                Consider(_fakeLatest, RepoUrl + "releases/latest", "fake (--coop-fake-latest)");
                return;
            }
            if (_enabled != null && !_enabled.Value)
            {
                _logLine = "Update check off ([Updates] CheckForUpdates = false).";
                return;
            }
            Task.Run(CheckAsync);
        }
        catch (Exception ex) { _logLine = $"Update check not started: {ex.Message}"; }
    }

    private static async Task CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var req = new HttpRequestMessage(HttpMethod.Get, _urlOverride ?? ApiUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", $"SoulstoneCoop/{Plugin.PluginVersion}");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logLine = $"Update check: HTTP {(int)resp.StatusCode} - skipped.";
                return;
            }
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
            Consider(tag, url, "GitHub");
        }
        catch (Exception ex)
        {
            // Offline, DNS, TLS, timeout, bad JSON: nothing to tell the player.
            _logLine = $"Update check failed ({ex.GetType().Name}: {ex.Message}) - skipped.";
        }
    }

    private static void Consider(string tag, string url, string source)
    {
        string latest = tag.Trim().TrimStart('v', 'V');
        if (!TryParse(latest, out var lv) || !TryParse(Plugin.PluginVersion, out var cv))
        {
            _logLine = $"Update check ({source}): can't compare '{tag}' with {Plugin.PluginVersion}.";
            return;
        }
        if (lv <= cv)
        {
            _logLine = $"Update check ({source}): latest is {latest}, this is {Plugin.PluginVersion} - up to date.";
            return;
        }
        string skipped = (_skipped?.Value ?? "").Trim().TrimStart('v', 'V');
        if (skipped.Length > 0 && TryParse(skipped, out var sv) && sv == lv)
        {
            _logLine = $"Update check ({source}): {latest} is out, but the player skipped it.";
            return;
        }
        // Only ever open this repository's pages.
        if (!url.StartsWith(RepoUrl, StringComparison.Ordinal)) url = RepoUrl + "releases/latest";
        _found = new Release { Tag = tag.Trim(), Version = latest, Url = url };
        _logLine = $"Update check ({source}): {latest} is out (this is {Plugin.PluginVersion}).";
    }

    /// <summary>"1.2.3", "1.2", "1.2.3-beta+x" → Version (pre-release/build suffix dropped).</summary>
    internal static bool TryParse(string s, out Version v)
    {
        v = new Version(0, 0);
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s.Substring(0, cut);
        if (!Version.TryParse(s, out var p) || p == null) return false;
        v = new Version(p.Major, Math.Max(p.Minor, 0), Math.Max(p.Build, 0));
        return true;
    }

    /// <summary>Main thread, every frame: the log line from the worker, and (once) the release to announce.</summary>
    internal static Release? Poll()
    {
        var line = _logLine;
        if (line != null)
        {
            _logLine = null;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][update] {line}");
        }
        if (_taken) return null;
        var r = _found;
        if (r != null) _taken = true;
        return r;
    }

    internal static void Skip(Release r)
    {
        try { if (_skipped != null) _skipped.Value = r.Tag; } catch { }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][update] Won't remind about {r.Tag} again.");
    }
}
