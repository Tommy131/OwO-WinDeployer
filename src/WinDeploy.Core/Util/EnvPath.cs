using System.Runtime.InteropServices;

namespace WinDeploy.Core.Util;

/// <summary>
/// User-scope PATH / environment variable management. Setting a User-target variable persists to the registry;
/// we then broadcast <c>WM_SETTINGCHANGE("Environment")</c> so newly launched shells / Explorer pick the change
/// up without a sign-out (already-running processes still keep their cached environment).
/// </summary>
public static class EnvPath
{
    /// <summary>Appends <paramref name="dir"/> to the user PATH if absent. Returns true if changed.</summary>
    public static bool AddToUserPath(string dir)
    {
        dir = dir.TrimEnd('\\', '/');
        if (dir.Length == 0) return false;

        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(p => string.Equals(p.TrimEnd('\\', '/'), dir, StringComparison.OrdinalIgnoreCase)))
            return false;

        var updated = current.Length == 0 ? dir : current.TrimEnd(';') + ";" + dir;
        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);
        BroadcastEnvironmentChange();

        // Reflect into the current process so subsequent detection in this run sees it.
        var proc = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable("PATH", proc.TrimEnd(';') + ";" + dir);
        return true;
    }

    public static void SetUserVar(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        BroadcastEnvironmentChange();
    }

    // ── WM_SETTINGCHANGE broadcast ──────────────────────────────────────────────
    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    /// <summary>Tell the shell that the user environment changed, so new processes inherit it. Best-effort.</summary>
    private static void BroadcastEnvironmentChange()
    {
        try { SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 3000, out _); }
        catch { /* non-Windows / message pump unavailable — registry value is still persisted */ }
    }
}
