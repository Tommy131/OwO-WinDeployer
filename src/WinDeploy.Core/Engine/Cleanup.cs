using WinDeploy.Core.Models;

namespace WinDeploy.Core.Engine;

/// <summary>Removes install residue (partial extract / clone + temp files) — used when an install is
/// cancelled mid-way so it doesn't leave dead bytes on disk. Returns the freed byte count.</summary>
public static class Cleanup
{
    public static long RemoveInstallResidue(CatalogItem item, PathResolver pr)
    {
        long freed = 0;
        var ins = item.Install;
        var tmp = System.IO.Path.GetTempPath();

        freed += TryDeleteFile(System.IO.Path.Combine(tmp, $"windeploy_{item.Id}.zip"));
        freed += TryDeleteDir(System.IO.Path.Combine(tmp, $"windeploy_{item.Id}_x"));

        if (ins.Method == "portable" && ins.ExtractTo != null)
            freed += TryDeleteDir(pr.Resolve(item.InstallPathOverride ?? ins.ExtractTo));
        if (ins.Method == "git" && ins.Dest != null)
            freed += TryDeleteDir(pr.Resolve(item.InstallPathOverride ?? ins.Dest));

        return freed;
    }

    public static long DirSize(string path)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(f).Length; } catch { /* skip */ }
        }
        catch { /* skip */ }
        return total;
    }

    private static long TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            var size = new FileInfo(path).Length;
            File.Delete(path);
            return size;
        }
        catch { return 0; }
    }

    private static long TryDeleteDir(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            var size = DirSize(path);
            Directory.Delete(path, true);
            return size;
        }
        catch { return 0; }
    }
}
