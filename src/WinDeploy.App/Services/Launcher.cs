using System.Diagnostics;
using System.IO;
using System.Text;
using WinDeploy.Core;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services;

/// <summary>Resolves and launches an installed app's main executable. ARP DisplayIcon is preferred but
/// validated (Steam's points at uninstall.exe, Apifox's at a .ico) — invalid ones fall through to the
/// Start-menu shortcut (most reliable for GUI apps), then the install dir, then detect hints.
/// Pass a <c>log</c> to record every resolution step for the run-progress detail view.</summary>
public static class Launcher
{
    private static readonly string[] Runnable = { ".exe", ".bat", ".cmd", ".com", ".lnk" };
    private static readonly string[] SkipExe = { "unins", "setup", "update", "crash", "report", "helper", "service" };

    public static bool TryLaunch(CatalogItem item, PathResolver pr, out string detail, Action<string>? log = null)
    {
        var target = Resolve(item, pr, log);
        if (target == null) { detail = "未能定位可执行文件，请从开始菜单启动"; return false; }
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
            Process.Start(psi);
            log?.Invoke("已启动: " + target);
            detail = target;
            return true;
        }
        catch (Exception ex) { log?.Invoke("启动失败: " + ex.Message); detail = ex.Message; return false; }
    }

    /// <summary>A path/command ShellExecute can launch, or null. Order: valid DisplayIcon exe →
    /// Start-menu shortcut → install-location exe → detect hints.</summary>
    public static string? Resolve(CatalogItem item, PathResolver pr, Action<string>? log = null)
    {
        var arp = Arp.Find(item.Detect?.Arp, item.Name, IdToName(item.Install.Id));
        log?.Invoke("注册表项: " + (arp?.DisplayName ?? "未匹配"));

        var icon = ValidExeFromDisplayIcon(arp?.DisplayIcon);
        if (icon != null) { log?.Invoke("DisplayIcon → " + icon); return icon; }
        if (!string.IsNullOrWhiteSpace(arp?.DisplayIcon)) log?.Invoke("DisplayIcon 非可启动(卸载器/图标)，从其所在目录挑选: " + arp!.DisplayIcon);

        // DisplayIcon's folder usually holds the real exe even when the icon points at uninstall.exe/.ico.
        var dirExe = ExeFromDisplayIconDir(arp, item);
        if (dirExe != null) { log?.Invoke("安装目录挑选 → " + dirExe); return dirExe; }

        var loc = ExeFromInstallLocation(arp, item);
        if (loc != null) { log?.Invoke("InstallLocation 挑选 → " + loc); return loc; }

        var lnk = FindStartMenuShortcut(item.Name);
        if (lnk != null) { log?.Invoke("开始菜单快捷方式 → " + lnk); return lnk; }

        var det = ExeFromDetect(item, pr);
        if (det != null) { log?.Invoke("detect → " + det); return det; }

        log?.Invoke("未能定位可执行文件");
        return null;
    }

    /// <summary>The best .exe path for icon extraction (no Start-menu .lnk), or null.</summary>
    public static string? ResolveExePath(CatalogItem item, PathResolver pr)
    {
        var arp = Arp.Find(item.Detect?.Arp, item.Name, IdToName(item.Install.Id));
        return ValidExeFromDisplayIcon(arp?.DisplayIcon)
               ?? ExeFromDisplayIconDir(arp, item)
               ?? ExeFromInstallLocation(arp, item)
               ?? ExeFromDetect(item, pr);
    }

    /// <summary>Process-matching target (name + install dir) — no Start-menu. Falls back to an install
    /// dir derived from ARP (catches Steam / Squirrel / Electron multi-process apps).</summary>
    public static (string Proc, string? Dir)? ProcessTarget(CatalogItem item, PathResolver pr)
    {
        var arp = Arp.Find(item.Detect?.Arp, item.Name, IdToName(item.Install.Id));
        var exe = ValidExeFromDisplayIcon(arp?.DisplayIcon)
                  ?? ExeFromDisplayIconDir(arp, item)
                  ?? ExeFromInstallLocation(arp, item)
                  ?? ExeFromDetect(item, pr);
        if (!string.IsNullOrWhiteSpace(exe))
        {
            var name = Path.GetFileNameWithoutExtension(exe);
            if (!string.IsNullOrWhiteSpace(name)) return (name!, Path.GetDirectoryName(exe));
        }
        var dir = InstallDirFromArp(arp);
        if (dir != null) return ("", dir);

        // No ARP (MSIX / Store apps like Claude run from WindowsApps): match by process base-name.
        var proc = item.Detect?.Proc;
        if (string.IsNullOrWhiteSpace(proc)) proc = SingleAsciiToken(item.Name) ?? IdLastSegment(item.Install.Id);
        if (!string.IsNullOrWhiteSpace(proc)) return (proc!, null);

        return null;
    }

    /// <summary>The name if it's a single ASCII alphanumeric token (e.g. "Claude", "Discord"); else null.</summary>
    private static string? SingleAsciiToken(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.All(c => c < 128 && char.IsLetterOrDigit(c)) ? s : null;
    }

    private static string? IdLastSegment(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var last = id.Split('.').Last();
        return SingleAsciiToken(last);
    }

    // ── resolution helpers ─────────────────────────────────────────────────

    private static string? ValidExeFromDisplayIcon(string? icon)
    {
        var p = PathOfDisplayIcon(icon);
        if (p == null || !IsRunnable(p)) return null;          // .ico / .dll etc. rejected
        if (IsUninstaller(Path.GetFileNameWithoutExtension(p))) return null;
        return File.Exists(p) ? p : null;
    }

    private static string? ExeFromInstallLocation(ArpEntry? arp, CatalogItem item)
        => arp?.InstallLocation is { Length: > 0 } loc && Directory.Exists(loc)
            ? PickExe(loc, item.Name, item.Id)
            : null;

    /// <summary>Pick the real exe from the DisplayIcon's folder — catches Steam (icon=uninstall.exe)
    /// and Apifox (icon=.ico) whose actual exe sits beside the uninstaller.</summary>
    private static string? ExeFromDisplayIconDir(ArpEntry? arp, CatalogItem item)
    {
        var p = PathOfDisplayIcon(arp?.DisplayIcon);
        var dir = p != null ? Path.GetDirectoryName(p) : null;
        return dir != null && Directory.Exists(dir) ? PickExe(dir, item.Name, item.Id) : null;
    }

    private static string? ExeFromDetect(CatalogItem item, PathResolver pr)
    {
        if (item.Detect?.Path is { } p)
        {
            var rp = pr.Resolve(p);
            if (File.Exists(rp) && IsRunnable(rp)) return rp;
            if (Directory.Exists(rp)) { var e = PickExe(rp, item.Name, item.Id); if (e != null) return e; }
        }
        if (!string.IsNullOrWhiteSpace(item.Detect?.Cmd))
        {
            var onPath = CommandFinder.Find(item.Detect!.Cmd!);
            if (onPath != null) return onPath;
        }
        return null;
    }

    /// <summary>An install dir for process matching: ARP InstallLocation, else the DisplayIcon's folder
    /// (for Squirrel app-x.x.x folders, use the parent so a version bump still matches).</summary>
    private static string? InstallDirFromArp(ArpEntry? arp)
    {
        if (arp == null) return null;
        if (!string.IsNullOrWhiteSpace(arp.InstallLocation) && Directory.Exists(arp.InstallLocation))
            return arp.InstallLocation.TrimEnd('\\', '/');

        var dir = PathOfDisplayIcon(arp.DisplayIcon) is { } p ? Path.GetDirectoryName(p) : null;
        if (dir == null || !Directory.Exists(dir)) return null;
        var leaf = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (leaf.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            if (parent != null && Directory.Exists(parent)) return parent;
        }
        return dir.TrimEnd('\\', '/');
    }

    /// <summary>"C:\App\app.exe,0" → "C:\App\app.exe" (no existence/extension check).</summary>
    private static string? PathOfDisplayIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var p = icon.Trim().Trim('"');
        var comma = p.LastIndexOf(',');
        if (comma > 1 && p.Length - comma <= 4) p = p[..comma];
        return p.Trim().Trim('"');
    }

    private static bool IsRunnable(string path)
        => Runnable.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static bool IsUninstaller(string name)
    {
        name = name.ToLowerInvariant();
        return name.Contains("unins") || name.Contains("uninstall");
    }

    private static string? PickExe(string dir, string name, string id)
    {
        try
        {
            var tokens = Tokens(name).Concat(Tokens(id)).Where(t => t.Length >= 2).ToHashSet();
            var candidates = new List<string>();
            candidates.AddRange(SafeExes(dir));
            foreach (var sub in SafeDirs(dir)) candidates.AddRange(SafeExes(sub));
            candidates = candidates
                .Where(f => !SkipExe.Any(s => Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Contains(s)))
                .ToList();
            if (candidates.Count == 0) return null;

            string N(string f) => Path.GetFileNameWithoutExtension(f).ToLowerInvariant();

            // 1) exact token match (steam.exe, Apifox.exe)
            var exact = candidates.FirstOrDefault(f => tokens.Contains(N(f)));
            if (exact != null) return exact;
            // 2) name starts with a token or vice-versa
            var pref = candidates.FirstOrDefault(f => tokens.Any(t => N(f).StartsWith(t) || t.StartsWith(N(f))));
            if (pref != null) return pref;
            // 3) substring
            var named = candidates.FirstOrDefault(f => tokens.Any(t => N(f).Contains(t) || t.Contains(N(f))));
            if (named != null) return named;
            // 4) largest exe in the top dir
            return SafeExes(dir)
                .Where(f => !SkipExe.Any(s => N(f).Contains(s)))
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
                    if (IsUninstaller(n)) continue;
                    if (tokens.All(t => n.Contains(t)) || tokens.Any(t => t.Length >= 4 && n.Contains(t)))
                        return lnk;
                }
            }
            catch { /* skip */ }
        }
        return null;
    }

    private static IEnumerable<string> Tokens(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
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
