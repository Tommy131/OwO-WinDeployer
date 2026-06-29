using System.IO;

namespace WinDeploy.App.Services.Infra;

/// <summary>Single source of truth for the app's local runtime-data root: logs, settings, icon cache,
/// FTP / Cloudflare / clipboard configs, reports, crash logs. Portable model — everything lives in a
/// <c>data</c> folder next to the executable, so the whole app is one self-contained folder you can move or
/// delete wholesale (the <c>data</c> folder is git-ignored). Falls back to <c>%LOCALAPPDATA%\WinDeploy</c>
/// only when the exe directory isn't writable (e.g. installed under Program Files).</summary>
public static class AppPaths
{
    /// <summary>Root for all local runtime data: <c>&lt;exe dir&gt;\data</c>, or <c>%LOCALAPPDATA%\WinDeploy</c> as fallback.</summary>
    public static string DataRoot { get; } = Resolve();

    public static string Logs => Path.Combine(DataRoot, "logs");
    public static string IconCache => Path.Combine(DataRoot, "iconcache");
    public static string Reports => Path.Combine(DataRoot, "reports");

    private static string Resolve()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            var fresh = !Directory.Exists(portable);
            Directory.CreateDirectory(portable);
            // Probe writability — the exe may sit under Program Files / a read-only mount.
            var probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            // One-time migration from the legacy %LOCALAPPDATA%\WinDeploy location — only into a brand-new /
            // empty portable folder, so live data is never clobbered.
            if (fresh || IsEmpty(portable)) MigrateLegacy(portable);
            return portable;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy");
            try { Directory.CreateDirectory(fallback); } catch { /* best effort */ }
            return fallback;
        }
    }

    private static bool IsEmpty(string dir)
    {
        try { return !Directory.EnumerateFileSystemEntries(dir).Any(); } catch { return false; }
    }

    /// <summary>Move the legacy <c>%LOCALAPPDATA%\WinDeploy</c> data into the portable folder once, then delete
    /// the legacy folder. Best-effort: a partial copy still leaves a working app, and the legacy dir is left
    /// alone if it IS the destination (the read-only-exe fallback path).</summary>
    private static void MigrateLegacy(string dest)
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy");
            if (!Directory.Exists(legacy)) return;
            if (string.Equals(Path.GetFullPath(legacy).TrimEnd('\\'),
                              Path.GetFullPath(dest).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

            foreach (var dir in Directory.GetDirectories(legacy, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(legacy, dest));
            foreach (var file in Directory.GetFiles(legacy, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(legacy, dest), overwrite: true);

            // Drop the old location after a successful copy. Retry briefly — antivirus (e.g. Huorong) or
            // Search indexing can transiently hold a freshly-copied file. Best-effort: a leftover legacy dir
            // is harmless since the data is already moved.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(legacy, recursive: true); break; }
                catch when (attempt < 4) { System.Threading.Thread.Sleep(150); }
            }
        }
        catch { /* best effort — leave the legacy dir intact if anything failed */ }
    }
}
