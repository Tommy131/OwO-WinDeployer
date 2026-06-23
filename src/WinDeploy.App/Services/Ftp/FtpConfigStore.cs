using System.IO;
using System.Text.Json;

namespace WinDeploy.App.Services.Ftp;

/// <summary>Persists the FTP server configuration to %LOCALAPPDATA%/WinDeploy/ftp.json.</summary>
public static class FtpConfigStore
{
    private static readonly string DirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy");
    private static readonly string FilePathValue = Path.Combine(DirPath, "ftp.json");
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FilePath => FilePathValue;

    public static FtpServerConfig Load()
    {
        try
        {
            if (File.Exists(FilePathValue))
                return JsonSerializer.Deserialize<FtpServerConfig>(File.ReadAllText(FilePathValue), Opt) ?? new();
        }
        catch { /* fall through to defaults */ }
        return new();
    }

    public static void Save(FtpServerConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            File.WriteAllText(FilePathValue, JsonSerializer.Serialize(cfg, Opt));
        }
        catch { /* best effort */ }
    }
}
