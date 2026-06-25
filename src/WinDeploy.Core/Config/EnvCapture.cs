using System.Diagnostics;
using System.Text.RegularExpressions;
using WinDeploy.Core.I18n;

namespace WinDeploy.Core.Config;

/// <summary>One well-known environment / agent config location to capture into the repo.</summary>
/// <param name="Id">Repo folder under <c>configs/</c>.</param>
/// <param name="Name">Display name in the results list.</param>
/// <param name="SourceDir">Machine directory (with %ENV% variables).</param>
/// <param name="Include">Top-level filename globs always captured (non-sensitive).</param>
/// <param name="Sensitive">Top-level filename globs captured ONLY when the user allows sensitive export
/// (private keys, tokens, credential stores).</param>
/// <param name="SensitiveDirs">Sub-directories copied wholesale ONLY when sensitive export is allowed
/// (e.g. GnuPG's <c>private-keys-v1.d</c>).</param>
/// <param name="RedactText">Redact secrets inside text files unless sensitive export is allowed.</param>
public sealed record EnvCaptureSource(
    string Id, string Name, string SourceDir,
    string[] Include, string[] Sensitive, string[] SensitiveDirs, bool RedactText);

/// <summary>
/// Captures common environment / dotfile / AI-agent configurations (SSH, GnuPG, git credentials, Codex,
/// Claude, OpenSSH server) into the repo's <c>configs/</c> tree. Private keys / tokens / credential stores are
/// excluded by default (honoring "secrets never enter version control") and only included when the user has
/// explicitly opted into a sensitive export; text configs are redacted unless that opt-in is given.
/// </summary>
public static class EnvCapture
{
    public static readonly IReadOnlyList<EnvCaptureSource> Sources = new[]
    {
        new EnvCaptureSource("ssh", "SSH (~/.ssh)", @"%USERPROFILE%\.ssh",
            Include: new[] { "config", "known_hosts", "*.pub", "authorized_keys" },
            Sensitive: new[] { "id_*", "*.pem", "*.key", "*.ppk" },
            SensitiveDirs: Array.Empty<string>(), RedactText: false),

        new EnvCaptureSource("gnupg", "GnuPG (~/.gnupg)", @"%USERPROFILE%\.gnupg",
            Include: new[] { "*.conf", "pubring.kbx", "trustdb.gpg" },
            Sensitive: new[] { "*.key", "*.sec", "secring.*", "random_seed" },
            SensitiveDirs: new[] { "private-keys-v1.d", "openpgp-revocs.d" }, RedactText: false),

        new EnvCaptureSource("git-creds", "Git credentials (~)", @"%USERPROFILE%",
            Include: Array.Empty<string>(),
            Sensitive: new[] { ".git-credentials", ".netrc" },
            SensitiveDirs: Array.Empty<string>(), RedactText: false),

        new EnvCaptureSource("codex", "Codex CLI (~/.codex)", @"%USERPROFILE%\.codex",
            Include: new[] { "*.json", "*.toml", "*.yaml", "*.yml", "config", "*.md" },
            Sensitive: new[] { "auth*", "*token*", "*credential*", "*.pem", "*.key" },
            SensitiveDirs: Array.Empty<string>(), RedactText: true),

        new EnvCaptureSource("claude", "Claude Code (~/.claude)", @"%USERPROFILE%\.claude",
            Include: new[] { "settings.json", "*.json", "CLAUDE.md" },
            Sensitive: new[] { ".credentials*", "*token*", "*.key" },
            SensitiveDirs: Array.Empty<string>(), RedactText: true),

        new EnvCaptureSource("openssh", "OpenSSH server (ProgramData\\ssh)", @"%PROGRAMDATA%\ssh",
            Include: new[] { "sshd_config", "ssh_config", "*.conf", "*.pub" },
            Sensitive: new[] { "ssh_host_*" },
            SensitiveDirs: Array.Empty<string>(), RedactText: false),
    };

    /// <summary>Capture every source into <c>&lt;repoRoot&gt;/configs/&lt;id&gt;</c>. One <see cref="ConfigResult"/>
    /// per source. <paramref name="allowSensitive"/> includes private keys / tokens and disables redaction.</summary>
    public static List<ConfigResult> Capture(string repoRoot, bool allowSensitive)
        => Sources.Select(s => CaptureOne(repoRoot, s, allowSensitive)).ToList();

    /// <summary>Restore every captured source from <c>&lt;repoRoot&gt;/configs/&lt;id&gt;</c> back onto this machine
    /// (the apply / new-machine direction). Existing files are backed up first; restored private keys / credentials
    /// have their ACL tightened to the current user so e.g. OpenSSH will accept them.</summary>
    public static List<ConfigResult> Apply(string repoRoot)
        => Sources.Select(s => ApplyOne(repoRoot, s)).ToList();

