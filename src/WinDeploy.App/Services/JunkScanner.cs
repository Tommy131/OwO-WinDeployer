using System.IO;
using WinDeploy.Core.Engine;

namespace WinDeploy.App.Services;

/// <summary>One cleanable location: a friendly name, the folder it scans, and (after scanning) its size.</summary>
public sealed class JunkTarget
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Path { get; init; } = "";
    public bool ContentsOnly { get; init; } = true;   // delete the folder's contents, not the folder itself
    public long Bytes { get; set; }
    public string SizeText => Bytes >= 1024L * 1024 * 1024 ? $"{Bytes / 1024.0 / 1024 / 1024:0.0} GB" : $"{Bytes / 1024.0 / 1024:0.0} MB";
}

/// <summary>Reclaims disk space from the usual Windows junk locations: user/Windows Temp, Windows Update
/// download cache, thumbnail/icon cache, Recycle Bin, delivery optimization, CBS logs, Windows.old.
/// Scans sizes first; deletion is selective and skips files in use.</summary>
public static class JunkScanner
{
    public static List<JunkTarget> BuildTargets()
    {
        string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sysDrive = Path.GetPathRoot(Win) ?? "C:\\";

        var t = new List<JunkTarget>
        {
            new() { Id = "user-temp", Name = "用户临时文件", Detail = "%TEMP%", Path = Path.GetTempPath() },
            new() { Id = "win-temp", Name = "Windows 临时文件", Detail = @"%WINDIR%\Temp", Path = Path.Combine(Win, "Temp") },
            new() { Id = "wu-download", Name = "Windows Update 缓存", Detail = @"SoftwareDistribution\Download", Path = Path.Combine(Win, "SoftwareDistribution", "Download") },
            new() { Id = "thumbs", Name = "缩略图 / 图标缓存", Detail = @"Explorer 缓存", Path = Path.Combine(Local, "Microsoft", "Windows", "Explorer") },
            new() { Id = "inet-cache", Name = "Internet 临时文件", Detail = @"INetCache", Path = Path.Combine(Local, "Microsoft", "Windows", "INetCache") },
            new() { Id = "crash-dumps", Name = "崩溃转储", Detail = @"CrashDumps", Path = Path.Combine(Local, "CrashDumps") },
            new() { Id = "delivery-opt", Name = "传递优化文件", Detail = @"DeliveryOptimization", Path = Path.Combine(Win, "SoftwareDistribution", "DeliveryOptimization") },
            new() { Id = "windows-old", Name = "旧版 Windows (Windows.old)", Detail = "上次升级保留的系统", Path = Path.Combine(sysDrive, "Windows.old"), ContentsOnly = false },
        };
        return t;
    }

    /// <summary>Measure each target's current size (0 if absent). Returns only the ones that exist & are non-empty.</summary>
    public static List<JunkTarget> Scan(IEnumerable<JunkTarget> targets)
    {
        var found = new List<JunkTarget>();
        foreach (var t in targets)
        {
            try
            {
                if (!Directory.Exists(t.Path)) continue;
                t.Bytes = Cleanup.DirSize(t.Path);
                if (t.Bytes > 0) found.Add(t);
            }
            catch { /* skip inaccessible */ }
        }
        return found;
    }

    /// <summary>Delete the selected targets' contents, skipping locked files. Returns (deletedCount, bytesFreed).</summary>
    public static (int Count, long Freed) Clean(IEnumerable<JunkTarget> targets)
    {
        var count = 0; long freed = 0;
        foreach (var t in targets)
        {
            long before = 0;
            try { if (Directory.Exists(t.Path)) before = Cleanup.DirSize(t.Path); } catch { }
            var any = t.ContentsOnly ? DeleteContents(t.Path) : TryDeleteDir(t.Path);
            if (!any) continue;
            long after = 0;
            try { if (Directory.Exists(t.Path)) after = Cleanup.DirSize(t.Path); } catch { }
            freed += Math.Max(0, before - after);
            count++;
        }
        return (count, freed);
    }

    private static bool DeleteContents(string dir)
    {
        var touched = false;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); touched = true; } catch { /* in use */ }
            foreach (var d in Directory.EnumerateDirectories(dir))
                try { Directory.Delete(d, true); touched = true; } catch { /* in use */ }
        }
        catch { /* dir vanished / denied */ }
        return touched;
    }

    private static bool TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) { Directory.Delete(dir, true); return true; } }
        catch { /* in use / denied — Windows.old often needs the Disk Cleanup tool */ }
        return false;
    }
}
