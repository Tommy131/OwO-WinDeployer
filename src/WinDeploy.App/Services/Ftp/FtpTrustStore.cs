using System.IO;
using System.Text.Json;

namespace WinDeploy.App.Services.Ftp;

/// <summary>Trust-on-first-use (TOFU) pin store for FTPS server certificates, keyed by "host:port". The first
/// time a non-CA-trusted (e.g. self-signed) cert is seen for a host it is pinned — accepted and recorded; on a
/// later connection a <em>different</em> cert for the same host is rejected (possible MITM). This keeps the
/// self-signed-server workflow usable while still detecting interception after the first connect. Persisted to
/// %LOCALAPPDATA%/WinDeploy/ftp_trust.json.</summary>
public static class FtpTrustStore
{
    private static readonly string DirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy");
    private static readonly string FilePathValue = Path.Combine(DirPath, "ftp_trust.json");
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };
    private static readonly object Gate = new();

    private static Dictionary<string, string> LoadMap()
    {
        try
        {
            if (File.Exists(FilePathValue))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePathValue)) ?? new();
        }
        catch { /* unreadable / corrupt → start empty */ }
        return new();
    }

    /// <summary>The pinned SHA-256 thumbprint for a host:port, or null if none is recorded yet.</summary>
    public static string? Get(string hostKey)
    {
        lock (Gate) return LoadMap().TryGetValue(hostKey, out var tp) ? tp : null;
    }

    /// <summary>Pin (or update) the thumbprint for a host:port. Load-modify-save so it never clobbers others.</summary>
    public static void Set(string hostKey, string thumbprint)
    {
        lock (Gate)
        {
            try
            {
                var map = LoadMap();
                map[hostKey] = thumbprint;
                Directory.CreateDirectory(DirPath);
                File.WriteAllText(FilePathValue, JsonSerializer.Serialize(map, Opt));
            }
            catch { /* best effort */ }
        }
    }
}
