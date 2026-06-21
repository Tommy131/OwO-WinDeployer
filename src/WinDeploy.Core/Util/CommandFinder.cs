namespace WinDeploy.Core.Util;

/// <summary>Locates a command on PATH (honouring PATHEXT) without spawning a process.</summary>
public static class CommandFinder
{
    public static string? Find(string cmd)
    {
        if (Path.IsPathRooted(cmd))
            return File.Exists(cmd) ? cmd : null;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathEnv.Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var hasExt = !string.IsNullOrEmpty(Path.GetExtension(cmd));
        foreach (var d in dirs)
        {
            if (hasExt)
            {
                var p = Path.Combine(d, cmd);
                if (File.Exists(p)) return p;
                continue;
            }
            // No extension given: only accept a PATHEXT match (an .exe/.cmd/.bat),
            // never a bare extensionless file (e.g. VS Code ships a 'code' shell script that
            // is not a valid Windows executable — we want 'code.cmd').
            foreach (var e in exts)
            {
                var f = Path.Combine(d, cmd + e);
                if (File.Exists(f)) return f;
            }
        }
        return null;
    }

    public static bool Exists(string cmd) => Find(cmd) != null;
}
