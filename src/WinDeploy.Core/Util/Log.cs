namespace WinDeploy.Core.Util;

/// <summary>Minimal coloured console logger.</summary>
public static class Log
{
    public static bool UseColor { get; set; } = true;

    public static void Info(string m) => Write(m, null, "  ");
    public static void Step(string m) => Write(m, ConsoleColor.Cyan, "→ ");
    public static void Ok(string m) => Write(m, ConsoleColor.Green, "✓ ");
    public static void Warn(string m) => Write(m, ConsoleColor.Yellow, "! ");
    public static void Err(string m) => Write(m, ConsoleColor.Red, "✗ ");

    private static void Write(string m, ConsoleColor? c, string prefix)
    {
        if (UseColor && c.HasValue) Console.ForegroundColor = c.Value;
        Console.WriteLine(prefix + m);
        if (UseColor && c.HasValue) Console.ResetColor();
    }
}
