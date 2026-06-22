using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine;

/// <summary>Idempotency: decides whether an item is already present. Returns true on ANY positive
/// signal (command on PATH, file/dir, winget id, or ARP display-name) — so an app installed outside
/// winget still detects correctly.</summary>
public static class Detection
{
    private static string? _wingetList;

    /// <summary>Drop the cached `winget list` so detection re-reads after an install/uninstall.</summary>
    public static void ResetCache() => _wingetList = null;

    public static async Task<bool> IsInstalledAsync(CatalogItem item, PathResolver pr)
    {
        var d = item.Detect;
        if (d?.Cmd != null && CommandFinder.Exists(d.Cmd)) return true;
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

    private static async Task<bool> WingetHasAsync(string id)
    {
        try
        {
            var r = await Proc.RunAsync("winget", new[]
            {
                "list", "--id", id, "-e", "--disable-interactivity", "--accept-source-agreements",
            });
            return r.Ok; // exit 0 = found; avoids the list table truncating long ids
        }
        catch { return false; }
    }

    private static async Task<bool> AllWingetHaveAsync(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (!await WingetHasAsync(id)) return false;
        return true;
    }

    /// <summary>Matches a display-name prefix against the full `winget list` (which enumerates ARP,
    /// so it includes apps installed outside winget like 火绒 / 微信). Loaded once per process.</summary>
    private static async Task<bool> ArpHasAsync(string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return false;
        _wingetList ??= await LoadWingetListAsync();
        foreach (var line in _wingetList.Split('\n'))
            if (line.TrimStart().StartsWith(hint, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task<string> LoadWingetListAsync()
    {
        try
        {
            var r = await Proc.RunAsync("winget", new[] { "list", "--disable-interactivity", "--accept-source-agreements" });
            return r.StdOut;
        }
        catch { return ""; }
    }
}
