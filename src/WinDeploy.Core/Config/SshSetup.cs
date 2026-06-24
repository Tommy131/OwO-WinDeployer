using WinDeploy.Core.I18n;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Config;

/// <summary>
/// SSH per-device policy: generate a fresh ed25519 key on this machine (never from the repo),
/// apply non-secret config/known_hosts, and optionally register the public key on GitHub.
/// </summary>
public static class SshSetup
{
    public static async Task<List<ConfigResult>> RunAsync(string repoRoot, bool register, CancellationToken ct)
    {
        var results = new List<ConfigResult>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDir = Path.Combine(home, ".ssh");
        Directory.CreateDirectory(sshDir);

        var key = Path.Combine(sshDir, "id_ed25519");
        var pub = key + ".pub";

        if (!File.Exists(key))
        {
            var comment = $"{Environment.UserName}@{Environment.MachineName}";
            var r = await Proc.RunAsync("ssh-keygen", new[] { "-t", "ed25519", "-f", key, "-N", "", "-C", comment }, ct: ct);
            results.Add(r.Ok ? ConfigResult.Ok(Localizer.T("engine.ssh.keyName"), Localizer.T("engine.ssh.generated"))
                             : ConfigResult.Fail(Localizer.T("engine.ssh.keyName"), $"ssh-keygen exit {r.ExitCode}"));
        }
        else results.Add(ConfigResult.Skip(Localizer.T("engine.ssh.keyName"), Localizer.T("engine.ssh.keyExists")));

        // Apply non-secret host config + known_hosts from the repo.
        var repoSsh = Path.Combine(repoRoot, "configs", "ssh");
        foreach (var f in new[] { "config", "known_hosts" })
        {
            var s = Path.Combine(repoSsh, f);
            if (!File.Exists(s)) continue;
            var d = Path.Combine(sshDir, f);
            if (File.Exists(d)) { try { File.Copy(d, $"{d}.bak.{DateTime.Now:yyyyMMddHHmmss}", true); } catch { } }
            File.Copy(s, d, true);
            results.Add(ConfigResult.Ok(Localizer.T("engine.ssh.configName"), Localizer.Format("engine.ssh.applyConfig", f)));
        }

        // Outward action — only when explicitly requested.
        if (register)
        {
            if (!File.Exists(pub)) results.Add(ConfigResult.Skip(Localizer.T("engine.ssh.registerName"), Localizer.T("engine.ssh.noPubKey")));
            else
            {
                var gh = CommandFinder.Find("gh");
                if (gh is null) results.Add(ConfigResult.Skip(Localizer.T("engine.ssh.registerName"), Localizer.T("engine.ssh.ghNotInstalled")));
                else
                {
                    var r = await Proc.RunAsync(gh, new[] { "ssh-key", "add", pub, "--title", Environment.MachineName }, ct: ct);
                    results.Add(r.Ok ? ConfigResult.Ok(Localizer.T("engine.ssh.registerName"), Localizer.T("engine.ssh.registered"))
                                     : ConfigResult.Fail(Localizer.T("engine.ssh.registerName"), $"gh exit {r.ExitCode}"));
                }
            }
        }

        return results;
    }
}
