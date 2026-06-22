using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services;

/// <summary>Checks which winget packages have an available upgrade by parsing one `winget upgrade`
/// run, cached per process. Update availability for an item = its package id appears in that output.</summary>
public static class UpdateChecker
{
    private static string? _cache;

    public static async Task<string> WingetUpgradeOutputAsync(bool force = false)
    {
        if (_cache != null && !force) return _cache;
        try
        {
            var r = await Proc.RunAsync("winget", new[]
            {
                "upgrade", "--include-unknown", "--disable-interactivity", "--accept-source-agreements",
            });
            _cache = r.StdOut;
        }
        catch { _cache = ""; }
        return _cache!;
    }

    public static void Reset() => _cache = null;

    /// <summary>True if the item (winget / winget-bundle) has an available upgrade in <paramref name="output"/>.</summary>
    public static bool HasUpgrade(CatalogItem item, string output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        var ins = item.Install;
        if (ins.Method == "winget" && !string.IsNullOrEmpty(ins.Id))
            return output.Contains(ins.Id, StringComparison.OrdinalIgnoreCase);
        if (ins.Method == "winget-bundle" && ins.Ids is { Count: > 0 } ids)
            return ids.Any(id => output.Contains(id, StringComparison.OrdinalIgnoreCase));
        return false;
    }
}
