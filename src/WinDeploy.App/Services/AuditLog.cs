using System.IO;
using System.Text;

namespace WinDeploy.App.Services;

/// <summary>Global append-only audit log: app lifecycle + user operations (install / update /
/// uninstall / launch / settings / env vars / paths). Writes to %LOCALAPPDATA%\WinDeploy\logs\app.log.
/// Never throws from a logging call; raises <see cref="Logged"/> for the live in-app viewer.</summary>
public static class AuditLog
{
    private static readonly object Gate = new();
    private static string? _file;

    /// <summary>Fired (on a background thread) with each new formatted line.</summary>
    public static event Action<string>? Logged;

    public static string FilePath => _file ??= Build();
    public static string Folder => Path.GetDirectoryName(FilePath)!;

    private static string Build()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy", "logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "app.log");
    }

    /// <summary>App lifecycle (start / stop).</summary>
    public static void App(string msg) => Write("应用", msg);
    /// <summary>A user-initiated operation worth auditing.</summary>
    public static void Action(string msg) => Write("操作", msg);
    public static void Warn(string msg) => Write("警告", msg);
    public static void Error(string msg) => Write("错误", msg);

    private static void Write(string category, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{category}] {msg}";
        try { lock (Gate) File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false)); }
        catch { /* logging must never break the app */ }
        try { Logged?.Invoke(line); } catch { /* ignore subscriber faults */ }
    }

    public static string ReadTail(int maxLines = 2000)
    {
        try
        {
            if (!File.Exists(FilePath)) return "";
            var lines = File.ReadAllLines(FilePath);
            return string.Join(Environment.NewLine, lines.Length > maxLines ? lines[^maxLines..] : lines);
        }
        catch (Exception ex) { return "读取日志失败：" + ex.Message; }
    }

    public static void Clear()
    {
        try { lock (Gate) File.WriteAllText(FilePath, ""); } catch { /* ignore */ }
    }
}
