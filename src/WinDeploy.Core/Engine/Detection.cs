using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine;

/// <summary>Idempotency: decides whether an item is already present on this machine.</summary>
public static class Detection
{
    public static async Task<bool> IsInstalledAsync(CatalogItem item, PathResolver pr)
    {
        var d = item.Detect;
        if (d != null)
        {
            if (d.Cmd != null && CommandFinder.Exists(d.Cmd)) return true;
            if (d.Path != null)
            {
                var rp = pr.Resolve(d.Path);
                if (File.Exists(rp) || Directory.Exists(rp)) return true;
            }
            if (d.WingetId != null && await WingetHasAsync(d.WingetId)) return true;
            return false; // explicit detect provided but nothing matched
        }

        // No explicit detect — infer from install method.
        return item.Install.Method switch
        {
            "winget" when item.Install.Id != null => await WingetHasAsync(item.Install.Id),
            "portable" when item.Install.ExtractTo != null => Directory.Exists(pr.Resolve(item.Install.ExtractTo)),
            "git" when item.Install.Dest != null => Directory.Exists(pr.Resolve(item.Install.Dest)),
            _ => false,
        };
    }

    private static async Task<bool> WingetHasAsync(string id)
    {
        try
        {
            var r = await Proc.RunAsync("winget", new[]
            {
                "list", "--id", id, "-e", "--disable-interactivity", "--accept-source-agreements",
            });
            return r.Ok && r.StdOut.Contains(id, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