    private static ConfigResult ApplyOne(string repoRoot, EnvCaptureSource s)
    {
        var src = Path.Combine(repoRoot, "configs", s.Id);
        if (!Directory.Exists(src)) return ConfigResult.Skip(s.Name, Localizer.T("engine.envcap.noRepo"));

        string dir;
        try { dir = Environment.ExpandEnvironmentVariables(s.SourceDir); }
        catch { return ConfigResult.Skip(s.Name, Localizer.T("engine.envcap.noRepo")); }

        var sensitive = s.Sensitive.Select(Glob).ToList();
        var n = 0;
        var files = new List<ConfigFileInfo>();

        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories); }
        catch { return ConfigResult.Skip(s.Name, Localizer.T("engine.envcap.noRepo")); }

        foreach (var path in all)
        {
            try
            {
                var rel = Path.GetRelativePath(src, path);
                var d = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                if (File.Exists(d)) { try { File.Copy(d, $"{d}.bak.{Stamp}", true); } catch { /* best effort */ } }
                File.Copy(path, d, true);
                if (sensitive.Any(r => r.IsMatch(Path.GetFileName(d)))) HardenAcl(d);   // private key / credential
                n++;
                string? text = null;
                if (Secrets.IsTextConfig(Path.GetFileName(d))) { try { text = File.ReadAllText(d); } catch { /* preview only */ } }
                files.Add(new ConfigFileInfo { Path = d, Size = new FileInfo(d).Length, Preview = ConfigSync.MakePreview(text) });
            }
            catch { /* skip a single bad file */ }
        }

        return n == 0
            ? ConfigResult.Skip(s.Name, Localizer.T("engine.envcap.empty"))
            : ConfigResult.Ok(s.Name, Localizer.Format("engine.envcap.applied", n), files);
    }

    private static string Stamp => DateTime.Now.ToString("yyyyMMddHHmmss");

    /// <summary>Tighten a restored private key / credential file's ACL to the current user only (remove
    /// inheritance), so tools like OpenSSH don't reject a too-permissive key. Best-effort, no elevation needed.</summary>
    private static void HardenAcl(string file)
    {
        try
        {
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var psi = new ProcessStartInfo("icacls", $"\"{file}\" /inheritance:r /grant:r \"{user}:F\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { /* the file is still restored; the user can fix permissions manually */ }
    }

    private static ConfigResult CaptureOne(string repoRoot, EnvCaptureSource s, bool allowSensitive)
    {
        string dir;
        try { dir = Environment.ExpandEnvironmentVariables(s.SourceDir); }
        catch { return ConfigResult.Skip(s.Name, Localizer.T("engine.envcap.notFound")); }
        if (!Directory.Exists(dir)) return ConfigResult.Skip(s.Name, Localizer.T("engine.envcap.notFound"));

        var dst = Path.Combine(repoRoot, "configs", s.Id);
        var include = s.Include.Select(Glob).ToList();
        var sensitive = s.Sensitive.Select(Glob).ToList();

        int n = 0, redacted = 0, skippedSensitive = 0;
        var files = new List<ConfigFileInfo>();

        IEnumerable<string> topLevel;
        try { topLevel = Directory.EnumerateFiles(dir); }
        catch { return ConfigResult.Skip(s.Name, Localizer.T("engine.envcap.notFound")); }

        foreach (var path in topLevel)
        {
            var name = Path.GetFileName(path);
            var inc = include.Any(r => r.IsMatch(name));
            var sens = sensitive.Any(r => r.IsMatch(name));
            if (!inc && !sens) continue;
            if (sens && !inc && !allowSensitive) { skippedSensitive++; continue; }

            try
            {
                Directory.CreateDirectory(dst);
                var d = Path.Combine(dst, name);
                string? text = null;
                if (s.RedactText && !allowSensitive && Secrets.IsTextConfig(name))
                {
                    var (rt, c) = Secrets.Redact(File.ReadAllText(path));
                    File.WriteAllText(d, rt);
                    redacted += c;
                    text = rt;
                }
                else
                {
                    File.Copy(path, d, true);
                    if (Secrets.IsTextConfig(name)) { try { text = File.ReadAllText(d); } catch { /* preview only */ } }
                }
                n++;
                files.Add(new ConfigFileInfo { Path = ConfigSync.RepoRel(repoRoot, d), Size = new FileInfo(d).Length, Preview = ConfigSync.MakePreview(text) });
            }
            catch { /* a single unreadable file shouldn't abort the source */ }
        }

        // Sensitive sub-directories (whole-tree) — only on an explicit sensitive export.
        if (allowSensitive)
            foreach (var sub in s.SensitiveDirs)
            {
                var subDir = Path.Combine(dir, sub);
                if (!Directory.Exists(subDir)) continue;
                try
                {
                    foreach (var path in Directory.EnumerateFiles(subDir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(dir, path);
                        var d = Path.Combine(dst, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                        File.Copy(path, d, true);
                        n++;
                        files.Add(new ConfigFileInfo { Path = ConfigSync.RepoRel(repoRoot, d), Size = new FileInfo(d).Length, Preview = ConfigSync.MakePreview(null) });
                    }
                }
                catch { /* skip an unreadable sensitive dir */ }
            }

        if (n == 0)
            return ConfigResult.Skip(s.Name, skippedSensitive > 0
                ? Localizer.Format("engine.envcap.onlySensitive", skippedSensitive)
                : Localizer.T("engine.envcap.empty"));

        var msg = redacted > 0 ? Localizer.Format("engine.envcap.capturedRedacted", n, redacted)
                : skippedSensitive > 0 ? Localizer.Format("engine.envcap.capturedSkipped", n, skippedSensitive)
                : Localizer.Format("engine.envcap.captured", n);
        return ConfigResult.Ok(s.Name, msg, files);
    }

    /// <summary>Filename glob (<c>*</c>, <c>?</c>) → anchored case-insensitive regex.</summary>
    private static Regex Glob(string pattern)
        => new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
}
