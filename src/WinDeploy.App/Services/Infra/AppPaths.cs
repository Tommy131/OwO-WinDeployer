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
            Directory.CreateDirectory(portable);
            // Probe writability — the exe may sit under Program Files / a read-only mount.
            var probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
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
}
