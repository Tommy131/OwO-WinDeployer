using System.IO;
using System.Text.Json;

namespace WinDeploy.App.Services;

public sealed class AppSettings
{
    public string? DevRoot { get; set; }
    public string? ToolsDir { get; set; }
    public string? DownloadDir { get; set; }
    public string? RepoUrl { get; set; }
    public string? Mirror { get; set; }
    public string? RedactKeywords { get; set; }
    public string? Theme { get; set; }   // system | light | dark
}

/// <summary>Persists GUI settings to %LOCALAPPDATA%/WinDeploy/settings.json.</summary>
public static class SettingsStore
{
    private static readonly string DirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy");
    private static readonly string FilePathValue = Path.Combine(DirPath, "settings.json");
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static string FilePath => FilePathValue;
    public static string Folder => DirPath;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePathValue))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePathValue)) ?? new();
        }
        catch { /* fall through to defaults */ }

        // Create the file on first run so the path shown in the UI always exists.
        var def = new AppSettings();
        Save(def);
        return def;
    }

    public static void Save(AppSettings s)
    {
        try { Directory.CreateDirectory(DirPath); File.WriteAllText(FilePathValue, JsonSerializer.Serialize(s, Opt)); }
        catch { /* best effort */ }
    }
}
