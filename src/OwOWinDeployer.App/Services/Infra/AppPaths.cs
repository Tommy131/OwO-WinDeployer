using System.IO;

namespace OwOWinDeployer.App.Services.Infra;

/// <summary>Single source of truth for the app's local runtime-data root: logs, settings, icon cache,
/// FTP / Cloudflare / clipboard configs, reports, crash logs. Portable model — everything lives in a
/// <c>data</c> folder next to the executable, so the whole app is one self-contained folder you can move or
/// delete wholesale (the <c>data</c> folder is git-ignored). Falls back to <c>%LOCALAPPDATA%\OwOWinDeployer</c>
/// only when the exe directory isn't writable (e.g. installed under Program Files).
///
/// Migration from the legacy %LOCALAPPDATA% location is NOT automatic/silent — <c>App.OnStartup</c> detects it
/// via <see cref="HasLegacyToMigrate"/>, prompts the user, runs <see cref="MigrateFromLegacy"/> while nothing
/// has the old files open, then closes so the user reopens against the new location.</summary>
public static class AppPaths
{
    /// <summary>Root for all local runtime data: <c>&lt;exe dir&gt;\data</c>, or <c>%LOCALAPPDATA%\OwOWinDeployer</c> as fallback.</summary>
    public static string DataRoot { get; } = Resolve();

    /// <summary>The current-brand per-user fallback location (also used to tell fallback from portable mode).</summary>
    public static string LegacyDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwOWinDeployer");

    /// <summary>The PRE-REBRAND per-user location (<c>%LOCALAPPDATA%\WinDeploy</c>). Kept as an extra migration
    /// source so data written by older "WinDeploy"-branded builds still moves forward into the new location.</summary>
    private static readonly string OldBrandDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy");

    /// <summary>True in portable mode (data next to the exe), false on the read-only fallback (== LegacyDir).</summary>
    public static bool IsPortable => !PathEquals(DataRoot, LegacyDir);

    public static string Logs => Path.Combine(DataRoot, "logs");
    public static string IconCache => Path.Combine(DataRoot, "iconcache");
    public static string Reports => Path.Combine(DataRoot, "reports");

    /// <summary>The legacy folder to migrate FROM: the current-brand fallback if it holds data, else the
    /// pre-rebrand "WinDeploy" folder. Null when neither has migratable content.</summary>
    private static string? MigrationSource()
    {
        if (Directory.Exists(LegacyDir) && !IsEmpty(LegacyDir)) return LegacyDir;
        if (Directory.Exists(OldBrandDir) && !IsEmpty(OldBrandDir)) return OldBrandDir;
        return null;
    }

    /// <summary>There's legacy data worth migrating: portable mode, a legacy folder exists with content, and
    /// the portable folder is still empty (so a migration won't clobber anything). Check BEFORE anything writes
    /// into DataRoot, or the empty-check will already be false.</summary>
    public static bool HasLegacyToMigrate()
        => IsPortable && MigrationSource() is not null && IsEmpty(DataRoot);

    /// <summary>Copy the legacy folder's contents into <see cref="DataRoot"/>, then delete the legacy folder.
    /// Call only when nothing has the old files open (i.e. very early in startup, before logs/settings load).
    /// Returns: ok=false only when the copy itself failed; ok=true with a non-null <c>warning</c> (= the legacy
    /// path) when data moved but the old folder couldn't be removed (harmless leftover).</summary>
    public static (bool ok, string? warning) MigrateFromLegacy()
    {
        var legacy = MigrationSource();
        if (legacy is null) return (true, null);
        try
        {
            foreach (var dir in Directory.GetDirectories(legacy, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(legacy, DataRoot));
            foreach (var file in Directory.GetFiles(legacy, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(legacy, DataRoot), overwrite: true);
        }
        catch (Exception ex) { return (false, ex.Message); }

        // Remove the old location (best-effort). Retry briefly — antivirus (e.g. 火绒) or Search indexing can
        // transiently hold a just-copied file. A leftover legacy dir is harmless: the data is already moved.
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(legacy, recursive: true); return (true, null); }
                catch when (attempt < 4) { System.Threading.Thread.Sleep(150); }
            }
        }
        catch { return (true, legacy); }
    }

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
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwOWinDeployer");
            try { Directory.CreateDirectory(fallback); } catch { /* best effort */ }
            return fallback;
        }
    }

    private static bool IsEmpty(string dir)
    {
        try { return !Directory.EnumerateFileSystemEntries(dir).Any(); } catch { return false; }
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                         StringComparison.OrdinalIgnoreCase);
}
