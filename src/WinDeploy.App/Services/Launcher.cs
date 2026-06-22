using System.Diagnostics;
using System.IO;
using System.Text;
using WinDeploy.Core;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services;

/// <summary>Best-effort "launch this installed app" resolver. Tries, in order: ARP DisplayIcon →
/// ARP InstallLocation exe → detect.path → detect.cmd (on PATH) → Start-menu shortcut.
/// Returns false (with a reason) if nothing launchable was found.</summary>
public static class Launcher
{
    private static readonly string[] Runnable = { ".exe", ".bat", ".cmd", ".lnk", ".com" };
    private static readonly string[] SkipExe = { "unins", "setup", "update", "crash", "report", "helper", "service" };

    public static bool TryLaunch(CatalogItem item, PathResolver pr, out string detail)
    {
        var target = Resolve(item, pr);
        if (target == null) { detail = "未能定位可执行文件，请从开始菜单启动"; return false; }
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
            Process.Start(psi);
            detail = target;
            return true;
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }

    /// <summary>A path/command ShellExecute can launch, or null. Includes the (recursive) Start-menu
    /// shortcut fallback — fine for a single launch, too slow to call for every catalog item.</summary>
    public static string? Resolve(CatalogItem item, PathResolver pr)
        => ResolveExe(item, pr) ?? FindStartMenuShortcut(item.Name);

    /// <summary>Resolve the item's executable WITHOUT the Start-menu scan — cheap enough to call per
    /// item (used by process matching). ARP DisplayIcon → InstallLocation exe → detect.path → detect.cmd.</summary>
    public static string? ResolveExe(CatalogItem item, PathResolver pr)
    {
        var arp = Arp.Find(item.Detect?.Arp, item.Name, IdToName(item.Install.Id));

        var icon = ExeFromDisplayIcon(arp?.DisplayIcon);
        if (icon != null && File.Exists(icon)) return icon;

        if (!string.IsNullOrWhiteSpace(arp?.InstallLocation) && Directory.Exists(arp!.InstallLocation))
        {
            var exe = PickExe(arp.InstallLocation!, item.Name, item.Id);
            if (exe != null) return exe;
        }

        if (item.Detect?.Path is { } p)
        {
            var rp = pr.Resolve(p);
            if (File.Exists(rp) && IsRunnable(rp)) return rp;
            if (Directory.Exists(rp)) { var exe = PickExe(rp, item.Name, item.Id); if (exe != null) return exe; }
        }

        if (!string.IsNullOrWhiteSpace(item.Detect?.Cmd))
        {
            var onPath = CommandFinder.Find(item.Detect!.Cmd!);
            if (onPath != null) return onPath;
        }

        return null;
    }

    /// <summary>"C:\App\app.exe,0" → "C:\App\app.exe" (only if it points at a runnable file).</summary>
    private static string? ExeFromDisplayIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var p = icon.Trim().Trim('"');
        var comma = p.LastIndexOf(',');
        if (comma > 1 && p.Length - comma <= 4) p = p[..comma];
        p = p.Trim().Trim('"');
        return IsRunnable(p) ? p : null;
    }

    private static bool IsRunnable(string path)
        => Runnable.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Pick the most app-like exe in a folder: a name-matching one (top dir + immediate
    /// subdirs), skipping uninstallers/updaters; else the largest top-level exe.</summary>
    private static string? PickExe(string dir, string name, string id)
    {
        try
        {
            var tokens = Tokens(name).Concat(Tokens(id)).Where(t => t.Length >= 2).ToHashSet();

            var candidates = new List<string>();
            candidates.AddRange(SafeExes(dir));
            foreach (var sub in SafeDirs(dir)) candidates.AddRange(SafeExes(sub));

            var named = candidates.FirstOrDefault(f =>
            {
                var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (SkipExe.Any(s => n.Contains(s))) return false;
                return tokens.Any(t => n.Contains(t) || t.Contains(n));
            });
            if (named != null) return named;

            return SafeExes(dir)
                .Where(f => !SkipExe.Any(s => Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Contains(s)))
                .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0; } })
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static IEnumerable<string> SafeExes(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.exe"); } catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    private static string? FindStartMenuShortcut(string name)
    {
        var tokens = Tokens(name).Where(t => t.Length >= 2).ToHashSet();
        if (tokens.Count == 0) return null;
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                 })
        {
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    var n = Path.GetFileNameWithoutExtension(lnk).ToLowerInvariant();
                    if (tokens.Any(t => n.Contains(t))) return lnk;
                }
            }
            catch { /* skip */ }
        }
        return null;
    }

    /// <summary>Lowercase alphanumeric word tokens of a display name ("VS Code" → vs, code).</summary>
    private static IEnumerable<string> Tokens(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else sb.Append(' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? IdToName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var last = id.Split('.').Last();
        var sb = new StringBuilder();
        for (var i = 0; i < last.Length; i++)
        {
            if (i > 0 && char.IsUpper(last[i]) && !char.IsUpper(last[i - 1])) sb.Append(' ');
            sb.Append(last[i]);
        }
        return sb.ToString();
    }
}
