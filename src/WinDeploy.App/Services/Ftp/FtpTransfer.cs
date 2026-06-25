using System.IO;
using System.Text;

namespace WinDeploy.App.Services.Ftp;

/// <summary>One-shot upload of a local file or folder to an FTP / FTPS server, reusing <see cref="FtpClient"/>.
/// Used by the remote-deploy dialog as an alternative to SSH/SCP.</summary>
public static class FtpTransfer
{
    /// <summary><paramref name="tlsMode"/> is "none" (FTP) or "explicit" (FTPS). Navigates to (creating as
    /// needed) <paramref name="remoteDir"/>, then uploads <paramref name="sourcePath"/> (a file or directory).
    /// Returns success plus a transcript for display.</summary>
    public static async Task<(bool Ok, string Output)> UploadAsync(
        string host, int port, string tlsMode, string user, string pass,
        string sourcePath, string remoteDir, CancellationToken ct = default)
    {
        var log = new StringBuilder();
        using var c = new FtpClient();
        c.Log += m => log.AppendLine(m);
        try
        {
            await c.ConnectAsync(host, port, tlsMode, user, pass, ct);
            if (!string.IsNullOrWhiteSpace(remoteDir)) await EnsureDirAsync(c, remoteDir, ct);

            if (Directory.Exists(sourcePath))
            {
                var name = Path.GetFileName(sourcePath.TrimEnd('\\', '/'));
                await c.UploadDirectoryAsync(sourcePath, name,
                    new Progress<string>(f => log.AppendLine("↑ " + f)), null, ct);
            }
            else if (File.Exists(sourcePath))
            {
                var name = Path.GetFileName(sourcePath);
                log.AppendLine("↑ " + name);
                await c.UploadAsync(sourcePath, name, null, ct);
            }
            else { log.AppendLine("✗ " + sourcePath); return (false, log.ToString()); }

            return (true, log.ToString());
        }
        catch (Exception ex) { log.AppendLine("✗ " + ex.Message); return (false, log.ToString()); }
    }

    /// <summary>Connect (and authenticate) only, to verify the host / credentials / TLS settings.</summary>
    public static async Task<(bool Ok, string Output)> TestAsync(
        string host, int port, string tlsMode, string user, string pass, CancellationToken ct = default)
    {
        var log = new StringBuilder();
        using var c = new FtpClient();
        c.Log += m => log.AppendLine(m);
        try { await c.ConnectAsync(host, port, tlsMode, user, pass, ct); return (true, log.ToString()); }
        catch (Exception ex) { log.AppendLine("✗ " + ex.Message); return (false, log.ToString()); }
    }

    /// <summary>Change into <paramref name="remoteDir"/>, creating each segment that doesn't exist. Absolute
    /// paths (leading /) start from the root.</summary>
    private static async Task EnsureDirAsync(FtpClient c, string remoteDir, CancellationToken ct)
    {
        var path = remoteDir.Replace('\\', '/');
        if (path.StartsWith('/')) { await c.ChangeDirAsync("/", ct); path = path.TrimStart('/'); }
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            try { await c.ChangeDirAsync(seg, ct); }
            catch
            {
                await c.MakeDirAsync(seg, ct);
                await c.ChangeDirAsync(seg, ct);
            }
        }
    }
}
