using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine;

/// <summary>Idempotency: decides whether an item is already present. Returns true on ANY positive
/// signal (command on PATH, file/dir, winget id, or ARP display-name) — so an app installed outside
/// winget still detects correctly.</summary>
public static class Detection
{
    // A `winget list` invocation contacts winget's sources and costs ~13s cold / ~2s warm — and offline it
    // stalls until its timeout. So we run it EXACTLY ONCE per detection pass (shared task, lock-guarded so
    // 8 concurrent detections don't each spawn one) and answer every per-item winget query from that single
    // output in-memory. The previous design spawned one `winget list --id X` per catalog item (~150), which
    // offline turned the install-center "detecting" state into a multi-minute (effectively stuck) wait.
    private static readonly object _listLock = new();
    private static Task<string>? _listTask;
    private static Task<HashSet<string>>? _idsTask;

    /// <summary>False after a `winget list` load times out (offline / winget unresponsive) — lets callers
    /// (e.g. the update check) skip further winget round-trips this pass instead of each eating the timeout.</summary>
    public static bool WingetReachable { get; private set; } = true;

    /// <summary>Drop the cached `winget list` so detection re-reads after an install/uninstall.</summary>
    public static void ResetCache()
    {
        lock (_listLock) { _listTask = null; _idsTask = null; WingetReachable = true; }
    }

    public static async Task<bool> IsInstalledAsync(CatalogItem item, PathResolver pr)
    {
        var d = item.Detect;
        if (d?.Cmd != null && CommandFinder.Exists(d.Cmd)) return true;
        if (d?.EnvVar != null && EnvVarDir(d.EnvVar) != null) return true;
        if (d?.Path != null)
        {
            var rp = pr.Resolve(d.Path);
            if (File.Exists(rp) || Directory.Exists(rp)) return true;
        }
        if (d?.WingetId != null && await WingetHasAsync(d.WingetId)) return true;
        if (d?.Arp != null && await ArpHasAsync(d.Arp)) return true;

        switch (item.Install.Method)
        {
            case "winget" when item.Install.Id != null:
                if (await WingetHasAsync(item.Install.Id)) return true;
                break;
            case "winget-bundle" when item.Install.Ids is { Count: > 0 } ids:
                if (await AllWingetHaveAsync(ids)) return true;
                break;
            case "portable" when (item.InstallPathOverride ?? item.Install.ExtractTo) != null:
                if (Directory.Exists(pr.Resolve(item.InstallPathOverride ?? item.Install.ExtractTo!))) return true;
                break;
            case "git" when (item.InstallPathOverride ?? item.Install.Dest) != null:
                if (Directory.Exists(pr.Resolve(item.InstallPathOverride ?? item.Install.Dest!))) return true;
                break;
            case "github-release" when item.InstallPathOverride != null:
                if (Directory.Exists(pr.Resolve(item.InstallPathOverride))) return true;
                break;
        }
        return false;
    }

    /// <summary>If the env var (User or Machine) is set to an existing directory, return that resolved
    /// directory; else null. Used both for detection and to backfill the install path.</summary>
    public static string? EnvVarDir(string name)
    {
        foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.Process })
        {
            var v = Environment.GetEnvironmentVariable(name, target);
            if (string.IsNullOrWhiteSpace(v)) continue;
            try { var p = Environment.ExpandEnvironmentVariables(v.Trim()); if (Directory.Exists(p)) return p.TrimEnd('\\', '/'); }
            catch { /* bad value */ }
        }
        return null;
    }

    /// <summary>Cap on the single `winget list` — must exceed its real cost (~13s cold when winget
    /// auto-refreshes its sources) so it isn't killed mid-flight online, while still bounding the wait
    /// offline (where the source refresh stalls) so detection can't hang forever.</summary>
    private const int WingetListTimeoutSeconds = 25;

    /// <summary>Is the winget package installed? Answered from the single cached `winget list` — winget
    /// correlates ARP entries to their package id, so apps installed outside winget still match.</summary>
    private static async Task<bool> WingetHasAsync(string id)
        => !string.IsNullOrEmpty(id) && (await WingetIdsAsync()).Contains(id);

    private static async Task<bool> AllWingetHaveAsync(IEnumerable<string> ids)
    {
        var have = await WingetIdsAsync();
        return ids.All(have.Contains);
    }

    /// <summary>Matches a display-name prefix against the full `winget list` (which enumerates ARP,
    /// so it includes apps installed outside winget like 火绒 / 微信). Loaded once per process.</summary>
    private static async Task<bool> ArpHasAsync(string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return false;
        var list = await WingetListAsync();
        foreach (var line in list.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(hint, StringComparison.OrdinalIgnoreCase)) continue;
            // Require the hint to match the full display-name token: the next char must be whitespace
            // or end-of-string.  This prevents "哔哩哔哩直播姬" from matching the hint "哔哩哔哩".
            if (trimmed.Length == hint.Length || char.IsWhiteSpace(trimmed[hint.Length])) return true;
        }
        return false;
    }

    /// <summary>The set of every whitespace-delimited token in `winget list` — winget package ids never
    /// contain spaces, so id membership is an exact token lookup (no substring false-positives, and the
    /// redirected output isn't column-truncated, so long ids match). Built once from the shared list.</summary>
    private static Task<HashSet<string>> WingetIdsAsync()
    {
        lock (_listLock) return _idsTask ??= BuildWingetIdsAsync();
    }

    private static async Task<HashSet<string>> BuildWingetIdsAsync()
    {
        var text = await WingetListAsync();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
            foreach (var tok in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                set.Add(tok);
        return set;
    }

    /// <summary>The single, shared `winget list` for this detection pass (see the field comment).</summary>
    private static Task<string> WingetListAsync()
    {
        lock (_listLock) return _listTask ??= LoadWingetListAsync();
    }

    private static async Task<string> LoadWingetListAsync()
    {
        try
        {
            var r = await Proc.RunAsync("winget", new[] { "list", "--disable-interactivity", "--accept-source-agreements" },
                timeoutSeconds: WingetListTimeoutSeconds);
            WingetReachable = true;
            return r.StdOut;
        }
        catch { WingetReachable = false; return ""; }   // timed out / winget missing → treat as "nothing detected"
    }
}
