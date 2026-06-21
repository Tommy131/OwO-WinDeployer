namespace WinDeploy.Core.Util;

/// <summary>
/// User-scope PATH / environment variable management. Setting a User-target variable on Windows
/// persists to the registry and broadcasts WM_SETTINGCHANGE automatically.
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

        // Reflect into the current process so subsequent detection in this run sees it.
        var proc = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable("PATH", proc.TrimEnd(';') + ";" + dir);
        return true;
    }

    public static void SetUserVar(string name, string value)
        => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
}
