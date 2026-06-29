using System.IO;
using System.Text.Json;

namespace WinDeploy.App.Services.Clip;

/// <summary>Persists clipboard-sync settings to %LOCALAPPDATA%/WinDeploy/clipsync.json, and (only when the
/// user opts in via <see cref="ClipSyncConfig.PersistHistory"/>) the shared board to clipsync-history.json.
/// Settings never contain clipboard content; history does, so it is opt-in and easily cleared.</summary>
public static class ClipConfigStore
{
    private static readonly string DirPath = AppPaths.DataRoot;
    private static readonly string ConfigPath = Path.Combine(DirPath, "clipsync.json");
    private static readonly string HistoryPath = Path.Combine(DirPath, "clipsync-history.json");

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static ClipSyncConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<ClipSyncConfig>(File.ReadAllText(ConfigPath), Opt) ?? new();
        }
        catch { /* fall through to defaults */ }
        return new();
    }

    public static void Save(ClipSyncConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, Opt));
        }
        catch { /* best effort */ }
    }

    public static List<ClipEntry> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<List<ClipEntry>>(File.ReadAllText(HistoryPath), Opt) ?? new();
        }
        catch { /* fall through */ }
        return new();
    }

    public static void SaveHistory(IEnumerable<ClipEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(entries, Opt));
        }
        catch { /* best effort */ }
    }

    /// <summary>Remove any persisted history (called when the user turns persistence off or clears the board).</summary>
    public static void ClearHistory()
    {
        try { if (File.Exists(HistoryPath)) File.Delete(HistoryPath); }
        catch { /* best effort */ }
    }
}
