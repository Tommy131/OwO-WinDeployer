using System.Diagnostics;
using System.IO;
using WinDeploy.Core;
using WinDeploy.Core.Models;

namespace WinDeploy.App.Services;

/// <summary>One running process belonging to a catalog item.</summary>
public sealed record ProcItem(int Pid, string Name, long MemBytes, string? Path);

/// <summary>Finds and controls the processes of a catalog item. Matching is by the resolved
/// executable's process-name, verified by module path under the install dir when readable.</summary>
public static class ProcessControl
{
    /// <summary>Running processes that belong to <paramref name="item"/> (empty if none / unresolved).</summary>
    public static List<ProcItem> Find(CatalogItem item, PathResolver pr)
    {
        var result = new List<ProcItem>();
        var exe = Launcher.ResolveExe(item, pr);
        if (string.IsNullOrWhiteSpace(exe)) return result;

        var procName = Path.GetFileNameWithoutExtension(exe);
        if (string.IsNullOrWhiteSpace(procName)) return result;
        var baseDir = Path.GetDirectoryName(exe);

        Process[] procs;
        try { procs = Process.GetProcessesByName(procName); } catch { return result; }
        foreach (var p in procs)
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* access denied / bitness mismatch */ }

                // When the path is readable and we know the install dir, require a match to avoid
                // killing an unrelated same-named process.
                if (path != null && !string.IsNullOrEmpty(baseDir)
                    && !path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new ProcItem(p.Id, p.ProcessName, SafeMem(p), path));
            }
            catch { /* skip */ }
            finally { try { p.Dispose(); } catch { /* ignore */ } }
        }
        return result;
    }

    public static bool Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Kill every process of the item. Returns how many were killed.</summary>
    public static int KillAll(CatalogItem item, PathResolver pr)
    {
        var n = 0;
        foreach (var p in Find(item, pr))
            if (Kill(p.Pid)) n++;
        return n;
    }

    private static long SafeMem(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }
}
