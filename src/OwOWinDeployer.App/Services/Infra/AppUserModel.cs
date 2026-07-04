using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OwOWinDeployer.App.Services.Infra;

/// <summary>Gives the process a stable AppUserModelID and registers a friendly DisplayName (+ icon) for it.
/// Tray balloon tips are rendered as toasts on Windows 10/11, and the toast's attribution line comes from the
/// process AUMID — without an explicit one, Windows invents an ugly "NotifyIconGeneratedAumid_…" string. With
/// this registered, the toast (and taskbar grouping) is attributed to "OwO! Win Deployer".</summary>
public static class AppUserModel
{
    /// <summary>Stable, reverse-DNS-style id — kept constant across versions so taskbar pinning / toast
    /// identity persist.</summary>
    public const string Aumid = "Tommy131.OwODeployer";

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, int[] piconid, int nIcons, int flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Register the friendly name/icon and bind the AUMID to this process. Call once at startup,
    /// before any tray balloon / toast is shown. Best-effort — never throws.</summary>
    public static void Configure()
    {
        try { RegisterDisplayName(); } catch { /* registry may be locked down */ }
        try { SetCurrentProcessExplicitAppUserModelID(Aumid); } catch { /* old OS / policy */ }
    }

    private static void RegisterDisplayName()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}");
        if (key == null) return;
        key.SetValue("DisplayName", AppInfo.Name, RegistryValueKind.String);
        if (IconPath is { } icon) key.SetValue("IconUri", icon, RegistryValueKind.String);
    }

    /// <summary>Path to a cached PNG of the app icon (extracted from the exe — single-file builds embed it,
    /// so it isn't on disk otherwise), used as the toast attribution icon and body logo. Null on failure.</summary>
    public static string? IconPath { get; } = EnsureIcon();

    private static string? EnsureIcon()
    {
        try
        {
            var dir = AppPaths.DataRoot;
            Directory.CreateDirectory(dir);
            // PNG (not .ico) — recommended for toast images; the toast renderer handles it reliably.
            // Stamp the filename with the app version: when a release ships a new icon, a fixed "app.png" would
            // keep serving the previous icon forever (it's only written when missing), and Windows would also hold
            // its own toast-image cache keyed by path. A version-stamped name misses on upgrade → regenerates from
            // the new exe icon, and the fresh path busts Windows' cache. Stale stamps are swept first.
            var pngPath = Path.Combine(dir, $"app-{AppInfo.Version}.png");
            if (!File.Exists(pngPath))
            {
                foreach (var stale in Directory.EnumerateFiles(dir, "app*.png"))
                    try { File.Delete(stale); } catch { /* held open by another instance / already gone */ }

                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return null;
                using var bmp = ExtractIcon(exe, 256);
                if (bmp == null) return null;
                bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            return pngPath;
        }
        catch { return null; }
    }

    /// <summary>Extract the app icon from the exe as a bitmap, preferring a large <paramref name="size"/>px frame
    /// (the toast logo renders far bigger than the 32px default) via PrivateExtractIcons; falls back to the default
    /// associated icon if the OS won't return the requested size.</summary>
    private static System.Drawing.Bitmap? ExtractIcon(string exe, int size)
    {
        try
        {
            var handles = new IntPtr[1];
            var ids = new int[1];
            if (PrivateExtractIcons(exe, 0, size, size, handles, ids, 1, 0) > 0 && handles[0] != IntPtr.Zero)
            {
                var hicon = handles[0];
                using var large = System.Drawing.Icon.FromHandle(hicon);
                try { return large.ToBitmap(); }   // copies pixels → safe to destroy the source handle next
                finally { DestroyIcon(hicon); }
            }
        }
        catch { /* fall through to the default extractor */ }

        try { using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe); return ico?.ToBitmap(); }
        catch { return null; }
    }
}
