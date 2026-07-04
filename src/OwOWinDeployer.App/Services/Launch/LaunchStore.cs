using System.IO;
using System.Text.Json;
using OwOWinDeployer.App.Services.Infra;

namespace OwOWinDeployer.App.Services.Launch;

/// <summary>Persists the 快速启动 list to &lt;data&gt;/launcher.json. Best-effort: a read error yields an empty
/// list, a write error is swallowed (the in-memory list stays authoritative for the session).</summary>
public static class LaunchStore
{
    private static readonly string DirPath = AppPaths.DataRoot;
    private static readonly string ConfigPath = Path.Combine(DirPath, "launcher.json");

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static LaunchConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<LaunchConfig>(File.ReadAllText(ConfigPath), Opt) ?? new();
        }
        catch { /* fall through to an empty list */ }
        return new();
    }

    public static void Save(LaunchConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, Opt));
        }
        catch { /* best effort */ }
    }
}
