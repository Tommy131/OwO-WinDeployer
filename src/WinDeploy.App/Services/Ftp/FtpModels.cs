using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace WinDeploy.App.Services.Ftp;

/// <summary>Per-user / per-group capabilities, FileZilla-style. Stored as a readable flags string
/// ("List, Download, Upload") thanks to <see cref="JsonStringEnumConverter"/>.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FtpPerm
{
    None      = 0,
    List      = 1 << 0,   // 列目录（LIST/NLST/MLSD）
    Download  = 1 << 1,   // 下载（RETR）
    Upload    = 1 << 2,   // 上传（STOR/STOU）
    Append    = 1 << 3,   // 续传 / 追加（APPE、REST 续传上传）
    Delete    = 1 << 4,   // 删除文件（DELE）
    Rename    = 1 << 5,   // 重命名（RNFR/RNTO）
    CreateDir = 1 << 6,   // 新建目录（MKD）
    DeleteDir = 1 << 7,   // 删除目录（RMD）

    ReadOnly  = List | Download,
    Full      = List | Download | Upload | Append | Delete | Rename | CreateDir | DeleteDir,
}

/// <summary>A user group: members inherit its home + permissions when the user opts to (FileZilla 用户组语义)。</summary>
public sealed class FtpGroup
{
    public string Name { get; set; } = "";
    /// <summary>Default home directory for members who don't set their own.</summary>
    public string? Home { get; set; }
    public FtpPerm Permissions { get; set; } = FtpPerm.ReadOnly;
    public string? Description { get; set; }
}

/// <summary>A login account. The password is never stored in clear — only a PBKDF2 salt+hash pair.</summary>
public sealed class FtpUser
{
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";   // base64(PBKDF2-SHA256)
    public string PasswordSalt { get; set; } = "";   // base64(16 bytes)

    /// <summary>Group membership (name into <see cref="FtpServerConfig.Groups"/>), or null for none.</summary>
    public string? Group { get; set; }

    /// <summary>Root directory the user is confined to. Falls back to the group home when blank.</summary>
    public string Home { get; set; } = "";

    /// <summary>When true and a group is set, the group's permissions/home apply; otherwise the user's own.</summary>
    public bool UseGroupPermissions { get; set; } = true;

    public FtpPerm Permissions { get; set; } = FtpPerm.ReadOnly;
    public bool Enabled { get; set; } = true;

    /// <summary>Per-user concurrent-connection cap (0 = use the server's per-user default).</summary>
    public int MaxConnections { get; set; }
}

/// <summary>The whole FTP/FTPS server configuration, persisted as ftp.json.</summary>
public sealed class FtpServerConfig
{
    // ── listener ───────────────────────────────────────────────────────────
    public int Port { get; set; } = 21;
    public string ListenAddress { get; set; } = "0.0.0.0";

    // ── TLS（none | explicit | implicit）────────────────────────────────────
    public string TlsMode { get; set; } = "explicit";
    /// <summary>Explicit mode: refuse login until the client has issued AUTH TLS.</summary>
    public bool RequireTls { get; set; } = false;
    public int ImplicitPort { get; set; } = 990;
    /// <summary>PEM certificate (.crt/.pem/.cer) or a PKCS#12 bundle (.pfx/.p12). Blank → auto self-signed.</summary>
    public string? CertPath { get; set; }
    /// <summary>PEM private key (.key) when <see cref="CertPath"/> is a PEM certificate.</summary>
    public string? KeyPath { get; set; }
    /// <summary>Password for a .pfx bundle (ignored for PEM).</summary>
    public string? CertPassword { get; set; }

    // ── passive data ports ───────────────────────────────────────────────────
    public int PassiveMin { get; set; } = 50000;
    public int PassiveMax { get; set; } = 50100;
    /// <summary>Public IP to advertise in PASV replies behind NAT; blank → the control connection's local IP.</summary>
    public string? PassiveExternalIp { get; set; }

    // ── limits ─────────────────────────────────────────────────────────────
    public int MaxConnections { get; set; } = 20;
    public int MaxConnectionsPerUser { get; set; } = 0;   // 0 = 不限
    public int MaxConnectionsPerIp { get; set; } = 0;     // 0 = 不限

    // ── anonymous ────────────────────────────────────────────────────────────
    public bool AllowAnonymous { get; set; } = false;
    public string? AnonymousHome { get; set; }
    public FtpPerm AnonymousPermissions { get; set; } = FtpPerm.ReadOnly;

    // ── accounts ─────────────────────────────────────────────────────────────
    public List<FtpGroup> Groups { get; set; } = new();
    public List<FtpUser> Users { get; set; } = new();

    public bool TlsEnabled => !string.Equals(TlsMode, "none", StringComparison.OrdinalIgnoreCase);
    public bool ImplicitTls => string.Equals(TlsMode, "implicit", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolve a user's effective home / permissions / connection cap, applying group inheritance.</summary>
    public (string Home, FtpPerm Perms, int MaxConn) Resolve(FtpUser u)
    {
        var grp = u.Group != null ? Groups.FirstOrDefault(g => g.Name == u.Group) : null;
        var home = !string.IsNullOrWhiteSpace(u.Home) ? u.Home : (grp?.Home ?? "");
        var perms = (u.UseGroupPermissions && grp != null) ? grp.Permissions : u.Permissions;
        var max = u.MaxConnections > 0 ? u.MaxConnections : MaxConnectionsPerUser;
        return (home, perms, max);
    }
}

/// <summary>PBKDF2-SHA256 password hashing — secrets never touch disk in clear.</summary>
public static class FtpPassword
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static (string Hash, string Salt) Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? ""), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string hashB64, string saltB64)
    {
        try
        {
            if (string.IsNullOrEmpty(hashB64) || string.IsNullOrEmpty(saltB64)) return false;
            var salt = Convert.FromBase64String(saltB64);
            var expected = Convert.FromBase64String(hashB64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password ?? ""), salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }
}
